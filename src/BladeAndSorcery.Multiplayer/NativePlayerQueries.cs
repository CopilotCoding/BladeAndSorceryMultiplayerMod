using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BladeAndSorcery.Multiplayer.Core;
using HarmonyLib;
using ThunderRoad;
using ThunderRoad.AI.Get;

namespace BladeAndSorcery.Multiplayer;

// Scope player recognition to native AI queries. Remote humans keep their
// native physical replica and never acquire a local Player/VR input component.
[HarmonyPatch]
internal static class NetworkHumanAiQueryPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(GetCreature),nameof(GetCreature.Evaluate));
        yield return AccessTools.Method(typeof(GetGrabber),"RagdollInteract");
        yield return AccessTools.Method(typeof(GetRagdollTouch),"CheckConditions");
    }
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions,MethodBase original)
    {
        var player=AccessTools.Field(typeof(Creature),nameof(Creature.isPlayer));
        var state=AccessTools.PropertyGetter(typeof(Creature),nameof(Creature.state));
        int players=0;
        foreach(var instruction in instructions)
        {
            if(instruction.LoadsField(player)){players++;instruction.opcode=OpCodes.Call;instruction.operand=AccessTools.Method(typeof(NetworkHumanAiQueryPatch),nameof(IsHuman));}
            else if(instruction.Calls(state)){instruction.opcode=OpCodes.Call;instruction.operand=AccessTools.Method(typeof(NetworkHumanAiQueryPatch),nameof(CombatState));}
            yield return instruction;
        }
        if(players==0)throw new InvalidOperationException("Native AI player query changed: "+original.Name);
    }
    private static bool IsHuman(Creature creature)=>creature.isPlayer || MultiplayerRuntime.Instance?.World?.Find(creature)?.Descriptor.Kind==EntityKind.Player;
    private static Creature.State CombatState(Creature creature)
    {
        var entity=MultiplayerRuntime.Instance?.World?.Find(creature);
        return entity?.Descriptor.Kind==EntityKind.Player && !entity.Owned && entity.SourceAlive && !creature.isKilled ? Creature.State.Alive : creature.state;
    }
}
