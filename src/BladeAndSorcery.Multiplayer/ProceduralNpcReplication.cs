using System;
using System.Collections.Generic;
using System.Linq;
using BladeAndSorcery.Multiplayer.Core;
using ThunderRoad;
using UnityEngine;

namespace BladeAndSorcery.Multiplayer;

internal sealed partial class GameWorld
{
    private readonly List<ProceduralNpcSpawnRequest> proceduralSpawns=new List<ProceduralNpcSpawnRequest>();
    internal bool ProceduralWorldReady=>!disposed && Session.Connected && SceneUsable;
    internal bool CanHostProceduralNpc=>ProceduralWorldReady && Session.IsHost && ProceduralNpcDamageWindowPatch.Available;
    internal ProceduralNpcStopReason ProceduralRemovalReason=>disposed ? ProceduralNpcStopReason.SessionEnded : !sceneReady || LevelManager.isLoadingLocked ? ProceduralNpcStopReason.WorldChanged : ProceduralNpcStopReason.Despawned;
    internal ProceduralNpcHandle RegisterProceduralNpc(Creature creature,Transform left,Transform right)
    {
        if(!CanHostProceduralNpc)throw new InvalidOperationException("Only the ready host can register procedural NPCs.");
        if(creature==null || !creature.loaded || creature.isPlayer || creature==Player.local?.creature || creature.isKilled || !ProceduralNpcHandle.UsableTarget(left) || !ProceduralNpcHandle.UsableTarget(right))
            throw new ArgumentException("A loaded live NPC and both virtual grip targets are required.");
        if(creature.ragdoll==null || creature.handLeft==null || creature.handRight==null || creature.ragdoll.headPart==null || creature.ragdoll.parts.Count>Protocol.MaxParts || creature.ragdoll.parts.Select(GameMath.PartKey).Distinct().Count()!=creature.ragdoll.parts.Count)
            throw new ArgumentException("Procedural NPC requires compatible humanoid parts with unique type/section keys, up to 20 bodies.");
        var entity=Find(creature);
        if(entity!=null && (!entity.Owned || entity.Descriptor.Kind!=EntityKind.Creature || entity.ProceduralControl!=null))throw new InvalidOperationException("Creature is already controlled or is not a host NPC.");
        if(entity==null && Registry.Count>=Protocol.MaxEntities-Protocol.MaxPeers)throw new InvalidOperationException("Network entity capacity reached.");
        entity ??= Register(creature,EntityKind.Creature,true);
        entity.Descriptor.ProceduralNpc=true;
        var handle=new ProceduralNpcHandle(entity,left,right);entity.ProceduralControl=handle;
        entity.PrepareVirtualHandHandles();PublishSpawn(entity.Descriptor);
        creature.handLeft.grabbedHandle?.item?.RefreshCollision();creature.handRight.grabbedHandle?.item?.RefreshCollision();
        return handle;
    }
    internal ProceduralNpcSpawnRequest SpawnProceduralNpc(string id,Vector3 position,float yaw,Action<Creature> ready,Action<string>? failed)
    {
        if(!CanHostProceduralNpc)throw new InvalidOperationException("Only the ready host can spawn procedural NPCs.");
        if(ready==null)throw new ArgumentNullException(nameof(ready));
        if(!Protocol.Finite(position.x) || !Protocol.Finite(position.y) || !Protocol.Finite(position.z) || !Protocol.Finite(yaw) || !Catalog.TryGetData<CreatureData>(id,out var data,false))throw new ArgumentException("Invalid native creature catalog or pose.");
        var request=new ProceduralNpcSpawnRequest(this,clearGeneration,ready,failed);proceduralSpawns.Add(request);
        try{data.SpawnAsync(position,yaw,pooled:false,callback:creature=>
        {
            if(!request.IsPending || disposed || request.Generation!=clearGeneration || !CanHostProceduralNpc)
            {if(creature!=null)creature.Despawn();FailProceduralSpawn(request,"World changed before native spawn completed.");return;}
            if(creature==null){FailProceduralSpawn(request,"Native spawn returned no creature.");return;}
            request.IsPending=false;proceduralSpawns.Remove(request);
            try{ready(creature);}catch(Exception error){request.Error="NPC setup failed: "+error.Message;creature.Despawn();NotifyProceduralSpawnFailure(request);}
        });}catch(Exception error){FailProceduralSpawn(request,"Native spawn failed: "+error.Message);}
        return request;
    }
    internal void FailProceduralSpawn(ProceduralNpcSpawnRequest request,string error)
    {
        if(!request.IsPending)return;request.IsPending=false;request.Error=error;proceduralSpawns.Remove(request);NotifyProceduralSpawnFailure(request);
    }
    private void NotifyProceduralSpawnFailure(ProceduralNpcSpawnRequest request)
    {try{request.Failed?.Invoke(request.Error);}catch(Exception error){Log?.Invoke("Procedural spawn failure callback failed: "+error);}}
    private void TickProceduralNpcs()
    {
        foreach(var request in proceduralSpawns.ToArray())if(Time.realtimeSinceStartupAsDouble>request.Deadline)FailProceduralSpawn(request,"Native spawn timed out.");
        foreach(var entity in Entities.Values.ToArray())
            if(entity.ProceduralControl is {StopReason:null} control && (!ProceduralNpcHandle.UsableTarget(control.LeftTarget) || !ProceduralNpcHandle.UsableTarget(control.RightTarget)))control.Stop(ProceduralNpcStopReason.TargetsLost);
    }
    private void ClearProceduralSpawns()
    {foreach(var request in proceduralSpawns.ToArray())FailProceduralSpawn(request,"Multiplayer world ended or changed.");}
}

internal sealed partial class ReplicatedEntity
{
    internal ProceduralNpcHandle? ProceduralControl;
    internal bool UsesVirtualHands=>Descriptor.Kind==EntityKind.Player || Descriptor.ProceduralNpc;
    internal bool ProceduralCanDamage=>Descriptor.ProceduralNpc && Creature!=null && !Creature.isKilled && (Owned ? ProceduralControl?.CanSimulate==true : SourceProceduralCanDamage);
    private bool SourceProceduralCanDamage;
    internal void PrepareVirtualHandHandles()
    {
        foreach(var handle in Creature!.ragdoll.parts.SelectMany(p=>p.handles))
        {
            if(bodyHandleData.ContainsKey(handle))continue;
            bodyHandleData[handle]=handle.handleRagdollData;
            var data=(HandleRagdollData)handle.handleRagdollData.Clone();data.useIK=false;data.moveStep=false;handle.handleRagdollData=data;
        }
    }
    internal void EnableProceduralReplica()
    {
        if(Owned || !Descriptor.ProceduralNpc || Creature==null)return;
        PrepareVirtualHandHandles();
        leftController ??= CreateController(Creature.handLeft);rightController ??= CreateController(Creature.handRight);
        foreach(var side in new[]{Side.Left,Side.Right})
        {var hand=Creature.GetHand(side);if(hand.grabbedHandle!=null)AttachNetworkGrip(hand,HandTarget(side));}
    }
}
