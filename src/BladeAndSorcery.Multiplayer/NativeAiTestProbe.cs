using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using ThunderRoad;
using ThunderRoad.AI;
using ThunderRoad.AI.Action;

namespace BladeAndSorcery.Multiplayer;

// Observe native AI decision exits without changing any return value. This patch
// is installed only in the explicitly isolated headset-free test runtime.
[HarmonyPatch(typeof(BrainModuleMelee),nameof(BrainModuleMelee.EvaluateNode))]
internal static class NativeAiTestProbe
{
    private static readonly Dictionary<int,string> exits=new Dictionary<int,string>();
    private static readonly Dictionary<ulong,string> picks=new Dictionary<ulong,string>();
    private static bool Prepare() => !string.IsNullOrEmpty(MultiplayerRuntime.Instance?.TestDirectory);
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var code=instructions.ToList(); exits.Clear();picks.Clear();int exit=0;
        for(int i=0;i<code.Count;i++)
        {
            var instruction=code[i];
            if(instruction.opcode==OpCodes.Ret)
            {
                exits[exit]=string.Join("; ",code.Skip(System.Math.Max(0,i-14)).Take(System.Math.Min(i,14)).Where(c=>c.operand is System.Reflection.MemberInfo).Select(c=>((System.Reflection.MemberInfo)c.operand).Name));
                instruction.opcode=OpCodes.Ldarg_0;instruction.operand=null;
                yield return instruction;
                yield return new CodeInstruction(OpCodes.Ldarg_1);
                yield return new CodeInstruction(OpCodes.Ldc_I4,exit++);
                yield return new CodeInstruction(OpCodes.Call,AccessTools.Method(typeof(NativeAiTestProbe),nameof(Observe)));
                yield return new CodeInstruction(OpCodes.Ret);
                continue;
            }
            if(instruction.Calls(AccessTools.Method(typeof(MeleeIdle),nameof(MeleeIdle.PickBestMotion))))
                instruction.operand=AccessTools.Method(typeof(NativeAiTestProbe),nameof(Pick));
            yield return instruction;
        }
    }
    private static AttackMotion? Pick(MeleeIdle idle,BrainModuleMelee melee,UnityEngine.Transform target,float maxdelta)
    {
        var selected=idle.PickBestMotion(melee,target,maxdelta);
        var entity=MultiplayerRuntime.Instance?.World?.Find(melee.creature);
        if(entity!=null) picks[entity.Descriptor.Id]="selected="+(selected?.id ?? "null")+" delta="+maxdelta;
        return selected;
    }
    private static State Observe(State result,BrainModuleMelee melee,AttackMelee node,int exit)
    {
        var world=MultiplayerRuntime.Instance?.World;var entity=world?.Find(melee.creature);
        if(world!=null && entity!=null)
        {
            string detail="";
            if(exit==25 && node.targetTransform!=null)
            {
                var target=node.targetTransform;
                var velocity=target.GetComponent<Locomotion>()?.physicBody.velocity ?? target.GetPhysicBodyInParent()?.velocity ?? UnityEngine.Vector3.zero;
                var distance=(target.position.ToXZ()-melee.creature.transform.position.ToXZ()).magnitude;
                detail=" targetMode="+node.target+" pick="+(picks.TryGetValue(entity.Descriptor.Id,out var pick) ? pick : "none")+" target="+target.name+" distance="+distance+" velocity="+velocity.ToString("F4")+" speed="+melee.GetBrainSpeedMultiplier(Interactable.HandSide.Right)+" motions="+string.Join(",",melee.meleeIdle?.attackMotions.Select(m=>
                {
                    float peak=m.GetRealPeakTime(melee.GetBrainSpeedMultiplier(m.attackSide));
                    float predicted=(target.position.ToXZ()+velocity.ToXZ()*peak-melee.creature.transform.position.ToXZ()).magnitude;
                    float reach=m.includeWeaponReach ? melee.GetWeaponReach(m.attackSide) : 0;
                    return m.id+"/peak="+peak+"/predicted="+predicted+"/reach="+reach+"/min="+(m.minRange*melee.creature.transform.lossyScale.y)+"/weight="+m.weight;
                }) ?? System.Array.Empty<string>());
            }
            world.AiEvaluations[entity.Descriptor.Id]=result+" exit "+exit+": "+exits[exit]+detail;
        }
        return result;
    }
}
