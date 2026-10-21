using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using BladeAndSorcery.Multiplayer.Core;

int passed = 0;
void Test(string name, Action action) { action(); Console.WriteLine("PASS " + name); passed++; }
void Check(bool value, string why) { if (!value) throw new Exception(why); }
void Reject(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Malformed data was accepted"); }
Pose pose = new Pose(new Vector3(1, 2, 3), Quaternion.Identity);
Test("snapshot round trip and datagram size", () =>
{
    var state = new EntitySnapshot { Id = 0x100000001, Epoch = 2, Sequence = uint.MaxValue, Time = 123, Root = new BodyState { Pose = pose }, Head = pose, LeftHand = pose, RightHand = pose, Parts = Enumerable.Range(0, Protocol.MaxParts).Select(i => new BodyState { Part = i, Pose = pose, Velocity = new Vector3(i, 0, 0) }).ToArray() };
    state.LeftGrip = state.RightGrip = new GripState { Active=true, ConnectedAnchor=new Vector3(.1f,.2f,.3f), PositionSpring=2000, PositionDamper=100, PositionMaxForce=5000, RotationSpring=1000, RotationDamper=50, RotationMaxForce=2500, AngularMask=12 };
    state.Flags = 512 | 256 | 16;state.PoseRevision=42;
    var bytes = Protocol.Encode(w => Protocol.Snapshot(w, state)); Check(bytes.Length + Protocol.HeaderBytes <= Protocol.MaxSnapshot, "Snapshot fragments");
    using var r = Protocol.Read(bytes); var decoded = Protocol.Snapshot(r); Protocol.End(r);
    Check(decoded.Parts.Length == Protocol.MaxParts && decoded.Parts[5].Velocity.X == 5 && decoded.Id == state.Id, "Lost state");
    Check(decoded.Flags == state.Flags, "Lost physical ragdoll or native invincibility state");
    Check(decoded.PoseRevision==42,"Lost teleport revision");
    Check(decoded.LeftGrip.AngularMask == 12 && decoded.RightGrip.PositionSpring == 2000 && decoded.LeftGrip.ConnectedAnchor == state.LeftGrip.ConnectedAnchor, "Lost native grip settings");
    Console.WriteLine("Maximum snapshot bytes including envelope: " + (bytes.Length + Protocol.HeaderBytes));
    state.Flags = 1024;
    Reject(() => { using var invalid = Protocol.Read(Protocol.Encode(w => Protocol.Snapshot(w, state))); Protocol.Snapshot(invalid); });
});
Test("scene identity and initial combat state survive late-join descriptor serialization", () =>
{
    var original = new EntityDescriptor { Id=0x100000001, Owner=1, Epoch=3, Kind=EntityKind.Player, CatalogId="PlayerDefaultMale", SceneKey="spawner:Arena[0]/Props[2]/Crate[1]:0", Pose=pose, Health=23, MaxHealth=100, SlicedParts=new[]{128,131} };
    using var r = Protocol.Read(Protocol.Encode(w => Protocol.Descriptor(w,original)));
    var decoded=Protocol.Descriptor(r); Protocol.End(r);
    Check(decoded.SceneKey == original.SceneKey && decoded.Health == 23 && decoded.SlicedParts.SequenceEqual(original.SlicedParts) && decoded.Epoch == 3,"Lost late-join state");
});
Test("malformed payload rejection", () =>
{
    Reject(() => { using var r = Protocol.Read(new byte[] { 255, 255 }); Protocol.String(r); });
    Reject(() => { using var r = Protocol.Read(Protocol.Encode(w => { w.Write(float.NaN); w.Write(0f); w.Write(0f); })); Protocol.Vector(r); });
    Reject(() => { using var r = Protocol.Read(Protocol.Encode(w => { Protocol.Vector(w, Vector3.Zero); for (int i = 0; i < 4; i++) w.Write(0f); })); Protocol.Pose(r); });
});
Test("procedural mode round trips only for host-owned NPC descriptors", () =>
{
    var d=new EntityDescriptor{Id=0x100000001,Owner=1,Epoch=1,Kind=EntityKind.Creature,CatalogId="HumanMale",Pose=pose,ProceduralNpc=true};
    EntityDescriptor Read(){using var r=Protocol.Read(Protocol.Encode(w=>Protocol.Descriptor(w,d)));var value=Protocol.Descriptor(r);Protocol.End(r);return value;}
    Check(Read().ProceduralNpc,"Late join loses procedural mode");
    d.Owner=2;Reject(()=>Read());d.Owner=1;d.Kind=EntityKind.Player;Reject(()=>Read());d.Kind=EntityKind.Item;Reject(()=>Read());
    d.Kind=EntityKind.Creature;d.ProceduralNpc=false;Check(!Read().ProceduralNpc,"Ordinary NPC opted in implicitly");
});
Test("ownership transfer rejects stale writers and competing requests", () =>
{
    var table = new OwnershipTable(); table.Add(new EntityDescriptor { Id = 42, Owner = 1, Epoch = 1 });
    Check(table.Transfer(42, 2, 1), "Valid transfer rejected");
    Check(!table.Transfer(42, 3, 1) && !table.Owns(42, 1, 1) && table.Owns(42, 2, 2), "Stale ownership accepted");
});
Test("interpolation, bounded prediction, stale and reordered snapshots", () =>
{
    var buffer = new SnapshotBuffer { Delay = .05, MaxExtrapolation = .075 };
    EntitySnapshot State(uint seq, float x) => new EntitySnapshot { Id = 1, Epoch = 1, Sequence = seq, Root = new BodyState { Pose = new Pose(new Vector3(x, 0, 0), Quaternion.Identity), Velocity = Vector3.UnitX }, Head = pose, LeftHand = pose, RightHand = pose };
    Check(buffer.Push(State(uint.MaxValue, 0), 0), "First frame rejected");
    Check(buffer.Push(State(0, 1), .1), "Sequence rollover rejected");
    Check(!buffer.Push(State(uint.MaxValue, -1), .11), "Old frame accepted");
    Check(Math.Abs(buffer.Sample(.1)!.Root.Pose.Position.X - .5) < .001, "Interpolation wrong");
    Check(Math.Abs(buffer.Sample(99)!.Root.Pose.Position.X - 1.075) < .001, "Extrapolation is unbounded");
});
Test("teleports discard interpolation history, survive dropped packets and reject stale revisions", () =>
{
    var buffer=new SnapshotBuffer{Delay=.065};
    EntitySnapshot State(uint sequence,uint revision,float x)=>new EntitySnapshot{Id=1,Epoch=1,Sequence=sequence,PoseRevision=revision,Root=new BodyState{Pose=new Pose(new Vector3(x,0,0),Quaternion.Identity)},Head=pose,LeftHand=pose,RightHand=pose};
    Check(buffer.Push(State(1,0,0),1) && buffer.Push(State(2,0,1),1.03),"Ordinary samples rejected");
    Check(!buffer.Discontinuity && buffer.Count==2,"Normal movement lost interpolation");
    // The first packets for revision 1 were lost; its later snapshot still marks the teleport.
    Check(buffer.Push(State(5,1,10),1.12) && buffer.Discontinuity && buffer.Count==1,"Teleport retained old poses");
    Check(buffer.Sample(1.12)!.Root.Pose.Position.X==10 && buffer.Sample(1.12)!.PoseRevision==1,"Teleport blended through the old location");
    Check(!buffer.Push(State(4,0,2),1.13) && !buffer.Push(State(6,0,2),1.14),"Stale sequence/revision restored old history");
    Check(buffer.Push(State(7,1,11),1.15) && !buffer.Discontinuity,"Persistent teleport revision repeatedly snapped ordinary movement");
    var next=State(1,0,20);next.Epoch=2;Check(buffer.Push(next,1.2),"New owner could not reset its revision");
    buffer.Clear();Check(buffer.Push(State(1,uint.MaxValue,0),2) && buffer.Push(State(2,0,5),2.03) && buffer.Discontinuity,"Revision rollover failed");
});
Test("gameplay events deduplicate independent peers", () =>
{
    var window = new EventWindow();
    Check(window.Accept(0x100000001) && !window.Accept(0x100000001) && window.Accept(0x200000001), "Duplicate handling incorrect");
    var small = new EventWindow(4);
    Check(small.Accept(0x100000001) && small.Accept(0x100000008) && !small.Accept(0x100000001) && !small.Accept(0x100000004), "Evicted old events became replayable");
    Check(small.Accept(0x100000007) && !small.Accept(0x100000007) && small.Accept(0x200000001), "In-window reordering or peer isolation failed");
    small.Clear(); Check(small.Accept(0x100000001),"Session reset did not clear event history");
});
Test("actual UDP handshake, relay identity, rejection and disconnect", () =>
{
    using var host = new NetworkSession(); using var a = new NetworkSession(); using var b = new NetworkSession();
    host.Log += s => Console.WriteLine("host: " + s); a.Log += s => Console.WriteLine("A: " + s); b.Log += s => Console.WriteLine("B: " + s);
    host.Host("Host", "test", 0, "secret"); a.Join("127.0.0.1", "A", "test", host.LocalPort, "secret"); b.Join("127.0.0.1", "B", "test", host.LocalPort, "secret");
    var clock = Stopwatch.StartNew();
    void PumpUntil(Func<bool> done, int milliseconds = 7000)
    {
        var deadline = clock.ElapsedMilliseconds + milliseconds;
        while (!done() && clock.ElapsedMilliseconds < deadline) { double t = clock.Elapsed.TotalSeconds; host.Poll(t); a.Poll(t); b.Poll(t); Thread.Sleep(2); }
        Check(done(), "UDP test timed out");
    }
    PumpUntil(() => a.Connected && b.Connected && a.Peers.Count == 3 && b.Peers.Count == 3);
    int receivedA = 0, receivedB = 0; ushort observedSource = 0;
    a.Message += (source, kind, bytes) => { if (kind == MessageKind.Gameplay) receivedA++; };
    b.Message += (source, kind, bytes) => { if (kind == MessageKind.Gameplay) { receivedB++; observedSource = source; } };
    host.Validate = (_, _, payload) => payload.Length == 3;
    a.Publish(MessageKind.Gameplay, new byte[] { 1, 2, 3 });
    PumpUntil(() => receivedA == 1 && receivedB == 1);
    Check(observedSource == a.LocalId && observedSource != 1, "Host impersonated client");
    var full = new EntitySnapshot { Id = 1, Epoch = 1, Head = pose, LeftHand = pose, RightHand = pose, Root = new BodyState { Pose = pose }, Parts = Enumerable.Range(0, Protocol.MaxParts).Select(i => new BodyState { Part = i, Pose = pose }).ToArray() };
    full.LeftGrip = full.RightGrip = new GripState { Active=true, PositionSpring=1000, AngularMask=12 };
    int fullSnapshots = 0;
    b.Message += (_, kind, bytes) => { if (kind == MessageKind.State) { using var reader = Protocol.Read(bytes); Check(Protocol.Snapshot(reader).Parts.Length == Protocol.MaxParts, "Lost body parts"); fullSnapshots++; } };
    host.Validate = (_, kind, payload) => kind == MessageKind.State || payload.Length == 3;
    a.Publish(MessageKind.State, Protocol.Encode(w => Protocol.Snapshot(w, full)), false);
    PumpUntil(() => fullSnapshots == 1);
    a.Publish(MessageKind.Gameplay, new byte[] { 9 });
    PumpUntil(() => host.RejectedPackets == 1);
    Check(receivedB == 1, "Rejected message was relayed");
    a.Dispose(); PumpUntil(() => host.Peers.Count == 2 && b.Peers.Count == 2);
});
Test("simultaneous connections respect a host-selected two-player limit", () =>
{
    using var host=new NetworkSession(); using var a=new NetworkSession(); using var b=new NetworkSession();
    int rejected=0; a.Closed += _ => rejected++; b.Closed += _ => rejected++;
    host.Host("Host","capacity-test",0,"",2);
    a.Join("127.0.0.1","A","capacity-test",host.LocalPort); b.Join("127.0.0.1","B","capacity-test",host.LocalPort);
    var clock=Stopwatch.StartNew();
    int peak=host.Peers.Count;
    while (clock.ElapsedMilliseconds < 7000 && !(host.Peers.Count == 2 && rejected > 0 && (a.Connected || b.Connected)))
    {
        double now=clock.Elapsed.TotalSeconds; host.Poll(now); a.Poll(now); b.Poll(now); Thread.Sleep(2);
        peak=Math.Max(peak,host.Peers.Count);
    }
    Check(peak <= 2 && host.MaxPlayers == 2 && host.Peers.Count == 2 && (a.Connected ? 1 : 0)+(b.Connected ? 1 : 0) == 1 && rejected > 0,$"Admission incorrect: host={host.Peers.Count}, peak={peak}, A={a.Connected}, B={b.Connected}, rejected={rejected}");
});
Console.WriteLine($"{passed} protocol tests passed.");
