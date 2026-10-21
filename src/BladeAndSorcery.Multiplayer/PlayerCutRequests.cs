using System;
using System.Collections.Generic;
using System.Linq;
using BladeAndSorcery.Multiplayer.Core;
using Newtonsoft.Json;
using ThunderRoad;
using UnityEngine;

namespace BladeAndSorcery.Multiplayer;

internal sealed class PlayerCutRequest
{
    public int Version=2;
    public int[] Parts=Array.Empty<int>();
    public PlayerCutGeometry[] Geometry=Array.Empty<PlayerCutGeometry>();
    public float[] Up=Array.Empty<float>();
    public string SourceMaterial="",TargetMaterial="";
}

internal sealed class PlayerCutGeometry
{
    public int Part;
    public float Distance,UpDot,ForwardDot;
}

internal sealed partial class GameWorld
{
    private readonly Dictionary<(ulong,int),double> nextPlayerCutRequest=new Dictionary<(ulong,int),double>();
    private readonly List<(GameplayAction Action,double Due)> pendingPlayerCuts=new List<(GameplayAction,double)>();

    // A player's native blade owns its cut contact. The victim still validates
    // protection, materials and the original native slice measurements before
    // applying a cut. Re-testing another pose would discard an already valid hit.
    // Returning false here defers the outcome; it never pretends a slice occurred.
    internal bool? HandlePlayerCut(Damager damager,CollisionInstance hit)
    {
        if(Applying || disposed) return null;
        var victim=Find(hit.damageStruct.hitRagdollPart?.ragdoll.creature);
        var weapon=Find(damager.collisionHandler?.item);
        var holder=Find(weapon?.Item?.mainHandler?.creature ?? weapon?.Item?.lastHandler?.creature);
        bool nativeFighter=holder!=null && (holder.Descriptor.Kind==EntityKind.Player || holder.Descriptor.Kind==EntityKind.Creature && holder.Descriptor.Owner==weapon?.Descriptor.Owner);
        if(victim?.Descriptor.Kind!=EntityKind.Player || weapon?.Item==null || !nativeFighter ||
            victim.Descriptor.Owner==weapon.Descriptor.Owner) return null;
        // Preserve valid native contacts on the victim as well. A source request
        // adds another valid contact path; it must not disable the owner's own
        // native cutting behavior. The victim publishes either outcome once.
        if(!weapon.Owned) return victim.Owned ? null : (bool?)false;
        if(victim.SourceInvincible || !GameManager.CheckContentActive(BuildSettings.ContentFlag.Dismemberment) || !Match.AllowHit(victim.Creature!,hit)) return false;
        int index=Array.IndexOf(Damagers(weapon.Item),damager);
        if(index<0) return false;
        var key=(weapon.Descriptor.Id,index);
        double time=Time.timeAsDouble;
        if(nextPlayerCutRequest.TryGetValue(key,out double until) && time<until) return false;
        var contact=hit.damageStruct.hitRagdollPart;
        if(contact==null) return false;
        var tier=damager.data.GetTier(damager.collisionHandler);
        var candidates=new List<RagdollPart>();
        if(contact.sliceAllowed && (Damager.easyDismemberment || damager.CheckSlice(hit.contactPoint,contact,damagerTier:tier))) candidates.Add(contact);
        else if(contact.parentPart!=null && contact.parentPart.sliceAllowed && damager.CheckSlice(hit.contactPoint,contact.parentPart,damagerTier:tier)) candidates.Add(contact.parentPart);
        else foreach(var bone in contact.bone.childs)
            if(bone.part!=null && bone.part.sliceAllowed && damager.CheckSlice(hit.contactPoint,bone.part,damagerTier:tier)) candidates.Add(bone.part);
        if(candidates.Count==0) return false;
        var up=contact.transform.InverseTransformDirection(damager.transform.up);
        var data=new PlayerCutRequest {Parts=candidates.Select(GameMath.PartKey).Distinct().ToArray(),Up=new[]{up.x,up.y,up.z},
            SourceMaterial=hit.sourceMaterial?.id ?? "",TargetMaterial=hit.targetMaterial?.id ?? ""};
        data.Geometry=candidates.Select(p=>{
            p.GetSlicePositionAndDirection(out var point,out var normal);
            return new PlayerCutGeometry {Part=GameMath.PartKey(p),Distance=Vector3.Project(hit.contactPoint-point,normal).magnitude,
                UpDot=Vector3.Dot(normal,damager.transform.up),ForwardDot=Vector3.Dot(normal,damager.transform.forward)};
        }).ToArray();
        nextPlayerCutRequest[key]=time+damager.data.dismembermentNoPenetrationDuration;
        if(nextPlayerCutRequest.Count>512)
            foreach(var old in nextPlayerCutRequest.Where(p=>p.Value<=time).Select(p=>p.Key).ToArray()) nextPlayerCutRequest.Remove(old);
        SendAction(weapon,new GameplayAction {Kind=ActionKind.DamageRequest,Other=victim.Descriptor.Id,Handle=index,Part=GameMath.PartKey(contact),
            Position=contact.transform.InverseTransformPoint(hit.contactPoint).Net(),Direction=contact.transform.InverseTransformDirection(damager.transform.forward).Net(),
            Value=hit.impactVelocity.magnitude,Text=JsonConvert.SerializeObject(data)});
        // The native cut attempt must not also create a stabbing constraint in
        // this same TryHit call. Preserve its native no-penetration cooldown
        // while the victim resolves the requested slice.
        hit.damageStruct.penetration=DamageStruct.Penetration.None;
        NativeCutCooldown.Commit(damager,contact);
        TracePlayerCut("cut-request",weapon,victim,damager,data.Parts,"native attacker contact",hit.impactVelocity.magnitude);
        return false;
    }
    private static PlayerCutRequest? ReadPlayerCut(GameplayAction action)
        => JsonConvert.DeserializeObject<PlayerCutRequest>(action.Text,new JsonSerializerSettings {TypeNameHandling=TypeNameHandling.None,MaxDepth=4});
    private bool ValidatePlayerCutRequest(GameplayAction action)
    {
        if(action.Handle<0 || action.Handle>=128 || action.Value<=0 || action.Value>500 || action.Side!=0 ||
            !Registry.TryGet(action.Entity,out var weapon) || weapon.Kind!=EntityKind.Item ||
            !Registry.TryGet(action.Other,out var victim) || victim.Kind!=EntityKind.Player || victim.Owner==weapon.Owner) return false;
        var data=ReadPlayerCut(action);
        return data?.Version==2 && data.Parts!=null && data.Parts.Length>0 && data.Parts.Length<=Protocol.MaxParts &&
            data.Parts.Distinct().Count()==data.Parts.Length && data.Parts.All(p=>p>0 && p<=65535) &&
            data.Geometry!=null && data.Geometry.Length==data.Parts.Length && data.Geometry.All(g=>g!=null &&
                Protocol.Finite(g.Distance) && g.Distance>=0 && g.Distance<=4 && Protocol.Finite(g.UpDot) && Mathf.Abs(g.UpDot)<=1.001f &&
                Protocol.Finite(g.ForwardDot) && Mathf.Abs(g.ForwardDot)<=1.001f) && data.Geometry.Select(g=>g.Part).SequenceEqual(data.Parts) &&
            data.Up!=null && data.Up.Length==3 && data.Up.All(Protocol.Finite) && data.Up.Sum(v=>v*v)>.5f && data.Up.Sum(v=>v*v)<1.5f &&
            action.Direction.LengthSquared()>.5f && action.Direction.LengthSquared()<1.5f &&
            !string.IsNullOrEmpty(data.SourceMaterial) && data.SourceMaterial.Length<=128 && !string.IsNullOrEmpty(data.TargetMaterial) && data.TargetMaterial.Length<=128;
    }
    private void ApplyPlayerCutRequest(GameplayAction action)
    {
        if(!Entities.TryGetValue(action.Other,out var victim) || !victim.Owned || pendingPlayerCuts.Count>=1024) return;
        // Match the remote weapon's interpolated presentation. A reliable event
        // arriving ahead of its pose must not detach a head before the swing.
        double delay=Entities.TryGetValue(action.Entity,out var weapon) ? weapon.Buffer.Delay : .065;
        pendingPlayerCuts.Add((action,Now+delay));
    }
    private void TickPlayerCutRequests()
    {
        for(int i=0;i<pendingPlayerCuts.Count;)
        {
            var pending=pendingPlayerCuts[i];
            if(Now<pending.Due){i++;continue;}
            pendingPlayerCuts.RemoveAt(i);ResolvePlayerCutRequest(pending.Action);
        }
    }
    private void ResolvePlayerCutRequest(GameplayAction action)
    {
        if(!Entities.TryGetValue(action.Entity,out var weapon) || weapon.Item==null ||
            weapon.Descriptor.Epoch!=action.Epoch || !Entities.TryGetValue(action.Other,out var victim) || !victim.Owned || victim.Creature==null) return;
        var damager=Damagers(weapon.Item).ElementAtOrDefault(action.Handle);
        var contact=GameMath.FindPart(victim.Creature,action.Part);
        var data=ReadPlayerCut(action);
        // Ownership and the native source measurements authenticate the contact.
        // Do not require the interpolated receiver grip to still exist here.
        if(damager==null || contact==null || data==null) return;
        playerPenetrationWeapons.Add(weapon.Descriptor.Id);
        void Rejected(string reason)=>TracePlayerCut("cut-rejected",weapon,victim,damager,data.Parts,reason,action.Value);
        if(Player.IsInvincible){Rejected("victim invincible");return;}
        var hit=new CollisionInstance {sourceColliderGroup=damager.colliderGroup,targetColliderGroup=contact.colliderGroup,
            sourceCollider=damager.colliderGroup.colliders.FirstOrDefault(),targetCollider=contact.colliderGroup.colliders.FirstOrDefault(),
            contactPoint=contact.transform.TransformPoint(action.Position.Game()),
            sourceMaterial=Catalog.GetData<MaterialData>(data.SourceMaterial,false),targetMaterial=Catalog.GetData<MaterialData>(data.TargetMaterial,false)};
        hit.damageStruct.active=true;hit.damageStruct.damager=damager;hit.damageStruct.damagerData=damager.data;hit.damageStruct.hitRagdollPart=contact;
        if(!Match.AllowHit(victim.Creature,hit)){Rejected("match protection");return;}
        var modifier=damager.data.damageModifierData?.GetModifier(hit);
        var tier=damager.data.GetTier(damager.collisionHandler);
        if(hit.sourceMaterial==null || hit.targetMaterial==null || tier==null || modifier?.allowPenetration!=true ||
            !contact.colliderGroup.data.allowPenetration || !damager.data.penetrationAllowed || damager.penetrationDepth<=0 ||
            !Damager.dismembermentEnabled || (!damager.data.dismembermentAllowed && !Damager.easyDismemberment) ||
            (!Damager.easyDismemberment && action.Value<=damager.data.dismembermentMinVelocity*tier.dismembermentMinVelocityMultiplier))
        {Rejected("native material or speed restriction");return;}
        hit.damageStruct.materialModifier=modifier;
        var forward=contact.transform.TransformDirection(action.Direction.Game());
        hit.impactVelocity=forward*action.Value;hit.contactNormal=-forward;
        hit.damageStruct.damageType=DamageType.Slash;hit.damageStruct.time=Time.time;
        var related=new List<RagdollPart>{contact};
        if(contact.parentPart!=null) related.Add(contact.parentPart);
        related.AddRange(contact.bone.childs.Where(b=>b.part!=null).Select(b=>b.part));
        if(data.Parts.All(key=>GameMath.FindPart(victim.Creature,key)?.isSliced==true))
        {
            TracePlayerCut("cut-already-resolved",weapon,victim,damager,data.Parts,"victim native cut already resolved",action.Value);return;
        }
        var accepted=new List<int>();
        foreach(var geometry in data.Geometry)
        {
            int partKey=geometry.Part;
            var part=GameMath.FindPart(victim.Creature,partKey);
            float angleMultiplier=weapon.Item.sliceAngleMultiplier?.Value ?? 1;
            float vertical=tier.dismembermentMaxVerticalAngle/90f*angleMultiplier;
            float horizontal=tier.dismembermentMaxHorizontalAngle/90f*angleMultiplier;
            if(part==null || !related.Contains(part) || part.isSliced || !part.sliceAllowed ||
                (!(Damager.easyDismemberment && part==contact) && !(geometry.Distance<part.sliceWidth &&
                    geometry.UpDot<vertical && geometry.UpDot>-vertical && geometry.ForwardDot<horizontal && geometry.ForwardDot>-horizontal))) continue;
            if(!part.TrySlice()) continue;
            accepted.Add(partKey);
            NativeCutCooldown.Commit(damager,contact);
            if(ShouldForceKillSlice(part) && !victim.Creature.isKilled)
            {
                hit.damageStruct.hitRagdollPart=part;hit.damageStruct.sliceDone=true;
                hit.damageStruct.damage=float.PositiveInfinity;
                victim.Creature.Kill(hit);
            }
        }
        TracePlayerCut(accepted.Count>0 ? "cut-accepted" : "cut-rejected",weapon,victim,damager,
            accepted.Count>0 ? accepted.ToArray() : data.Parts,accepted.Count>0 ? "validated native source cut" : "native cut measurements or part rejected",action.Value);
    }
    private void TracePlayerCut(string phase,ReplicatedEntity weapon,ReplicatedEntity victim,Damager damager,int[] parts,string reason,float speed)
    {
        if(!CombatCaptureActive && string.IsNullOrEmpty(MultiplayerRuntime.Instance?.TestDirectory)) return;
        NativeCombatTrace.Add(new {time=Now,phase,weapon=weapon.Descriptor.Id.ToString(),victim=victim.Descriptor.Id.ToString(),
            catalog=weapon.Descriptor.CatalogId,damager=damager.data.id,parts,reason,speed});
        if(NativeCombatTrace.Count>2000) NativeCombatTrace.RemoveAt(0);
    }
}
