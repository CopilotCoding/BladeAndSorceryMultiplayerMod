using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BladeAndSorcery.Multiplayer.Core;
using HarmonyLib;
using ThunderRoad;
using UnityEngine;

namespace BladeAndSorcery.Multiplayer;

// Native ragdoll handles assume every grabber has a local PlayerHand. Remote
// humans instead have a network controller; keep the original handle/joint path.
[HarmonyPatch]
internal static class NetworkBodyGripAnchorPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(HandleRagdoll),nameof(HandleRagdoll.OnGrab));
        yield return AccessTools.Method(typeof(HandleRagdoll),nameof(HandleRagdoll.OnUnGrab));
        yield return AccessTools.Method(typeof(HandleRagdoll),"ManagedUpdate");
    }
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var code=instructions.ToList();int anchors=0;
        var playerHand=AccessTools.Field(typeof(RagdollHand),nameof(RagdollHand.playerHand));
        var grip=AccessTools.Field(typeof(PlayerHand),nameof(PlayerHand.grip));
        for(int i=0;i<code.Count;i++)
        {
            if(i+2<code.Count && code[i].LoadsField(playerHand) && code[i+1].LoadsField(grip))
            {
                int end=i+2;
                if(code[end].operand is MethodInfo gameObject && gameObject.Name=="get_gameObject") end++;
                if(end>=code.Count || code[end].operand is not MethodInfo transform || transform.Name!="get_transform") throw new InvalidOperationException("Native body grip anchor changed");
                if(code.Skip(i+1).Take(end-i).Any(c=>((System.Collections.ICollection)AccessTools.Field(typeof(CodeInstruction),"labels").GetValue(c)).Count>0 || c.blocks.Count>0)) throw new InvalidOperationException("Unexpected branch inside native body grip anchor");
                code[i].opcode=OpCodes.Call;code[i].operand=AccessTools.Method(typeof(NetworkBodyGripAnchorPatch),nameof(GripTarget));
                yield return code[i];i=end;anchors++;continue;
            }
            if(code[i].Calls(AccessTools.Method(typeof(Ragdoll),nameof(Ragdoll.SetState),new[]{typeof(Ragdoll.State)})))
            {
                code[i].opcode=OpCodes.Call;code[i].operand=AccessTools.Method(typeof(NetworkBodyGripAnchorPatch),nameof(SetNpcState));
            }
            if(code[i].Calls(AccessTools.Method(typeof(Ragdoll),nameof(Ragdoll.SetPinForceMultiplier))))
            {
                code[i].opcode=OpCodes.Call;code[i].operand=AccessTools.Method(typeof(NetworkBodyGripAnchorPatch),nameof(SetNpcArmForces));
            }
            if(code[i].Calls(AccessTools.Method(typeof(Ragdoll),nameof(Ragdoll.ResetPinForce))))
            {
                code[i].opcode=OpCodes.Call;code[i].operand=AccessTools.Method(typeof(NetworkBodyGripAnchorPatch),nameof(ResetNpcArmForces));
            }
            yield return code[i];
        }
        if(anchors!=1) throw new InvalidOperationException("Native body grip anchor count changed");
    }
    private static Transform GripTarget(RagdollHand hand) => hand.playerHand!=null ? hand.playerHand.grip.transform : MultiplayerRuntime.Instance?.World?.Find(hand.creature)?.ControllerTarget(hand.side) ?? hand.grip;
    private static void SetNpcState(Ragdoll ragdoll,Ragdoll.State state)
    {
        if(MultiplayerRuntime.Instance?.World?.Find(ragdoll.creature)?.Descriptor.Kind==EntityKind.Player) return;
        ragdoll.SetState(state);
    }
    private static bool NetworkPlayer(Ragdoll ragdoll) => MultiplayerRuntime.Instance?.World?.Find(ragdoll.creature)?.Descriptor.Kind==EntityKind.Player;
    private static void SetNpcArmForces(Ragdoll ragdoll,float spring,float damper,float maxPosition,float maxRotation,bool limits,bool noRoot,RagdollPart.Type parts,Ragdoll.Region region)
    {
        if(!NetworkPlayer(ragdoll)) ragdoll.SetPinForceMultiplier(spring,damper,maxPosition,maxRotation,limits,noRoot,parts,region);
    }
    private static void ResetNpcArmForces(Ragdoll ragdoll,bool limits,bool noRoot,RagdollPart.Type parts)
    {
        if(!NetworkPlayer(ragdoll)) ragdoll.ResetPinForce(limits,noRoot,parts);
    }
}

// Native NPC posture/lift forces must not compete with a human's VR tracking
// or the remote replica controller. Physical grip joints remain active.
[HarmonyPatch(typeof(HandleRagdoll),"ManagedUpdate")]
internal static class NetworkBodyGripPosturePatch
{
    private static bool Prefix(HandleRagdoll __instance) => MultiplayerRuntime.Instance?.World?.Find(__instance.ragdollPart.ragdoll.creature)?.Descriptor.Kind!=EntityKind.Player;
}

[HarmonyPatch]
internal static class NetworkBodyGripPinPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(HandleRagdoll),nameof(HandleRagdoll.OnGrab));
        yield return AccessTools.Method(typeof(HandleRagdoll),nameof(HandleRagdoll.OnUnGrab));
    }
    private static void Postfix(HandleRagdoll __instance)
    {
        var entity=MultiplayerRuntime.Instance?.World?.Find(__instance.ragdollPart.ragdoll.creature);
        if(entity==null || entity.Descriptor.Kind!=EntityKind.Player) return;
        entity.RefreshPhysicalHandMass();
        if(!entity.Owned) entity.ClearRemotePinForces();
        else if(!Ragdoll.IsPhysicalState(entity.Creature!.ragdoll.state))
            foreach(var part in entity.Creature.ragdoll.parts) part.DisableCharJointLimit();
    }
}
