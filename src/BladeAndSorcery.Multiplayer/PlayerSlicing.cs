using System.Collections.Generic;
using System.Linq;
using BladeAndSorcery.Multiplayer.Core;
using ThunderRoad;

namespace BladeAndSorcery.Multiplayer;

internal sealed partial class GameWorld
{
    internal readonly List<(double Time, ulong Victim, int Part, float Speed, bool Sliced)> SliceTrace = new List<(double, ulong, int, float, bool)>();
    internal bool AllowPhysicalPlayerSlice(CollisionInstance hit)
    {
        var creature = hit.damageStruct.hitRagdollPart?.ragdoll.creature;
        var victim = Find(creature);
        return Applying || victim == null || victim.Descriptor.Kind != EntityKind.Player ||
            (victim.Owned && !Player.IsInvincible && Match.AllowHit(creature!,hit));
    }
    private static void ApplyNativeSlice(ReplicatedEntity entity, RagdollPart part)
    {
        var target = part.sliceChildAndDisableSelf ?? part;
        if (part.isSliced || !entity.RemoteSliceTargets.Add(target)) return;
        entity.PendingRemoteSlices.Enqueue(part);
    }
    private void TickRemoteSlices()
    {
        foreach (var entity in Entities.Values.ToArray())
        {
            var c = entity.Creature;
            if (entity.Owned || c == null || entity.PendingRemoteSlices.Count == 0 ||
                entity.AppearanceUpdating || Now < entity.NextRemoteSliceAt ||
                c.equipment.GetPendingApparelLoading() > 0 || c.ragdoll.sliceRunning) continue;
            var part = entity.PendingRemoteSlices.Dequeue();
            if (part == null || part.isSliced) continue;
            applyDepth++;
            try
            {
                if (!part.TrySlice()) entity.RemoteSliceTargets.Remove(part.sliceChildAndDisableSelf ?? part);
            }
            finally {applyDepth--;entity.NextRemoteSliceAt=Now+.1;}
        }
    }
    internal Ragdoll.State PlayerSliceState(Ragdoll ragdoll, CollisionInstance hit)
    {
        if (ragdoll.state != Ragdoll.State.Kinematic || Applying || disposed) return ragdoll.state;
        var victim = Find(ragdoll.creature);
        var weapon = Find(hit.sourceColliderGroup?.collisionHandler?.item);
        if (victim == null || !victim.Owned || victim.Descriptor.Kind != EntityKind.Player ||
            weapon == null || weapon.Descriptor.Owner == victim.Descriptor.Owner ||
            Player.IsInvincible || !Match.AllowHit(ragdoll.creature,hit)) return ragdoll.state;
        // Only this hit's native slicing predicate sees a physical state. Local VR
        // control, collision bodies and tracking retain the game's actual mode.
        return Ragdoll.State.Standing;
    }
    internal bool FinishFatalPlayerSlice(Creature creature, CollisionInstance hit)
    {
        if (Applying || disposed || !hit.damageStruct.active || !hit.damageStruct.sliceDone ||
            !ShouldForceKillSlice(hit.damageStruct.hitRagdollPart) || Player.IsInvincible) return false;
        var victim = Find(creature);
        if (victim == null || !victim.Owned || victim.Descriptor.Kind != EntityKind.Player) return false;
        // Native NPC slicing uses infinite damage for fatal parts. The native
        // player damage cap would turn that into an ordinary nonfatal sword hit.
        creature.Kill(hit);
        return true;
    }
    internal void TraceSlice(CollisionInstance hit, bool sliced)
    {
        if (string.IsNullOrEmpty(MultiplayerRuntime.Instance?.TestDirectory) || Applying ||
            hit.damageStruct.hitRagdollPart is not { } part || Find(part.ragdoll.creature) is not { } victim) return;
        SliceTrace.Add((Now,victim.Descriptor.Id,GameMath.PartKey(part),hit.impactVelocity.magnitude,sliced));
        if (SliceTrace.Count > 100) SliceTrace.RemoveAt(0);
    }
}
