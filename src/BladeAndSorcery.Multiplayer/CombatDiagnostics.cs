using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json;
using ThunderRoad;
using UnityEngine;

namespace BladeAndSorcery.Multiplayer;

internal sealed partial class GameWorld
{
    internal readonly List<object> NativeCombatTrace=new List<object>();
    private readonly Dictionary<string,double> nextCombatProbe=new Dictionary<string,double>();
    internal bool CombatCaptureActive;
    internal void StartCombatCapture()
    {
        NativeCombatTrace.Clear();nextCombatProbe.Clear();CombatCaptureActive=true;
    }
    internal void TraceNativeCombat(string phase,Damager damager,CollisionInstance hit,bool result)
    {
        // Observation only; enabled by the isolated runtime or an explicit capture. Never
        // replace a native decision or loosen a blade/material/velocity test.
        if((!CombatCaptureActive && string.IsNullOrEmpty(MultiplayerRuntime.Instance?.TestDirectory)) || Applying) return;
        var part=hit.targetColliderGroup?.collisionHandler?.ragdollPart;
        var victim=Find(part?.ragdoll.creature);var weapon=Find(damager.collisionHandler?.item);
        if(part==null || victim==null || weapon==null || damager.type==Damager.Type.Blunt) return;
        if(phase=="pressure" && hit.damageStruct.penetration!=DamageStruct.Penetration.None && !result) return;
        string key=phase+":"+damager.GetInstanceID()+":"+victim.Descriptor.Id+":"+GameMath.PartKey(part)+":"+result;
        if(nextCombatProbe.TryGetValue(key,out double next) && Now<next) return;
        if(nextCombatProbe.Count>4096) nextCombatProbe.Clear();
        nextCombatProbe[key]=Now+.05;
        try
        {
            var data=damager.data;var tier=data.GetTier(damager.collisionHandler);
            var modifier=hit.damageStruct.materialModifier ?? data.damageModifierData?.GetModifier(hit);
            float threshold=data.penetrationPressureForceCurve.Evaluate(hit.pressureRelativeVelocity.magnitude)*(modifier?.pressureForceMultiplier ?? 1);
            var candidates=new List<RagdollPart>{part};
            if(part.parentPart!=null) candidates.Add(part.parentPart);
            candidates.AddRange(part.bone.childs.Where(b=>b.part!=null).Select(b=>b.part));
            var sliceChecks=data.dismembermentAllowed && (phase=="hit" || phase=="slice") ? candidates.Distinct().Select(p=>new {
                part=GameMath.PartKey(p),type=p.type.ToString(),allowed=p.sliceAllowed,
                aligned=damager.CheckSlice(hit.contactPoint,p,damagerTier:tier)
            }).Cast<object>().ToArray() : Array.Empty<object>();
            NativeCombatTrace.Add(new {
                time=Now,phase,result,weapon=weapon.Descriptor.Id.ToString(),catalog=weapon.Descriptor.CatalogId,
                sourceOwned=weapon.Owned,targetOwned=victim.Owned,timeScale=Time.timeScale,fixedDeltaTime=Time.fixedDeltaTime,
                frameDelta=Time.unscaledDeltaTime,simulationDelta=Time.deltaTime,
                damager=data.id,kind=damager.type.ToString(),victim=victim.Descriptor.Id.ToString(),part=GameMath.PartKey(part),partType=part.type.ToString(),
                speed=hit.impactVelocity.magnitude,pressureSpeed=hit.pressureRelativeVelocity.magnitude,pressure=hit.pressureForce.magnitude,threshold,
                pressureAngle=damager.CheckAngles(hit.pressureForce),impactAngle=damager.CheckAngles(hit.impactVelocity,hit.contactNormal),
                sourceMaterial=hit.sourceMaterial?.id,targetMaterial=hit.targetMaterial?.id,
                materialAllows=modifier?.allowPenetration,materialPressure=modifier?.pressureAllowed,minimumVelocity=modifier?.penetrationMinVelocity,
                effectiveMinimumVelocity=damager.overrideMinPenetrationVelocity ?? modifier?.penetrationMinVelocity,
                selfSpeed=damager.collisionHandler!.physicBody.velocity.magnitude,minimumSelfSpeed=data.minSelfVelocity,
                angularSpeed=damager.collisionHandler.physicBody.angularVelocity.magnitude,
                maxAngularSpeed=damager.collisionHandler.physicBody.rigidBody.maxAngularVelocity,
                sourceSnapshotSpeed=weapon.Owned ? (float?)null : weapon.Buffer.Sample(Now)?.Root.Velocity.Length(),
                selectedDamager=hit.damageStruct.damager?.data?.id,minimumHitSpeed=modifier?.minVelocity,
                dataAllows=data.penetrationAllowed,dataPressure=data.penetrationPressureAllowed,depth=damager.penetrationDepth,length=damager.penetrationLength,
                activeCollisionSlots=damager.collisionHandler.collisions.Count(c=>c.active),
                penetrationSlots=damager.collisionHandler.collisions.Count(c=>c.damageStruct.penetration!=DamageStruct.Penetration.None),
                penetrationJoints=damager.collisionHandler.collisions.Count(c=>c.damageStruct.penetrationJoint!=null),
                orphanedPenetrations=damager.collisionHandler.collisions.Count(c=>c.damageStruct.penetration!=DamageStruct.Penetration.None && c.damageStruct.penetrationJoint==null),
                contactHasJoint=hit.damageStruct.penetrationJoint!=null,partBlocksPiercing=damager.collisionHandler.item?.CanPenetratePart(part),
                targetAllows=hit.targetColliderGroup?.data?.allowPenetration,mode=hit.damageStruct.penetration.ToString(),badAngle=hit.damageStruct.badAngle,
                point=new[]{hit.contactPoint.x,hit.contactPoint.y,hit.contactPoint.z},forward=new[]{damager.transform.forward.x,damager.transform.forward.y,damager.transform.forward.z},
                mass=damager.collisionHandler!.physicBody.mass,partKinematic=part.physicBody.isKinematic,
                nativeSlice=part.sliceAllowed,dataAllowsSlice=data.dismembermentAllowed,
                cutMinimum=data.dismembermentMinVelocity*(tier?.dismembermentMinVelocityMultiplier ?? 1),
                ragdoll=part.ragdoll.state.ToString(),health=part.ragdoll.creature.currentHealth,
                held=damager.collisionHandler.item?.handlers.Count,sliceChecks,
                grips=damager.collisionHandler.item?.handlers.Select(h=>new {side=h.side.ToString(),axis=h.gripInfo?.axisPosition,
                    controllerKinematic=h.gripInfo?.joint?.GetComponent<Rigidbody>()?.isKinematic,
                    controllerIterations=h.gripInfo?.joint?.GetComponent<Rigidbody>()?.solverIterations,
                    controllerMaxAngularSpeed=h.gripInfo?.joint?.GetComponent<Rigidbody>()?.maxAngularVelocity}).ToArray()
            });
            if(NativeCombatTrace.Count>2000) NativeCombatTrace.RemoveAt(0);
        }
        catch(Exception e) when(e is MissingReferenceException || e is NullReferenceException) { }
    }
}

