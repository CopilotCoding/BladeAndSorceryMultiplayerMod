using System;
using System.Collections.Generic;
using System.Linq;
using BladeAndSorcery.Multiplayer.Core;
using Newtonsoft.Json;
using ThunderRoad;
using UnityEngine;

namespace BladeAndSorcery.Multiplayer;

internal sealed class PlayerWeaponDamageData
{
    public string Kind="weapon-hit";
    public int Version=2;
    public ulong Attacker;
    public DamageType DamageType;
    public DamageStruct.Penetration Mode;
    public bool BadAngle,HitBack;
    public int PushLevel;
    public string SourceMaterial="",TargetMaterial="";
}

internal sealed partial class GameWorld
{
    private readonly List<(GameplayAction Action,double Due)> pendingPlayerWeaponDamage=new List<(GameplayAction,double)>();
    private bool applyingPlayerWeaponDamage;
    private static PlayerWeaponDamageData? ReadWeaponDamage(GameplayAction action)
        => JsonConvert.DeserializeObject<PlayerWeaponDamageData>(action.Text,new JsonSerializerSettings{TypeNameHandling=TypeNameHandling.None,MaxDepth=4});
    // Cut requests predate this discriminator. Missing Kind remains a cut;
    // the damage schema requires an explicit weapon-hit field on the wire.
    private static bool IsWeaponDamage(GameplayAction action)
        => JsonConvert.DeserializeObject<Dictionary<string,object>>(action.Text,new JsonSerializerSettings{TypeNameHandling=TypeNameHandling.None,MaxDepth=4})?.TryGetValue("Kind",out var kind)==true && kind?.ToString()=="weapon-hit";
    private bool ValidatePlayerDamageRequest(GameplayAction action)
    {
        if(NpcHit(action))return ValidateNpcHit(action);
        if(!IsWeaponDamage(action)) return ValidatePlayerCutRequest(action);
        if(action.Handle<0 || action.Handle>=128 || action.Part<=0 || action.Part>65535 || action.Side!=0 || action.Value<=0 || action.Value>1000000 ||
            action.Direction.LengthSquared()>250000 || !Registry.TryGet(action.Entity,out var source) || source.Kind!=EntityKind.Item ||
            !Registry.TryGet(action.Other,out var victim) || victim.Kind!=EntityKind.Player || victim.Owner==source.Owner) return false;
        var data=ReadWeaponDamage(action);
        return data?.Version==2 && (data.Attacker==0 || IsHumanWeaponAttacker(data.Attacker,source.Owner)) && Enum.IsDefined(typeof(DamageType),data.DamageType) && data.DamageType!=DamageType.Unknown &&
            data.Mode>=DamageStruct.Penetration.None && data.Mode<=DamageStruct.Penetration.Skewer && data.PushLevel>=-1 && data.PushLevel<=128 &&
            !string.IsNullOrEmpty(data.SourceMaterial) && data.SourceMaterial.Length<=128 && !string.IsNullOrEmpty(data.TargetMaterial) && data.TargetMaterial.Length<=128;
    }
    internal bool? HandleNativePlayerWeaponDamage(Creature creature,CollisionInstance hit)
    {
        if(Applying || applyingPlayerWeaponDamage || disposed || !hit.damageStruct.active || hit.damageStruct.sliceDone || hit.damageStruct.damage<=0) return null;
        var victim=Find(creature);var damager=hit.damageStruct.damager;var source=Find(damager?.collisionHandler?.item);
        if(victim==null || source?.Item==null || damager==null || victim.Descriptor.Owner==source.Descriptor.Owner || !PlayerWeaponOwnsPenetration(source,victim)) return null;
        // Native source damage includes initial impact and deep penetration
        // callbacks. Mirrored physics must not apply a second copy of either.
        if(!source.Owned) return false;
        int index=Array.IndexOf(Damagers(source.Item),damager);
        var part=hit.damageStruct.hitRagdollPart;
        if(index<0 || part==null || hit.damageStruct.damage<=0 || !Protocol.Finite(hit.damageStruct.damage) || hit.sourceMaterial==null || hit.targetMaterial==null) return false;
        var data=new PlayerWeaponDamageData{DamageType=hit.damageStruct.damageType,Mode=hit.damageStruct.penetration,BadAngle=hit.damageStruct.badAngle,
            HitBack=hit.damageStruct.hitBack,PushLevel=hit.damageStruct.pushLevel,SourceMaterial=hit.sourceMaterial.id,TargetMaterial=hit.targetMaterial.id};
        var attacker=Find(source.Item.mainHandler?.creature ?? source.Item.lastHandler?.creature);
        if(attacker?.Descriptor.Kind==EntityKind.Player && attacker.Descriptor.Owner==source.Descriptor.Owner)data.Attacker=attacker.Descriptor.Id;
        SendAction(source,new GameplayAction{Kind=ActionKind.DamageRequest,Other=victim.Descriptor.Id,Handle=index,Part=GameMath.PartKey(part),
            Value=hit.damageStruct.damage,Position=part.transform.InverseTransformPoint(hit.contactPoint).Net(),
            Direction=part.transform.InverseTransformDirection(hit.impactVelocity).Net(),Text=JsonConvert.SerializeObject(data)});
        return false;
    }
    private void ApplyPlayerDamageRequest(GameplayAction action)
    {
        if(NpcHit(action)){ApplyNpcHit(action);return;}
        if(!IsWeaponDamage(action)){ApplyPlayerCutRequest(action);return;}
        if(!Entities.TryGetValue(action.Other,out var victim) || !victim.Owned || pendingPlayerWeaponDamage.Count>=1024) return;
        double delay=Entities.TryGetValue(action.Entity,out var source) ? source.Buffer.Delay : .065;
        pendingPlayerWeaponDamage.Add((action,Now+delay));
    }
    private void TickPlayerWeaponDamage()
    {
        for(int i=0;i<pendingPlayerWeaponDamage.Count;)
        {
            var pending=pendingPlayerWeaponDamage[i];
            if(Now<pending.Due){i++;continue;}
            pendingPlayerWeaponDamage.RemoveAt(i);ResolvePlayerWeaponDamage(pending.Action);
        }
    }
    private void ResolvePlayerWeaponDamage(GameplayAction action)
    {
        if(!Entities.TryGetValue(action.Entity,out var source) || source.Item==null || source.Descriptor.Epoch!=action.Epoch ||
            !Entities.TryGetValue(action.Other,out var victim) || !victim.Owned || victim.Creature==null || victim.Creature.isKilled || Player.IsInvincible) return;
        var damager=Damagers(source.Item).ElementAtOrDefault(action.Handle);var part=GameMath.FindPart(victim.Creature,action.Part);var data=ReadWeaponDamage(action);
        // This event already passed source ownership/epoch validation. A delayed
        // local grip (or a missing source replica) is not evidence against the
        // native contact observed by the weapon owner.
        if(damager==null || part==null || data==null) return;
        playerPenetrationWeapons.Add(source.Descriptor.Id);
        var hit=new CollisionInstance{sourceColliderGroup=damager.colliderGroup,sourceCollider=damager.colliderGroup.colliders.FirstOrDefault(),
            targetColliderGroup=part.colliderGroup,targetCollider=part.colliderGroup.colliders.FirstOrDefault(),
            sourceMaterial=Catalog.GetData<MaterialData>(data.SourceMaterial,false),targetMaterial=Catalog.GetData<MaterialData>(data.TargetMaterial,false),
            contactPoint=part.transform.TransformPoint(action.Position.Game()),impactVelocity=part.transform.TransformDirection(action.Direction.Game())};
        if(hit.sourceMaterial==null || hit.targetMaterial==null || !Match.AllowHit(victim.Creature,hit)) return;
        hit.contactNormal=hit.impactVelocity.sqrMagnitude>0 ? -hit.impactVelocity.normalized : -damager.transform.forward;
        hit.damageStruct=new DamageStruct(data.DamageType,action.Value){damager=damager,damagerData=damager.data,hitRagdollPart=part,
            badAngle=data.BadAngle,hitBack=data.HitBack,pushLevel=data.PushLevel,penetration=data.Mode,time=Time.time};
        hit.damageStruct.materialModifier=(data.BadAngle ? damager.data.badAngleModifierData : damager.data.damageModifierData)?.GetModifier(hit);
        if(hit.damageStruct.materialModifier==null) return;
        var previousPvpHit=npcStylePvpHit;
        if(IsHumanWeaponAttacker(data.Attacker,source.Descriptor.Owner))npcStylePvpHit=hit;
        applyingPlayerWeaponDamage=true;
        try{victim.Creature.Damage(hit);}
        finally{applyingPlayerWeaponDamage=false;npcStylePvpHit=previousPvpHit;}
        // Normal owner damage/kill callbacks publish health, score and respawn;
        // do not suppress them with the general remote replay guard.
    }
}
