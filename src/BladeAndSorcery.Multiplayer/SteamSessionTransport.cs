using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using BladeAndSorcery.Multiplayer.Core;
using Steamworks;

namespace BladeAndSorcery.Multiplayer;

internal sealed class SteamSessionTransport : ISessionTransport
{
    internal const int VirtualPort=73;
    private sealed class Peer : ISessionPeer
    {
        private readonly SteamSessionTransport transport;
        internal readonly HSteamNetConnection Handle;
        internal readonly CSteamID User;
        internal bool Announced;
        internal bool Accepted;
        internal readonly double Created=UnityEngine.Time.realtimeSinceStartupAsDouble;
        public int Ping {get;internal set;}
        internal Peer(SteamSessionTransport transport,HSteamNetConnection handle,CSteamID user){this.transport=transport;Handle=handle;User=user;}
        public void Send(byte[] bytes,byte channel,bool reliable)
        {
            if(!transport.peers.ContainsKey(Handle))return;
            var pinned=GCHandle.Alloc(bytes,GCHandleType.Pinned);
            EResult result;
            try {result=SteamNetworkingSockets.SendMessageToConnection(Handle,pinned.AddrOfPinnedObject(),(uint)bytes.Length,
                reliable ? Constants.k_nSteamNetworkingSend_ReliableNoNagle : Constants.k_nSteamNetworkingSend_UnreliableNoDelay,out _);}
            finally {pinned.Free();}
            if(result!=EResult.k_EResultOK && reliable)transport.Close(this,"Steam reliable send failed: "+result);
        }
        public void Disconnect()=>transport.Close(this,"Session closed");
    }
    private readonly CSteamID lobby;
    private readonly int limit;
    private readonly Dictionary<HSteamNetConnection,Peer> peers=new Dictionary<HSteamNetConnection,Peer>();
    private Callback<SteamNetConnectionStatusChangedCallback_t>? status;
    private HSteamListenSocket listen;
    private bool host,disposed;
    private CSteamID expectedHost;
    private double nextPing;
    public string Name=>"Steam P2P";
    public bool PasswordChallenge=>true;
    public int LocalPort=>0;
    public Func<bool>? CanAccept {get;set;}
    public event Action<ISessionPeer>? Connected;
    public event Action<ISessionPeer,string>? Disconnected;
    public event Action<ISessionPeer,byte[]>? Received;
    public event Action<string>? Error;
    internal SteamSessionTransport(CSteamID lobby,int maxPlayers){this.lobby=lobby;limit=maxPlayers;}
    private void Start()
    {
        if(!SteamUser.BLoggedOn() || SteamUtils.GetAppID().m_AppId!=629730)throw new IOException("Steam must be online with Blade & Sorcery's normal AppID.");
        SteamNetworkingUtils.InitRelayNetworkAccess();SteamNetworkingSockets.InitAuthentication();
        status=Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnConnectionChanged);
    }
    public void Host(int port,string key)
    {
        Start();host=true;
        listen=SteamNetworkingSockets.CreateListenSocketP2P(VirtualPort,0,Array.Empty<SteamNetworkingConfigValue_t>());
        if(listen==HSteamListenSocket.Invalid)throw new IOException("Steam could not create the P2P listener under this game's AppID.");
    }
    public void Join(string address,int port,string key)
    {
        Start();host=false;
        if(!ulong.TryParse(address,out ulong id))throw new IOException("Invalid Steam host identity.");
        expectedHost=new CSteamID(id);
        if(expectedHost==SteamUser.GetSteamID())throw new IOException("Steam peers need separate Steam accounts; use Direct IP for two local instances.");
        var identity=new SteamNetworkingIdentity();identity.SetSteamID(expectedHost);
        var handle=SteamNetworkingSockets.ConnectP2P(ref identity,VirtualPort,0,Array.Empty<SteamNetworkingConfigValue_t>());
        if(handle==HSteamNetConnection.Invalid)throw new IOException("Steam could not start the P2P connection.");
        peers[handle]=new Peer(this,handle,expectedHost){Accepted=true};
    }
    private bool IsMember(CSteamID user)
    {
        for(int i=0;i<SteamMatchmaking.GetNumLobbyMembers(lobby);i++)if(SteamMatchmaking.GetLobbyMemberByIndex(lobby,i)==user)return true;
        return false;
    }
    private void OnConnectionChanged(SteamNetConnectionStatusChangedCallback_t change)
    {
        if(disposed)return;
        if(!peers.TryGetValue(change.m_hConn,out var peer))
        {
            // Do not consume callbacks belonging to other native Steam users.
            if(!host || change.m_info.m_hListenSocket!=listen)return;
            var user=change.m_info.m_identityRemote.GetSteamID();
            if(change.m_info.m_eState!=ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting)return;
            if(!user.IsValid() || CanAccept?.Invoke()!=true || peers.Count>=limit-1 || peers.Values.Any(p=>p.User==user))
            {SteamNetworkingSockets.CloseConnection(change.m_hConn,1001,"Lobby membership or capacity rejected",false);return;}
            peer=new Peer(this,change.m_hConn,user);peers[change.m_hConn]=peer;
            // Lobby and networking callbacks can arrive in either order. Wait
            // briefly for the native membership cache instead of rejecting a
            // legitimate join which reached the socket callback first.
            if(IsMember(user) && !Accept(peer))return;
        }
        if(change.m_info.m_eState==ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected)
        {
            if(!IsMember(peer.User) || (!host && peer.User!=expectedHost)){Close(peer,"Steam lobby membership changed");return;}
            if(!peer.Announced){peer.Announced=true;Connected?.Invoke(peer);}
        }
        else if(change.m_info.m_eState==ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer ||
            change.m_info.m_eState==ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally)
            Close(peer,"Steam connection ended ("+change.m_info.m_eEndReason+")");
    }
    private void Close(Peer peer,string reason)
    {
        if(!peers.Remove(peer.Handle))return;
        SteamNetworkingSockets.CloseConnection(peer.Handle,1000,reason,true);
        Disconnected?.Invoke(peer,reason);
    }
    private bool Accept(Peer peer)
    {
        if(SteamNetworkingSockets.AcceptConnection(peer.Handle)!=EResult.k_EResultOK){Close(peer,"Steam connection could not be accepted");return false;}
        peer.Accepted=true;return true;
    }
    public void Poll()
    {
        foreach(var peer in peers.Values.ToArray())
        {
            if(host && !peer.Accepted)
            {
                if(IsMember(peer.User))Accept(peer);
                else if(UnityEngine.Time.realtimeSinceStartupAsDouble-peer.Created>1.5)Close(peer,"Steam lobby membership was not confirmed");
                continue;
            }
            if(!peer.Announced)continue;
            var messages=new IntPtr[64];
            int count=SteamNetworkingSockets.ReceiveMessagesOnConnection(peer.Handle,messages,messages.Length);
            if(count<0){Close(peer,"Steam receive failed");continue;}
            try
            {
                for(int i=0;i<count;i++)
                {
                    var message=SteamNetworkingMessage_t.FromIntPtr(messages[i]);
                    if(message.m_cbSize<0 || message.m_cbSize>Protocol.MaxPacket){Error?.Invoke("Rejected oversized Steam message");Close(peer,"Invalid packet size");break;}
                    var bytes=new byte[message.m_cbSize];Marshal.Copy(message.m_pData,bytes,0,bytes.Length);Received?.Invoke(peer,bytes);
                }
            }
            finally {for(int i=0;i<count;i++)SteamNetworkingMessage_t.Release(messages[i]);}
            if(UnityEngine.Time.realtimeSinceStartupAsDouble>=nextPing)
            {
                if(!IsMember(peer.User)){Close(peer,"Peer left the Steam lobby");continue;}
                var info=new SteamNetConnectionRealTimeStatus_t{reserved=new uint[15]};var lane=new SteamNetConnectionRealTimeLaneStatus_t{reserved=new uint[10]};
                if(SteamNetworkingSockets.GetConnectionRealTimeStatus(peer.Handle,ref info,0,ref lane)==EResult.k_EResultOK)peer.Ping=info.m_nPing;
            }
        }
        if(UnityEngine.Time.realtimeSinceStartupAsDouble>=nextPing)nextPing=UnityEngine.Time.realtimeSinceStartupAsDouble+1;
    }
    public void SetImpairment(int delayMilliseconds,int lossPercent) { }
    public void Dispose()
    {
        if(disposed)return;disposed=true;status?.Dispose();status=null;
        foreach(var peer in peers.Values.ToArray())SteamNetworkingSockets.CloseConnection(peer.Handle,1000,"Leaving session",true);
        peers.Clear();if(listen!=HSteamListenSocket.Invalid)SteamNetworkingSockets.CloseListenSocket(listen);listen=HSteamListenSocket.Invalid;
    }
}
