using System;
using System.Collections.Generic;
using System.Linq;
using BladeAndSorcery.Multiplayer.Core;
using HarmonyLib;
using Newtonsoft.Json;
using ThunderRoad;
using ThunderRoad.Skill.Spell;
using UnityEngine;

namespace BladeAndSorcery.Multiplayer;

internal sealed class NpcHitData
{
    public string Kind="npc-hit";
    public DamageType Type;
    public string Damager="";
    public bool HitBack;
    public int Push;
}
internal sealed class NpcEffectData
{
    public string Kind="npc-effect",Spell="",Effect="";
    public int Step;
}
internal sealed class NpcProjectileData
{
    public string Kind="npc-projectile",Effect="";
    public ulong Caster;
    public int Side;
    public string[] Damagers=Array.Empty<string>();
}

internal sealed partial class GameWorld
{
    private readonly Dictionary<(ulong,int),EffectInstance> npcChargeEffects=new Dictionary<(ulong,int),EffectInstance>();
    private readonly HashSet<ulong> projectileEffects=new HashSet<ulong>();
    internal int NpcEffectsSent,NpcEffectsReceived,NpcMagicHits,NpcProjectilesSent,NpcProjectilesReceived;
    private static readonly JsonSerializerSettings NpcJson=new JsonSerializerSettings{TypeNameHandling=TypeNameHandling.None,MaxDepth=6};
    private static bool NpcHit(GameplayAction action)=>action.Text.StartsWith("{\"Kind\":\"npc-hit\"",StringComparison.Ordinal);
    private bool ValidateNpcHit(GameplayAction action)
    {
        var data=JsonConvert.DeserializeObject<NpcHitData>(action.Text,NpcJson);
        return data?.Kind=="npc-hit" && Registry.TryGet(action.Entity,out var actor) && actor.Kind==EntityKind.Creature && actor.Owner==1 &&
            Registry.TryGet(action.Other,out var target) && target.Kind==EntityKind.Player && action.Part>0 && action.Part<=65535 &&
            action.Value>0 && action.Value<=1000000 && Enum.IsDefined(typeof(DamageType),data.Type) && data.Type!=DamageType.Unknown &&
            data.Damager.Length<=128 && data.Push>=-1 && data.Push<=128 && action.Side>=0 && action.Side<=1;
    }
    internal bool? HandleNativeNpcMagic(Creature creature,CollisionInstance hit)
    {
        if(Applying || applyingPlayerWeaponDamage || disposed || !hit.damageStruct.active || hit.damageStruct.damage<=0) return null;
        var victim=Find(creature);if(victim?.Descriptor.Kind!=EntityKind.Player)return null;
        var item=hit.damageStruct.damager?.collisionHandler?.item ?? hit.sourceColliderGroup?.collisionHandler?.item;
        // Melee weapons use the existing native damager/material replay. Magic
        // outcomes instead belong to the caster, surviving projectile despawn.
        var body=hit.sourceColliderGroup?.collisionHandler?.ragdollPart;
        bool ranged=item!=null && !item.IsHanded() && item.lastHandler!=null;
        if(hit.casterHand==null && item?.GetComponent<ItemMagicProjectile>()==null && body==null && !ranged)return null;
        var hand=hit.casterHand?.ragdollHand ?? item?.lastHandler;
        var actor=Find(hand?.creature ?? body?.ragdoll.creature);if(actor?.Descriptor.Kind!=EntityKind.Creature)return null;
        if(!actor.Owned)return false;
        if(victim.Owned)return null;
        var part=hit.damageStruct.hitRagdollPart ?? creature.ragdoll.targetPart;
        var damager=hit.damageStruct.damager;float amount=hit.damageStruct.damage;
        if(damager!=null)amount=Mathf.Clamp(amount*damager.data.GetTier(damager.collisionHandler).playerDamageMultiplier,damager.data.playerMinDamage,damager.data.playerMaxDamage);
        if(part==null || !Protocol.Finite(amount) || amount<=0)return false;
        SendAction(actor,new GameplayAction{Kind=ActionKind.DamageRequest,Other=victim.Descriptor.Id,Part=GameMath.PartKey(part),Side=(int)(hand?.side ?? Side.Right),Value=amount,
            Position=part.transform.InverseTransformPoint(hit.contactPoint).Net(),Direction=part.transform.InverseTransformDirection(hit.impactVelocity).Net(),
            Text=JsonConvert.SerializeObject(new NpcHitData{Type=hit.damageStruct.damageType,Damager=damager?.data.id ?? "",HitBack=hit.damageStruct.hitBack,Push=hit.damageStruct.pushLevel})});
        return false;
    }
    private void ApplyNpcHit(GameplayAction action)
    {
        if(!Entities.TryGetValue(action.Other,out var victim) || !victim.Owned || victim.Creature==null || victim.Creature.isKilled ||
            !Entities.TryGetValue(action.Entity,out var actor) || actor.Creature==null)return;
        var part=GameMath.FindPart(victim.Creature,action.Part);var data=JsonConvert.DeserializeObject<NpcHitData>(action.Text,NpcJson);if(part==null || data==null)return;
        var hit=new CollisionInstance{casterHand=(action.Side==0 ? actor.Creature.handRight : actor.Creature.handLeft).caster,
            targetColliderGroup=part.colliderGroup,targetCollider=part.colliderGroup.colliders.FirstOrDefault(),
            contactPoint=part.transform.TransformPoint(action.Position.Game()),impactVelocity=part.transform.TransformDirection(action.Direction.Game())};
        hit.contactNormal=hit.impactVelocity.sqrMagnitude>0 ? -hit.impactVelocity.normalized : Vector3.up;
        hit.damageStruct=new DamageStruct(data.Type,action.Value){hitRagdollPart=part,hitBack=data.HitBack,pushLevel=data.Push,time=Time.time,
            damagerData=Catalog.GetData<DamagerData>(data.Damager,false)};
        applyingPlayerWeaponDamage=true;
        NpcMagicHits++;
        try{victim.Creature.Damage(hit);}finally{applyingPlayerWeaponDamage=false;}
    }
    internal void PublishNpcProjectile(ItemMagicProjectile projectile,EffectData effect,Ragdoll shooter)
    {
        if(!SceneUsable || Applying || !Session.IsHost || projectile.item==null)return;
        var hand=projectile.item.lastHandler;var actor=Find(hand?.creature ?? shooter?.creature);
        if(actor?.Descriptor.Kind!=EntityKind.Creature || !actor.Owned)return;
        var entity=Find(projectile.item) ?? Register(projectile.item);
        entity.Descriptor.Appearance=JsonConvert.SerializeObject(new NpcProjectileData{Caster=actor.Descriptor.Id,Side=(int)(hand?.side ?? Side.Right),Effect=effect?.id ?? "",
            Damagers=Damagers(projectile.item).Select(d=>d.data?.id ?? "").ToArray()});
        PublishSpawn(entity.Descriptor);
        NpcProjectilesSent++;
    }
    internal void PublishNpcArrow(RagdollHand hand,Item arrow)
    {
        if(!SceneUsable || Applying || !Session.IsHost || arrow==null)return;
        var actor=Find(hand?.creature);if(actor?.Descriptor.Kind!=EntityKind.Creature || !actor.Owned)return;
        var entity=Find(arrow) ?? Register(arrow);
        entity.Descriptor.Appearance=JsonConvert.SerializeObject(new NpcProjectileData{Caster=actor.Descriptor.Id,Side=(int)hand!.side,Damagers=Damagers(arrow).Select(d=>d.data?.id ?? "").ToArray()});
        PublishSpawn(entity.Descriptor);NpcProjectilesSent++;
    }
    internal void ApplyNpcProjectile(ReplicatedEntity entity)
    {
        if(entity.Owned || entity.Item==null || entity.Descriptor.Appearance.Length==0 || projectileEffects.Contains(entity.Descriptor.Id))return;
        var data=JsonConvert.DeserializeObject<NpcProjectileData>(entity.Descriptor.Appearance,NpcJson);if(data?.Kind!="npc-projectile")return;
        if(!Entities.TryGetValue(data.Caster,out var actor) || actor.Creature==null)return;
        // A looted arrow is now a player's weapon. Old NPC launch metadata must
        // never overwrite the new grabber's native damage attribution.
        if(entity.Descriptor.Owner!=actor.Descriptor.Owner || Find(entity.Item.lastHandler?.creature)?.Descriptor.Kind==EntityKind.Player)
        {projectileEffects.Add(entity.Descriptor.Id);return;}
        projectileEffects.Add(entity.Descriptor.Id);
        NpcProjectilesReceived++;
        entity.Item.lastHandler=data.Side==0 ? actor.Creature.handRight : actor.Creature.handLeft;
        var damagers=Damagers(entity.Item);
        for(int i=0;i<Math.Min(data.Damagers.Length,damagers.Length);i++)if(Catalog.TryGetData<DamagerData>(data.Damagers[i],out var damage,false))damagers[i].Load(damage,damagers[i].collisionHandler);
        var projectile=entity.Item.GetComponent<ItemMagicProjectile>();if(projectile==null)return;
        projectile.guidance=GuidanceMode.NonGuided;projectile.expire=false;
        projectile.Fire(Vector3.zero,Catalog.GetData<EffectData>(data.Effect,false),null,actor.Creature.ragdoll);
    }
    internal bool RemoteNpcProjectile(ItemMagicProjectile projectile)=>Find(projectile.item) is {Owned:false} e && e.Descriptor.Appearance.StartsWith("{\"Kind\":\"npc-projectile\"",StringComparison.Ordinal);
    private bool ValidateNpcEffect(GameplayAction action)
    {
        var data=JsonConvert.DeserializeObject<NpcEffectData>(action.Text,NpcJson);
        return Registry.TryGet(action.Entity,out var actor) && actor.Kind==EntityKind.Creature && actor.Owner==1 && action.Side>=0 && action.Side<=1 &&
            data?.Kind=="npc-effect" && data.Spell.Length<=128 && data.Effect.Length<=128 && data.Step>=-1 && data.Step<=33 &&
            (data.Step==33 ? action.Value>0 && action.Value<=600 && Registry.TryGet(action.Other,out var target) && target.Kind==EntityKind.Player : action.Other==0);
    }
    internal void PublishNpcCast(SpellCaster caster,SpellCastData spell,SpellCaster.CastStep step)
    {
        var actor=Find(caster.mana?.creature);if(!SceneUsable || Applying || actor?.Descriptor.Kind!=EntityKind.Creature || !actor.Owned)return;
        SendAction(actor,new GameplayAction{Kind=ActionKind.Spell,Side=(int)caster.ragdollHand.side,Text=JsonConvert.SerializeObject(new NpcEffectData{Spell=spell.id,Step=(int)step})});
        NpcEffectsSent++;
    }
    internal void PublishNpcBolt(SpellCastLightning spell,Vector3 source,Vector3 target)
    {
        var actor=Find(spell.spellCaster?.mana?.creature);if(!SceneUsable || Applying || actor?.Descriptor.Kind!=EntityKind.Creature || !actor.Owned)return;
        SendAction(actor,new GameplayAction{Kind=ActionKind.Spell,Position=source.Net(),Direction=target.Net(),Text=JsonConvert.SerializeObject(new NpcEffectData{Step=-1,Effect=spell.boltEffectId})});
        NpcEffectsSent++;
    }
    internal bool NativeNpcStatus(ThunderEntity target,StatusData data,object handler,float duration,bool playEffect)
    {
        if(Applying || handler is not SpellCastCharge spell || target is not Creature creature)return true;
        var actor=Find(spell.spellCaster?.mana?.creature);var victim=Find(creature);
        if(actor?.Descriptor.Kind!=EntityKind.Creature || victim?.Descriptor.Kind!=EntityKind.Player)return true;
        if(!actor.Owned)return false;
        if(victim.Owned)return true;
        if(Protocol.Finite(duration) && duration>0)SendAction(actor,new GameplayAction{Kind=ActionKind.Spell,Other=victim.Descriptor.Id,Value=Mathf.Min(duration,600),
            Text=JsonConvert.SerializeObject(new NpcEffectData{Step=33,Spell=data.id})});
        return false;
    }
    private void ApplyNpcEffect(ReplicatedEntity actor,GameplayAction action)
    {
        if(actor.Creature==null)return;
        var data=JsonConvert.DeserializeObject<NpcEffectData>(action.Text,NpcJson);if(data==null)return;
        NpcEffectsReceived++;
        if(data.Step==33)
        {
            if(Entities.TryGetValue(action.Other,out var victim) && victim.Owned && victim.Creature!=null && !victim.Creature.isKilled && Catalog.TryGetData<StatusData>(data.Spell,out var status,false))
                victim.Creature.Inflict(status,actor,action.Value);
            return;
        }
        if(data.Step==-1){if(Catalog.TryGetData<EffectData>(data.Effect,out var bolt,false))SpellCastLightning.PlayBolt(bolt,null,null,action.Position.Game(),action.Direction.Game());return;}
        var key=(actor.Descriptor.Id,action.Side);var step=(SpellCaster.CastStep)data.Step;
        if(step==SpellCaster.CastStep.CastStop || step==SpellCaster.CastStep.ChargeStop || step==SpellCaster.CastStep.ChargeThrow)
        {if(npcChargeEffects.TryGetValue(key,out var old))old.End();npcChargeEffects.Remove(key);return;}
        if(step!=SpellCaster.CastStep.CastStart || npcChargeEffects.ContainsKey(key))return;
        if(Catalog.TryGetData<SpellCastCharge>(data.Spell,out var spell,false) && spell.chargeEffectData!=null)
        {
            var hand=action.Side==0 ? actor.Creature.handRight : actor.Creature.handLeft;
            var effect=spell.chargeEffectData.Spawn(hand.caster.Orb);effect.Play();npcChargeEffects[key]=effect;
        }
    }
    private void TickNpcEffects()
    {
        foreach(var entity in Entities.Values)if(entity.Item!=null)ApplyNpcProjectile(entity);
        foreach(var key in npcChargeEffects.Keys.ToArray())
            if(!Entities.TryGetValue(key.Item1,out var actor) || actor.Creature==null || actor.Creature.isKilled){npcChargeEffects[key].End();npcChargeEffects.Remove(key);}
    }
    private void ClearNpcEffects(){foreach(var effect in npcChargeEffects.Values)effect.End();npcChargeEffects.Clear();projectileEffects.Clear();}
}

