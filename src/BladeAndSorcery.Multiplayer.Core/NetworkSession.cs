using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace BladeAndSorcery.Multiplayer.Core;

/// <summary>Single-threaded session router. Poll from Unity's main thread; no Unity objects cross threads.</summary>
public sealed class NetworkSession : IDisposable
{
    private readonly ISessionTransport transport;
    private readonly Dictionary<ISessionPeer,byte[]> challenges=new Dictionary<ISessionPeer,byte[]>();
    private bool active;
    private bool hasPolled;
    private string password="";
    private readonly Dictionary<ISessionPeer, ushort> authenticated = new Dictionary<ISessionPeer, ushort>();
    private readonly Dictionary<ushort, PeerInfo> participants = new Dictionary<ushort, PeerInfo>();
    private readonly Dictionary<ISessionPeer, double> pending = new Dictionary<ISessionPeer, double>();
    private readonly Dictionary<ISessionPeer,double> rejecting = new Dictionary<ISessionPeer,double>();

    private ISessionPeer? server;
    private ushort nextPeer = 2;
    private double now;
    private string build = "", name = "", key = "";
    public bool IsHost { get; private set; }
    public ushort LocalId { get; private set; }
    public bool Active => active;
    public bool Connected => LocalId != 0;
    public Guid SessionId { get; private set; }
    public IReadOnlyDictionary<ushort, PeerInfo> Peers => participants;
    public long SentBytes { get; private set; }
    public long ReceivedBytes { get; private set; }
    public int RejectedPackets { get; private set; }
    public int Ping => server?.Ping ?? 0;
    public int LocalPort => transport.LocalPort;
    public string TransportName=>transport.Name;
    public int MaxPlayers { get; private set; } = 4;
    public uint WorldRevision { get; private set; } = 1;
    public void SetWorldRevision(uint revision) { if (revision == 0) throw new ArgumentOutOfRangeException(nameof(revision)); WorldRevision = revision; }
    public event Action<PeerInfo>? PeerJoined;
    public event Action<ushort>? PeerLeft;
    public event Action<ushort, MessageKind, byte[]>? Message;
    public event Action<string>? Log;
    public event Action<string>? Closed;
    // The game adapter validates entity authority and payload semantics before a host relays anything.
    public Func<ushort, MessageKind, byte[], bool>? Validate;
    private ContentManifest content = new ContentManifest();
    public void SetContentManifest(ContentManifest manifest)
    { if(Active)throw new InvalidOperationException("Content cannot change during a session");content=manifest ?? throw new ArgumentNullException(nameof(manifest)); }

