using System;
using System.Collections.Generic;
using System.Linq;
using BladeAndSorcery.Multiplayer.Core;
using ThunderRoad;
using UnityEngine;

namespace BladeAndSorcery.Multiplayer;

internal sealed partial class ReplicatedEntity
{
    private Rigidbody[]? attachedBodies;
    internal string[] SourceBodyPaths=Array.Empty<string>();
    private Rigidbody[] AttachedBodies()
    {
        if(attachedBodies!=null)return attachedBodies;
        var item=Item;if(item==null || !item.worldAttached)return attachedBodies=Array.Empty<Rigidbody>();
        var root=item.physicBody.rigidBody;var seen=new HashSet<Rigidbody>{root};var pending=new Queue<Rigidbody>();pending.Enqueue(root);
        var joints=UnityEngine.Object.FindObjectsOfType<Joint>();
        while(pending.Count>0 && seen.Count<=Protocol.MaxParts)
        {
            var body=pending.Dequeue();
            foreach(var joint in joints)
            {
                var local=joint.GetComponent<Rigidbody>();
                var next=local==body ? joint.connectedBody : joint.connectedBody==body ? local : null;
                if(next==null || seen.Contains(next) || next.gameObject.scene!=item.gameObject.scene || next.GetComponentInParent<Creature>()!=null || next.GetComponentInParent<Player>()!=null)continue;
                var otherItem=next.GetComponentInParent<Item>();if(otherItem!=null && otherItem!=item)continue;
                seen.Add(next);pending.Enqueue(next);
            }
        }
        return attachedBodies=seen.Where(b=>b!=root).OrderBy(b=>GameMath.ScenePath(b.transform),StringComparer.Ordinal).Take(Protocol.MaxParts).ToArray();
    }
    internal string[] AttachedBodyPaths()=>AttachedBodies().Where(b=>b!=null).Select(b=>GameMath.ScenePath(b.transform)).ToArray();
    private BodyState[] CaptureAttachedBodies()=>AttachedBodies().Select((body,index)=>GameMath.Capture(body,index)).ToArray();
    private Rigidbody? AttachedBody(int index)
    {
        if(index<0 || index>=SourceBodyPaths.Length)return null;
        string path=SourceBodyPaths[index];return AttachedBodies().FirstOrDefault(body=>body!=null && GameMath.ScenePath(body.transform)==path);
    }
    private void DriveAttachedBodies(EntitySnapshot state)
    {
        foreach(var part in state.Parts){var body=AttachedBody(part.Part);if(body!=null)GameMath.Drive(body,part,45,160);}
    }
    private bool AttachedBodiesStill(EntitySnapshot state)=>state.Parts.All(part=>AttachedBody(part.Part)!=null && part.Velocity.LengthSquared()<.001f && part.AngularVelocity.LengthSquared()<.001f);
    private void SettleAttachedBodies(EntitySnapshot state)
    {
        foreach(var part in state.Parts)
        {
            var body=AttachedBody(part.Part);if(body==null)continue;
            var interpolation=body.interpolation;body.interpolation=RigidbodyInterpolation.None;
            body.position=part.Pose.Position.Game();body.rotation=part.Pose.Rotation.Game();body.transform.SetPositionAndRotation(body.position,body.rotation);
            if(!body.isKinematic){body.velocity=part.Velocity.Game();body.angularVelocity=part.AngularVelocity.Game();body.Sleep();}
            body.interpolation=interpolation;
        }
    }
}
