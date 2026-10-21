using System;
using System.Collections.Generic;
using System.Linq;
using BladeAndSorcery.Multiplayer.Core;
using Newtonsoft.Json;
using ThunderRoad;
using UnityEngine;

namespace BladeAndSorcery.Multiplayer;

internal sealed class PenetrationData
{
    public bool SourceAuthoritative;
    public ulong Token;
    public bool WentThrough;
    public int SourceCollider, TargetCollider;
    public DamageStruct.Penetration Mode;
    public int TargetGroup;
    public string WorldCollider = "";
    public string SourceMaterial = "", TargetMaterial = "";
    public float[] Up = Array.Empty<float>();
    public float[] WeaponPosition = Array.Empty<float>(), WeaponRotation = Array.Empty<float>();
    public float[] Anchor = Array.Empty<float>(), ConnectedAnchor = Array.Empty<float>();
    public float[] Axis = Array.Empty<float>(), SecondaryAxis = Array.Empty<float>();
    public float[] PointPosition = Array.Empty<float>(), PointRotation = Array.Empty<float>();
}

internal sealed partial class GameWorld
{
    private readonly Dictionary<string, GameplayAction> penetrations = new Dictionary<string, GameplayAction>();
    private readonly Dictionary<string, Collider> worldColliders = new Dictionary<string, Collider>();
    private readonly Dictionary<string,(ulong Weapon,ulong Target,Damager Damager,CollisionInstance Hit,ConfigurableJoint Joint)> mirroredPenetrations = new Dictionary<string,(ulong,ulong,Damager,CollisionInstance,ConfigurableJoint)>();
    private readonly HashSet<ulong> playerPenetrationWeapons=new HashSet<ulong>();
    private readonly Dictionary<CollisionInstance,GameplayAction> penetrationBindings=new Dictionary<CollisionInstance,GameplayAction>();
    private uint nextPenetrationToken;
    private readonly List<(GameplayAction Action,double Due)> pendingMirroredPenetrations=new List<(GameplayAction,double)>();
    private void QueuePenetration(GameplayAction action)
    {
        var data=JsonConvert.DeserializeObject<PenetrationData>(action.Text,new JsonSerializerSettings{TypeNameHandling=TypeNameHandling.None,MaxDepth=4})!;
        if(!data.SourceAuthoritative){ApplyPenetration(action);return;}
        if(pendingMirroredPenetrations.Count>=1024) return;
        double delay=Entities.TryGetValue(action.Entity,out var source) ? source.Buffer.Delay : .065;
        pendingMirroredPenetrations.Add((action,Now+delay));
    }
    private void TickMirroredPenetrations()
    {
        for(int i=0;i<pendingMirroredPenetrations.Count;)
        {
            var pending=pendingMirroredPenetrations[i];
            if(Now<pending.Due){i++;continue;}
            pendingMirroredPenetrations.RemoveAt(i);
            if(!Ready(pending.Action) || !Registry.TryGet(pending.Action.Entity,out var actor) || actor.Epoch!=pending.Action.Epoch) continue;
            applyDepth++;
            try{ApplyPenetration(pending.Action);}
            finally{applyDepth--;}
        }
    }
    private bool PlayerWeaponOwnsPenetration(ReplicatedEntity source,ReplicatedEntity? target)
    {
        if(target?.Descriptor.Kind!=EntityKind.Player || source.Item==null) return false;
        var holder=Find(source.Item.mainHandler?.creature ?? source.Item.lastHandler?.creature);
        if(holder?.Descriptor.Kind==EntityKind.Player){playerPenetrationWeapons.Add(source.Descriptor.Id);return true;}
        // Native enemy AI and its attack windows run on its simulation owner.
        // A delayed replica must not independently re-decide a short stab/hit.
        if(holder?.Descriptor.Kind==EntityKind.Creature && holder.Descriptor.Owner==source.Descriptor.Owner) return true;
        // An actual NPC holder overrides remembered human use. A missing remote
        // body must not erase the reliable player-grab history of this weapon.
        if(source.Item.mainHandler!=null && source.Item.mainHandler.creature!=null) return false;
        return playerPenetrationWeapons.Contains(source.Descriptor.Id);
    }
    internal bool AllowPenetration(Damager damager, CollisionInstance collision)
    {
        if (Applying || disposed) return true;
        var source = Find(damager.collisionHandler?.item);
        if (source == null) return true;
        var target = PenetrationTarget(collision);
        // Native VR contact/pressure and withdrawal belong to the player wielding
        // the blade. Re-deciding them from a delayed remote grip loses stabs and
        // can prevent the real hand from clearing an existing penetration.
        if(PlayerWeaponOwnsPenetration(source,target)) return source.Owned;
        // Other combat retains target ownership; static terrain uses the weapon.
        return target?.Owned ?? source.Owned;
    }
    internal bool AllowDepenetration(Damager damager,CollisionInstance collision)
    {
        if(AllowPenetration(damager,collision)) return true;
        var damage=collision.damageStruct;var joint=damage.penetrationJoint;
        // Local teardown of an invalid replica is not a new combat decision.
        // Native UpdatePenetration explicitly requests cleanup in these cases.
        return damage.penetration==DamageStruct.Penetration.None || joint==null || !joint.gameObject.activeInHierarchy ||
            joint.connectedBody==null || !joint.connectedBody.gameObject.activeInHierarchy ||
            collision.targetCollider==null || !collision.targetCollider.enabled || !collision.targetCollider.gameObject.activeInHierarchy;
    }
    private ReplicatedEntity? PenetrationTarget(CollisionInstance collision)
        => collision.targetColliderGroup?.collisionHandler is { } handler ? Find(handler.ragdollPart?.ragdoll.creature) ?? Find(handler.item) : null;
    private static Damager[] Damagers(Item item) => item.collisionHandlers.SelectMany(h => h.damagers).Distinct().ToArray();
    internal void PenetrationChanged(Damager damager, CollisionInstance collision, bool entered,bool wentThrough=false)
    {
        if(!entered)
        {
            if(!penetrationBindings.TryGetValue(collision,out var entry)) return;
            penetrationBindings.Remove(collision);
            if(Applying || disposed || !Entities.TryGetValue(entry.Entity,out var owner) || !owner.Owned) return;
            // Closing a constraint uses its original identity and native entry
            // metadata. Cuts, collider reordering and respawns cannot change the
            // exit into a different penetration or an unrelated terrain event.
            var exitData=JsonConvert.DeserializeObject<PenetrationData>(entry.Text,new JsonSerializerSettings{TypeNameHandling=TypeNameHandling.None,MaxDepth=4})!;exitData.WentThrough=wentThrough;
            SendAction(owner,new GameplayAction{Kind=ActionKind.Penetration,Other=entry.Other,Part=entry.Part,Handle=entry.Handle,
                Side=0,Value=entry.Value,Position=entry.Position,Direction=entry.Direction,Text=JsonConvert.SerializeObject(exitData)});return;
        }
        if (Applying || disposed || !AllowPenetration(damager, collision)) return;
        var source = Find(damager.collisionHandler?.item); if (source?.Item == null) return;
        var target = PenetrationTarget(collision);
        bool sourceAuthority=PlayerWeaponOwnsPenetration(source,target);
        var actor = sourceAuthority ? source : target ?? source;
        if (!actor.Owned || collision.targetCollider == null) return;
        var frame = collision.damageStruct.hitRagdollPart?.transform ?? target?.Transform ?? collision.targetCollider.transform;
        var up = frame.InverseTransformDirection(damager.transform.up);
        var localPosition = frame.InverseTransformPoint(source.Item.transform.position);
        var localRotation = Quaternion.Inverse(frame.rotation) * source.Item.transform.rotation;
        var joint = collision.damageStruct.penetrationJoint;
        var penPoint = collision.damageStruct.penetrationPoint;
        static float[] V(Vector3 v) => new[]{v.x,v.y,v.z};
        var pointRotation = penPoint != null ? penPoint.localRotation : Quaternion.identity;
        var data = new PenetrationData
        {
            SourceAuthoritative=sourceAuthority,
            Token=((ulong)Session.LocalId<<32) | (nextPenetrationToken==uint.MaxValue ? throw new InvalidOperationException("Penetration sequence exhausted") : ++nextPenetrationToken),
            Mode = collision.damageStruct.penetration,
            SourceCollider = damager.colliderGroup.colliders.IndexOf(collision.sourceCollider),
            TargetCollider = collision.targetColliderGroup?.colliders.IndexOf(collision.targetCollider) ?? 0,
            TargetGroup = target?.Item != null ? target.Item.colliderGroups.IndexOf(collision.targetColliderGroup) : 0,
            SourceMaterial = collision.sourceMaterial?.id ?? "", TargetMaterial = collision.targetMaterial?.id ?? "",
            WorldCollider = target == null ? ColliderKey(collision.targetCollider) : "",
            Up = new[] {up.x,up.y,up.z},
            WeaponPosition = new[] {localPosition.x,localPosition.y,localPosition.z},
            WeaponRotation = new[] {localRotation.x,localRotation.y,localRotation.z,localRotation.w}
            ,Anchor = V(joint != null ? joint.anchor : Vector3.zero)
            ,ConnectedAnchor = V(joint != null ? joint.connectedAnchor : Vector3.zero)
            ,Axis = V(joint != null ? joint.axis : Vector3.right)
            ,SecondaryAxis = V(joint != null ? joint.secondaryAxis : Vector3.up)
            ,PointPosition = V(penPoint != null ? penPoint.localPosition : Vector3.zero)
            ,PointRotation = new[]{pointRotation.x,pointRotation.y,pointRotation.z,pointRotation.w}
        };
        var action=new GameplayAction
        {
            Kind=ActionKind.Penetration, Other=target != null ? (sourceAuthority ? target.Descriptor.Id : source.Descriptor.Id) : 0,
            Part=collision.damageStruct.hitRagdollPart != null ? GameMath.PartKey(collision.damageStruct.hitRagdollPart) : 0,
            Handle=Array.IndexOf(Damagers(source.Item),damager), Side=entered ? 1 : 0,
            Value=Mathf.Max(.001f,collision.damageStruct.penetrationJoint?.linearLimit.limit ?? damager.penetrationDepth),
            Position=frame.InverseTransformPoint(collision.contactPoint).Net(),
            Direction=frame.InverseTransformDirection(damager.transform.forward).Net(),
            Text=JsonConvert.SerializeObject(data)
        };
        penetrationBindings[collision]=action;SendAction(actor,action);
    }
    private static string PenetrationKey(GameplayAction a)
    {
        var data = JsonConvert.DeserializeObject<PenetrationData>(a.Text,new JsonSerializerSettings{TypeNameHandling=TypeNameHandling.None,MaxDepth=4})!;
        if(data.Token!=0) return a.Entity+":"+data.Token;
        return a.Entity + ":" + a.Other + ":" + a.Handle + ":" + a.Part + ":" + data.TargetGroup + ":" + data.TargetCollider + ":" + data.WorldCollider;
    }
    private static string ColliderKey(Collider collider) => GameMath.ScenePath(collider.transform) + ":collider:" + Array.IndexOf(collider.GetComponents<Collider>(), collider);
    private bool ClearMirroredPenetration(string key,bool wentThrough=false)
    {
        if(!mirroredPenetrations.TryGetValue(key,out var saved)) return false;
        mirroredPenetrations.Remove(key);
        // A native collision slot can be reused. An old exit must never detach
        // the replacement joint occupying that same CollisionInstance object.
        if(saved.Damager!=null && saved.Hit.damageStruct.damager==saved.Damager &&
            ReferenceEquals(saved.Hit.damageStruct.penetrationJoint,saved.Joint) &&
            (saved.Joint!=null || saved.Hit.damageStruct.penetration!=DamageStruct.Penetration.None)) saved.Damager.UnPenetrate(saved.Hit,wentThrough);
        return true;
    }
    private void ReleaseEntityPenetrations(ReplicatedEntity entity,bool removingNativeObject)
    {
        pendingMirroredPenetrations.RemoveAll(p=>p.Action.Entity==entity.Descriptor.Id || p.Action.Other==entity.Descriptor.Id);
        applyDepth++;
        try
        {
            foreach(var key in mirroredPenetrations.Where(p=>p.Value.Weapon==entity.Descriptor.Id || p.Value.Target==entity.Descriptor.Id).Select(p=>p.Key).ToArray()) ClearMirroredPenetration(key);
            playerPenetrationWeapons.Remove(entity.Descriptor.Id);
            if(!removingNativeObject && !entity.SpawnedRemotely && entity.Descriptor.Kind!=EntityKind.Player) return;
            // Clear incoming native constraints before destroying a fighter or
            // prop. Item.allActive also includes original local weapons already
            // detached from the registry during session shutdown.
            foreach(var item in Item.allActive.ToArray())
                if(item!=null) foreach(var handler in item.collisionHandlers)
                {
                    if(handler==null || handler.collisions==null) continue;
                    foreach(var hit in handler.collisions.ToArray())
                    {
                        if(hit==null) continue;
                        var damager=hit.damageStruct.damager;
                        bool targetsCreature=entity.Creature!=null && hit.damageStruct.hitRagdollPart!=null && hit.damageStruct.hitRagdollPart.ragdoll?.creature==entity.Creature;
                        bool targetsItem=entity.Item!=null && hit.damageStruct.hitItem==entity.Item;
                        if(damager!=null && hit.damageStruct.penetration!=DamageStruct.Penetration.None && (targetsCreature || targetsItem)) damager.UnPenetrate(hit);
                    }
                }
        }
        finally{applyDepth--;}
    }
    private void ApplyPenetration(GameplayAction a)
    {
        var data = JsonConvert.DeserializeObject<PenetrationData>(a.Text, new JsonSerializerSettings { TypeNameHandling=TypeNameHandling.None, MaxDepth=4 });
        if (data == null || !ValidPenetrationData(data)) return;
        string key=PenetrationKey(a);
        // Use the exact joint created by the entry event, even when its target's
        // collider list changed after a cut or the target is being despawned.
        if(a.Side==0 && ClearMirroredPenetration(key,data.WentThrough)) return;
        ulong sourceId=data.SourceAuthoritative || a.Other==0 ? a.Entity : a.Other;
        ulong targetId=data.SourceAuthoritative ? a.Other : a.Entity;
        if (!Entities.TryGetValue(sourceId, out var source) || source.Item == null) return;
        if(data.SourceAuthoritative) playerPenetrationWeapons.Add(sourceId);
        var damager = Damagers(source.Item).ElementAtOrDefault(a.Handle); if (damager == null) return;
        Collider? collider; Transform? frame;
        if (a.Other != 0 && Entities.TryGetValue(targetId, out var target))
        {
            var part = target.Creature != null ? GameMath.FindPart(target.Creature,a.Part) : null;
            var group = part?.colliderGroup ?? target.Item?.colliderGroups.ElementAtOrDefault(Math.Max(0,data.TargetGroup));
            collider = group?.colliders.ElementAtOrDefault(Math.Max(0,data.TargetCollider)) ?? group?.colliders.FirstOrDefault();
            frame = part?.transform ?? target.Transform;
        }
        else
        {
            if (!worldColliders.TryGetValue(data.WorldCollider,out collider) || collider == null)
                foreach (var candidate in UnityEngine.Object.FindObjectsOfType<Collider>(true)) worldColliders[ColliderKey(candidate)] = candidate;
            worldColliders.TryGetValue(data.WorldCollider,out collider);
            frame = collider?.transform;
        }
        if (collider == null || frame == null) { Log?.Invoke("Penetration target collider unavailable"); return; }
        var targetGroup = collider.GetComponentInParent<ColliderGroup>();
        var sourceCollider = damager.colliderGroup.colliders.ElementAtOrDefault(Math.Max(0,data.SourceCollider));
        if(sourceCollider==null || targetGroup==null) return;
        var existing = damager.collisionHandler.collisions.Where(c => c.damageStruct.damager == damager && c.damageStruct.penetrationJoint != null &&
            c.IsSameSourceColliderGroup(sourceCollider,damager.colliderGroup) && c.IsSameTargetColliderGroup(collider,targetGroup)).ToArray();
        if (a.Side == 0) { foreach (var c in existing) damager.UnPenetrate(c,data.WentThrough); return; }
        if (existing.Length > 0)
        {
            mirroredPenetrations[key]=(sourceId,a.Other!=0 ? targetId : 0,damager,existing[0],existing[0].damageStruct.penetrationJoint);
            penetrationBindings[existing[0]]=a;return;
        }
        var point = frame.TransformPoint(a.Position.Game());
        var forward = frame.TransformDirection(a.Direction.Game());
        if (sourceCollider == null || targetGroup == null) return;
        // An ordinary hit for this collider group may already occupy the native
        // collision slot. Reuse it instead of asking ForcePierce for a duplicate.
        var hit = damager.collisionHandler.collisions.FirstOrDefault(c => c.active && c.IsSameSourceColliderGroup(sourceCollider,damager.colliderGroup) && c.IsSameTargetColliderGroup(collider,targetGroup) &&
            c.damageStruct.penetrationJoint==null && c.damageStruct.penetration==DamageStruct.Penetration.None);
        if (hit == null)
        {
            if (!damager.collisionHandler.TryGetFreeCollisionIndexAndNotSameGroup(sourceCollider,damager.colliderGroup,collider,targetGroup,out int slot)) { Log?.Invoke("Native penetration collision slots full"); return; }
            hit = new CollisionInstance(); damager.collisionHandler.collisions[slot] = hit;
        }
        hit.damageStruct.Reset();
        hit.active = true; hit.sourceCollider = sourceCollider; hit.sourceColliderGroup = damager.colliderGroup;
        hit.targetCollider = collider; hit.targetColliderGroup = targetGroup;
        hit.sourceMaterial = Catalog.GetData<MaterialData>(data.SourceMaterial,false);
        hit.targetMaterial = Catalog.GetData<MaterialData>(data.TargetMaterial,false);
        hit.contactPoint = point; hit.contactNormal = -forward; hit.pressureForce = forward;
        hit.damageStruct.active = true; hit.damageStruct.damager = damager; hit.damageStruct.damagerData = damager.data;
        hit.damageStruct.damageType = damager.data.damageModifierData.damageType; hit.damageStruct.penetration = data.Mode;
        hit.damageStruct.hitItem = targetGroup.collisionHandler?.item; hit.damageStruct.hitRagdollPart = targetGroup.collisionHandler?.ragdollPart;
        hit.damageStruct.materialModifier = damager.data.damageModifierData.GetModifier(hit);
        if (hit.damageStruct.materialModifier == null) { hit.damageStruct.penetration = DamageStruct.Penetration.None; Log?.Invoke("Native penetration material unavailable"); return; }
        var rb = source.Item.physicBody.rigidBody;
        var velocity = rb.velocity; var angular = rb.angularVelocity;
        bool held = source.Item.handlers.Count > 0;
        if (!held)
        {
            var rotation = new Quaternion(data!.WeaponRotation[0],data.WeaponRotation[1],data.WeaponRotation[2],data.WeaponRotation[3]);
            source.Item.transform.SetPositionAndRotation(frame.TransformPoint(new Vector3(data.WeaponPosition[0],data.WeaponPosition[1],data.WeaponPosition[2])),frame.rotation * rotation);
            rb.position=source.Item.transform.position; rb.rotation=source.Item.transform.rotation;
        }
        // A held blade keeps its live physics pose. Reconstruct native effects and
        // bookkeeping, then copy the actual native joint's local frames instead
        // of recalculating them from a delayed weapon pose or a temporary raycast.
        damager.Penetrate(hit,true,true,depth:a.Value,velocityMultiplier:1);
        static Vector3 V(float[] v) => new Vector3(v[0],v[1],v[2]);
        if (hit.damageStruct.penetrationJoint != null)
        {
            mirroredPenetrations[key]=(sourceId,a.Other!=0 ? targetId : 0,damager,hit,hit.damageStruct.penetrationJoint);
            penetrationBindings[hit]=a;
            var joint = hit.damageStruct.penetrationJoint;
            joint.anchor=V(data!.Anchor); joint.connectedAnchor=V(data.ConnectedAnchor);
            joint.axis=V(data.Axis); joint.secondaryAxis=V(data.SecondaryAxis);
            var limit = joint.linearLimit; limit.limit = a.Value; joint.linearLimit = limit;
            hit.damageStruct.penetrationPoint.localPosition=V(data.PointPosition);
            hit.damageStruct.penetrationPoint.localRotation=new Quaternion(data.PointRotation[0],data.PointRotation[1],data.PointRotation[2],data.PointRotation[3]);
        }
        if (held && !rb.isKinematic) {rb.velocity=velocity;rb.angularVelocity=angular;}
    }
    private static bool ValidPenetrationData(PenetrationData? data)
    {
        static bool Vector(float[]? v) => v != null && v.Length == 3 && v.All(Protocol.Finite);
        static bool Rotation(float[]? q) => q != null && q.Length == 4 && q.All(Protocol.Finite) && q.Sum(v=>v*v) > .5f && q.Sum(v=>v*v) < 1.5f;
        return data != null && data.Token!=0 && data.Mode >= DamageStruct.Penetration.Pressure && data.Mode <= DamageStruct.Penetration.Skewer && Vector(data.Up) && Vector(data.WeaponPosition) && Rotation(data.WeaponRotation) &&
            Vector(data.Anchor) && Vector(data.ConnectedAnchor) && Vector(data.Axis) && Vector(data.SecondaryAxis) && Vector(data.PointPosition) && Rotation(data.PointRotation) &&
            data.SourceCollider >= -1 && data.SourceCollider < 128 && data.TargetCollider >= -1 && data.TargetCollider < 128 && data.WorldCollider != null && data.WorldCollider.Length <= 2048;
    }
    private bool ValidatePenetration(GameplayAction action)
    {
        if (action.Handle < 0 || action.Handle >= 128 || action.Side < 0 || action.Side > 1 || action.Value < 0 || action.Value > 20) return false;
        var data = JsonConvert.DeserializeObject<PenetrationData>(action.Text,new JsonSerializerSettings { TypeNameHandling=TypeNameHandling.None, MaxDepth=4 });
        if(!ValidPenetrationData(data)) return false;
        ulong weaponId=data!.SourceAuthoritative || action.Other==0 ? action.Entity : action.Other;
        ulong targetId=data.SourceAuthoritative ? action.Other : action.Entity;
        if(!Registry.TryGet(weaponId,out var weapon) || weapon.Kind!=EntityKind.Item) return false;
        if(data.SourceAuthoritative) return action.Other!=0 && Registry.TryGet(targetId,out var victim) && victim.Kind==EntityKind.Player;
        return action.Other==0 ? data.WorldCollider.Length>0 : Registry.TryGet(targetId,out _);
    }
}