public sealed partial class MultiplayerRuntime
{
    internal string LastCombatCapturePath="";
    internal void BeginCombatCapture()
    {
        if(World==null){WriteLog("Join or host a session before recording combat.");return;}
        World.StartCombatCapture();
        connectionNotice="Combat capture recording. Reproduce the hit, then press Save combat capture in Diagnostics.";
        WriteLog("Combat capture started");RefreshNativeStatus();
    }
    internal void SaveCombatCapture()
    {
        if(World==null){WriteLog("No active session to capture.");return;}
        try
        {
            string folder=TestDirectory.Length>0 ? TestDirectory : Path.Combine(ModFolder,"Diagnostics");
            Directory.CreateDirectory(folder);
            string path=Path.Combine(folder,"combat-capture-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff")+".json");
            var report=new {time=DateTime.UtcNow.ToString("o"),game=GameSettings.GetVersionString(),
                mod=typeof(MultiplayerRuntime).Assembly.ManifestModule.ModuleVersionId.ToString("N"),
                level=Level.current?.data?.id,peer=Session?.LocalId,contacts=World.NativeCombatTrace.ToArray()};
            File.WriteAllText(path,JsonConvert.SerializeObject(report,Formatting.Indented));
            World.CombatCaptureActive=false;LastCombatCapturePath=path;
            connectionNotice="Combat capture saved in the mod's Diagnostics folder.";
            WriteLog("Combat capture saved: "+path);RefreshNativeStatus();
        }
        catch(Exception e) when(e is IOException || e is UnauthorizedAccessException)
        {WriteLog("Could not save combat capture: "+e.Message);}
    }
    [ModOption("Record combat capture",valueSourceName=nameof(PressAction),saveValue=false,applyOnLoad=false)]
    [ModOptionButton,ModOptionCategory("Diagnostics",5),ModOptionOrder(0)]
    public static void RecordCombatAction(int press=0)=>Instance?.BeginCombatCapture();
    [ModOption("Save combat capture",valueSourceName=nameof(PressAction),saveValue=false,applyOnLoad=false)]
    [ModOptionButton,ModOptionCategory("Diagnostics",5),ModOptionOrder(1)]
    public static void SaveCombatAction(int press=0)=>Instance?.SaveCombatCapture();
}

[HarmonyPatch(typeof(Damager),nameof(Damager.TryHit))]
internal static class HitDecisionProbe
{
    private static void Postfix(Damager __instance,CollisionInstance collisionInstance,bool __result)
        => MultiplayerRuntime.Instance?.World?.TraceNativeCombat("hit",__instance,collisionInstance,__result);
}

[HarmonyPatch(typeof(Damager),nameof(Damager.CanHit))]
internal static class HitCandidateProbe
{
    private static void Postfix(Damager __instance,CollisionInstance collisionInstance,bool __result)
        => MultiplayerRuntime.Instance?.World?.TraceNativeCombat("candidate",__instance,collisionInstance,__result);
}

[HarmonyPatch(typeof(Damager),nameof(Damager.TryHitByPressure))]
internal static class PressureDecisionProbe
{
    private static void Postfix(Damager __instance,CollisionInstance collisionInstance,bool __result)
        => MultiplayerRuntime.Instance?.World?.TraceNativeCombat("pressure",__instance,collisionInstance,__result);
}
[HarmonyPatch(typeof(Damager),nameof(Damager.CheckPenetration))]
internal static class PenetrationDecisionProbe
{
    private static void Postfix(Damager __instance,CollisionInstance collisionInstance,bool __result)
        => MultiplayerRuntime.Instance?.World?.TraceNativeCombat("penetration",__instance,collisionInstance,__result);
}
[HarmonyPatch(typeof(Damager),nameof(Damager.TryPerformSlice))]
internal static class SliceDecisionProbe
{
    private static void Postfix(Damager __instance,CollisionInstance collisionInstance,bool __result)
        => MultiplayerRuntime.Instance?.World?.TraceNativeCombat("slice",__instance,collisionInstance,__result);
}
