using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using ThunderRoad;

namespace BladeAndSorcery.Multiplayer;

// Filter the native forced-fatality decision for this one cut. Shared catalog
// data stays unchanged, so NPCs and unrelated/native deaths keep their rules.
[HarmonyPatch]
internal static class LimbLossPhysicsPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(Damager),nameof(Damager.TryHit));
        yield return AccessTools.Method(typeof(RagdollPart),"OnJointBreak");
    }
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions,MethodBase original)
    {
        var field=AccessTools.Field(typeof(CreatureData.PartData),nameof(CreatureData.PartData.sliceForceKill));
        bool weapon=original.DeclaringType==typeof(Damager);
        int matches=0;
        foreach(var instruction in instructions)
        {
            yield return instruction;
            if(!instruction.LoadsField(field)) continue;
            matches++;
            yield return new CodeInstruction(weapon ? OpCodes.Ldarg_1 : OpCodes.Ldarg_0);
            yield return new CodeInstruction(OpCodes.Call,AccessTools.Method(typeof(LimbLossPhysicsPatch),weapon ? nameof(WeaponFatality) : nameof(JointFatality)));
        }
        if(matches!=1) throw new InvalidOperationException("Native limb fatality decision changed: "+original.Name);
    }
    private static bool WeaponFatality(bool native,CollisionInstance hit)
        => Filter(native,hit.damageStruct.hitRagdollPart);
    private static bool JointFatality(bool native,RagdollPart part) => Filter(native,part);
    private static bool Filter(bool native,RagdollPart? part)
        => native && (part==null || MultiplayerRuntime.Instance?.World?.SurvivesLimbSlice(part)!=true);
}
