using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using ThunderRoad;

namespace BladeAndSorcery.Multiplayer;

// Only explicit procedural registrations bypass native animation attack windows.
// Native angles, materials, velocity, self-damage and ownership rules still apply.
[HarmonyPatch]
internal static class ProceduralNpcDamageWindowPatch
{
    internal static bool Available=true;
    private static IEnumerable<MethodBase> TargetMethods()
    {yield return AccessTools.Method(typeof(Damager),nameof(Damager.CanHit));yield return AccessTools.Method(typeof(Damager),nameof(Damager.TryHit));}
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var code=instructions.ToList();var field=AccessTools.Field(typeof(Brain),nameof(Brain.canDamage));
        if(code.Count(i=>i.LoadsField(field))!=1)
        {
            Available=false;MultiplayerRuntime.ReportOptionalPatch("Procedural NPC damage hooks are unavailable after another patch changed the native method. Procedural registration is disabled; ordinary NPCs retain the incoming method.");
            foreach(var instruction in code)yield return instruction;yield break;
        }
        foreach(var instruction in code)
        {
            if(instruction.LoadsField(field)){instruction.opcode=OpCodes.Call;instruction.operand=AccessTools.Method(typeof(ProceduralNpcDamageWindowPatch),nameof(CanDamage));}
            yield return instruction;
        }
    }
    internal static bool CanDamage(Brain brain)
    {
        var entity=MultiplayerRuntime.Instance?.World?.Find(brain.creature);
        return entity?.Descriptor.ProceduralNpc==true ? entity.ProceduralCanDamage : brain.canDamage;
    }
}
