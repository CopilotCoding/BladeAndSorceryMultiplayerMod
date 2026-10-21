using System.Collections.Generic;
using System.Linq;
using BladeAndSorcery.Multiplayer.Core;
using ThunderRoad;
using UnityEngine;

namespace BladeAndSorcery.Multiplayer;

internal sealed partial class ReplicatedEntity
{
    internal bool InitialPoseSnapped {get;private set;}
    internal bool ReceiveSnapshot(EntitySnapshot snapshot,double now)
    {
        if(!Buffer.Push(snapshot,now)) return false;
        LastArrival=now;
        // Late joiners must finish native cuts before detached bones can be
        // placed independently; snapping an intact joint to detached positions
        // would stretch the whole ragdoll while apparel/cuts are still loading.
        if(!InitialPoseSnapped && Creature!=null && Descriptor.SlicedParts.All(key=>GameMath.FindPart(Creature,key)?.isSliced==true))
        {
            SnapCreaturePose(snapshot);InitialPoseSnapped=true;
        }
        return true;
    }
    internal void SnapTestPose()
    {
        var snapshot=Buffer.Sample(World.Now+Buffer.Delay);
        if(snapshot==null || Creature==null) return;
        Buffer.Clear();Buffer.Push(snapshot,World.Now);
        SnapCreaturePose(snapshot);
    }
    private void SnapCreaturePose(EntitySnapshot snapshot)
    {
        var c=Creature!;
        c.transform.SetPositionAndRotation(snapshot.Root.Pose.Position.Game(),snapshot.Root.Pose.Rotation.Game());
        if(Descriptor.Kind==EntityKind.Player) UpdateTrackedSkeleton(snapshot,(snapshot.Flags&257)!=0);
        foreach(var state in snapshot.Parts)
        {
            var part=GameMath.FindPart(c,state.Part);if(part==null) continue;
            var body=part.physicBody.rigidBody;
            var interpolation=body.interpolation;body.interpolation=RigidbodyInterpolation.None;
            part.transform.SetPositionAndRotation(state.Pose.Position.Game(),state.Pose.Rotation.Game());
            body.position=part.transform.position;body.rotation=part.transform.rotation;
            if(!body.isKinematic) {body.velocity=state.Velocity.Game();body.angularVelocity=state.AngularVelocity.Game();}
            body.interpolation=interpolation;
        }
        if(leftController!=null) SetController(leftController,snapshot.LeftHand);
        if(rightController!=null) SetController(rightController,snapshot.RightHand);
        Physics.SyncTransforms();
        static void SetController(Rigidbody body,Core.Pose pose)
        {
            body.transform.SetPositionAndRotation(pose.Position.Game(),pose.Rotation.Game());
            body.position=body.transform.position;body.rotation=body.transform.rotation;
        }
    }
    private sealed class TrackedJoint
    {
        internal RagdollPart Part=null!,Parent=null!;
        internal CharacterJoint Joint=null!;
        internal Vector3 RestAnchor;
        internal bool Automatic;
    }
    private readonly List<TrackedJoint> trackedJoints=new List<TrackedJoint>();
    private readonly Dictionary<int,BodyState> trackedTargets=new Dictionary<int,BodyState>();
    private bool trackedAnchorsActive;
    private void PrepareTrackedSkeleton()
    {
        RestoreTrackedSkeleton();trackedJoints.Clear();
        foreach(var part in Creature!.ragdoll.parts)
        {
            var joint=part.characterJoint;
            if(joint==null || joint.connectedBody==null) continue;
            var parent=Creature.ragdoll.parts.FirstOrDefault(p=>p.physicBody.rigidBody==joint.connectedBody);
            if(parent!=null) trackedJoints.Add(new TrackedJoint {Part=part,Parent=parent,Joint=joint,RestAnchor=joint.connectedAnchor,Automatic=joint.autoConfigureConnectedAnchor});
        }
    }
    private void RestoreTrackedSkeleton()
    {
        if(!trackedAnchorsActive) return;
        foreach(var entry in trackedJoints)
            if(entry.Joint!=null) {entry.Joint.connectedAnchor=entry.RestAnchor;entry.Joint.autoConfigureConnectedAnchor=entry.Automatic;}
        trackedAnchorsActive=false;trackedTargets.Clear();
    }
    private void UpdateTrackedSkeleton(EntitySnapshot snapshot,bool physical)
    {
        if(physical) {RestoreTrackedSkeleton();return;}
        // A tracked native player poses kinematic bones through IK. An NPC's
        // rest joint locations can resist those same poses by several centimetres,
        // even with angular limits disabled. Derive compatible joint frames from
        // the received bone poses while retaining dynamic bodies and constraints.
        trackedTargets.Clear();foreach(var body in snapshot.Parts) trackedTargets[body.Part]=body;
        foreach(var entry in trackedJoints)
        {
            var joint=entry.Joint;
            if(joint==null || entry.Part.isSliced || BodyGripControls(entry.Part) || joint.connectedBody==null ||
                !trackedTargets.TryGetValue(GameMath.PartKey(entry.Part),out var child) ||
                !trackedTargets.TryGetValue(GameMath.PartKey(entry.Parent),out var parent)) continue;
            var childAnchor=child.Pose.Position.Game()+child.Pose.Rotation.Game()*Vector3.Scale(joint.anchor,entry.Part.transform.lossyScale);
            var local=Quaternion.Inverse(parent.Pose.Rotation.Game())*(childAnchor-parent.Pose.Position.Game());
            var scale=entry.Parent.transform.lossyScale;
            if(Mathf.Abs(scale.x)<.00001f || Mathf.Abs(scale.y)<.00001f || Mathf.Abs(scale.z)<.00001f) continue;
            joint.autoConfigureConnectedAnchor=false;
            joint.connectedAnchor=new Vector3(local.x/scale.x,local.y/scale.y,local.z/scale.z);
        }
        trackedAnchorsActive=true;
    }
}
