using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using ThunderRoad;

namespace BladeAndSorcery.Multiplayer;

[HarmonyPatch(typeof(Creature), nameof(Creature.Despawn), new Type[] { })]
internal static class RemotePlayerLifetimePatch
{
    private static bool Prefix(Creature __instance)
        => MultiplayerRuntime.Instance?.World?.AllowNativeCreatureDespawn(__instance) ?? true;
}

[HarmonyPatch(typeof(Creature), nameof(Creature.Damage), new[] { typeof(CollisionInstance) })]
internal static class DamagePatch
{
    private static bool Prefix(Creature __instance, CollisionInstance collisionInstance)
    {
        var world = MultiplayerRuntime.Instance?.World;
        if(world?.HandleNativeNpcMagic(__instance,collisionInstance)==false)
        {world.TraceDamage(__instance,collisionInstance,false);return false;}
        if(world?.HandleNativePlayerWeaponDamage(__instance,collisionInstance)==false)
        {world.TraceDamage(__instance,collisionInstance,false);return false;}
        bool allowed = world == null || world.Applying || (world.AllowDamage(__instance) && world.Match.AllowHit(__instance, collisionInstance));
        world?.TraceDamage(__instance, collisionInstance, allowed);
        return allowed && !(world?.FinishFatalPlayerSlice(__instance, collisionInstance) ?? false);
    }
}

[HarmonyPatch(typeof(Ragdoll), nameof(Ragdoll.TrySlice), new[] { typeof(RagdollPart) })]
internal static class SlicePatch
{
    private static bool Prefix(Ragdoll __instance, ref bool __result)
    {
        var world = MultiplayerRuntime.Instance?.World;
        if (world == null || world.AllowSlice(__instance)) return true;
        __result = false; return false;
    }
}

[HarmonyPatch(typeof(Damager), nameof(Damager.TryHit))]
internal static class PlayerSlicePhysicsPatch
{
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        var state = AccessTools.Field(typeof(Ragdoll), nameof(Ragdoll.state));
        // Change only the native Kinematic exclusion inside the slicing condition.
        // The real ragdoll state and all native blade/material/velocity tests stay intact.
        var matches = Enumerable.Range(0, code.Count-2).Where(i => code[i].LoadsField(state) &&
            code[i+1].LoadsConstant((int)Ragdoll.State.Kinematic) &&
            (code[i+2].opcode == OpCodes.Beq || code[i+2].opcode == OpCodes.Beq_S)).ToArray();
        if (matches.Length != 1) throw new InvalidOperationException("Native player-slicing condition changed; refusing an incompatible combat patch");
        int match = matches[0];
        for (int i = 0; i < code.Count; i++)
        {
            if (i != match) { yield return code[i]; continue; }
            // Preserve incoming labels and exception boundaries on the replaced instruction.
            code[i].opcode = OpCodes.Ldarg_1; code[i].operand = null;
            yield return code[i];
            yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(PlayerSlicePhysicsPatch), nameof(SliceState)));
        }
    }
    private static Ragdoll.State SliceState(Ragdoll ragdoll, CollisionInstance hit)
        => MultiplayerRuntime.Instance?.World?.PlayerSliceState(ragdoll,hit) ?? ragdoll.state;
}

[HarmonyPatch]
internal static class RemotePlayerBladeAnglePatch
{
    private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(Damager), nameof(Damager.CanHit));
        yield return AccessTools.Method(typeof(Damager), nameof(Damager.TryHit));
    }
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var left = AccessTools.Field(typeof(Item), nameof(Item.leftNpcHand));
        var right = AccessTools.Field(typeof(Item), nameof(Item.rightNpcHand));
        int count = 0;
        foreach (var instruction in instructions)
        {
            yield return instruction;
            if (!instruction.LoadsField(left) && !instruction.LoadsField(right)) continue;
            count++;
            yield return new CodeInstruction(OpCodes.Call,AccessTools.Method(typeof(RemotePlayerBladeAnglePatch),nameof(NpcHand)));
        }
        if (count != 2) throw new InvalidOperationException("Native blade-angle condition changed; refusing an incompatible combat patch");
    }
    private static RagdollHand? NpcHand(RagdollHand? hand)
        => hand != null && MultiplayerRuntime.Instance?.World?.Find(hand.creature)?.Descriptor.Kind == BladeAndSorcery.Multiplayer.Core.EntityKind.Player ? null : hand;
}

[HarmonyPatch(typeof(Damager), nameof(Damager.TryPerformSlice))]
internal static class SliceTracePatch
{
    private static bool Prefix(Damager __instance,CollisionInstance collisionInstance, ref bool __result)
    {
        if(MultiplayerRuntime.Instance?.World?.HandlePlayerCut(__instance,collisionInstance) is bool handled)
        {__result=handled;return false;}
        if (MultiplayerRuntime.Instance?.World?.AllowPhysicalPlayerSlice(collisionInstance) != false) return true;
        __result = false; return false;
    }
    private static void Postfix(CollisionInstance collisionInstance, bool __result)
        => MultiplayerRuntime.Instance?.World?.TraceSlice(collisionInstance,__result);
}

