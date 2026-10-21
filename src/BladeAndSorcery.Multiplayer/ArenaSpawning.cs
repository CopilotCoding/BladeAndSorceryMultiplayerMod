using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BladeAndSorcery.Multiplayer.Core;
using ThunderRoad;
using UnityEngine;
using UnityEngine.AI;
using Pose = BladeAndSorcery.Multiplayer.Core.Pose;

namespace BladeAndSorcery.Multiplayer;

internal sealed partial class GameWorld
{
    // Names retained internally for the existing test diagnostics/wire message.
    // These slots now apply to every playable map, including Home.
    private readonly Dictionary<ushort, Pose> arenaSpawnSlots = new Dictionary<ushort, Pose>();
    private readonly Dictionary<ulong, EntityDescriptor> pendingSceneSpawns = new Dictionary<ulong, EntityDescriptor>();
    private Pose? localArenaSpawn;
    private bool localPlayerRegistered;
    private bool IsArena => Level.current?.data?.id == "Arena";
    private bool TryChoosePlayerSpawn(ushort peer,out Pose result)
    {
        result=default;
        var anchors=new List<Vector3>{Player.local.transform.position};
        if(PlayerSpawner.current!=null) anchors.Add(PlayerSpawner.current.transform.position);
        anchors.AddRange(PlayerSpawner.allActive.Where(s=>s!=null && s.spawnBody).OrderBy(s=>GameMath.ScenePath(s.transform)).Select(s=>s.transform.position));
        foreach(var origin in anchors)
        for(int attempt=0;attempt<64;attempt++)
        {
            float angle=(peer*137.5f+attempt*137.5f)*Mathf.Deg2Rad;
            float radius=1.8f+attempt/12*.6f;
            var point=origin+new Vector3(Mathf.Sin(angle),0,Mathf.Cos(angle))*radius;
            Vector3 floor;
            if(NavMesh.SamplePosition(point,out var nav,.8f,NavMesh.AllAreas) && Mathf.Abs(nav.position.y-origin.y)<1.8f) floor=nav.position;
            else if(Physics.Raycast(point+Vector3.up*1.5f,Vector3.down,out var hit,3f,ThunderRoadSettings.current.groundLayer,QueryTriggerInteraction.Ignore) && Vector3.Dot(hit.normal,Vector3.up)>.7f) floor=hit.point;
            else continue;
            var target=floor+Vector3.up*.08f;
            // Check actual standing-room clearance, not just a point on the mesh.
            if(Physics.CheckCapsule(target+Vector3.up*.35f,target+Vector3.up*1.5f,.28f,Physics.DefaultRaycastLayers,QueryTriggerInteraction.Ignore)) continue;
            if(arenaSpawnSlots.Any(s=>s.Key!=peer && Vector3.Distance(s.Value.Position.Game(),target)<1.1f)) continue;
            if(Entities.Values.Any(e=>e.Descriptor.Kind==EntityKind.Player && e.Descriptor.Owner!=peer && e.Creature!=null && !e.Creature.isKilled && Vector3.Distance(e.Creature.transform.position,target)<1.1f)) continue;
            var direction=origin-target;direction.y=0;
            result=new Pose(target.Net(),(direction.sqrMagnitude>.01f ? Quaternion.LookRotation(direction) : Quaternion.identity).Net());return true;
        }
        return false;
    }
    private Pose ChooseArenaSpawn(ushort peer)
        =>TryChoosePlayerSpawn(peer,out var pose) ? pose : throw new InvalidOperationException("No clear native player spawn was found on "+Level.current?.data?.id);
    private void SendArenaSpawn(ushort peer)
    {
        if(!Session.IsHost || !SceneUsable || Player.local?.creature==null) return;
        if(!arenaSpawnSlots.TryGetValue(peer,out var pose))
        {
            if(!TryChoosePlayerSpawn(peer,out pose)){SetTravelStatus("Waiting for a safe spawn for player "+peer+" on "+Level.current.data.id);return;}
            arenaSpawnSlots[peer]=pose;
        }
        Session.SendTo(peer,MessageKind.ArenaSpawn,Protocol.Encode(w=>Protocol.Pose(w,pose)));
    }
    private void ReceiveArenaSpawn(ushort source,BinaryReader reader)
    {
        var pose=Protocol.Pose(reader);Protocol.End(reader);
        if(source!=1 || Session.IsHost || !localSceneLoaded || LevelManager.isLoadingLocked) return;
        bool first=!localArenaSpawn.HasValue;localArenaSpawn=pose;
        if(first && Player.local?.creature!=null) TeleportLocal(pose);
        sceneReady=true;sceneDeadline=0;stableScene=expectedScene;recoveringScene=false;
        SetTravelStatus("Arrived in "+Level.current.data.id+".");
    }
    private void TeleportLocal(Pose pose)=>Player.local.Teleport(pose.Position.Game(),pose.Rotation.Game(),false,false);
    private void PrepareLocalPlayerSpawn()
    {
        if(Session.IsHost && localPlayerRegistered && TryChoosePlayerSpawn(Session.LocalId,out var respawn)) localArenaSpawn=respawn;
        if(localArenaSpawn.HasValue && Vector3.Distance(Player.local.transform.position,localArenaSpawn.Value.Position.Game())>.5f) TeleportLocal(localArenaSpawn.Value);
    }
}
