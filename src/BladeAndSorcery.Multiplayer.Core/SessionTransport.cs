using System;
using System.Collections.Generic;
using System.IO;
using LiteNetLib;

namespace BladeAndSorcery.Multiplayer.Core;

public interface ISessionPeer
{
    int Ping { get; }
    void Send(byte[] bytes, byte channel, bool reliable);
    void Disconnect();
}

// The game adapter can provide a Steam transport without making the core
// protocol assembly depend on Unity or the game's Steamworks assembly.
public interface ISessionTransport : IDisposable
{
    string Name { get; }
    bool PasswordChallenge { get; }
    int LocalPort { get; }
    Func<bool>? CanAccept { get; set; }
    event Action<ISessionPeer>? Connected;
    event Action<ISessionPeer,string>? Disconnected;
    event Action<ISessionPeer,byte[]>? Received;
    event Action<string>? Error;
    void Host(int port,string key);
    void Join(string address,int port,string key);
    void Poll();
    void SetImpairment(int delayMilliseconds,int lossPercent);
}

internal sealed class UdpSessionTransport : ISessionTransport
{
    private sealed class Peer : ISessionPeer
    {
        internal readonly NetPeer Native;
        internal Peer(NetPeer native)=>Native=native;
        public int Ping=>Native.Ping;
        public void Send(byte[] bytes,byte channel,bool reliable)=>Native.Send(bytes,channel,reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable);
        public void Disconnect()=>Native.Disconnect();
    }
    private readonly EventBasedNetListener listener=new EventBasedNetListener();
    private readonly Dictionary<NetPeer,Peer> peers=new Dictionary<NetPeer,Peer>();
    private NetManager? manager;
    private string key="";
    public string Name=>"Direct IP / UDP";
    public bool PasswordChallenge=>false;
    public int LocalPort=>manager?.LocalPort ?? 0;
    public Func<bool>? CanAccept {get;set;}
    public event Action<ISessionPeer>? Connected;
    public event Action<ISessionPeer,string>? Disconnected;
    public event Action<ISessionPeer,byte[]>? Received;
    public event Action<string>? Error;
    internal UdpSessionTransport()
    {
        listener.ConnectionRequestEvent+=request=>{if(CanAccept?.Invoke()!=true){request.Reject();return;}request.AcceptIfKey(key);};
        listener.PeerConnectedEvent+=native=>{var peer=new Peer(native);peers[native]=peer;Connected?.Invoke(peer);};
        // LiteNetLib also reports refused connection attempts before PeerConnected.
        // Surface those exits so admission failures cannot leave the UI connecting forever.
        listener.PeerDisconnectedEvent+=(native,info)=>{if(!peers.TryGetValue(native,out var peer))peer=new Peer(native);peers.Remove(native);Disconnected?.Invoke(peer,info.Reason.ToString());};
        listener.NetworkErrorEvent+=(_,error)=>Error?.Invoke("Socket error: "+error);
        listener.NetworkReceiveEvent+=(native,reader,channel,delivery)=>
        {
            try {if(reader.AvailableBytes<=Protocol.MaxPacket && peers.TryGetValue(native,out var peer)) Received?.Invoke(peer,reader.GetRemainingBytes());}
            finally {reader.Recycle();}
        };
    }
    private void Start(int port,string admissionKey)
    {
        key=admissionKey;
        manager=new NetManager(listener){AutoRecycle=false,ChannelsCount=3,DisconnectTimeout=12000,UpdateTime=15,ReconnectDelay=500,MaxConnectAttempts=12};
        if(!manager.Start(port)){manager=null;throw new IOException("Could not bind UDP port "+port);}
    }
    public void Host(int port,string key)=>Start(port,key);
    public void Join(string address,int port,string key){Start(0,key);manager!.Connect(address,port,key);}
    // Retain the existing explicit local-port option used by protocol tools.
    internal void Join(string address,int port,string key,int localPort){Start(localPort,key);manager!.Connect(address,port,key);}
    public void Poll()=>manager?.PollEvents();
    public void SetImpairment(int delayMilliseconds,int lossPercent)
    {
        if(manager==null)return;
        manager.SimulateLatency=delayMilliseconds>0;manager.SimulationMinLatency=Math.Max(0,delayMilliseconds/2);manager.SimulationMaxLatency=Math.Max(0,delayMilliseconds);
        manager.SimulatePacketLoss=lossPercent>0;manager.SimulationPacketLossChance=Math.Max(0,Math.Min(50,lossPercent));
    }
    public void Dispose(){manager?.Stop();manager=null;peers.Clear();}
}
