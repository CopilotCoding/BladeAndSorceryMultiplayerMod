using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using BladeAndSorcery.Multiplayer.Core;
using HarmonyLib;
using ThunderRoad;

namespace BladeAndSorcery.Multiplayer;

// A remote human has no local Player component. Adapt only the native AI's
// player-specific predicates; never change Creature.isPlayer or ragdoll control.
[HarmonyPatch(typeof(BrainModuleDetection), nameof(BrainModuleDetection.CheckIncomingStrike))]
internal static class NetworkPlayerDefensePatch
{
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        var state = AccessTools.PropertyGetter(typeof(Creature),nameof(Creature.state));
        var player = AccessTools.Field(typeof(Player),nameof(Player.currentCreature));
        int states=0, players=0;
        foreach(var instruction in code)
        {
            if(instruction.Calls(state))
            {
                states++; instruction.opcode=OpCodes.Call;
                instruction.operand=AccessTools.Method(typeof(NetworkPlayerDefensePatch),nameof(CombatState));
            }
            if(instruction.LoadsField(player))
            {
                players++; instruction.opcode=OpCodes.Ldarg_1; instruction.operand=null;
                yield return instruction;
                yield return new CodeInstruction(OpCodes.Call,AccessTools.Method(typeof(NetworkPlayerDefensePatch),nameof(PlayerTarget)));
            }
            else yield return instruction;
        }
        if(states!=1 || players!=1) throw new InvalidOperationException("Native AI defense predicates changed; refusing an incompatible patch");
    }
    private static Creature.State CombatState(Creature creature)
    {
        var entity=MultiplayerRuntime.Instance?.World?.Find(creature);
        return entity?.Descriptor.Kind==EntityKind.Player && !entity.Owned && !creature.isKilled && entity.SourceAlive
            ? Creature.State.Alive : creature.state;
    }
    private static Creature PlayerTarget(Creature target)
        => MultiplayerRuntime.Instance?.World?.Find(target)?.Descriptor.Kind==EntityKind.Player ? target : Player.currentCreature;
}

[HarmonyPatch(typeof(BrainModuleMelee),nameof(BrainModuleMelee.TriggerAttack))]
internal static class NetworkPlayerMeleePhysicsPatch
{
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var player=AccessTools.Field(typeof(Player),nameof(Player.currentCreature));
        int count=0;
        foreach(var instruction in instructions)
        {
            if(!instruction.LoadsField(player)) {yield return instruction;continue;}
            count++; instruction.opcode=OpCodes.Ldarg_0; instruction.operand=null;
            yield return instruction;
            yield return new CodeInstruction(OpCodes.Call,AccessTools.Method(typeof(NetworkPlayerMeleePhysicsPatch),nameof(PlayerTarget)));
        }
        if(count!=1) throw new InvalidOperationException("Native AI attack physics predicate changed; refusing an incompatible patch");
    }
    private static Creature PlayerTarget(BrainModuleMelee melee)
    {
        var world=MultiplayerRuntime.Instance?.World;
        var target=melee.creature.brain.currentTarget;
        return world?.Find(melee.creature)?.Descriptor.Kind==EntityKind.Creature && world.Find(target)?.Descriptor.Kind==EntityKind.Player
            ? target : Player.currentCreature;
    }
}