internal static class NativeCutCooldown
{
    private static readonly AccessTools.FieldRef<Damager,float> Time=AccessTools.FieldRefAccess<Damager,float>("lastSliceTime");
    private static readonly AccessTools.FieldRef<Damager,RagdollPart> Part=AccessTools.FieldRefAccess<Damager,RagdollPart>("lastSliceRagdollPart");
    internal static void Commit(Damager damager,RagdollPart contact)
    {
        Time(damager)=UnityEngine.Time.time;Part(damager)=contact;
    }
}

[HarmonyPatch(typeof(RagdollPart), nameof(RagdollPart.TrySlice))]
internal static class NativeSliceOriginPatch
{
    private static void Prefix(RagdollPart __instance, out ReplicatedEntity? __state)
    {
        var world = MultiplayerRuntime.Instance?.World;
        var entity = world?.Find(__instance.ragdoll.creature);
        __state = world != null && !world.Applying && entity?.Owned == true ? entity : null;
        if (__state != null) __state.SliceOrigins[__instance.sliceChildAndDisableSelf ?? __instance] = __instance;
    }
    private static void Postfix(RagdollPart __instance, ReplicatedEntity? __state, bool __result)
    {
        if (!__result && __state != null) __state.SliceOrigins.Remove(__instance.sliceChildAndDisableSelf ?? __instance);
    }
}

[HarmonyPatch(typeof(Damager), nameof(Damager.CheckPenetration))]
internal static class RemotePlayerPenetrationPatch
{
    private static void Prefix(Damager __instance, ref Damager.PenetrationConditions conditions)
    {
        var world = MultiplayerRuntime.Instance?.World;
        var holder = __instance.collisionHandler?.item?.mainHandler?.creature;
        if (holder != null && world?.Find(holder)?.Descriptor.Kind == BladeAndSorcery.Multiplayer.Core.EntityKind.Player)
            conditions &= ~Damager.PenetrationConditions.NotNPCHandler;
    }
}

[HarmonyPatch(typeof(Damager),nameof(Damager.TryHitByPressure))]
internal static class NetworkPlayerPressureStabPatch
{
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var code=instructions.ToList();int count=0;
        var player=AccessTools.Field(typeof(Creature),nameof(Creature.player));
        for(int i=0;i<code.Count;i++)
        {
            if(code[i].LoadsField(player) && i+1<code.Count && code[i+1].operand is System.Reflection.MethodInfo method && method.Name=="op_Implicit")
            {
                code[i].opcode=OpCodes.Call;code[i].operand=AccessTools.Method(typeof(NetworkPlayerPressureStabPatch),nameof(IsHuman));
                yield return code[i];i++;count++;continue;
            }
            yield return code[i];
        }
        if(count!=1) throw new InvalidOperationException("Native pressure-stab human predicate changed");
    }
    private static bool IsHuman(Creature creature) => creature.player!=null || MultiplayerRuntime.Instance?.World?.Find(creature)?.Descriptor.Kind==BladeAndSorcery.Multiplayer.Core.EntityKind.Player;
}

[HarmonyPatch(typeof(Damager), nameof(Damager.Penetrate))]
internal static class PenetrationOwnerPatch
{
    private static bool Prefix(Damager __instance, CollisionInstance collisionInstance)
    {
        bool allowed = MultiplayerRuntime.Instance?.World?.AllowPenetration(__instance,collisionInstance) ?? true;
        if (!allowed) collisionInstance.damageStruct.penetration = DamageStruct.Penetration.None;
        return allowed;
    }
}

[HarmonyPatch(typeof(Damager), nameof(Damager.UnPenetrate))]
internal static class DepenetrationOwnerPatch
{
    private static bool Prefix(Damager __instance, CollisionInstance collisionInstance) => MultiplayerRuntime.Instance?.World?.AllowDepenetration(__instance,collisionInstance) ?? true;
}

[HarmonyPatch(typeof(Item), nameof(Item.RefreshCollision),new[]{typeof(bool)})]
internal static class RemotePlayerItemCollisionPatch
{
    private static void Postfix(Item __instance) => MultiplayerRuntime.Instance?.World?.RefreshPlayerItemCollision(__instance);
}

[HarmonyPatch(typeof(Breakable),nameof(Breakable.CanBeBroken))]
internal static class BreakableOwnerPatch
{
    private static bool Prefix(Breakable __instance,ref bool __result)
    {
        if(MultiplayerRuntime.Instance?.World?.AllowNativeBreak(__instance)!=false) return true;
        __result=false;return false;
    }
}
