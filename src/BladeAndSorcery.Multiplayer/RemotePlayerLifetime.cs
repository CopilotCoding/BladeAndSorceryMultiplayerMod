using System.Linq;
using BladeAndSorcery.Multiplayer.Core;
using ThunderRoad;

namespace BladeAndSorcery.Multiplayer;

internal sealed partial class GameWorld
{
    // Remote humans use native creatures but do not have a local Player
    // component. Native NPC cleanup must not own their network lifetime.
    internal bool AllowNativeCreatureDespawn(Creature creature)
    {
        if(Applying || disposed || !sceneReady || !Session.Connected ||
            LevelManager.isLoadingLocked || GameManager.isQuitting) return true;
        var entity=Find(creature);
        return entity==null || entity.Owned || !entity.SpawnedRemotely;
    }

    private void RecoverMissingRemotePlayers()
    {
        foreach(var entity in Entities.Values.Where(e=>!e.Owned && e.SpawnedRemotely &&
            e.Creature==null && e.Item==null && e.Descriptor.Kind!=EntityKind.Item).ToArray())
        {
            var state=entity.Buffer.Sample(Now);
            var descriptor=entity.Descriptor;
            if(state!=null)
            {
                descriptor.Pose=state.Root.Pose;
                descriptor.Killed=(state.Flags&1)!=0;
                earlySnapshots[descriptor.Id]=state;
            }
            if(slices.TryGetValue(descriptor.Id,out var cuts)) descriptor.SlicedParts=cuts.Select(a=>a.Part).Distinct().ToArray();
            // Preserve the registry, ownership epoch and reliable constraints.
            // Only the lost local representation is replaced.
            Entities.Remove(descriptor.Id);
            foreach(var key in creatures.Where(p=>ReferenceEquals(p.Value,entity)).Select(p=>p.Key).ToArray()) creatures.Remove(key);
            entity.Dispose();
            Log?.Invoke("Recovering missing remote "+descriptor.Kind+" "+descriptor.Id);
            SpawnRemote(descriptor);
        }
    }
}
