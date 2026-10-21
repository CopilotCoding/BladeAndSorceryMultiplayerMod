using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Threading;
using System.Text.Json;
using BladeAndSorcery.Multiplayer.Core;

// Checks the Steam transport contract in memory, plus real UDP loopback.
// No Steam backend, Unity, VR or weapon contacts: this cannot validate real Steam.
int passed=0;
var checks=new List<string>();
void Check(bool value,string reason){if(!value)throw new Exception(reason);}
void Run(string name,Action check){check();passed++;checks.Add(name);Console.WriteLine("PASS "+name);}
void Pump(NetworkSession host,params NetworkSession[] clients){for(int i=0;i<8;i++){host.Poll(5000+i*.01);foreach(var client in clients)client.Poll(5000+i*.01);}}
Run("challenge admission and shared reliable/unreliable routing",()=>
{
    var wire=new MemoryTransport();using var host=new NetworkSession(wire);host.Host("Host","build",password:"secret",maxPlayers:4);
    using var a=new NetworkSession(new MemoryTransport(wire));using var b=new NetworkSession(new MemoryTransport(wire));
    int joined=0;host.PeerJoined+=_=>joined++;
    a.Join("","A","build",password:"secret");b.Join("","B","build",password:"secret");
    Check(joined==0,"Admitted before proof");Pump(host,a,b);
    Check(a.Connected && b.Connected && joined==2 && host.Peers.Count==3 && a.SessionId==host.SessionId,"Admission failed");
    int received=0;ushort sender=0;b.Message+=(source,kind,bytes)=>{sender=source;received++;};
    a.Publish(MessageKind.Gameplay,new byte[]{1,2,3});a.Publish(MessageKind.State,new byte[]{4},false);Pump(host,a,b);
    Check(received==2 && sender==a.LocalId,"Shared routing lost events or identity");
});
Run("wrong password receives no world state or membership",()=>
{
    var wire=new MemoryTransport();using var host=new NetworkSession(wire);host.Host("H","build",password:"secret");
    using var client=new NetworkSession(new MemoryTransport(wire));int joined=0,messages=0;string closed="";
    host.PeerJoined+=_=>{joined++;host.Publish(MessageKind.Spawn,new byte[]{1});};client.Message+=(_,_,_)=>messages++;client.Closed+=reason=>closed+=reason;
    client.Join("","C","build",password:"wrong");Pump(host,client);
    Check(!client.Connected && host.Peers.Count==1 && joined==0 && messages==0 && closed.Contains("password"),"Password gate failed");
});
Run("incompatible builds are rejected",()=>
{
    var wire=new MemoryTransport();using var host=new NetworkSession(wire);host.Host("H","build");
    using var client=new NetworkSession(new MemoryTransport(wire));client.Join("","C","old-build");Pump(host,client);
    Check(!client.Connected && host.Peers.Count==1,"Build mismatch admitted");
});
Run("capacity includes host and handshake reservations",()=>
{
    var wire=new MemoryTransport();using var host=new NetworkSession(wire);host.Host("H","build",maxPlayers:2);
    using var a=new NetworkSession(new MemoryTransport(wire));using var b=new NetworkSession(new MemoryTransport(wire));
    a.Join("","A","build");b.Join("","B","build");Pump(host,a,b);
    Check(a.Connected && !b.Connected && host.Peers.Count==2,"Capacity exceeded");
});
Run("external callbacks before first poll do not expire at game clock",()=>
{
    var wire=new MemoryTransport();using var host=new NetworkSession(wire);host.Host("H","build");
    var clientWire=new MemoryTransport(wire){ConnectImmediately=true};using var client=new NetworkSession(clientWire);
    client.Join("","C","build");Pump(host,client);
    Check(client.Connected,"Pre-poll Steam callback timed out immediately");
});
Run("challenge replay on another connection is rejected",()=>
{
    var wire=new MemoryTransport();using var host=new NetworkSession(wire);host.Host("H","build",password:"secret");
    var first=new MemoryTransport(wire);using var a=new NetworkSession(first);a.Join("","A","build",password:"secret");Pump(host,a);
    var proof=first.Sent.First(bytes=>(MessageKind)bytes[6]==MessageKind.Hello);
    var replay=new MemoryTransport(wire){ReplaceHello=proof};using var b=new NetworkSession(replay);b.Join("","B","build",password:"secret");Pump(host,a,b);
    Check(a.Connected && !b.Connected && host.Peers.Count==2,"Old challenge response replayed");
});
Run("Direct IP protocol 6 Hello carries content manifest without challenge proof",()=>
{
    var wire=new MemoryTransport{PasswordChallenge=false};using var host=new NetworkSession(wire);host.Host("H","build");
    var clientWire=new MemoryTransport(wire){PasswordChallenge=false};using var client=new NetworkSession(clientWire);client.Join("","C","build");Pump(host,client);
    var bytes=clientWire.Sent.First();using var reader=Protocol.Read(bytes);reader.BaseStream.Position=Protocol.HeaderBytes;
    Check((MessageKind)bytes[6]==MessageKind.Hello && reader.ReadUInt16()==Protocol.Version && Protocol.String(reader)=="build" && Protocol.String(reader,64)=="C","UDP Hello changed");Check(ContentManifest.Read(reader).Count==0,"Unexpected content manifest");Protocol.End(reader);
    Check(client.Connected,"UDP contract admission failed");
});
Run("stale world revisions are rejected after travel",()=>
{
    var wire=new MemoryTransport();using var host=new NetworkSession(wire);host.Host("H","build");
    using var client=new NetworkSession(new MemoryTransport(wire));client.Join("","C","build");Pump(host,client);
    host.SetWorldRevision(2);int received=0;host.Message+=(_,_,_)=>received++;
    client.Publish(MessageKind.Gameplay,new byte[]{1});Pump(host,client);
    Check(received==0 && host.RejectedPackets>0,"Stale world message relayed");
});
Run("real Direct IP loopback still admits and routes through LiteNetLib",()=>
{
    using var host=new NetworkSession();host.Host("H","build",port:0,password:"udp-secret");
    using var client=new NetworkSession();client.Join("127.0.0.1","C","build",host.LocalPort,"udp-secret");
    var timer=Stopwatch.StartNew();
    while(!client.Connected && timer.Elapsed.TotalSeconds<5){host.Poll(timer.Elapsed.TotalSeconds);client.Poll(timer.Elapsed.TotalSeconds);Thread.Sleep(2);}
    Check(client.Connected && host.Peers.Count==2,"Actual UDP admission regressed");
    bool received=false;host.Message+=(source,kind,bytes)=>received=source==client.LocalId && kind==MessageKind.Gameplay && bytes.SequenceEqual(new byte[]{7,8});
    client.Publish(MessageKind.Gameplay,new byte[]{7,8});
    while(!received && timer.Elapsed.TotalSeconds<6){host.Poll(timer.Elapsed.TotalSeconds);client.Poll(timer.Elapsed.TotalSeconds);Thread.Sleep(2);}
    Check(received,"Actual UDP gameplay routing regressed");
});
Run("travel agreement waits for everyone and rejects racing/stale replies",()=>
{
    var agreement=new TravelAgreement();
    Check(agreement.Begin(7,new ushort[]{1,2,3},10),"First map selection rejected");
    Check(!agreement.Ready && !agreement.Begin(8,new ushort[]{1,2},11),"Simultaneous selection replaced pending travel");
    Check(!agreement.Reply(6,2,true,"") && !agreement.Reply(7,4,true,""),"Stale/uninvited ready accepted");
    Check(agreement.Reply(7,2,true,"") && !agreement.Ready,"Started before slow peer");
    Check(!agreement.Reply(7,2,true,""),"Duplicate ready accepted");
    Check(agreement.Reply(7,3,true,"") && agreement.Ready,"Did not release complete agreement");
    agreement.End();Check(agreement.Begin(8,new ushort[]{1,2},20),"Could not propose subsequent map");
    agreement.Reply(8,2,false,"Map unavailable");Check(!agreement.Ready && agreement.Failure.Contains("unavailable"),"Missing map was committed");
});
Run("travel agreement handles loading timeout and departed peer",()=>
{
    var agreement=new TravelAgreement();agreement.Begin(1,new ushort[]{1,2,3},10,30);
    agreement.Reply(1,2,true,"");Check(!agreement.TimedOut(39) && agreement.TimedOut(40),"Incorrect deadline");
    agreement.Remove(3);Check(agreement.Ready,"Departing peer permanently blocked travel");
});
Run("new world control messages route while stale Scene cannot roll back revision",()=>
{
    var wire=new MemoryTransport();using var host=new NetworkSession(wire);host.Host("H","build");
    using var client=new NetworkSession(new MemoryTransport(wire));client.Join("","C","build");Pump(host,client);
    var received=new List<MessageKind>();client.Message+=(_,kind,_)=>received.Add(kind);
    host.Publish(MessageKind.ScenePrepare,new byte[]{1});Pump(host,client);
    Check(received.Contains(MessageKind.ScenePrepare),"Map preflight not routed");
    host.SetWorldRevision(3);host.Publish(MessageKind.Scene,new byte[]{2});Pump(host,client);Check(client.WorldRevision==3,"World commit not applied");
    host.SetWorldRevision(2);host.Publish(MessageKind.Scene,new byte[]{3});Pump(host,client);Check(client.WorldRevision==3,"Stale Scene rolled revision backwards");
});
Run("matching mod manifests admit over both transport contracts",()=>
{
    foreach(bool challenge in new[]{false,true})
    {
        var wire=new MemoryTransport{PasswordChallenge=challenge};using var host=new NetworkSession(wire);
        var manifest=new ContentManifest(new[]{new ContentEntry{Id="weapon",Name="Weapon pack",Hash=new string('A',64)}});
        host.SetContentManifest(manifest);host.Host("H","build");
        using var client=new NetworkSession(new MemoryTransport(wire){PasswordChallenge=challenge});client.SetContentManifest(manifest);client.Join("","C","build");Pump(host,client);
        Check(client.Connected && host.Peers.Count==2,"Matching content rejected");
    }
});
Run("missing extra and changed mods reject before world membership with useful notices",()=>
{
    foreach(bool challenge in new[]{false,true})foreach(string mode in new[]{"missing","changed","extra"})
    {
        var wire=new MemoryTransport{PasswordChallenge=challenge};using var host=new NetworkSession(wire);
        host.SetContentManifest(new ContentManifest(new[]{new ContentEntry{Id="pack",Name="Example pack",Hash=new string('A',64)}}));host.Host("H","build");
        using var client=new NetworkSession(new MemoryTransport(wire){PasswordChallenge=challenge});
        var records=new List<ContentEntry>();if(mode!="missing")records.Add(new ContentEntry{Id="pack",Name="Example pack",Hash=new string(mode=="changed" ? 'B' : 'A',64)});
        if(mode=="extra")records.Add(new ContentEntry{Id="extra",Name="Extra pack",Hash=new string('C',64)});
        client.SetContentManifest(new ContentManifest(records));int joined=0,messages=0;string notice="";
        host.PeerJoined+=_=>joined++;client.Message+=(_,_,_)=>messages++;client.Closed+=reason=>notice+=reason;
        client.Join("","C","build");Pump(host,client);
        Check(!client.Connected && joined==0 && messages==0 && host.Peers.Count==1 && notice.Contains("Mod mismatch") && notice.Contains("pack"),"Mismatch did not fail cleanly: "+mode);
    }
});
Run("manifest canonical ordering defensive copy and bounded validation",()=>
{
    var a=new ContentEntry{Id="a",Name="A",Hash=new string('A',64)};var b=new ContentEntry{Id="b",Name="B",Hash=new string('B',64)};
    var manifest=new ContentManifest(new[]{a,b});var reversed=new ContentManifest(new[]{b,a});a.Hash=new string('C',64);
    Check(manifest.Difference(reversed)=="","Manifest changed after construction or depends on enumeration order");
    foreach(var records in new[]{new[]{b,b},new[]{new ContentEntry{Id="bad",Hash="bad"}},Enumerable.Range(0,129).Select(i=>new ContentEntry{Id=i.ToString(),Name="x",Hash=new string('A',64)}).ToArray()})
    {bool rejected=false;try{_ = new ContentManifest(records);}catch(InvalidDataException){rejected=true;}Check(rejected,"Malformed manifest accepted");}
    bool activeRejected=false;using var host=new NetworkSession(new MemoryTransport());host.Host("H","build");try{host.SetContentManifest(manifest);}catch(InvalidOperationException){activeRejected=true;}Check(activeRejected,"Live manifest replaced");
});
Run("custom item state survives descriptor serialization and enforces wire size",()=>
{
    var descriptor=new EntityDescriptor{Id=1,Owner=1,Epoch=1,Kind=EntityKind.Item,CatalogId="ModWeapon",Pose=new Pose(System.Numerics.Vector3.Zero,System.Numerics.Quaternion.Identity),ItemState="{\"Custom\":[{\"Type\":\"Example:Ammo\",\"Json\":\"{\\\"count\\\":7}\"}]}"};
    var bytes=Protocol.Encode(w=>Protocol.Descriptor(w,descriptor));using var reader=Protocol.Read(bytes);var copy=Protocol.Descriptor(reader);Protocol.End(reader);
    Check(copy.ItemState==descriptor.ItemState,"Custom item state was dropped");descriptor.ItemState=new string('x',24001);
    bool rejected=false;try{Protocol.Encode(w=>Protocol.Descriptor(w,descriptor));}catch(InvalidDataException){rejected=true;}Check(rejected,"Oversized custom state accepted");
});
Run("real UDP content rejection delivers its named reason before closing",()=>
{
    using var host=new NetworkSession();host.SetContentManifest(new ContentManifest(new[]{new ContentEntry{Id="pack",Name="Real UDP fixture pack",Hash=new string('A',64)}}));host.Host("H","build",0);
    using var client=new NetworkSession();string first="";client.Closed+=reason=>{if(first.Length==0)first=reason;};client.Join("127.0.0.1","C","build",host.LocalPort);
    var timer=Stopwatch.StartNew();while(timer.ElapsedMilliseconds<5000 && first.Length==0){host.Poll(timer.Elapsed.TotalSeconds);client.Poll(timer.Elapsed.TotalSeconds);Thread.Sleep(2);}
    Check(first.StartsWith("Mod mismatch",StringComparison.Ordinal) && first.Contains("Real UDP fixture pack") && !client.Connected && host.Peers.Count==1,"Named UDP rejection was lost: "+first);
});
Console.WriteLine(passed+" shared-session checks passed. Real Steam accounts/relay remain unverified.");
if(args.Length==1)File.WriteAllText(args[0],JsonSerializer.Serialize(new{time=DateTime.UtcNow,passed=true,count=passed,checks,coreMvid=typeof(NetworkSession).Assembly.ManifestModule.ModuleVersionId.ToString("N"),steamBackendVerified=false,combatTestsRun=false}));

