using ThunderRoad;
using System;
using System.Linq;
using HarmonyLib;
using BladeAndSorcery.Multiplayer.Core;

namespace BladeAndSorcery.Multiplayer;

internal sealed partial class ReplicatedEntity
{
    private bool itemLifetimeProtected;
    internal void RefreshItemLifetime()
    {
        if(Item==null) return;
        // Native cleanup runs on the simulation owner. Replicas must wait for
        // its reliable removal instead of independently cleaning shared objects.
        if(!Owned)
        {
            if(!itemLifetimeProtected){oldDisallowDespawn=Item.DisallowDespawn;itemLifetimeProtected=true;}
            Item.DisallowDespawn=true;
        }
        else if(itemLifetimeProtected)
        {
            Item.DisallowDespawn=oldDisallowDespawn;itemLifetimeProtected=false;
        }
        // The native owner may change this flag while nocking, firing or
        // storing an item. Do not overwrite those live native decisions.
    }
}

internal sealed partial class GameWorld
{
    internal int RecoveredItemReplicas,SuppressedItemDespawns;
    internal bool AllowNativeItemDespawn(Item item)
    {
        if(Applying || disposed || !sceneReady || !Session.Connected || LevelManager.isLoadingLocked || GameManager.isQuitting)return true;
        var entity=Find(item);if(entity==null || entity.Owned)return true;
        SuppressedItemDespawns++;return false;
    }
    private void RecoverMissingRemoteItems()
    {
        foreach(var entity in Entities.Values.Where(e=>!e.Owned && e.Descriptor.Kind==EntityKind.Item && e.Item==null).ToArray())
        {
            var descriptor=entity.Descriptor;var state=entity.Buffer.Sample(Now);
            if(state!=null){descriptor.Pose=state.Root.Pose;earlySnapshots[descriptor.Id]=state;}
            Entities.Remove(descriptor.Id);
            foreach(var key in items.Where(p=>ReferenceEquals(p.Value,entity)).Select(p=>p.Key).ToArray())items.Remove(key);
            entity.Dispose();RecoveredItemReplicas++;
            Log?.Invoke("Recovering missing remote item "+descriptor.CatalogId+" "+descriptor.Id);
            SpawnRemote(descriptor);
        }
    }
}

[HarmonyPatch(typeof(Item),nameof(Item.Despawn),new Type[0])]
internal static class RemoteItemLifetimePatch
{
    private static bool Prefix(Item __instance)=>MultiplayerRuntime.Instance?.World?.AllowNativeItemDespawn(__instance)!=false;
}
