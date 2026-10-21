using System;
using System.Linq;
using BladeAndSorcery.Multiplayer.Core;
using Newtonsoft.Json.Linq;
using ThunderRoad;
using UnityEngine;

namespace BladeAndSorcery.Multiplayer;

// Isolated integration fixture only. No targeting, attack/guard decisions or AI.
internal sealed class ProceduralNpcTestRig : MonoBehaviour
{
    internal ProceduralNpcHandle Handle=null!;
    internal Transform Left=null!,Right=null!;
    internal int Stops;
    internal string Reason="";
    internal Vector3 NeutralRight;
    internal void Setup(Creature creature)
    {
        creature.brain.Stop();creature.brain.enabled=false;creature.animator.enabled=false;creature.ragdoll.ik.enabled=false;
        creature.ragdoll.SetState(Ragdoll.State.Inert);creature.locomotion.enabled=false;
        foreach(var part in creature.ragdoll.parts){part.physicBody.rigidBody.isKinematic=true;part.physicBody.rigidBody.useGravity=false;}
        Left=Target(creature.handLeft);Right=Target(creature.handRight);
        Handle=MultiplayerModApi.RegisterProceduralNpc(creature,Left,Right);
        Handle.Stopped+=(handle,reason)=>{Stops++;Reason=reason.ToString();ProceduralNpcTestProbe.Record(handle.NetworkId,Reason);};
        NeutralRight=Right.position;
    }
    private Transform Target(RagdollHand hand)
    {
        var target=new GameObject("TestOnly.VirtualGrip."+hand.side);target.transform.SetParent(transform,true);
        target.transform.SetPositionAndRotation(hand.grip.position,hand.grip.rotation);
        var body=target.AddComponent<Rigidbody>();body.isKinematic=true;body.useGravity=false;return target.transform;
    }
    internal void Equip(Action ready)
    {
        Catalog.GetData<ItemData>("SwordShortCommon").SpawnAsync(item=>
        {
            if(!Handle.CanSimulate){item.Despawn();return;}
            var hand=Handle.Creature.handRight;hand.TryRelease();
            var grip=item.mainHandleRight ?? item.handles[0];hand.Grab(grip,grip.GetDefaultOrientation(Side.Right),0,false);
            var old=hand.gripInfo.joint;
            var anchor=old!=null ? old.connectedAnchor : item.transform.InverseTransformPoint(hand.gripInfo.transform.position);
            if(old!=null)Destroy(old);
            Right.SetPositionAndRotation(hand.gripInfo.transform.position,hand.gripInfo.transform.rotation);
            Right.GetComponent<Rigidbody>().position=Right.position;Right.GetComponent<Rigidbody>().rotation=Right.rotation;
            var joint=Right.gameObject.AddComponent<ConfigurableJoint>();joint.autoConfigureConnectedAnchor=false;
            joint.connectedBody=item.physicBody.rigidBody;joint.connectedAnchor=anchor;joint.anchor=Vector3.zero;joint.enableCollision=true;
            grip.SetJointConfig(joint,hand,Vector2.one,Vector2.one,hand.creature.data.forceMaxPosition,hand.creature.data.forceMaxRotation,grip.data.rotationDrive);
            hand.gripInfo.joint=joint;hand.gripInfo.type=ThunderRoad.Handle.GripInfo.Type.HandJoint;hand.gripInfo.hasPlayerJoint=false;
            item.physicBody.rigidBody.useGravity=false;NeutralRight=Right.position;ready();
        },Right.position,Right.rotation,pooled:false);
    }
    private void OnDestroy()
    {
        if(Left!=null)Destroy(Left.gameObject);if(Right!=null)Destroy(Right.gameObject);
    }
}

