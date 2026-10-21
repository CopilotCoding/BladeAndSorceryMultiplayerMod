using System.Collections.Generic;
using System.Linq;
using BladeAndSorcery.Multiplayer.Core;
using ThunderRoad;

namespace BladeAndSorcery.Multiplayer;

internal sealed partial class GameWorld
{
    private readonly Dictionary<ulong,double> ownershipRetryAt = new Dictionary<ulong,double>();
    internal IEnumerable<GameplayAction> ActiveGrips => grips.Values;
    private void ReconcileOwnedGrips()
    {
        foreach (var actor in Entities.Values.Where(e => e.Owned && e.Creature != null).ToArray())
        {
            foreach (var side in new[]{Side.Left,Side.Right})
            {
                var hand = actor.Creature!.GetHand(side);
                var handle = hand.grabbedHandle;
                var target = handle?.item != null ? Find(handle.item) : handle is HandleRagdoll body ? Find(body.ragdollPart.ragdoll.creature) : null;
                var key = (actor.Descriptor.Id << 1) | (uint)side;
                if (grips.TryGetValue(key,out var cached) && cached.Other != (target?.Descriptor.Id ?? 0))
                {
                    // A received native grab can force this local hand to release
                    // while echo suppression is active. Publish its resulting state.
                    SendAction(actor,new GameplayAction {Kind=ActionKind.Release,Other=cached.Other,Side=(int)side});
                }
                if (handle != null && target != null && !grips.ContainsKey(key))
                    HandGrab(actor,side,handle,hand.gripInfo?.axisPosition ?? 0,hand.gripInfo?.orientation);
                if (target?.Item == null || target.Owned || target.Item.handlers.Any(h => Find(h.creature)?.Descriptor.Owner != Session.LocalId)) continue;
                // The first request can arrive before the grab that frees the old
                // owner's hand. Retry after native constraints have converged.
                if (ownershipRetryAt.TryGetValue(target.Descriptor.Id,out var at) && Now < at) continue;
                ownershipRetryAt[target.Descriptor.Id] = Now + .5;
                Session.Publish(MessageKind.OwnershipRequest,Protocol.Encode(w => {w.Write(target.Descriptor.Id);w.Write(target.Descriptor.Epoch);}));
            }
        }
        foreach (var id in ownershipRetryAt.Keys.Where(id => !Entities.TryGetValue(id,out var e) || e.Owned || e.Item == null || e.Item.handlers.Count == 0).ToArray()) ownershipRetryAt.Remove(id);
    }
}
