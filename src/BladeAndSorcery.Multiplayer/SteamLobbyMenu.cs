using System;
using System.Linq;
using Steamworks;
using ThunderRoad;

namespace BladeAndSorcery.Multiplayer;

public sealed partial class MultiplayerRuntime
{
    private SteamLobbyService? steamLobbies;
    private string steamUnavailable="Waiting for the game's Steam connection.";
    private double nextSteamInit;
    private int steamMenuRevision=-1;
    private bool refreshingSteamVisibility;
    private CSteamID steamSelection;
    private bool steamStopping;
    private void RunSteamAction(Action<SteamLobbyService> action)
    {
        if(!EnsureSteamLobbies(true))return;
        try {action(steamLobbies!);}
        catch(Exception error)
        {
            if(Session?.TransportName=="Steam P2P")Disconnect();else steamLobbies!.Leave();
            steamLobbies!.ReportError("Steam action failed: "+error.Message);
        }
    }
    private bool EnsureSteamLobbies(bool report)
    {
        if(steamStopping)return false;
        if(steamLobbies!=null)return true;
        try {steamLobbies=new SteamLobbyService(this);steamMenuRevision=-1;return true;}
        catch(Exception error) when(error is InvalidOperationException || error is DllNotFoundException || error is EntryPointNotFoundException || error is System.IO.IOException)
        {steamUnavailable="Steam unavailable: "+error.Message;if(report)WriteLog(steamUnavailable);return false;}
    }
    private void TickSteamLobbies()
    {
        if(steamStopping)return;
        try
        {
            if(steamLobbies==null && UnityEngine.Time.realtimeSinceStartupAsDouble>=nextSteamInit)
            {nextSteamInit=UnityEngine.Time.realtimeSinceStartupAsDouble+5;EnsureSteamLobbies(false);}
            steamLobbies?.Poll();
        }
        catch(Exception error)
        {
            WriteLog("Steam session error: "+error.Message);
            if(Session?.TransportName=="Steam P2P")Disconnect();
            else steamLobbies?.Leave();
        }
    }
    [ModOption("Steam status",saveValue=false,applyOnLoad=false)]
    [ModOptionTextDisplay("Steam lobbies use your game's existing Steam connection.","")]
    [ModOptionCategory("Steam",1),ModOptionOrder(-3)]
    public static string SteamStatus="Steam lobbies use your game's existing Steam connection.";
    public static ModOptionInt[] SteamVisibilityChoices()=>new[]{new ModOptionInt("Public",0),new ModOptionInt("Friends only",1),new ModOptionInt("Invite only",2)};
    [ModOption("Steam visibility (next lobby)",valueSourceName=nameof(SteamVisibilityChoices),defaultValueIndex=1,saveValue=false,applyOnLoad=false)]
    [ModOptionCategory("Steam",1),ModOptionOrder(-2)]
    public static void SteamVisibilityChoice(int value=1)
    {
        var runtime=Instance;if(runtime==null || runtime.refreshingSteamVisibility || value<0 || value>2)return;
        int previous=runtime.Configuration.SteamLobbyVisibility;runtime.Configuration.SteamLobbyVisibility=value;
        if(!runtime.SaveConnectionSettings())runtime.Configuration.SteamLobbyVisibility=previous;
    }
    [ModOption("Host Steam lobby",valueSourceName=nameof(PressAction),saveValue=false,applyOnLoad=false)]
    [ModOptionButton,ModOptionCategory("Steam",1),ModOptionOrder(-1)]
    public static void HostSteamAction(int press=0)=>Instance?.RunSteamAction(service=>service.Host());
    [ModOption("Refresh Steam lobbies",valueSourceName=nameof(PressAction),saveValue=false,applyOnLoad=false)]
    [ModOptionButton,ModOptionCategory("Steam",1),ModOptionOrder(0)]
    public static void RefreshSteamAction(int press=0)=>Instance?.RunSteamAction(service=>service.Refresh());
    public static ModOptionInt[] SteamLobbyChoices()
    {
        var rows=Instance?.steamLobbies?.Results;
        return rows==null || rows.Count==0 ? new[]{new ModOptionInt("No lobbies - press Refresh",0)} :
            rows.Select((row,i)=>new ModOptionInt((row.Friend ? "Friend: " : "")+row.Name+" "+row.Members+"/"+row.Limit+(row.Compatible ? "" : " [different build]"),i)).ToArray();
    }
    [ModOption("Selected Steam lobby",valueSourceName=nameof(SteamLobbyChoices),saveValue=false,applyOnLoad=false)]
    [ModOptionCategory("Steam",1),ModOptionOrder(1)]
    public static void SelectSteamLobby(int index=0)
    {
        if(Instance==null)return;
        var rows=Instance.steamLobbies?.Results;
        Instance.steamSelection=rows!=null && index>=0 && index<rows.Count ? rows[index].Id : default;
    }
    [ModOption("Selected lobby details",saveValue=false,applyOnLoad=false)]
    [ModOptionTextDisplay("Select a lobby after refreshing.","")]
    [ModOptionCategory("Steam",1),ModOptionOrder(2)]
    public static string SteamLobbyDetails="Select a lobby after refreshing.";
    [ModOption("Join selected Steam lobby",valueSourceName=nameof(PressAction),saveValue=false,applyOnLoad=false)]
    [ModOptionButton,ModOptionCategory("Steam",1),ModOptionOrder(3)]
    public static void JoinSteamAction(int press=0)
    {
        Instance?.RunSteamAction(service=>service.Join(Instance.steamSelection));
    }
    [ModOption("Invite Steam friends",valueSourceName=nameof(PressAction),saveValue=false,applyOnLoad=false)]
    [ModOptionButton,ModOptionCategory("Steam",1),ModOptionOrder(4)]
    public static void InviteSteamAction(int press=0)=>Instance?.RunSteamAction(service=>service.InviteFriends());
    [ModOption("Join invited Steam lobby",valueSourceName=nameof(PressAction),saveValue=false,applyOnLoad=false)]
    [ModOptionButton,ModOptionCategory("Steam",1),ModOptionOrder(5)]
    public static void JoinSteamInviteAction(int press=0)=>Instance?.RunSteamAction(service=>service.JoinInvited());
    private void RefreshSteamMenu()
    {
        SteamStatus=steamLobbies?.Status ?? steamUnavailable;RefreshNativeText("Steam status",SteamStatus);
        var row=steamLobbies?.Results.FirstOrDefault(r=>r.Id==steamSelection);
        SteamLobbyDetails=row==null ? "Refresh, select a lobby, then press Join selected Steam lobby. Set a required password in Player & hosting first." :
            row.Map+" / "+row.Mode+" / "+row.Visibility+" / "+row.Members+" of "+row.Limit+" / "+(row.Password ? "Password required" : "No password")+" / "+(row.Compatible ? "Compatible" : "Different game/mod build");
        RefreshNativeText("Selected lobby details",SteamLobbyDetails);
        refreshingSteamVisibility=true;
        try
        {
            foreach(var option in ModManager.loadedMods.Where(m=>m.fullPath==ModFolder).SelectMany(m=>m.modOptions))
            {
                if(option.parameterValues==null || option.parameterValues.Length==0)continue;
                if(option.name=="Steam visibility (next lobby)" && option.currentValueIndex!=Configuration.SteamLobbyVisibility)
                {option.Apply(Configuration.SteamLobbyVisibility);option.uiComponent?.Reload();}
                if(option.name=="Selected Steam lobby" && steamLobbies!=null && steamMenuRevision!=steamLobbies.Revision)
                {
                    int index=Math.Max(0,steamLobbies.Results.FindIndex(r=>r.Id==steamSelection));
                    option.parameterValues=SteamLobbyChoices();option.Apply(index);option.uiComponent?.Reload();
                }
            }
            steamMenuRevision=steamLobbies?.Revision ?? -1;
        }
        finally{refreshingSteamVisibility=false;}
    }
    internal object SteamStatusSnapshot()=>new
    {
        available=steamLobbies!=null,status=steamLobbies?.Status ?? steamUnavailable,
        lobby=steamLobbies?.Current.m_SteamID.ToString() ?? "0",hosting=steamLobbies?.Hosting ?? false,busy=steamLobbies?.Busy ?? false,
        transport=Session?.TransportName,connected=Session?.Connected ?? false,
        results=steamLobbies?.Results.Select(r=>new{id=r.Id.m_SteamID.ToString(),name=r.Name,map=r.Map,mode=r.Mode,visibility=r.Visibility,members=r.Members,max=r.Limit,compatible=r.Compatible,passwordRequired=r.Password,friend=r.Friend}).ToArray()
    };
    internal void JoinSteamAutomation(ulong lobby)=>RunSteamAction(service=>service.Join(new CSteamID(lobby)));
    internal void BeforeNativeSteamShutdown()
    {
        // StoreSteam.Unload can run before the mod's OnDestroy. Close only our
        // resources while the game's API is still initialized.
        steamStopping=true;
        try {if(Session?.TransportName=="Steam P2P")Disconnect();}
        catch(Exception error){UnityEngine.Debug.LogWarning("[BASMP] Steam session shutdown cleanup: "+error.Message);}
        try {steamLobbies?.Dispose();}
        catch(Exception error){UnityEngine.Debug.LogWarning("[BASMP] Steam lobby shutdown cleanup: "+error.Message);}
        finally {steamLobbies=null;}
    }
}

internal static class NativeSteamShutdown
{
    internal static void Prefix()=>MultiplayerRuntime.Instance?.BeforeNativeSteamShutdown();
}