internal static class ProceduralNpcTestProbe
{
    private static readonly System.Collections.Generic.List<object> history=new System.Collections.Generic.List<object>();
    internal static void Record(ulong id,string reason)=>history.Add(new{id=id.ToString(),reason});
    private static Vector3 Vector(JToken a)=>new Vector3((float)a[0]!, (float)a[1]!, (float)a[2]!);
    internal static object? Execute(MultiplayerRuntime runtime,JObject command,Action<object> respond)
    {
        string op=(string)command["op"]!;var world=runtime.World;
        if(op=="procedural-history")return history.ToArray();
        if(op=="procedural-info")return new{api=MultiplayerModApi.ProceduralNpcApiVersion,canHost=MultiplayerModApi.CanHostProceduralNpc,ready=MultiplayerModApi.WorldReady,
            humans=MultiplayerModApi.GetHumanPlayers().Select(h=>new{id=h.NetworkId.ToString(),peer=h.PeerId,h.Name,h.IsLocal,h.IsAlive,replica=MultiplayerModApi.IsNetworkReplica(h.Creature)}).ToArray()};
        if(op=="procedural-role-check")
        {
            bool denied=false;try{MultiplayerModApi.RegisterProceduralNpc(Player.local.creature,Player.local.transform,Player.local.transform);}catch(InvalidOperationException){denied=true;}catch(ArgumentException){denied=true;}
            return new{denied};
        }
        if(op=="procedural-spawn" || op=="procedural-cancel-spawn" || op=="procedural-normal-spawn")
        {
            var request=MultiplayerModApi.SpawnProceduralNpcAsync("HumanMale",command["position"]!=null ? Vector(command["position"]!) : new Vector3(-3,.08f,1),0,creature=>
            {
                if(op=="procedural-cancel-spawn"){creature.Despawn();return;}
                if(op=="procedural-normal-spawn"){creature.brain.Stop();creature.brain.enabled=false;var ordinary=world!.Register(creature,EntityKind.Creature);respond(new{id=ordinary.Descriptor.Id.ToString()});return;}
                var rig=creature.gameObject.AddComponent<ProceduralNpcTestRig>();rig.Setup(creature);
                rig.Equip(()=>respond(new{id=rig.Handle.NetworkId.ToString(),canSimulate=rig.Handle.CanSimulate}));
            },error=>respond(new{error,canceled=op=="procedural-cancel-spawn"}));
            if(op=="procedural-cancel-spawn")request.Cancel();
            return null;
        }
        if(world==null || !ulong.TryParse((string?)command["entity"],out var id) || !world.Entities.TryGetValue(id,out var entity) || entity.Creature==null)throw new InvalidOperationException("Expected a registered creature");
        var creature=entity.Creature;var fixture=creature.GetComponent<ProceduralNpcTestRig>();
        if(op=="procedural-inspect")
        {
            var state=entity.Owned ? entity.Capture(world.Now) : entity.Buffer.Sample(world.Now+entity.Buffer.Delay);
            var grip=state?.RightGrip ?? default;
            var actualJoint=creature.handRight.gripInfo?.joint;
            return new{id=id.ToString(),procedural=MultiplayerModApi.IsProceduralNpc(creature),replica=MultiplayerModApi.IsNetworkReplica(creature),
                canSimulate=entity.ProceduralControl?.CanSimulate,stops=fixture?.Stops,reason=fixture?.Reason,
                creature.brain.enabled,animation=creature.animator.enabled,nativeWindow=creature.brain.canDamage,effectiveWindow=ProceduralNpcDamageWindowPatch.CanDamage(creature.brain),
                target=state==null ? null : new[]{state.RightHand.Position.X,state.RightHand.Position.Y,state.RightHand.Position.Z},
                gripActive=grip.Active,spring=grip.PositionSpring,damper=grip.PositionDamper,maxForce=grip.PositionMaxForce,anchor=new[]{grip.ConnectedAnchor.X,grip.ConnectedAnchor.Y,grip.ConnectedAnchor.Z},
                held=world.Find(creature.handRight.grabbedHandle?.item)?.Descriptor.Id.ToString(),parts=state?.Parts.Length,
                colliders=creature.ragdoll.parts.Sum(p=>p.colliderGroup.colliders.Count),joints=creature.ragdoll.parts.Count(p=>p.characterJoint!=null),
                sourceJoint=creature.handRight.gripInfo?.joint!=null,
                appliedSpring=actualJoint==null ? (float?)null : actualJoint.xDrive.positionSpring,appliedDamper=actualJoint==null ? (float?)null : actualJoint.xDrive.positionDamper,
                appliedForce=actualJoint==null ? (float?)null : actualJoint.xDrive.maximumForce};
        }
        if(op=="procedural-window")
        {
            bool old=creature.brain.canDamage;creature.brain.canDamage=(bool?)command["value"] ?? false;
            bool effective=ProceduralNpcDamageWindowPatch.CanDamage(creature.brain);creature.brain.canDamage=old;
            return new{effective};
        }
        if(op=="procedural-drive")
        {
            var joint=fixture!.Right.GetComponent<ConfigurableJoint>();joint.xDrive=joint.yDrive=joint.zDrive=new JointDrive{positionSpring=1337,positionDamper=37,maximumForce=2300};return "custom source grip drive set";
        }
        if(op=="procedural-duplicate")
        {
            bool denied=false;try{MultiplayerModApi.RegisterProceduralNpc(creature,fixture!.Left,fixture.Right);}catch(InvalidOperationException){denied=true;}return new{denied};
        }
        if(op=="procedural-move")
        {
            if(fixture?.Handle.CanSimulate!=true)throw new InvalidOperationException("Fixture is not controlled here");
            fixture.Right.position=fixture.NeutralRight+Vector(command["offset"]!);
            var body=fixture.Right.GetComponent<Rigidbody>();body.position=fixture.Right.position;body.rotation=fixture.Right.rotation;
            return "virtual target moved";
        }
        if(op=="procedural-lose-target"){UnityEngine.Object.Destroy(fixture!.Right.gameObject);return "fixture target destroyed";}
        if(op=="procedural-despawn"){entity.ProceduralControl!.Despawn();return "native procedural despawn requested";}
        if(op=="procedural-slice")return new{sliced=creature.ragdoll.parts.First(p=>p.type==RagdollPart.Type.LeftArm).TrySlice()};
        if(op=="procedural-hit")
        {
            var victim=world.Entities.Values.First(e=>e.Descriptor.Kind==EntityKind.Player && e.Descriptor.Owner==(ushort)(int)command["victimPeer"]!);
            var sword=creature.handRight.grabbedHandle.item;var damager=sword.collisionHandlers.SelectMany(h=>h.damagers).First(d=>d.type==Damager.Type.Slash);
            var part=victim.Creature!.ragdoll.targetPart;
            var sourceCollider=damager.colliderGroup.colliders.First();var targetCollider=part.colliderGroup.colliders.First();
            var hit=new CollisionInstance{sourceColliderGroup=damager.colliderGroup,targetColliderGroup=part.colliderGroup,sourceCollider=sourceCollider,targetCollider=targetCollider,
                sourceMaterial=MaterialData.GetMaterial(sourceCollider),targetMaterial=MaterialData.GetMaterial(targetCollider),contactPoint=part.transform.position,impactVelocity=Vector3.forward*6,contactNormal=Vector3.back};
            hit.damageStruct=new DamageStruct(DamageType.Slash,7){damager=damager,damagerData=damager.data,hitRagdollPart=part,time=Time.time};
            victim.Creature.Damage(hit);return new{scriptedNativeContact=true,damage=7};
        }
        throw new InvalidOperationException("Unknown procedural fixture command");
    }
}
