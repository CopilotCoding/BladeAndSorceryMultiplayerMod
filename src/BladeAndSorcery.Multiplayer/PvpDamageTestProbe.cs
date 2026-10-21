using System;
using System.Linq;
using BladeAndSorcery.Multiplayer.Core;
using Newtonsoft.Json.Linq;
using ThunderRoad;
using UnityEngine;

namespace BladeAndSorcery.Multiplayer;

// Constructed-contact diagnostics, available only through the isolated driver.
// These exercise damage delivery, not weapon trajectories or cut quality.
internal static class PvpDamageTestProbe
{
    internal static object Execute(MultiplayerRuntime runtime,JObject command)
    {
        var world=runtime.World ?? throw new InvalidOperationException("Connect first");
        if((string?)command["op"]=="pvp-probe-body")return world.Entities.Values.Where(e=>e.Creature!=null).Select(e=>new {
            id=e.Descriptor.Id.ToString(),owner=e.Descriptor.Owner,e.Creature!.ragdoll.sliceRunning,
            sliced=e.Creature.ragdoll.parts.Where(p=>p.isSliced).Select(p=>(int)p.type).ToArray(),
            splitRenderers=e.Creature.renderers.Count(r=>r.splitRenderer!=null),
            missingSplitReveal=e.Creature.renderers.Count(r=>r.splitReveal!=null && r.splitReveal.revealMaterialController==null)
        }).ToArray();
        if((string?)command["op"]!="pvp-probe-hit")throw new InvalidOperationException("Unknown PvP probe");
        var actor=world.Find(Player.local?.creature) ?? throw new InvalidOperationException("Expected local fighter");
        var victim=world.Entities.Values.First(e=>e.Descriptor.Kind==EntityKind.Player && e.Descriptor.Owner==(ushort)(int)command["victimPeer"]!);
        var item=actor.Creature!.handRight.grabbedHandle?.item ?? throw new InvalidOperationException("Hold a sword first");
        var damager=item.collisionHandlers.SelectMany(h=>h.damagers).First(d=>d.type==Damager.Type.Slash);
        var part=victim.Creature!.ragdoll.targetPart;
        var source=damager.colliderGroup.colliders.First();var target=part.colliderGroup.colliders.First();
        float amount=Mathf.Clamp((float?)command["amount"] ?? 40,1,80);
        var hit=new CollisionInstance{sourceColliderGroup=damager.colliderGroup,targetColliderGroup=part.colliderGroup,sourceCollider=source,targetCollider=target,
            sourceMaterial=MaterialData.GetMaterial(source),targetMaterial=MaterialData.GetMaterial(target),contactPoint=part.transform.position,impactVelocity=Vector3.forward*6,contactNormal=Vector3.back};
        hit.damageStruct=new DamageStruct(DamageType.Slash,amount){damager=damager,damagerData=damager.data,hitRagdollPart=part,time=Time.time};
        float multiplier=victim.Creature.GetDamageMultiplier();
        float native=Mathf.Clamp(amount*damager.data.GetTier(damager.collisionHandler).playerDamageMultiplier,damager.data.playerMinDamage,damager.data.playerMaxDamage)*multiplier;
        victim.Creature.Damage(hit);
        return new{constructedContact=true,raw=amount,nativeExpected=native,npcExpected=amount*multiplier};
    }
}
