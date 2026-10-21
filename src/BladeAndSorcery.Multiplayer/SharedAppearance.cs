using System.Linq;
using Newtonsoft.Json;
using HarmonyLib;
using ThunderRoad;

namespace BladeAndSorcery.Multiplayer;

internal sealed partial class GameWorld
{
    internal void PublishUpdatedAppearance(ReplicatedEntity entity)
    {
        if(!SceneUsable || entity.Creature==null) return;
        var appearance=JsonConvert.DeserializeObject<AppearanceData>(entity.Descriptor.Appearance) ?? new AppearanceData();
        appearance.Wardrobes=entity.Creature.equipment.GetWornContents().Select(w=>w.referenceID).ToArray();
        appearance.Faction=entity.Creature.factionId;
        entity.Descriptor.Appearance=JsonConvert.SerializeObject(appearance);
        PublishSpawn(entity.Descriptor);
    }
    private void ApplyUpdatedAppearance(ReplicatedEntity entity)
    {
        var creature=entity.Creature;if(creature==null) return;
        var appearance=JsonConvert.DeserializeObject<AppearanceData>(entity.Descriptor.Appearance,new JsonSerializerSettings{TypeNameHandling=TypeNameHandling.None,MaxDepth=8});
        if(appearance==null) return;
        applyDepth++;
        try
        {
            creature.SetFaction(appearance.Faction);
            if(creature.equipment.GetWornContents().Select(w=>w.referenceID).OrderBy(id=>id).SequenceEqual(appearance.Wardrobes.OrderBy(id=>id)))return;
            creature.equipment.UnequipAllWardrobes();
            foreach(var wardrobe in appearance.Wardrobes.Take(32))
                if(Catalog.TryGetData<ItemData>(wardrobe,out _,false)) creature.equipment.EquipWardrobe(new ItemContent(wardrobe));
        }
        finally{applyDepth--;}
    }
}

[HarmonyPatch(typeof(Creature),nameof(Creature.SetFaction))]
internal static class SharedNativeFactionPatch
{
    private static void Postfix(Creature __instance)
    {
        var world=MultiplayerRuntime.Instance?.World;
        if(world!=null && !world.Applying && world.Find(__instance) is {Owned:true} entity)world.PublishUpdatedAppearance(entity);
    }
}
