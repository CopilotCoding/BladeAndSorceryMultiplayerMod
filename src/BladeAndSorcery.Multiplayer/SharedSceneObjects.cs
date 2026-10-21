using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using ThunderRoad;

namespace BladeAndSorcery.Multiplayer;

internal sealed partial class GameWorld
{
    private void PrepareClientWorld()
    {
        if(Session.IsHost || preparedClientWorld==Session.WorldRevision) return;
        preparedClientWorld=Session.WorldRevision;
        // Joining an already loaded Home must discard that PC's loose scene
        // copies before receiving the host world. Keep the local carried loadout.
        applyDepth++;
        try
        {
            foreach(var item in Item.allActive.ToArray())
            {
                if(item==null || Find(item)!=null || item.worldAttached || LocalCarriedItem(item)) continue;
                item.Despawn();
            }
            foreach(var creature in Creature.allActive.ToArray())
                if(creature!=null && creature!=Player.local?.creature && Find(creature)==null) creature.Despawn();
        }
        finally{applyDepth--;}
    }
    private static bool LocalCarriedItem(Item item)
        =>item.handlers.Any(h=>h.creature==Player.local?.creature) || item.holder?.GetRootHolder().creature==Player.local?.creature;
    internal bool CanSpawnSceneObjects=>Session.IsHost;
    private void RemoveForeignSceneSpawns()
    {
        if(Session.IsHost || spawning.Count>0) return;
        applyDepth++;
        try
        {
            foreach(var creature in Creature.allActive.ToArray())
                if(creature!=null && creature.loaded && creature!=Player.local?.creature && Find(creature)==null) creature.Despawn();
            foreach(var item in Item.allActive.ToArray())
                if(item!=null && item.loaded && item.spawner!=null && Find(item)==null && !LocalCarriedItem(item)) item.Despawn();
        }
        finally{applyDepth--;}
    }
}

[HarmonyPatch(typeof(ItemSpawner),nameof(ItemSpawner.Spawn),new[]{typeof(bool),typeof(bool),typeof(bool),typeof(System.Random),typeof(int),typeof(bool)})]
internal static class SharedSceneItemSpawnerPatch
{
    private static bool Prefix(ref int __result)
    {
        if(MultiplayerRuntime.Instance?.World?.CanSpawnSceneObjects!=false) return true;
        __result=0;return false;
    }
}

[HarmonyPatch(typeof(CreatureSpawner),nameof(CreatureSpawner.Spawn),new[]{typeof(Action),typeof(System.Random)})]
internal static class SharedSceneCreatureSpawnerPatch
{
    private static bool Prefix(Action completeCallback)
    {
        if(MultiplayerRuntime.Instance?.World?.CanSpawnSceneObjects!=false) return true;
        completeCallback?.Invoke();return false;
    }
}

[HarmonyPatch]
internal static class SharedSceneContainerSpawnerPatch
{
    private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(ContainerSpawner),nameof(ContainerSpawner.Spawn),new[]{typeof(Action<ItemContent,int>)});
        yield return AccessTools.Method(typeof(ContainerSpawner),nameof(ContainerSpawner.Spawn),new[]{typeof(Action<Item,int>)});
    }
    private static bool Prefix()=>MultiplayerRuntime.Instance?.World?.CanSpawnSceneObjects!=false;
}
