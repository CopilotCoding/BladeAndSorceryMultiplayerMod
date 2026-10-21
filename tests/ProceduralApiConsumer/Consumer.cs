using System;
using BladeAndSorcery.Multiplayer;
using ThunderRoad;
using UnityEngine;

// Compile-only separate-assembly consumer: no access to multiplayer internals,
// no controller/AI implementation. This validates the documented public surface.
public sealed class Consumer
{
    private ProceduralNpcHandle? handle;
    private ProceduralNpcSpawnRequest? request;
    public void Attach(Creature creature,Transform left,Transform right)
    {
        if(MultiplayerModApi.ProceduralNpcApiVersion!=1 || !MultiplayerModApi.ProceduralNpcAvailable || !MultiplayerModApi.CanHostProceduralNpc)return;
        handle=MultiplayerModApi.RegisterProceduralNpc(creature,left,right);
        handle.Stopped+=Stopped;
    }
    private void Stopped(ProceduralNpcHandle source,ProceduralNpcStopReason reason)
    {bool ended=source.StopReason.HasValue && !source.CanSimulate;GC.KeepAlive(ended);}
    public void Inspect()
    {
        if(!MultiplayerModApi.WorldReady || handle?.CanSimulate!=true)return;
        foreach(MultiplayerHuman human in MultiplayerModApi.GetHumanPlayers())
        {
            ulong id=human.NetworkId;ushort peer=human.PeerId;string name=human.Name;
            Creature creature=human.Creature;bool local=human.IsLocal,alive=human.IsAlive;
            Pose head=human.Head,left=human.LeftHand,right=human.RightHand;
            bool remote=MultiplayerModApi.IsNetworkReplica(creature),owned=MultiplayerModApi.IsLocallyOwned(creature),procedural=MultiplayerModApi.IsProceduralNpc(creature);
            GC.KeepAlive(new object[]{id,peer,name,local,alive,head,left,right,remote,owned,procedural,MultiplayerModApi.WorldRevision,handle.WorldRevision,handle.IsRegistered,handle.NetworkId,handle.Creature});
        }
    }
    public void Spawn(Vector3 position)
    {request=MultiplayerModApi.SpawnProceduralNpcAsync("HumanMale",position,0,creature=>GC.KeepAlive(creature),error=>GC.KeepAlive(error));}
    public void Cleanup()
    {
        if(request!=null){request.Cancel();GC.KeepAlive(request.IsPending);GC.KeepAlive(request.Error);}
        if(handle!=null){handle.Stopped-=Stopped;handle.Despawn();}
    }
}