    public NetworkSession(ISessionTransport? transport=null)
    {
        this.transport=transport ?? new UdpSessionTransport();
        this.transport.CanAccept=()=>IsHost && participants.Count+pending.Count+rejecting.Count<MaxPlayers;
        this.transport.Connected+=peer=>
        {
            Log?.Invoke(this.transport.Name+" transport connected; negotiating session");
            if(IsHost)
            {
                if(participants.Count+pending.Count+rejecting.Count>=MaxPlayers){peer.Disconnect();return;}
                pending[peer]=hasPolled ? now : double.NaN;
                if(this.transport.PasswordChallenge)
                {
                    var nonce=new byte[32];using(var random=RandomNumberGenerator.Create())random.GetBytes(nonce);
                    challenges[peer]=nonce;SendRaw(peer,MessageKind.AuthChallenge,1,nonce,true);
                }
            }
            else {server=peer;if(!this.transport.PasswordChallenge)SendHello(peer,null);}
        };
        this.transport.Disconnected+=(peer,reason)=>
        {
            if(!active)return;
            pending.Remove(peer);challenges.Remove(peer);rejecting.Remove(peer);
            if(IsHost)
            {
                if(!authenticated.TryGetValue(peer,out var id))return;
                authenticated.Remove(peer);participants.Remove(id);
                Broadcast(MessageKind.Left,1,Protocol.Encode(w=>w.Write(id)),true);PeerLeft?.Invoke(id);
            }
            else if(peer==server || server==null && active && LocalId==0){LocalId=0;participants.Clear();server=null;Closed?.Invoke("Disconnected: "+reason);}
        };
        this.transport.Error+=error=>Log?.Invoke(error);
        this.transport.Received+=(peer,bytes)=>
        {
            try {ReceivedBytes+=bytes.Length;Receive(peer,bytes);}
            catch(Exception e) when(e is IOException || e is InvalidDataException || e is ArgumentException || e is OverflowException)
            {RejectedPackets++;Log?.Invoke("Rejected malformed packet: "+e.Message);}
        };
    }
    private void Start(string playerName,string gameBuild,string password)
    {
        if(Active)throw new InvalidOperationException("Session is already running");
        hasPolled=false;
        name=playerName.Length<=32 ? playerName : playerName.Substring(0,32);
        build=gameBuild;this.password=password;key="BASMP/"+Protocol.Version+"/"+password;active=true;
    }
    public void Host(string playerName,string gameBuild,int port=9050,string password="",int maxPlayers=4)
    {
        if(maxPlayers<2 || maxPlayers>Protocol.MaxPeers)throw new ArgumentOutOfRangeException(nameof(maxPlayers));
        MaxPlayers=maxPlayers;Start(playerName,gameBuild,password);IsHost=true;LocalId=1;SessionId=Guid.NewGuid();nextPeer=2;
        participants.Add(1,new PeerInfo{Id=1,Name=name});
        try{transport.Host(port,key);}catch{Dispose();throw;}
        Log?.Invoke("Hosting "+transport.Name);
    }
    public void Join(string address,string playerName,string gameBuild,int port=9050,string password="",int localPort=0)
    {
        Start(playerName,gameBuild,password);IsHost=false;LocalId=0;
        try {if(transport is UdpSessionTransport udp)udp.Join(address,port,key,localPort);else transport.Join(address,port,key);}
        catch{Dispose();throw;}
        Log?.Invoke("Connecting through "+transport.Name);
    }
    public void SetImpairment(int delayMilliseconds,int lossPercent)=>transport.SetImpairment(delayMilliseconds,lossPercent);
    public void Poll(double time)
    {
        now=time;hasPolled=true;transport.Poll();
        foreach(var peer in rejecting.Where(p=>now>=p.Value).Select(p=>p.Key).ToArray()){rejecting.Remove(peer);peer.Disconnect();}
        foreach(var peer in pending.Where(p=>double.IsNaN(p.Value)).Select(p=>p.Key).ToArray())pending[peer]=now;
        foreach(var item in pending.Where(p=>now-p.Value>10).ToArray()){pending.Remove(item.Key);challenges.Remove(item.Key);item.Key.Disconnect();}
    }
    private byte[] Proof(byte[] nonce)
    {
        using var hmac=new HMACSHA256(Encoding.UTF8.GetBytes(password));
        return hmac.ComputeHash(nonce.Concat(Encoding.UTF8.GetBytes(build)).ToArray());
    }
    private void SendHello(ISessionPeer peer,byte[]? nonce)
        =>SendRaw(peer,MessageKind.Hello,0,Protocol.Encode(w=>{w.Write(Protocol.Version);Protocol.String(w,build);Protocol.String(w,name,64);content.Write(w);if(nonce!=null)w.Write(Proof(nonce));}),true);
    private bool AcceptProof(ISessionPeer peer,BinaryReader hello)
    {
        if(!transport.PasswordChallenge)return true;
        var supplied=hello.ReadBytes(32);
        if(!challenges.TryGetValue(peer,out var nonce) || supplied.Length!=32)return false;
        var expected=Proof(nonce);int difference=0;for(int i=0;i<expected.Length;i++)difference|=expected[i]^supplied[i];
        return difference==0;
    }
    private void Reject(ISessionPeer peer,string reason)
    {
        // Allow the reliable rejection to reach UDP clients before closing the peer.
        SendRaw(peer,MessageKind.Rejected,1,Protocol.Encode(w=>Protocol.String(w,reason,1024)),true);
        pending.Remove(peer);challenges.Remove(peer);rejecting[peer]=now+2;
    }
    public void Publish(MessageKind kind, byte[] payload, bool reliable = true)
    {
        if (!Connected) return;
        if (!Protocol.ApplicationMessage(kind)) throw new ArgumentException("Reserved session message");
        if (!reliable && payload.Length + Protocol.HeaderBytes > Protocol.MaxSnapshot) throw new InvalidDataException("Snapshot exceeds datagram budget");
        if (IsHost) Route(LocalId, kind, payload, reliable);
        else if (server != null) SendRaw(server, kind, 0, payload, reliable);
    }
    public void SendTo(ushort target, MessageKind kind, byte[] payload, bool reliable = true, ushort originalSource = 1)
    {
        if (!IsHost) throw new InvalidOperationException("Only the coordinator sends targeted state");
        foreach (var item in authenticated) if (item.Value == target) { SendRaw(item.Key, kind, originalSource, payload, reliable); return; }
    }
    private void Receive(ISessionPeer peer, byte[] bytes)
    {
        using var r = Protocol.Read(bytes);
        if (r.ReadUInt32() != Protocol.Magic || r.ReadUInt16() != Protocol.Version) throw new InvalidDataException("Protocol mismatch");
        var kind = (MessageKind)r.ReadByte(); ushort source = r.ReadUInt16();
        uint worldRevision = r.ReadUInt32();
        var payload = r.ReadBytes((int)(r.BaseStream.Length - r.BaseStream.Position));
        if (IsHost)
        {
            if(rejecting.ContainsKey(peer))return;
            if (!authenticated.TryGetValue(peer, out source))
            {
                if (kind != MessageKind.Hello || !pending.ContainsKey(peer)) throw new InvalidDataException("Unauthenticated packet");
                using var hello = Protocol.Read(payload);
                ushort version = hello.ReadUInt16(); string remoteBuild = Protocol.String(hello); string remoteName = Protocol.String(hello, 64); var remoteContent=ContentManifest.Read(hello); bool passwordAccepted=AcceptProof(peer,hello); Protocol.End(hello);
                if (version != Protocol.Version || remoteBuild != build || nextPeer == ushort.MaxValue) { Reject(peer,"Incompatible game/multiplayer build. Install the same release on every PC.");return; }
                if(!passwordAccepted){Reject(peer,"Incorrect server password");return;}
                string mismatch=content.Difference(remoteContent);
                if(mismatch.Length>0){Reject(peer,mismatch);Log?.Invoke(mismatch);return;}
                source = nextPeer++; pending.Remove(peer);challenges.Remove(peer); authenticated.Add(peer, source);
                var info = new PeerInfo { Id = source, Name = remoteName };
                participants.Add(source, info);
                SendRaw(peer, MessageKind.Welcome, 1, Protocol.Encode(w =>
                {
                    w.Write(source); w.Write(SessionId.ToByteArray()); w.Write((byte)MaxPlayers); w.Write((byte)participants.Count);
                    foreach (var p in participants.Values) { w.Write(p.Id); Protocol.String(w, p.Name, 64); }
                }), true);
                Broadcast(MessageKind.Joined, 1, Protocol.Encode(w => { w.Write(source); Protocol.String(w, remoteName, 64); }), true);
                if(authenticated.ContainsKey(peer)){PeerJoined?.Invoke(info); Log?.Invoke(remoteName + " joined as " + source);} return;
            }
            if (!Protocol.ApplicationMessage(kind)) throw new InvalidDataException("Reserved message from client");
            if (worldRevision != WorldRevision) { RejectedPackets++; return; }
            Route(source, kind, payload, kind != MessageKind.State);
        }
        else
        {
            if (peer != server) throw new InvalidDataException("Unknown server");
            using var body = Protocol.Read(payload);
            switch (kind)
            {
                case MessageKind.AuthChallenge:
                    if(LocalId!=0 || !transport.PasswordChallenge || payload.Length!=32)throw new InvalidDataException("Unexpected authentication challenge");
                    SendHello(peer,payload);break;
                case MessageKind.Rejected:
                    var rejection=Protocol.String(body,1024);Protocol.End(body);Closed?.Invoke(rejection);break;
                case MessageKind.Welcome:
                    if (LocalId != 0) throw new InvalidDataException("Repeated welcome");
                    LocalId = body.ReadUInt16(); var guid = body.ReadBytes(16);
                    if (guid.Length != 16 || LocalId < 2) throw new InvalidDataException("Invalid welcome");
                    SessionId = new Guid(guid); WorldRevision = worldRevision; MaxPlayers = body.ReadByte(); int count = body.ReadByte();
                    if (count > Protocol.MaxPeers) throw new InvalidDataException("Too many peers");
                    for (int i = 0; i < count; i++) { var p = new PeerInfo { Id = body.ReadUInt16(), Name = Protocol.String(body, 64) }; participants[p.Id] = p; }
                    Protocol.End(body); foreach (var p in participants.Values.ToArray()) if (p.Id != LocalId) PeerJoined?.Invoke(p);
                    Log?.Invoke("Connected as " + LocalId); break;
                case MessageKind.Joined:
                    var joined = new PeerInfo { Id = body.ReadUInt16(), Name = Protocol.String(body, 64) }; Protocol.End(body);
                    if (!participants.ContainsKey(joined.Id)) { participants.Add(joined.Id, joined); if (joined.Id != LocalId) PeerJoined?.Invoke(joined); }
                    break;
                case MessageKind.Left:
                    ushort id = body.ReadUInt16(); Protocol.End(body); participants.Remove(id); PeerLeft?.Invoke(id); break;
                default:
                    if (LocalId == 0 || !Protocol.ApplicationMessage(kind) || !participants.ContainsKey(source)) throw new InvalidDataException("Invalid source");
                    if (kind == MessageKind.Scene && source == 1)
                    {
                        if(worldRevision != WorldRevision && !Protocol.Newer(worldRevision,WorldRevision)){RejectedPackets++;return;}
                        WorldRevision = worldRevision;
                    }
                    else if (worldRevision != WorldRevision) { RejectedPackets++; return; }
                    Message?.Invoke(source, kind, payload); break;
            }
        }
    }
    private void Route(ushort source, MessageKind kind, byte[] payload, bool reliable)
    {
        if (Validate != null && !Validate(source, kind, payload)) { RejectedPackets++; return; }
        Broadcast(kind, source, payload, reliable); Message?.Invoke(source, kind, payload);
    }
    private void Broadcast(MessageKind kind, ushort source, byte[] payload, bool reliable)
    {
        foreach (var peer in authenticated.Keys.ToArray()) SendRaw(peer, kind, source, payload, reliable);
    }
    private void SendRaw(ISessionPeer peer, MessageKind kind, ushort source, byte[] payload, bool reliable)
    {
        var bytes = Protocol.Encode(w => { w.Write(Protocol.Magic); w.Write(Protocol.Version); w.Write((byte)kind); w.Write(source); w.Write(WorldRevision); w.Write(payload); });
        byte channel = kind == MessageKind.State ? (byte)0 : (byte)1;
        peer.Send(bytes, channel, reliable); SentBytes += bytes.Length;
    }
    public void Dispose()
    {
        active=false;transport.Dispose();server=null;LocalId=0;IsHost=false;password="";key="";challenges.Clear();
        authenticated.Clear(); pending.Clear(); participants.Clear();rejecting.Clear();
    }
}
