using System;
using System.Linq;
using BladeAndSorcery.Multiplayer.Core;
using ThunderRoad;
using UnityEngine;

namespace BladeAndSorcery.Multiplayer;

/// <summary>A point-in-time human roster entry. Creature references are local to this PC.</summary>
public sealed class MultiplayerHuman
{
    public ulong NetworkId {get;}
    public ushort PeerId {get;}
    public string Name {get;}
    public Creature Creature {get;}
    public bool IsLocal {get;}
    public bool IsAlive {get;}
    public UnityEngine.Pose Head {get;}
    public UnityEngine.Pose LeftHand {get;}
    public UnityEngine.Pose RightHand {get;}
    internal MultiplayerHuman(ReplicatedEntity e)
    {
        NetworkId=e.Descriptor.Id;PeerId=e.Descriptor.Owner;Creature=e.Creature!;IsLocal=e.Owned;
        Name=e.World.Session.Peers.TryGetValue(PeerId,out var peer) ? peer.Name : "Player "+PeerId;
        IsAlive=!Creature.isKilled && (IsLocal || e.SourceAlive);
        var head=IsLocal && Player.local!=null ? Player.local.head.transform : Creature.ragdoll.headPart.transform;
        Head=new UnityEngine.Pose(head.position,head.rotation);
        var left=e.HandTarget(Side.Left);var right=e.HandTarget(Side.Right);
        LeftHand=new UnityEngine.Pose(left.Position.Game(),left.Rotation.Game());RightHand=new UnityEngine.Pose(right.Position.Game(),right.Rotation.Game());
    }
}

public enum ProceduralNpcStopReason { Killed, Despawned, WorldChanged, SessionEnded, TargetsLost }

/// <summary>Host registration for one creature's network life. All members are main-thread only.</summary>
public sealed class ProceduralNpcHandle
{
    internal readonly ReplicatedEntity Entity;
    internal readonly Transform LeftTarget,RightTarget;
    public ulong NetworkId=>Entity.Descriptor.Id;
    public uint WorldRevision {get;}
    public Creature Creature {get;}
    public ProceduralNpcStopReason? StopReason {get;private set;}
    public bool IsRegistered=>Entity.World.Find(Creature)==Entity && Entity.ProceduralControl==this;
    public bool CanSimulate=>StopReason==null && IsRegistered && Entity.World.CanHostProceduralNpc && Creature!=null && !Creature.isKilled && UsableTarget(LeftTarget) && UsableTarget(RightTarget);
    internal static bool UsableTarget(Transform target)
    {
        if(target==null)return false;var p=target.position;var q=target.rotation;float length=q.x*q.x+q.y*q.y+q.z*q.z+q.w*q.w;
        return Protocol.Finite(p.x) && Protocol.Finite(p.y) && Protocol.Finite(p.z) && p.sqrMagnitude<=1e12f &&
            Protocol.Finite(q.x) && Protocol.Finite(q.y) && Protocol.Finite(q.z) && Protocol.Finite(q.w) && Protocol.Finite(length) && length>.0001f;
    }
    public event Action<ProceduralNpcHandle,ProceduralNpcStopReason>? Stopped;
    internal ProceduralNpcHandle(ReplicatedEntity e,Transform left,Transform right)
    {Entity=e;Creature=e.Creature!;LeftTarget=left;RightTarget=right;WorldRevision=e.World.Session.WorldRevision;}
    /// <summary>Despawn through native/network lifecycle. Weapons follow native loot/cleanup rules.</summary>
    public void Despawn()
    {if(IsRegistered && Entity.World.CanHostProceduralNpc && Creature!=null)Creature.Despawn();}
    internal void Stop(ProceduralNpcStopReason reason)
    {
        if(StopReason!=null)return;StopReason=reason;
        var handlers=Stopped;Stopped=null;
        if(handlers==null)return;
        foreach(Action<ProceduralNpcHandle,ProceduralNpcStopReason> callback in handlers.GetInvocationList())
            try{callback(this,reason);}catch(Exception error){Entity.World.CompatibilityWarning("procedural-stop-"+NetworkId,error);}
    }
}

/// <summary>Cancelable native spawn. A late native completion is discarded after cancellation/travel.</summary>
public sealed class ProceduralNpcSpawnRequest
{
    internal readonly GameWorld World;
    internal readonly int Generation;
    internal readonly double Deadline;
    internal readonly Action<Creature> Ready;
    internal readonly Action<string>? Failed;
    public bool IsPending {get;internal set;}=true;
    public string Error {get;internal set;}="";
    internal ProceduralNpcSpawnRequest(GameWorld world,int generation,Action<Creature> ready,Action<string>? failed)
    {World=world;Generation=generation;Deadline=Time.realtimeSinceStartupAsDouble+60;Ready=ready;Failed=failed;}
    public void Cancel()=>World.FailProceduralSpawn(this,"Spawn canceled.");
}

public static partial class MultiplayerModApi
{
    public const int ProceduralNpcApiVersion=1;
    public static bool ProceduralNpcAvailable=>ProceduralNpcDamageWindowPatch.Available;
    /// <summary>Gate creation AND every controller tick with this. False offline, on clients and during loading.</summary>
    public static bool CanHostProceduralNpc=>MultiplayerRuntime.Instance?.World?.CanHostProceduralNpc==true;
    public static bool WorldReady=>MultiplayerRuntime.Instance?.World?.ProceduralWorldReady==true;
    public static uint WorldRevision=>MultiplayerRuntime.Instance?.Session?.WorldRevision ?? 0;
    public static bool IsNetworkReplica(Creature creature)=>MultiplayerRuntime.Instance?.World?.Find(creature) is {Owned:false};
    public static bool IsProceduralNpc(Creature creature)=>MultiplayerRuntime.Instance?.World?.Find(creature)?.Descriptor.ProceduralNpc==true;
    public static bool IsLocallyOwned(Creature creature)=>MultiplayerRuntime.Instance?.World?.Find(creature)?.Owned==true;
    public static MultiplayerHuman[] GetHumanPlayers()
    {
        var world=MultiplayerRuntime.Instance?.World;
        if(world?.ProceduralWorldReady!=true)return Array.Empty<MultiplayerHuman>();
        return world.Entities.Values.Where(e=>e.Descriptor.Kind==EntityKind.Player && e.Creature!=null).OrderBy(e=>e.Descriptor.Owner).Select(e=>new MultiplayerHuman(e)).ToArray();
    }
    /// <summary>Targets are world-space native grip frames. The AI owns its host rig, joints and decision loop.</summary>
    public static ProceduralNpcHandle RegisterProceduralNpc(Creature creature,Transform leftGripTarget,Transform rightGripTarget)
        => HostWorld().RegisterProceduralNpc(creature,leftGripTarget,rightGripTarget);
    public static ProceduralNpcSpawnRequest SpawnProceduralNpcAsync(string catalogId,Vector3 position,float yaw,Action<Creature> onSpawned,Action<string>? onFailed=null)
        => HostWorld().SpawnProceduralNpc(catalogId,position,yaw,onSpawned,onFailed);
    private static GameWorld HostWorld()
    {
        var world=MultiplayerRuntime.Instance?.World;
        if(world?.CanHostProceduralNpc!=true)throw new InvalidOperationException("Procedural NPC control requires the connected host in a ready world.");
        return world;
    }
}
