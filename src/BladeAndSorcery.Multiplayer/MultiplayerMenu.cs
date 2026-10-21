using System;
using System.Linq;
using BladeAndSorcery.Multiplayer.Core;
using HarmonyLib;
using ThunderRoad;
using UnityEngine;

namespace BladeAndSorcery.Multiplayer;

public sealed partial class MultiplayerRuntime
{
    internal static readonly string[] MenuTabs={"Session","Steam","Direct IP","Player & hosting","Play","Diagnostics"};
    internal int SelectedMenuTab;
    private bool refreshingMatchOptions;
    public static ModOptionInt[] MultiplayerTabChoices()=>MenuTabs.Select((name,index)=>new ModOptionInt(name,index)).ToArray();
    [ModOption("Multiplayer tab",tooltip="Use the arrows to switch pages. Connection status stays visible on every page.",valueSourceName=nameof(MultiplayerTabChoices),saveValue=false,applyOnLoad=false)]
    [ModOptionOrder(-100)]
    public static void MenuTab(int page=0)
    {
        if(Instance==null || page<0 || page>=MenuTabs.Length)return;
        Instance.SelectedMenuTab=page;RefreshMultiplayerPages();
    }
    internal static void RefreshMultiplayerPages()
    {
        var runtime=Instance;if(runtime==null)return;
        foreach(var area in UnityEngine.Object.FindObjectsOfType<UIModsMenuContentArea>(true).Where(a=>a.ModData?.fullPath==ModFolder))
        {
            foreach(var category in area.OptionsListGroup.GetComponentsInChildren<UIExpandableList>(true))
            {
                if(category.Elements==null)continue; // Native uninitialized template.
                bool active=category.gameObject.name==MenuTabs[runtime.SelectedMenuTab];
                if(active)category.ExpandList();else category.CollapseList();
                // The page selector replaces a second layer of collapsible headings.
                category.gameObject.SetActive(false);
            }
            foreach(var option in area.ModData.modOptions.Where(o=>o.uiComponent!=null))
                if(option.uiComponent is Component component)component.gameObject.SetActive(string.IsNullOrEmpty(option.category) || option.category==MenuTabs[runtime.SelectedMenuTab]);
        }
    }
    [ModOption("Getting started",saveValue=false,applyOnLoad=false)]
    [ModOptionTextDisplay("Choose Steam for lobbies/invitations, or Direct IP for an address and UDP port. Set your name, password and player limit under Player & hosting before connecting. Once joined, choose maps and equipment normally; use the normal wave book for co-op.","")]
    [ModOptionCategory("Session",0),ModOptionOrder(0)]
    public static string SessionGuide="";
    [ModOption("Players",saveValue=false,applyOnLoad=false)]
    [ModOptionTextDisplay("No session.","")]
    [ModOptionCategory("Session",0),ModOptionOrder(1)]
    public static string SessionPlayers="No session.";
    [ModOption("Saved profile",saveValue=false,applyOnLoad=false)]
    [ModOptionTextDisplay("Your name, password and host player limit apply to either connection method.","")]
    [ModOptionCategory("Player & hosting",3),ModOptionOrder(-1)]
    public static string SavedProfile="";
    [ModOption("Co-op & travel",saveValue=false,applyOnLoad=false)]
    [ModOptionTextDisplay("Either player can select a map normally. Your held weapons, holsters, armor and supported inventory travel with you. Use the normal map wave book to select/start/stop co-op; team protection is automatic while a native wave runs.","")]
    [ModOptionCategory("Play",4),ModOptionOrder(-2)]
    public static string NativePlayGuide="";
    [ModOption("Native wave",saveValue=false,applyOnLoad=false)]
    [ModOptionTextDisplay("No native wave running. Use the normal wave book on the map.","")]
    [ModOptionCategory("Play",4),ModOptionOrder(0)]
    public static string NativeWaveStatus="";
    [ModOption("Friendly fire (host)",tooltip="Allow teammates to hurt one another in co-op and Teams. Free for all always allows PvP.",saveValue=false,applyOnLoad=false)]
    [ModOptionCategory("Play",4),ModOptionOrder(3)]
    public static void FriendlyFireOption(bool enabled=false)
    {
        var runtime=Instance;
        if(runtime==null || runtime.refreshingMatchOptions)return;
        if(runtime.World==null || runtime.Session?.IsHost!=true){runtime.connectionNotice="Host a session to change shared match rules.";return;}
        runtime.World.Match.FriendlyFire=enabled;runtime.World.Match.Broadcast();
    }
    private void RefreshUnifiedMenu()
    {
        SessionPlayers=Session?.Connected==true ? string.Join("\n",Session.Peers.Values.Select(p=>p.Name+(p.Id==Session.LocalId ? " (you)" : "")+(p.Id==1 ? " — host" : ""))) : "No session. Open Steam or Direct IP to connect.";
        SavedProfile=Configuration.PlayerName+" | host limit "+Configuration.MaxPlayers+" | "+(Configuration.Password.Length>0 ? "password saved" : "no password")+". Leave before editing connection settings.";
        NativeWaveStatus=World?.NativeWaveMenuStatus() ?? "Connect first, then use the map's normal wave book.";
        RefreshNativeText("Players",SessionPlayers);RefreshNativeText("Saved profile",SavedProfile);RefreshNativeText("Native wave",NativeWaveStatus);
        refreshingMatchOptions=true;
        try
        {
            foreach(var option in ModManager.loadedMods.Where(m=>m.fullPath==ModFolder).SelectMany(m=>m.modOptions))
            {
                int? index=option.name=="Game mode (host)" && World!=null ? (int)World.Match.Mode : option.name=="Friendly fire (host)" && World!=null ? (World.Match.FriendlyFire ? 1 : 0) : (int?)null;
                if(index.HasValue && option.currentValueIndex!=index.Value){option.Apply(index.Value);option.uiComponent?.Reload();}
            }
        }
        finally{refreshingMatchOptions=false;}
    }
}

internal static class NativeMultiplayerPages
{
    internal static void Postfix(UIModsMenuContentArea __instance)
    {if(__instance.ModData?.fullPath==MultiplayerRuntime.ModFolder)MultiplayerRuntime.RefreshMultiplayerPages();}
}

internal sealed partial class GameWorld
{
    internal string NativeWaveMenuStatus()
    {
        var state=nativeWaves.Values.FirstOrDefault(w=>w.Running) ?? nativeWaves.Values.FirstOrDefault(w=>w.Run>0);
        return state==null ? "No native wave selected. Use the normal wave book on this map." :
            state.Wave+" | "+(state.Running ? state.Alive+" alive, "+state.Queued+" waiting, "+state.Kills+" defeated" : state.Outcome==1 ? "complete — "+state.Kills+" defeated" : "stopped")+". Controls are in the normal wave book.";
    }
}
