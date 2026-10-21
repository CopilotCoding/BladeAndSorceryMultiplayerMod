using System.Collections.Generic;
using System.Linq;
using ThunderRoad;

namespace BladeAndSorcery.Multiplayer;

internal sealed partial class GameWorld
{
    private readonly Dictionary<Item,HashSet<Creature>> playerItemCollisionOverrides = new Dictionary<Item,HashSet<Creature>>();
    internal void RefreshPlayerItemCollision(Item item)
    {
        var holders=item.handlers.Select(h=>Find(h.creature)).Where(e=>e != null && (e.Descriptor.ProceduralNpc || !e.Owned && e.Descriptor.Kind==Core.EntityKind.Player)).Select(e=>e!).Distinct().ToArray();
        if (playerItemCollisionOverrides.TryGetValue(item,out var previous))
            foreach (var creature in previous)
                if (creature != null)
                    foreach (var collider in item.colliderGroups.SelectMany(g=>g.colliders)) creature.ragdoll.IgnoreCollision(collider,false);
        if (holders.Length == 0) { playerItemCollisionOverrides.Remove(item); return; }
        var current=new HashSet<Creature>();
        foreach (var holder in holders)
        {
            var creature=holder.Creature!; current.Add(creature);
            bool left=item.handlers.Any(h=>h.creature == creature && h.side == Side.Left);
            bool right=item.handlers.Any(h=>h.creature == creature && h.side == Side.Right);
            RagdollPart.Type exclusions;
            if (holder.GripSelfCollision)
            {
                var leftArm=RagdollPart.Type.LeftArm|RagdollPart.Type.LeftHand;
                var rightArm=RagdollPart.Type.RightArm|RagdollPart.Type.RightHand;
                var other=~(leftArm|rightArm);
                exclusions=left && right ? other : right ? leftArm|other : rightArm|other;
            }
            else
            {
                var feet=RagdollPart.Type.LeftFoot|RagdollPart.Type.RightFoot;
                exclusions=left && right ? feet : right ? RagdollPart.Type.LeftHand|feet : RagdollPart.Type.RightHand|feet;
            }
            // Use the same exclusions as native player gripping. The NPC/inert
            // branch otherwise returns early without the player's own-body filter.
            // Apply collider pairs directly so shared grips retain every player's
            // filter rather than replacing Item.ignoredRagdoll with one creature.
            foreach (var collider in item.colliderGroups.SelectMany(g=>g.colliders)) creature.ragdoll.IgnoreCollision(collider,true,exclusions);
        }
        playerItemCollisionOverrides[item]=current;
    }
    private void ClearPlayerItemCollision(Item item)
    {
        if (!playerItemCollisionOverrides.TryGetValue(item,out var previous)) return;
        foreach (var creature in previous)
            if (creature != null)
                foreach (var collider in item.colliderGroups.SelectMany(g=>g.colliders)) creature.ragdoll.IgnoreCollision(collider,false);
        playerItemCollisionOverrides.Remove(item);
    }
}
