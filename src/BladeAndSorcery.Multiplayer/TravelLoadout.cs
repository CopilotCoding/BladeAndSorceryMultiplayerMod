using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using ThunderRoad;
using UnityEngine;

namespace BladeAndSorcery.Multiplayer;

internal sealed class TravelLoadout
{
    internal sealed class Held
    {
        internal ItemContent Content = null!;
        internal Item.Owner Owner;
        internal readonly List<(Side Side,int Handle,int Orientation,float Axis)> Hands = new List<(Side,int,int,float)>();
    }
    internal readonly List<ContainerContent> Inventory = new List<ContainerContent>();
    internal readonly List<Held> Items = new List<Held>();
    internal bool Restoring;
    internal int Pending;
    internal int RestoreGeneration;
    internal Creature? Body;
    internal double Deadline;
}

internal sealed partial class GameWorld
{
    private TravelLoadout? travelLoadout;
    private void CaptureTravelLoadout()
    {
        if(travelLoadout!=null || Player.local?.creature is not { } body || body.isKilled) return;
        var snapshot=new TravelLoadout();
        var heldItems=new Dictionary<Item,TravelLoadout.Held>();
        snapshot.Inventory.AddRange(body.container.CloneContents().Where(c=>c is not ItemContent item || item.state is not ContentStateHolder));
        foreach(var holder in body.holders)
            foreach(var item in holder.items)
                snapshot.Inventory.Add(new ItemContent(item,new ContentStateHolder(holder.name),item.contentCustomData).CloneGeneric());
        foreach(var hand in new[]{body.handLeft,body.handRight})
        {
            var item=hand.grabbedHandle?.item;if(item==null || item.worldAttached) continue;
            if(!heldItems.TryGetValue(item,out var held))
            {
                held=new TravelLoadout.Held{Content=new ItemContent(item,null,item.contentCustomData).CloneGeneric(),Owner=item.owner};snapshot.Items.Add(held);
                heldItems.Add(item,held);
            }
            held.Hands.Add((hand.side,item.handles.IndexOf(hand.grabbedHandle!),hand.grabbedHandle!.orientations.IndexOf(hand.gripInfo?.orientation!),hand.gripInfo?.axisPosition ?? 0));
        }
        travelLoadout=snapshot;
        Log?.Invoke("Captured local travel equipment: "+snapshot.Items.Count+" held item(s), "+snapshot.Inventory.Count+" inventory/holster entries.");
    }
    internal void PrepareTravelPlayerInventory()
    {
        if(travelLoadout==null || Player.characterData==null) return;
        Player.characterData.inventory.SetPlayerInventory(Container.CloneContents(travelLoadout.Inventory));
    }
    private bool RestoreTravelLoadout()
    {
        var snapshot=travelLoadout;if(snapshot==null) return true;
        var body=Player.local?.creature;if(body==null || !body.loaded) return false;
        if(!snapshot.Restoring)
        {
            snapshot.Restoring=true;snapshot.Body=body;snapshot.Deadline=Now+45;
            snapshot.Pending=snapshot.Items.Count;
            int generation=Generation;
            snapshot.RestoreGeneration=generation;
            foreach(var held in snapshot.Items)
            {
                if(!Catalog.TryGetData<ItemData>(held.Content.referenceID,out _,false)){snapshot.Pending--;Log?.Invoke("Travel item unavailable: "+held.Content.referenceID);continue;}
                held.Content.Spawn(item=>
                {
                    if(snapshot.RestoreGeneration!=generation){if(item!=null)item.Despawn();return;}
                    snapshot.Pending--;
                    if(item==null) return;
                    if(disposed || generation!=Generation || Player.local?.creature!=body){item.Despawn();return;}
                    item.transform.position=Player.local.transform.position+Vector3.up;
                    foreach(var grip in held.Hands)
                    {
                        var hand=body.GetHand(grip.Side);
                        var handle=item.handles.ElementAtOrDefault(grip.Handle) ?? item.GetMainHandle(grip.Side);
                        if(handle==null) continue;
                        hand.TryRelease();hand.Grab(handle,handle.orientations.ElementAtOrDefault(grip.Orientation) ?? handle.GetDefaultOrientation(grip.Side),grip.Axis,true);
                    }
                },held.Owner,false);
            }
        }
        if(snapshot.Pending>0 || body.equipment.GetPendingApparelLoading()>0)
        {
            if(Now>snapshot.Deadline) FailScene("Restoring local equipment timed out.");
            return false;
        }
        travelLoadout=null;return true;
    }
    private void PreservePendingTravelInventory()
    {
        if(travelLoadout==null || Player.characterData==null) return;
        // A failed load must not discard weapons waiting for the new hand bodies.
        var contents=Container.CloneContents(travelLoadout.Inventory);
        contents.AddRange(travelLoadout.Items.Select(h=>(ContainerContent)h.Content.CloneGeneric()));
        Player.characterData.inventory.SetPlayerInventory(contents);travelLoadout=null;
    }
    internal bool CarriedByOtherPlayer(Item item)
        =>item.handlers.Any(h=>h.creature!=Player.local?.creature && Find(h.creature)?.Descriptor.Kind==BladeAndSorcery.Multiplayer.Core.EntityKind.Player) ||
          item.holder?.GetRootHolder().creature is { } creature && creature!=Player.local?.creature && Find(creature)?.Descriptor.Kind==BladeAndSorcery.Multiplayer.Core.EntityKind.Player;
}

