using System;
using System.Linq;
using BladeAndSorcery.Multiplayer.Core;
using ThunderRoad;
using UnityEngine;

namespace BladeAndSorcery.Multiplayer;

internal sealed partial class ReplicatedEntity
{
    internal uint PoseRevision;
    internal int TeleportSnaps;
    internal object? LastTeleportAudit;
    private int lastTeleportFrame=-1;
    internal bool TeleportPoseReady=>Time.frameCount>lastTeleportFrame;
    private void OnNativeTeleport(Vector3 delta,Quaternion rotation,EventTime time)
    {
        if(time!=EventTime.OnEnd || !Owned || World.Applying)return;
        unchecked{PoseRevision++;}
        // Native player IK and tracking finish later in the frame. Do not label
        // a mixture of the new root and old bones as the new teleport pose.
        lastTeleportFrame=Time.frameCount;
    }
    private void SnapTeleportPose(EntitySnapshot snapshot)
    {
        World.ApplyNetworkPose(()=>
        {
            if(Creature!=null)SnapCreaturePose(snapshot,true);
            else if(Item!=null)
            {
                var body=Item.physicBody.rigidBody;var interpolation=body.interpolation;
                body.interpolation=RigidbodyInterpolation.None;
                Item.Teleport(snapshot.Root.Pose.Position.Game(),snapshot.Root.Pose.Rotation.Game());
                body.position=snapshot.Root.Pose.Position.Game();body.rotation=snapshot.Root.Pose.Rotation.Game();
                if(!body.isKinematic){body.velocity=snapshot.Root.Velocity.Game();body.angularVelocity=snapshot.Root.AngularVelocity.Game();}
                SettleAttachedBodies(snapshot);hasRestingTarget=false;
                body.interpolation=interpolation;Physics.SyncTransforms();
            }
        });
        TeleportSnaps++;
        // Retained only by the isolated diagnostic driver; this measures the
        // immediate applied pose, before later native contact impulses.
        if(!string.IsNullOrEmpty(MultiplayerRuntime.Instance?.TestDirectory))
        {
            float partError=Creature==null ? 0 : snapshot.Parts.Select(p=>GameMath.FindPart(Creature,p.Part) is { } part ? Vector3.Distance(part.transform.position,p.Pose.Position.Game()) : 0).DefaultIfEmpty(0).Max();
            var sampled=Buffer.Sample(World.Now);
            LastTeleportAudit=new{revision=snapshot.PoseRevision,sequence=snapshot.Sequence,buffers=Buffer.Count,
                rootError=Vector3.Distance(Transform!.position,snapshot.Root.Pose.Position.Game()),
                rotationError=Quaternion.Angle(Transform.rotation,snapshot.Root.Pose.Rotation.Game()),partError,
                sampleError=sampled==null ? -1 : System.Numerics.Vector3.Distance(sampled.Root.Pose.Position,snapshot.Root.Pose.Position)};
        }
    }
}
internal sealed partial class GameWorld
{
    internal void ApplyNetworkPose(Action action)
    {applyDepth++;try{action();}finally{applyDepth--;}}
}
