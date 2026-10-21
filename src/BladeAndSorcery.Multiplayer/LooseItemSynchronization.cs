using BladeAndSorcery.Multiplayer.Core;
using ThunderRoad;
using UnityEngine;
using Pose = BladeAndSorcery.Multiplayer.Core.Pose;

namespace BladeAndSorcery.Multiplayer;

internal sealed partial class ReplicatedEntity
{
    private bool hasRestingTarget;
    private Pose restingTarget;
    private double restingTargetSince,lastLocalItemContact=-100;
    private Collider[]? looseItemColliders;
    private ushort ItemFlags()
    {
        var item=Item!;var body=item.physicBody.rigidBody;
        bool free=item.handlers.Count==0 && item.holder==null && !item.isPenetrating;
        bool still=free && (body.IsSleeping() || body.velocity.sqrMagnitude<.0001f && body.angularVelocity.sqrMagnitude<.0001f);
        return (ushort)((still ? 1 : 0) | (item.handlers.Count>0 ? 2 : 0) | (item.holder!=null ? 4 : 0) | (item.isPenetrating ? 8 : 0) | (item.worldAttached ? 16 : 0) | (body.isKinematic ? 32 : 0));
    }
    private void DriveLooseItem(EntitySnapshot state,double now)
    {
        var item=Item!;var body=item.physicBody.rigidBody;
        if(item.handlers.Count>0 || item.holder!=null || item.isPenetrating){hasRestingTarget=false;return;}
        bool ownerResting=(state.Flags&15)==1 && state.Root.Velocity.LengthSquared()<.001f && state.Root.AngularVelocity.LengthSquared()<.001f && AttachedBodiesStill(state);
        var target=state.Root.Pose;
        if(!ownerResting){hasRestingTarget=false;GameMath.Drive(item.physicBody,state.Root,45,120);return;}
        if(!hasRestingTarget || Vector3.Distance(restingTarget.Position.Game(),target.Position.Game())>.002f || Quaternion.Angle(restingTarget.Rotation.Game(),target.Rotation.Game())>.2f)
        {restingTarget=target;restingTargetSince=now;hasRestingTarget=true;}
        // Moving/contacting objects retain native dynamics. Once both ends settle,
        // friction must not leave the replica in a different resting orientation.
        if(now-restingTargetSince>=.4 && now-lastLocalItemContact>=.6 && body.velocity.sqrMagnitude<.01f && body.angularVelocity.sqrMagnitude<.0625f && !HasActiveOwnedContact(item) && !NearLocalPlayer(item))
        {
            bool changed=Vector3.Distance(body.position,target.Position.Game())>.001f || Quaternion.Angle(body.rotation,target.Rotation.Game())>.1f;
            SettleAttachedBodies(state);
            var interpolation=body.interpolation;body.interpolation=RigidbodyInterpolation.None;
            body.position=target.Position.Game();body.rotation=target.Rotation.Game();
            body.transform.SetPositionAndRotation(body.position,body.rotation);
            if(!body.isKinematic){body.velocity=state.Root.Velocity.Game();body.angularVelocity=state.Root.AngularVelocity.Game();body.Sleep();}
            body.interpolation=interpolation;
            if(changed)World.SettledItemCorrections++;
            return;
        }
        GameMath.Drive(item.physicBody,state.Root,45,120);
    }
    private bool NearLocalPlayer(Item item)
    {
        var player=Player.local?.creature;if(player==null)return false;
        var bounds=new Bounds(item.transform.position,Vector3.zero);
        looseItemColliders ??= item.GetComponentsInChildren<Collider>();
        foreach(var collider in looseItemColliders)if(collider!=null && collider.enabled && !collider.isTrigger)bounds.Encapsulate(collider.bounds);
        bounds.Expand(.12f);
        foreach(var part in player.ragdoll.parts)
            foreach(var collider in part.colliderGroup.colliders)
                if(collider!=null && collider.enabled && bounds.Intersects(collider.bounds))return true;
        return false;
    }
    private bool HasActiveOwnedContact(Item item)
    {
        foreach(var handler in item.collisionHandlers)
            foreach(var hit in handler.collisions)
                if(hit.active && (OwnedInteractor(hit.sourceColliderGroup) || OwnedInteractor(hit.targetColliderGroup)))return true;
        return false;
    }
    private bool OwnedInteractor(ColliderGroup? group)
    {
        var entity=World.Find(group?.collisionHandler?.item) ?? World.Find(group?.collisionHandler?.ragdollPart?.ragdoll.creature);
        return entity!=null && entity!=this && entity.Owned && (entity.Creature!=null || entity.Item?.handlers.Count>0);
    }
    private void NoteLocalItemContact(CollisionInstance hit)
    {
        if(Item==null || Owned)return;
        foreach(var group in new[]{hit.sourceColliderGroup,hit.targetColliderGroup})
        {
            var other=World.Find(group?.collisionHandler?.item) ?? World.Find(group?.collisionHandler?.ragdollPart?.ragdoll.creature);
            if(other!=null && other!=this && other.Owned)lastLocalItemContact=World.Now;
        }
    }
}
internal sealed partial class GameWorld { internal int SettledItemCorrections; }
