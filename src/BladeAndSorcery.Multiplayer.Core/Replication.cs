using System;
using System.Collections.Generic;
using System.Numerics;

namespace BladeAndSorcery.Multiplayer.Core;

public sealed class EventWindow
{
    private sealed class PeerWindow { internal uint Highest; internal readonly SortedSet<uint> Seen = new SortedSet<uint>(); }
    private readonly Dictionary<uint, PeerWindow> peers = new Dictionary<uint, PeerWindow>();
    private readonly int capacity;
    public EventWindow(int capacity = 8192) { if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity)); this.capacity = capacity; }
    public bool Accept(ulong id)
    {
        uint peer = (uint)(id >> 32), counter = (uint)id;
        if (peer == 0 || counter == 0) return false;
        if (!peers.TryGetValue(peer,out var window)) peers[peer] = window = new PeerWindow();
        if (counter <= window.Highest && window.Highest - counter >= capacity) return false;
        if (counter > window.Highest) window.Highest = counter;
        while (window.Seen.Count > 0 && window.Highest - window.Seen.Min >= capacity) window.Seen.Remove(window.Seen.Min);
        return window.Seen.Add(counter);
    }
    public void Clear() => peers.Clear();
}

public sealed class OwnershipTable
{
    private readonly Dictionary<ulong, EntityDescriptor> entities = new Dictionary<ulong, EntityDescriptor>();
    public IEnumerable<EntityDescriptor> Entities => entities.Values;
    public int Count => entities.Count;
    public bool TryGet(ulong id, out EntityDescriptor descriptor) => entities.TryGetValue(id, out descriptor!);
    public bool Add(EntityDescriptor entity)
    {
        if (entities.ContainsKey(entity.Id) || entities.Count >= Protocol.MaxEntities) return false;
        entities.Add(entity.Id, entity); return true;
    }
    public bool Owns(ulong id, ushort owner, uint epoch) => entities.TryGetValue(id, out var e) && e.Owner == owner && e.Epoch == epoch;
    public bool Transfer(ulong id, ushort owner, uint expectedEpoch)
    {
        if (!entities.TryGetValue(id, out var e) || e.Epoch != expectedEpoch || owner == 0 || e.Epoch == uint.MaxValue) return false;
        e.Owner = owner; e.Epoch++; return true;
    }
    public bool ApplyTransfer(ulong id, ushort owner, uint epoch)
    {
        if (!entities.TryGetValue(id, out var e) || owner == 0 || !Protocol.Newer(epoch, e.Epoch)) return false;
        e.Owner = owner; e.Epoch = epoch; return true;
    }
    public bool Remove(ulong id) => entities.Remove(id);
    public void Clear() => entities.Clear();
}

public sealed class SnapshotBuffer
{
    private readonly List<(double Arrival, EntitySnapshot State)> frames = new List<(double, EntitySnapshot)>();
    private uint epoch, sequence, poseRevision;
    private bool initialized;
    public double Delay = .065;
    public double MaxExtrapolation = .075;
    public int Count => frames.Count;
    public bool Discontinuity { get; private set; }
    public bool Push(EntitySnapshot state, double arrival)
    {
        if (initialized && state.Epoch == epoch && !Protocol.Newer(state.Sequence, sequence)) return false;
        if (initialized && state.Epoch != epoch && !Protocol.Newer(state.Epoch, epoch)) return false;
        if (initialized && state.Epoch == epoch && state.PoseRevision != poseRevision && !Protocol.Newer(state.PoseRevision, poseRevision)) return false;
        Discontinuity = initialized && state.Epoch == epoch && state.PoseRevision != poseRevision;
        if (!initialized || state.Epoch != epoch || Discontinuity) frames.Clear();
        initialized = true; epoch = state.Epoch; sequence = state.Sequence; poseRevision = state.PoseRevision;
        // Arrival time is in the local clock domain. Sender clocks need not match.
        frames.Add((arrival, state)); if (frames.Count > 32) frames.RemoveAt(0);
        return true;
    }
    public EntitySnapshot? Sample(double now)
    {
        if (frames.Count == 0) return null;
        double target = now - Delay;
        while (frames.Count > 2 && frames[1].Arrival <= target) frames.RemoveAt(0);
        var a = frames[0];
        if (target <= a.Arrival) return a.State;
        if (frames.Count > 1)
        {
            var b = frames[1];
            if (target <= b.Arrival)
            {
                float t = (float)Math.Max(0, Math.Min(1, (target - a.Arrival) / Math.Max(.00001, b.Arrival - a.Arrival)));
                return Blend(a.State, b.State, t);
            }
        }
        var last = frames[frames.Count - 1];
        float dt = (float)Math.Max(0, Math.Min(MaxExtrapolation, target - last.Arrival));
        var result = Blend(last.State, last.State, 0);
        result.Root = Predict(result.Root, dt);
        for (int i = 0; i < result.Parts.Length; i++) result.Parts[i] = Predict(result.Parts[i], dt);
        return result;
    }
    private static BodyState Predict(BodyState b, float dt)
    {
        b.Pose.Position += b.Velocity * dt;
        float speed = b.AngularVelocity.Length();
        if (speed > .001f) b.Pose.Rotation = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(b.AngularVelocity / speed, speed * dt) * b.Pose.Rotation);
        return b;
    }
    private static Pose Blend(Pose a, Pose b, float t) => new Pose(Vector3.Lerp(a.Position, b.Position, t), Quaternion.Slerp(a.Rotation, b.Rotation, t));
    private static BodyState Blend(BodyState a, BodyState b, float t) => new BodyState { Part = b.Part, Pose = Blend(a.Pose, b.Pose, t), Velocity = Vector3.Lerp(a.Velocity, b.Velocity, t), AngularVelocity = Vector3.Lerp(a.AngularVelocity, b.AngularVelocity, t) };
    private static EntitySnapshot Blend(EntitySnapshot a, EntitySnapshot b, float t)
    {
        var result = new EntitySnapshot { Id = b.Id, Epoch = b.Epoch, Sequence = b.Sequence, PoseRevision = b.PoseRevision, Time = b.Time, Flags = b.Flags, LeftGrip = b.LeftGrip, RightGrip = b.RightGrip, Root = Blend(a.Root, b.Root, t), Head = Blend(a.Head, b.Head, t), LeftHand = Blend(a.LeftHand, b.LeftHand, t), RightHand = Blend(a.RightHand, b.RightHand, t), Parts = new BodyState[b.Parts.Length] };
        for (int i = 0; i < b.Parts.Length; i++)
        {
            var source = b.Parts[i];
            for (int j = 0; j < a.Parts.Length; j++) if (a.Parts[j].Part == source.Part) { source = a.Parts[j]; break; }
            result.Parts[i] = Blend(source, b.Parts[i], t);
        }
        return result;
    }
    public void Clear() { frames.Clear(); initialized = false; Discontinuity = false; }
}