sealed class MemoryTransport : ISessionTransport
{
    private sealed class Peer : ISessionPeer
    {
        internal MemoryTransport Owner=null!;
        internal Peer Other=null!;
        internal bool Closed;
        public int Ping=>0;
        public void Send(byte[] bytes,byte channel,bool reliable)
        {
            Owner.Sent.Add(bytes);
            if(Owner.ReplaceHello!=null && (MessageKind)bytes[6]==MessageKind.Hello)bytes=Owner.ReplaceHello;
            var payload=bytes;Other.Owner.queue.Enqueue(()=>Other.Owner.Received?.Invoke(Other,payload));
        }
        public void Disconnect()
        {
            if(Closed)return;Closed=Other.Closed=true;
            Owner.queue.Enqueue(()=>Owner.Disconnected?.Invoke(this,"Closed"));Other.Owner.queue.Enqueue(()=>Other.Owner.Disconnected?.Invoke(Other,"Closed"));
        }
    }
    private readonly MemoryTransport? host;
    private readonly Queue<Action> queue=new Queue<Action>();
    internal readonly List<byte[]> Sent=new List<byte[]>();
    internal bool ConnectImmediately;
    internal byte[]? ReplaceHello;
    public string Name=>"Memory Steam contract";
    public bool PasswordChallenge {get;set;}=true;
    public int LocalPort=>0;
    public Func<bool>? CanAccept {get;set;}
    public event Action<ISessionPeer>? Connected;
    public event Action<ISessionPeer,string>? Disconnected;
    public event Action<ISessionPeer,byte[]>? Received;
    public event Action<string>? Error {add{}remove{}}
    internal MemoryTransport(MemoryTransport? host=null)=>this.host=host;
    public void Host(int port,string key){}
    public void Join(string address,int port,string key)
    {
        var local=new Peer{Owner=this};var remote=new Peer{Owner=host!};local.Other=remote;remote.Other=local;
        Action server=()=>host!.Connected?.Invoke(remote);Action client=()=>Connected?.Invoke(local);
        if(ConnectImmediately){server();client();}else{host!.queue.Enqueue(server);queue.Enqueue(client);}
    }
    public void Poll(){while(queue.Count>0)queue.Dequeue()();}
    public void SetImpairment(int delayMilliseconds,int lossPercent){}
    public void Dispose(){queue.Clear();}
}
