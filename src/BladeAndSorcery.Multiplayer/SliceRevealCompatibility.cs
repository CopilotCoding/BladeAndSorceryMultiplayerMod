using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using ThunderRoad;
using ThunderRoad.Reveal;
using UnityEngine;

namespace BladeAndSorcery.Multiplayer;

// Some reveal optimizers skip RevealDecal.Awake on LODs. OnSlice immediately
// dereferences the new decal's controller, so a missing controller aborts native
// slice completion, including the reliable slice event and sliceRunning reset.
[HarmonyPatch(typeof(Ragdoll),"OnSlice")]
internal static class SliceRevealCompatibility
{
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        int matches=0;
        foreach(var instruction in instructions)
        {
            if(instruction.operand is MethodInfo method && method.DeclaringType==typeof(GameObject) &&
                method.Name==nameof(GameObject.AddComponent) && method.IsGenericMethod && method.GetGenericArguments()[0]==typeof(RevealDecal))
            {
                instruction.opcode=OpCodes.Ldarg_0;instruction.operand=null;yield return instruction;
                yield return new CodeInstruction(OpCodes.Call,AccessTools.Method(typeof(SliceRevealCompatibility),nameof(CreateSplitReveal)));matches++;
            }
            else yield return instruction;
        }
        if(matches!=1)throw new InvalidOperationException("Native slice reveal creation changed; refusing an incompatible compatibility patch.");
    }
    private static RevealDecal CreateSplitReveal(GameObject target,Ragdoll ragdoll)
    {
        var decal=target.AddComponent<RevealDecal>();
        if(decal.revealMaterialController!=null || MultiplayerRuntime.Instance?.World?.Find(ragdoll.creature)==null)return decal;
        // Restore just the missing native dependency. Preserve an optimizer's
        // existing/shared controller whenever it supplied one.
        if(target.GetComponent<MaterialInstance>()==null)target.AddComponent<MaterialInstance>();
        decal.revealMaterialController=target.GetComponent<RevealMaterialController>() ?? target.AddComponent<RevealMaterialController>();
        var controller=decal.revealMaterialController;
        controller.width=(int)decal.maskWidth;controller.height=(int)decal.maskHeight;
        controller.maskPropertyName="_RevealMask";controller.maskPropertyHash=Shader.PropertyToID("_RevealMask");
        controller.restoreMaterialsOnReset=false;controller.renderTextureFormat=RenderTextureFormat.ARGB64;
        return decal;
    }
}
