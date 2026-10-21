using System.Linq;
using System;
using Newtonsoft.Json;
using HarmonyLib;
using ThunderRoad;

namespace BladeAndSorcery.Multiplayer;

internal sealed class NativeWardrobeState
{
    public string Id="";
    public NativeCustomRecord[] Custom=Array.Empty<NativeCustomRecord>();
}

internal sealed partial class GameWorld
{
    private NativeWardrobeState[] CaptureWardrobeContents(Creature creature)
    {
        try{return creature.equipment.GetWornContents().Take(32).Select(w=>new NativeWardrobeState{Id=w.referenceID,Custom=NativeCustomState.Capture(w.customDataList,CompatibilityWarning)}).ToArray();}
        catch(Exception error){CompatibilityWarning("armor custom data",error);return Array.Empty<NativeWardrobeState>();}
    }
    private ItemContent WardrobeContent(AppearanceData appearance,string id)
    {
        var record=appearance.WardrobeContents?.Take(32).FirstOrDefault(w=>w?.Id==id);
        if(record==null)return new ItemContent(id);
        if(record.Custom==null || record.Custom.Length>32)throw new System.IO.InvalidDataException("Too much armor custom data");
        return new ItemContent(id,null,ReadCompatibleCustomData(record.Custom));
    }
    internal void PublishUpdatedAppearance(ReplicatedEntity entity)
    {
        if(!SceneUsable || entity.Creature==null) return;
        var appearance=JsonConvert.DeserializeObject<AppearanceData>(entity.Descriptor.Appearance) ?? new AppearanceData();
        appearance.Wardrobes=entity.Creature.equipment.GetWornContents().Select(w=>w.referenceID).ToArray();
        appearance.WardrobeContents=CaptureWardrobeContents(entity.Creature);
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
            if(creature.equipment.GetWornContents().Select(w=>w.referenceID).OrderBy(id=>id).SequenceEqual(appearance.Wardrobes.OrderBy(id=>id)) &&
                JsonConvert.SerializeObject(CaptureWardrobeContents(creature),NativeCustomState.Settings)==JsonConvert.SerializeObject(appearance.WardrobeContents,NativeCustomState.Settings))return;
            creature.equipment.UnequipAllWardrobes();
            foreach(var wardrobe in appearance.Wardrobes.Take(32))
                if(Catalog.TryGetData<ItemData>(wardrobe,out _,false)) creature.equipment.EquipWardrobe(WardrobeContent(appearance,wardrobe));
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