[HarmonyPatch(typeof(ItemMagicProjectile),nameof(ItemMagicProjectile.Fire))]
internal static class NativeNpcProjectileFirePatch
{
    private static void Postfix(ItemMagicProjectile __instance,EffectData effectData,Ragdoll shooterRagdoll)=>MultiplayerRuntime.Instance?.World?.PublishNpcProjectile(__instance,effectData,shooterRagdoll);
}
[HarmonyPatch]
internal static class NativeNpcProjectileOwnerPatch
{
    private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
    {foreach(var name in new[]{"OnCollision","BaseBehavior","Update","FixedUpdate"})yield return AccessTools.Method(typeof(ItemMagicProjectile),name);}
    private static bool Prefix(ItemMagicProjectile __instance)=>!(MultiplayerRuntime.Instance?.World?.RemoteNpcProjectile(__instance) ?? false);
}
[HarmonyPatch(typeof(SpellCaster),nameof(SpellCaster.InvokeCastStep))]
internal static class NativeNpcCastPatch
{
    private static void Postfix(SpellCaster __instance,SpellCastData spell,SpellCaster.CastStep step)=>MultiplayerRuntime.Instance?.World?.PublishNpcCast(__instance,spell,step);
}
[HarmonyPatch(typeof(SpellCastLightning),nameof(SpellCastLightning.PlayBolt),new[]{typeof(Transform),typeof(Transform),typeof(Vector3?),typeof(Vector3?),typeof(Gradient)})]
internal static class NativeNpcBoltPatch
{
    private static void Postfix(SpellCastLightning __instance,Transform source,Transform target,Vector3? sourcePos,Vector3? targetPos)
        =>MultiplayerRuntime.Instance?.World?.PublishNpcBolt(__instance,sourcePos ?? (source!=null ? source.position : Vector3.zero),targetPos ?? (target!=null ? target.position : Vector3.zero));
}
[HarmonyPatch(typeof(ThunderEntity),nameof(ThunderEntity.Inflict),new[]{typeof(StatusData),typeof(object),typeof(float),typeof(object),typeof(bool)})]
internal static class NativeNpcStatusPatch
{
    private static bool Prefix(ThunderEntity __instance,StatusData data,object handler,float duration,bool playEffect)
        =>MultiplayerRuntime.Instance?.World?.NativeNpcStatus(__instance,data,handler,duration,playEffect) ?? true;
}
[HarmonyPatch(typeof(EventManager),nameof(EventManager.InvokeBowFire))]
internal static class NativeNpcArrowPatch
{
    private static void Postfix(RagdollHand hand,Item arrow)=>MultiplayerRuntime.Instance?.World?.PublishNpcArrow(hand,arrow);
}
