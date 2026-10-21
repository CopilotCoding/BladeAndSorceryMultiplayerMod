using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using ThunderRoad;

namespace BladeAndSorcery.Multiplayer;

internal sealed partial class GameWorld
{
    private readonly Dictionary<ulong,(Container Container,ItemContent Content,double Deadline)> pendingNativeStorage=new Dictionary<ulong,(Container,ItemContent,double)>();
    private void OnNativeBookItem(Item item)
    {
        if(SceneUsable && !Applying && item!=null && Find(item)==null) Register(item);
    }
    private void OnNativeInventoryItem(UIInventory inventory,ItemContent content,Item item)=>OnNativeBookItem(item);
    internal bool StoreNativeItem(Container container,Item item,bool despawn,ContentState state,List<ContentCustomData> customData)
    {
        var entity=Find(item);
        if(!SceneUsable || Applying || !despawn || entity==null || entity.Owned) return true;
        if(CarriedByOtherPlayer(item)){SetTravelStatus("Release the item before storing it; another player is holding it.");return false;}
        if(!pendingNativeStorage.ContainsKey(entity.Descriptor.Id))
        {
            pendingNativeStorage.Add(entity.Descriptor.Id,(container,new ItemContent(item,state,customData ?? item.contentCustomData).CloneGeneric(),Now+5));
            Session.Publish(BladeAndSorcery.Multiplayer.Core.MessageKind.OwnershipRequest,BladeAndSorcery.Multiplayer.Core.Protocol.Encode(w=>{w.Write(entity.Descriptor.Id);w.Write(entity.Descriptor.Epoch);}));
        }
        return false;
    }
    private void TickNativeItemStorage()
    {
        foreach(var pair in pendingNativeStorage.ToArray())
        {
            if(pair.Value.Container==null || !Entities.TryGetValue(pair.Key,out var entity) || entity.Item==null || pair.Value.Deadline<Now)
            {pendingNativeStorage.Remove(pair.Key);continue;}
            if(!entity.Owned) continue;
            pendingNativeStorage.Remove(pair.Key);
            if(CarriedByOtherPlayer(entity.Item)) continue;
            var content=pair.Value.Content;
            pair.Value.Container.AddItemContent(entity.Item,true,content.state,content.customDataList);
        }
    }
}

[HarmonyPatch(typeof(Container),nameof(Container.AddItemContent),new[]{typeof(Item),typeof(bool),typeof(ContentState),typeof(List<ContentCustomData>)})]
internal static class SharedNativeItemStoragePatch
{
    private static bool Prefix(Container __instance,Item item,bool despawnItem,ContentState state,List<ContentCustomData> customDataList,ref ItemContent __result)
    {
        if(MultiplayerRuntime.Instance?.World?.StoreNativeItem(__instance,item,despawnItem,state,customDataList)!=false) return true;
        __result=null!;return false;
    }
}