[HarmonyPatch(typeof(PlayerSpawner),nameof(PlayerSpawner.SpawnCoroutine))]
internal static class SharedTravelPlayerInventoryPatch
{
    private static void Prefix()=>MultiplayerRuntime.Instance?.World?.PrepareTravelPlayerInventory();
}

// A client's Home world belongs to the session. Saving it into that client's
// personal stash would overwrite their own Home with the host's furniture/loot.
[HarmonyPatch(typeof(ContainerSaver),nameof(ContainerSaver.Save))]
internal static class SharedHomeSavePatch
{
    private static bool Prefix()
    {
        var world=MultiplayerRuntime.Instance?.World;
        if(world==null || world.Session.IsHost) return true;
        if(Player.local?.creature!=null) Player.characterData.SaveCoroutine(true).AsSynchronous();
        return false;
    }
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var all=AccessTools.Field(typeof(Item),nameof(Item.all));
        foreach(var instruction in instructions)
        {
            if(instruction.LoadsField(all)){instruction.opcode=OpCodes.Call;instruction.operand=AccessTools.Method(typeof(SharedHomeSavePatch),nameof(PersistentWorldItems));}
            yield return instruction;
        }
    }
    private static List<Item> PersistentWorldItems()
    {
        var world=MultiplayerRuntime.Instance?.World;
        return world==null ? Item.all : Item.all.Where(i=>i!=null && !world.CarriedByOtherPlayer(i)).ToList();
    }
}

[HarmonyPatch(typeof(Container),"LoadContents")]
internal static class SharedHomePlacedContentsPatch
{
    private static void Prefix(Container __instance,out List<ContainerContent>? __state)
    {
        __state=null;var world=MultiplayerRuntime.Instance?.World;
        if(world==null || world.Session.IsHost || __instance.creature!=null) return;
        // Personal inventory stays local; only placed/holstered world instances
        // are supplied by the host's physical object registry.
        __state=__instance.contents.Where(c=>c is ItemContent item && (item.state is ContentStatePlaced || item.state is ContentStateHolder)).ToList();
        foreach(var content in __state) __instance.contents.Remove(content);
    }
    private static void Postfix(Container __instance,List<ContainerContent>? __state)
    {if(__state!=null) __instance.contents.AddRange(__state);}
}
