using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Steamworks;
using ThunderRoad;

namespace BladeAndSorcery.Multiplayer;

internal sealed class SteamLobbyEntry
{
    internal CSteamID Id;
    internal string Name="",Map="",Mode="",Visibility="";
    internal int Members,Limit;
    internal bool Compatible,Password,Friend;
}

internal sealed class SteamLobbyService : IDisposable
{
    private const string Marker="basmp";
    private readonly MultiplayerRuntime runtime;
    private readonly CallResult<LobbyCreated_t> created;
    private readonly CallResult<LobbyEnter_t> entered;
    private readonly CallResult<LobbyMatchList_t> listed;
    private readonly Callback<GameLobbyJoinRequested_t> invitation;
    private readonly Callback<LobbyChatUpdate_t> membership;
    private readonly Callback<LobbyDataUpdate_t> data;
    private readonly Dictionary<string,string> metadata=new Dictionary<string,string>();
    internal readonly List<SteamLobbyEntry> Results=new List<SteamLobbyEntry>();
    internal CSteamID Current {get;private set;}
    internal CSteamID Invited {get;private set;}
    internal bool Hosting {get;private set;}
    internal bool Busy=>phase=="Creating" || phase=="Joining";
    internal int Revision {get;private set;}
    internal string Status {get;private set;}="Steam ready. Choose visibility and Host Steam lobby, or Refresh Steam lobbies.";
    private string phase="Offline";
    private CSteamID joining,originalHost;
    private bool createPending,joinPending,inviteAutomatic,disposed;
    private int hostedVisibility;
    private bool connectionAnnounced;
    private string closedReason="";
    private double deadline,nextMetadata;
    internal SteamLobbyService(MultiplayerRuntime runtime)
    {
        this.runtime=runtime;
        if(!SteamUser.BLoggedOn() || SteamUtils.GetAppID().m_AppId!=629730)throw new IOException("Steam must be online in Blade & Sorcery.");
        created=CallResult<LobbyCreated_t>.Create((value,failed)=>Guard(()=>OnCreated(value,failed)));entered=CallResult<LobbyEnter_t>.Create((value,failed)=>Guard(()=>OnEntered(value,failed)));listed=CallResult<LobbyMatchList_t>.Create((value,failed)=>Guard(()=>OnListed(value,failed)));
        invitation=Callback<GameLobbyJoinRequested_t>.Create(value=>Guard(()=>QueueInvite(value.m_steamIDLobby)));
        membership=Callback<LobbyChatUpdate_t>.Create(value=>Guard(()=>{if(value.m_ulSteamIDLobby==Current.m_SteamID)CheckOwner();}));
        data=Callback<LobbyDataUpdate_t>.Create(value=>Guard(()=>
        {
            if(value.m_bSuccess==0)return;
            var id=new CSteamID(value.m_ulSteamIDLobby);
            if(id==Current)CheckOwner();else AddResult(id,false);
        }));
        string? launch=MultiplayerRuntime.Argument("+connect_lobby");
        if(launch==null)
        {
            SteamApps.GetLaunchCommandLine(out string commandLine,2048);
            var args=commandLine.Split(new[]{' ','\t','"'},StringSplitOptions.RemoveEmptyEntries);
            for(int i=0;i+1<args.Length;i++)if(args[i]=="+connect_lobby")launch=args[i+1];
        }
        if(ulong.TryParse(launch,NumberStyles.None,CultureInfo.InvariantCulture,out ulong lobbyId))QueueInvite(new CSteamID(lobbyId));
    }
    private double Now=>UnityEngine.Time.realtimeSinceStartupAsDouble;
    private void Notice(string text){Status=text;Revision++;runtime.WriteLog(text);}
    private void Guard(Action action)
    {
        try{action();}
        catch(Exception error)
        {
            if(runtime.Session?.TransportName=="Steam P2P" || Current.IsValid() || Busy)runtime.Disconnect();else Leave();
            Notice("Steam lobby error: "+error.Message);
        }
    }
    internal void NoteConnectionClosed(string reason){closedReason=reason;Status=reason;Revision++;}
    internal void ReportError(string reason){Notice(reason);}
    private bool Playable=>Level.current?.data!=null && Player.local?.creature!=null && !LevelManager.isLoadingLocked;
    private bool CanStart()
    {
        if(runtime.Session!=null || Current.IsValid() || Busy){Notice("Leave the current session before starting a Steam lobby.");return false;}
        if(createPending || joinPending){Notice("Waiting for the previous Steam request to finish.");return false;}
        if(!Playable){Notice("Load a playable map before hosting or joining Steam multiplayer.");return false;}
        return true;
    }
    internal void Host()
    {
        if(!CanStart())return;
        if(!runtime.EnsureCompatibilityReady(Host))return;
        closedReason="";connectionAnnounced=false;Hosting=true;phase="Creating";createPending=true;deadline=Now+30;hostedVisibility=runtime.Configuration.SteamLobbyVisibility;
        var type=hostedVisibility==0 ? ELobbyType.k_ELobbyTypePublic :
            hostedVisibility==2 ? ELobbyType.k_ELobbyTypePrivate : ELobbyType.k_ELobbyTypeFriendsOnly;
        var call=SteamMatchmaking.CreateLobby(type,runtime.Configuration.MaxPlayers);
        if(call==SteamAPICall_t.Invalid){createPending=false;Hosting=false;phase="Offline";Notice("Steam could not start lobby creation. Check Steam connectivity.");return;}
        created.Set(call);Notice("Creating Steam lobby...");
    }
    private void OnCreated(LobbyCreated_t result,bool ioFailure)
    {
        createPending=false;var id=new CSteamID(result.m_ulSteamIDLobby);
        if(disposed || phase!="Creating") {if(!ioFailure && result.m_eResult==EResult.k_EResultOK)SteamMatchmaking.LeaveLobby(id);return;}
        if(ioFailure || result.m_eResult!=EResult.k_EResultOK){phase="Offline";Hosting=false;Notice("Steam lobby creation failed: "+result.m_eResult);return;}
        Current=id;originalHost=SteamUser.GetSteamID();SteamMatchmaking.SetLobbyJoinable(Current,false);
        runtime.Connect(true,new SteamSessionTransport(Current,runtime.Configuration.MaxPlayers));
        if(runtime.Session?.Connected!=true){Leave();Notice("Steam lobby could not start its network session. Check the connection log.");return;}
        phase="Active";connectionAnnounced=true;metadata.Clear();UpdateMetadata();
        if(!SteamMatchmaking.SetLobbyJoinable(Current,true))throw new IOException("Steam could not open lobby admission.");
        Notice("Steam lobby ready. Use Invite Steam friends, or let players find it in Refresh Steam lobbies.");
    }
    internal void Join(CSteamID id)
    {
        if(!CanStart())return;
        if(!runtime.EnsureCompatibilityReady(()=>Join(id)))return;
        if(!id.IsValid()){Notice("Select a Steam lobby first.");return;}
        closedReason="";connectionAnnounced=false;phase="Joining";Hosting=false;joining=id;joinPending=true;deadline=Now+30;
        var call=SteamMatchmaking.JoinLobby(id);
        if(call==SteamAPICall_t.Invalid){joinPending=false;phase="Offline";Notice("Steam could not start the lobby join. Check Steam connectivity.");return;}
        entered.Set(call);Notice("Joining Steam lobby...");
    }
    private void OnEntered(LobbyEnter_t result,bool ioFailure)
    {
        joinPending=false;var id=new CSteamID(result.m_ulSteamIDLobby);
        if(disposed || phase!="Joining" || id!=joining){if(!ioFailure && result.m_EChatRoomEnterResponse==(uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)SteamMatchmaking.LeaveLobby(id);return;}
        if(ioFailure || result.m_EChatRoomEnterResponse!=(uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
        {phase="Offline";Notice("Steam lobby join failed: "+(EChatRoomEnterResponse)result.m_EChatRoomEnterResponse);return;}
        Current=id;
        if(SteamMatchmaking.GetLobbyData(Current,Marker)!="1" || SteamMatchmaking.GetLobbyData(Current,"basmp_schema")!="1" ||
            SteamMatchmaking.GetLobbyData(Current,"basmp_build")!=runtime.GameBuildIdentity)
        {Leave();Notice("This lobby uses a different game/mod build. Install the same version on both PCs.");return;}
        originalHost=SteamMatchmaking.GetLobbyOwner(Current);
        if(SteamMatchmaking.GetLobbyData(Current,"basmp_host")!=originalHost.m_SteamID.ToString(CultureInfo.InvariantCulture))
        {Leave();Notice("The Steam lobby host left or changed. Refresh the lobby list.");return;}
        if(SteamMatchmaking.GetLobbyData(Current,"basmp_password")=="1" && runtime.Configuration.Password.Length==0)
        {Invited=Current;inviteAutomatic=false;Leave();Notice("Password required. Set Edit server password, then select Join invited Steam lobby.");return;}
        runtime.Connect(false,new SteamSessionTransport(Current,8),originalHost.m_SteamID.ToString(CultureInfo.InvariantCulture));
        if(runtime.Session==null){Leave();Notice("Could not start the Steam connection.");return;}
        phase="Active";deadline=Now+30;Notice("Steam lobby joined; authenticating with its host...");
    }
    internal void Refresh()
    {
        Results.Clear();Revision++;
        SteamMatchmaking.AddRequestLobbyListStringFilter(Marker,"1",ELobbyComparison.k_ELobbyComparisonEqual);
        SteamMatchmaking.AddRequestLobbyListDistanceFilter(ELobbyDistanceFilter.k_ELobbyDistanceFilterWorldwide);
        SteamMatchmaking.AddRequestLobbyListResultCountFilter(50);
        var call=SteamMatchmaking.RequestLobbyList();
        if(call==SteamAPICall_t.Invalid){Notice("Steam could not start lobby discovery. Check Steam connectivity.");return;}
        listed.Set(call);
        for(int i=0;i<SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);i++)
        {
            var friend=SteamFriends.GetFriendByIndex(i,EFriendFlags.k_EFriendFlagImmediate);
            if(!SteamFriends.GetFriendGamePlayed(friend,out var game) || game.m_gameID.AppID().m_AppId!=629730 || !game.m_steamIDLobby.IsValid())continue;
            AddResult(game.m_steamIDLobby,true);SteamMatchmaking.RequestLobbyData(game.m_steamIDLobby);
        }
        Notice("Searching public and friends' Steam lobbies...");
    }
    private void OnListed(LobbyMatchList_t list,bool ioFailure)
    {
        if(disposed)return;
        if(ioFailure){Notice("Steam lobby search failed. Check Steam connectivity.");return;}
        for(int i=0;i<Math.Min(list.m_nLobbiesMatching,50u);i++)AddResult(SteamMatchmaking.GetLobbyByIndex(i),false);
        Notice(Results.Count+" Steam lobbies found. Select one, then Join selected Steam lobby.");
    }
    private void AddResult(CSteamID id,bool friend)
    {
        if(!id.IsValid() || SteamMatchmaking.GetLobbyData(id,Marker)!="1" || Results.Count>=50 && Results.All(r=>r.Id!=id))return;
        var entry=Results.FirstOrDefault(r=>r.Id==id);
        if(entry==null){entry=new SteamLobbyEntry{Id=id};Results.Add(entry);}
        entry.Friend|=friend;entry.Name=Clean(SteamMatchmaking.GetLobbyData(id,"basmp_name"),48);
        entry.Map=Clean(SteamMatchmaking.GetLobbyData(id,"basmp_map"),40);entry.Mode=Clean(SteamMatchmaking.GetLobbyData(id,"basmp_mode"),24);
        entry.Visibility=Clean(SteamMatchmaking.GetLobbyData(id,"basmp_visibility"),20);entry.Members=SteamMatchmaking.GetNumLobbyMembers(id);entry.Limit=SteamMatchmaking.GetLobbyMemberLimit(id);
        entry.Password=SteamMatchmaking.GetLobbyData(id,"basmp_password")=="1";
        entry.Compatible=SteamMatchmaking.GetLobbyData(id,"basmp_schema")=="1" && SteamMatchmaking.GetLobbyData(id,"basmp_build")==runtime.GameBuildIdentity;
        Revision++;
    }
    internal static string Clean(string text,int max)=>new string(text.Where(c=>!char.IsControl(c) && c!='<' && c!='>').Take(max).ToArray());
    internal void QueueInvite(CSteamID id){if(!id.IsValid())return;Invited=id;inviteAutomatic=true;Notice("Steam invitation received. Load a map; leave any current session before joining.");}
    internal void JoinInvited(){inviteAutomatic=false;Join(Invited);}
    internal void InviteFriends()
    {
        if(!Current.IsValid() || runtime.Session?.Connected!=true){Notice("Host or join a Steam lobby first.");return;}
        if(!SteamUtils.IsOverlayEnabled()){Notice("Enable the Steam overlay to open its invitation dialog.");return;}
        SteamFriends.ActivateGameOverlayInviteDialog(Current);
    }
    private void CheckOwner()
    {
        if(!Current.IsValid())return;
        bool present=false;
        for(int i=0;i<SteamMatchmaking.GetNumLobbyMembers(Current);i++)if(SteamMatchmaking.GetLobbyMemberByIndex(Current,i)==SteamUser.GetSteamID())present=true;
        if(!present || SteamMatchmaking.GetLobbyOwner(Current)!=originalHost || SteamMatchmaking.GetLobbyData(Current,"basmp_host")!=originalHost.m_SteamID.ToString(CultureInfo.InvariantCulture))
        {runtime.Disconnect();Notice("Steam host left. Start or join another lobby; host migration is not supported.");}
    }
    private void UpdateMetadata()
    {
        var values=new Dictionary<string,string>{{Marker,"1"},{"basmp_schema","1"},{"basmp_build",runtime.GameBuildIdentity},
            {"basmp_host",originalHost.m_SteamID.ToString(CultureInfo.InvariantCulture)},{"basmp_name",Clean(runtime.Configuration.PlayerName,32)+"'s lobby"},
            {"basmp_map",Level.current?.data?.id ?? "Loading"},{"basmp_mode",runtime.World?.Match.Mode.ToString() ?? "Arena"},
            {"basmp_password",runtime.Configuration.Password.Length>0 ? "1" : "0"},
            {"basmp_visibility",hostedVisibility==0 ? "Public" : hostedVisibility==2 ? "Invite only" : "Friends only"}};
        foreach(var value in values)if(!metadata.TryGetValue(value.Key,out var previous) || previous!=value.Value)
        {
            if(!SteamMatchmaking.SetLobbyData(Current,value.Key,value.Value))throw new IOException("Steam lobby metadata update failed: "+value.Key);
            metadata[value.Key]=value.Value;
        }
    }
    internal void Poll()
    {
        if(disposed)return;
        if(phase=="Active" && !connectionAnnounced && runtime.Session?.Connected==true)
        {connectionAnnounced=true;Notice("Connected through Steam. Leave session disconnects; Invite Steam friends opens the overlay.");}
        if((Busy || phase=="Active" && runtime.Session?.Connected==false) && Now>deadline)
        {runtime.Disconnect();Notice("Steam request timed out. Check Steam connectivity, refresh, and try again.");return;}
        if(inviteAutomatic && Invited.IsValid() && runtime.Session==null && !Busy && Playable)
        {inviteAutomatic=false;Join(Invited);}
        if(phase=="Active" && Current.IsValid() && Now>=nextMetadata)
        {
            nextMetadata=Now+2;
            if(!SteamUser.BLoggedOn()){runtime.Disconnect();Notice("Steam went offline. Reconnect to Steam before joining again.");return;}
            CheckOwner();if(Hosting && Current.IsValid())UpdateMetadata();
        }
    }
    internal void Leave()
    {
        bool wasActive=Current.IsValid() || Busy;
        if(Current.IsValid())
        {
            if(Hosting)SteamMatchmaking.SetLobbyJoinable(Current,false);
            SteamMatchmaking.LeaveLobby(Current);
        }
        Current=default;joining=default;Hosting=false;phase="Offline";metadata.Clear();Revision++;
        if(wasActive)Status=closedReason.Length>0 ? closedReason : "Steam lobby left. Host a lobby or refresh to join another.";
    }
    public void Dispose()
    {
        if(disposed)return;Leave();disposed=true;created.Dispose();entered.Dispose();listed.Dispose();invitation.Dispose();membership.Dispose();data.Dispose();
    }
}
