using System.Runtime.CompilerServices;
using ThunderRoad;

namespace BladeAndSorcery.Multiplayer;

internal sealed partial class GameWorld
{
    private sealed class LocalItemOrigin
    {
        internal float SpawnTime;
        internal string Catalog = "";
    }
    // Weak metadata preserves locally created objects across disconnects without
    // adding controllers, modifying their physics, or retaining destroyed items.
    private static readonly ConditionalWeakTable<Item,LocalItemOrigin> localItemOrigins = new ConditionalWeakTable<Item,LocalItemOrigin>();
    private static void RememberLocalOrigin(Item item)
    {
        var origin = localItemOrigins.GetValue(item,_ => new LocalItemOrigin());
        origin.SpawnTime = item.spawnTime; origin.Catalog = item.data.id;
    }
    private static bool HasLocalOrigin(Item item) => localItemOrigins.TryGetValue(item,out var origin) && origin.SpawnTime == item.spawnTime && origin.Catalog == item.data.id;
}
