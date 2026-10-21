using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;

namespace BladeAndSorcery.Multiplayer.Core;

public enum EntityKind : byte { Player, Item, Creature }
public enum MessageKind : byte { Hello = 1, Welcome, Joined, Left, Spawn, Despawn, State, OwnershipRequest, OwnershipGrant, Gameplay, Resync, Match, Ping, Scene, SceneRequest, SceneReady, ArenaSpawn, AuthChallenge, Rejected, ScenePrepare, ScenePrepared, SceneNotice, NativeWaveRequest, NativeWaveState }
public enum ActionKind : byte { Grab, Release, DamageRequest, Health, Slice, Death, Respawn, Imbue, Spell, Break, Snap, Unsnap, Telekinesis, Penetration, ModEvent }

public sealed class EntityDescriptor
{
    public ulong Id;
    public ushort Owner;
    public uint Epoch;
    public EntityKind Kind;
    public string CatalogId = "";
    public string SceneKey = "";
    public float Height = 1.75f;
    public string Appearance = "";
    public string ItemState = "";
    public Pose Pose;
    public float Health = 100, MaxHealth = 100;
    public bool Killed;
    public bool ProceduralNpc;
    public int[] SlicedParts = Array.Empty<int>();
}

public struct Pose
{
    public Vector3 Position;
    public Quaternion Rotation;
    public Pose(Vector3 position, Quaternion rotation) { Position = position; Rotation = rotation; }
}

public struct BodyState
{
    public int Part;
    public Pose Pose;
    public Vector3 Velocity, AngularVelocity;
}

public sealed class EntitySnapshot
{
    public ulong Id;
    public uint Epoch, Sequence, PoseRevision;
    public double Time;
    public BodyState Root;
    public Pose Head, LeftHand, RightHand;
    public ushort Flags;
    public GripState LeftGrip, RightGrip;
    public BodyState[] Parts = Array.Empty<BodyState>();
}

public struct GripState
{
    public bool Active;
    public Vector3 ConnectedAnchor;
    public float PositionSpring, PositionDamper, PositionMaxForce;
    public float RotationSpring, RotationDamper, RotationMaxForce;
    public byte AngularMask;
}

public sealed class GameplayAction
{
    public ulong EventId, Entity, Other;
    public uint Epoch;
    public ActionKind Kind;
    public int Part, Handle, Side;
    public float Value;
    public Vector3 Position, Direction;
    public string Text = "";
}

public sealed class PeerInfo
{
    public ushort Id;
    public string Name = "";
}

public static class Protocol
{
    public const ushort Version = 8;
    public static bool ApplicationMessage(MessageKind kind) => kind >= MessageKind.Spawn && kind <= MessageKind.ArenaSpawn || kind >= MessageKind.ScenePrepare && kind <= MessageKind.NativeWaveState;
    public const uint Magic = 0x504D5342;
    public const int MaxPeers = 8, MaxParts = 20, MaxEntities = 512;
    public const int HeaderBytes = 13;
    public const int MaxPacket = 96 * 1024, MaxSnapshot = 1000;
    public static bool Newer(uint candidate, uint previous) => unchecked((int)(candidate - previous)) > 0;
    public static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    public static byte[] Encode(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        write(writer);
        if (stream.Length > MaxPacket) throw new InvalidDataException("Packet exceeds protocol limit");
        return stream.ToArray();
    }
    public static BinaryReader Read(byte[] bytes)
    {
        if (bytes.Length > MaxPacket) throw new InvalidDataException("Oversize packet");
        return new BinaryReader(new MemoryStream(bytes, false), Encoding.UTF8);
    }
    public static void End(BinaryReader r) { if (r.BaseStream.Position != r.BaseStream.Length) throw new InvalidDataException("Trailing packet data"); }
    public static void String(BinaryWriter w, string value, int max = 128)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > max) throw new InvalidDataException("String exceeds protocol limit");
        w.Write((ushort)bytes.Length); w.Write(bytes);
    }
    public static string String(BinaryReader r, int max = 128)
    {
        int count = r.ReadUInt16();
        if (count > max || count > r.BaseStream.Length - r.BaseStream.Position) throw new InvalidDataException("Invalid string length");
        return new UTF8Encoding(false, true).GetString(r.ReadBytes(count));
    }
    public static void Vector(BinaryWriter w, Vector3 v) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
    public static Vector3 Vector(BinaryReader r)
    {
        var v = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        if (!Finite(v.X) || !Finite(v.Y) || !Finite(v.Z) || v.LengthSquared() > 1e12f) throw new InvalidDataException("Invalid vector");
        return v;
    }
    public static void Pose(BinaryWriter w, Pose p)
    {
        Vector(w, p.Position);
        var q = Quaternion.Normalize(p.Rotation);
        Quantized(w, q.X, 32767); Quantized(w, q.Y, 32767); Quantized(w, q.Z, 32767); Quantized(w, q.W, 32767);
    }
    public static Pose Pose(BinaryReader r)
    {
        var p = Vector(r);
        var q = new Quaternion(r.ReadInt16() / 32767f, r.ReadInt16() / 32767f, r.ReadInt16() / 32767f, r.ReadInt16() / 32767f);
        float length = q.LengthSquared();
        if (!Finite(length) || length < .5f || length > 1.5f) throw new InvalidDataException("Invalid rotation");
        return new Pose(p, Quaternion.Normalize(q));
    }
    private static void Quantized(BinaryWriter w, float value, float scale)
    {
        if (!Finite(value)) throw new InvalidDataException("Non-finite physical state");
        w.Write((short)Math.Max(short.MinValue, Math.Min(short.MaxValue, Math.Round(value * scale))));
    }
    private static void Velocity(BinaryWriter w, Vector3 v) { Quantized(w, v.X, 100); Quantized(w, v.Y, 100); Quantized(w, v.Z, 100); }
    private static Vector3 Velocity(BinaryReader r) => new Vector3(r.ReadInt16() / 100f, r.ReadInt16() / 100f, r.ReadInt16() / 100f);
    public static void Body(BinaryWriter w, BodyState b) { w.Write(b.Part); Pose(w, b.Pose); Velocity(w, b.Velocity); Velocity(w, b.AngularVelocity); }
    public static BodyState Body(BinaryReader r) => new BodyState { Part = r.ReadInt32(), Pose = Pose(r), Velocity = Velocity(r), AngularVelocity = Velocity(r) };
    public static void Descriptor(BinaryWriter w, EntityDescriptor e)
    {
        w.Write(e.Id); w.Write(e.Owner); w.Write(e.Epoch); w.Write((byte)e.Kind);
        String(w, e.CatalogId); String(w, e.SceneKey, 2048); w.Write(e.Height); String(w, e.Appearance, 60000); Pose(w, e.Pose);
        w.Write(e.Health); w.Write(e.MaxHealth); w.Write(e.Killed); w.Write((byte)e.SlicedParts.Length); foreach (int part in e.SlicedParts) w.Write(part);
        String(w,e.ItemState,24000);
        w.Write(e.ProceduralNpc);
    }
    public static EntityDescriptor Descriptor(BinaryReader r)
    {
        var e = new EntityDescriptor { Id = r.ReadUInt64(), Owner = r.ReadUInt16(), Epoch = r.ReadUInt32(), Kind = (EntityKind)r.ReadByte(), CatalogId = String(r), SceneKey = String(r, 2048), Height = r.ReadSingle(), Appearance = String(r, 60000), Pose = Pose(r) };
        e.Health = r.ReadSingle(); e.MaxHealth = r.ReadSingle(); e.Killed = r.ReadBoolean(); int count = r.ReadByte();
        if (count > MaxParts || !Finite(e.Health) || !Finite(e.MaxHealth) || e.MaxHealth <= 0 || e.MaxHealth > 1000000 || e.Health < 0 || e.Health > e.MaxHealth) throw new InvalidDataException("Invalid initial combat state");
        e.SlicedParts = new int[count]; for (int i = 0; i < count; i++) e.SlicedParts[i] = r.ReadInt32();
        e.ItemState=String(r,24000);
        e.ProceduralNpc=r.ReadBoolean();
        if(e.ProceduralNpc && (e.Kind!=EntityKind.Creature || e.Owner!=1))throw new InvalidDataException("Procedural NPCs must be host-owned creatures");
        if (e.Id == 0 || e.Owner == 0 || e.Epoch == 0 || e.Kind > EntityKind.Creature || !Finite(e.Height) || e.Height < .3f || e.Height > 4) throw new InvalidDataException("Invalid entity descriptor");
        return e;
    }
    public static void Snapshot(BinaryWriter w, EntitySnapshot s)
    {
        if (s.Parts.Length > MaxParts) throw new InvalidDataException("Too many ragdoll parts");
        w.Write(s.Id); w.Write(s.Epoch); w.Write(s.Sequence); w.Write(s.PoseRevision); w.Write(s.Time); w.Write(s.Flags);
        Body(w, s.Root); Pose(w, s.Head); Pose(w, s.LeftHand); Pose(w, s.RightHand);
        Grip(w, s.LeftGrip); Grip(w, s.RightGrip);
        w.Write((byte)s.Parts.Length); foreach (var part in s.Parts) Body(w, part);
    }
    public static EntitySnapshot Snapshot(BinaryReader r)
    {
        var s = new EntitySnapshot { Id = r.ReadUInt64(), Epoch = r.ReadUInt32(), Sequence = r.ReadUInt32(), PoseRevision = r.ReadUInt32(), Time = r.ReadDouble(), Flags = r.ReadUInt16(), Root = Body(r), Head = Pose(r), LeftHand = Pose(r), RightHand = Pose(r) };
        if (s.Flags > 1023) throw new InvalidDataException("Unknown snapshot flags");
        s.LeftGrip = Grip(r); s.RightGrip = Grip(r);
        if (double.IsNaN(s.Time) || double.IsInfinity(s.Time)) throw new InvalidDataException("Invalid timestamp");
        int count = r.ReadByte(); if (count > MaxParts) throw new InvalidDataException("Too many parts");
        s.Parts = new BodyState[count];
        var seen = new HashSet<int>();
        for (int i = 0; i < count; i++) { s.Parts[i] = Body(r); if (!seen.Add(s.Parts[i].Part)) throw new InvalidDataException("Duplicate body part"); }
        return s;
    }
    private static void Grip(BinaryWriter w, GripState grip)
    {
        w.Write(grip.Active); if (!grip.Active) return;
        Vector(w, grip.ConnectedAnchor); w.Write(grip.AngularMask);
        w.Write(grip.PositionSpring); w.Write(grip.PositionDamper); w.Write(grip.PositionMaxForce);
        w.Write(grip.RotationSpring); w.Write(grip.RotationDamper); w.Write(grip.RotationMaxForce);
    }
    private static GripState Grip(BinaryReader r)
    {
        var grip = new GripState { Active = r.ReadBoolean() }; if (!grip.Active) return grip;
        grip.ConnectedAnchor = Vector(r); grip.AngularMask = r.ReadByte();
        grip.PositionSpring = r.ReadSingle(); grip.PositionDamper = r.ReadSingle(); grip.PositionMaxForce = r.ReadSingle();
        grip.RotationSpring = r.ReadSingle(); grip.RotationDamper = r.ReadSingle(); grip.RotationMaxForce = r.ReadSingle();
        if (grip.AngularMask > 15 || !ValidForce(grip.PositionSpring) || !ValidForce(grip.PositionDamper) || !ValidForce(grip.PositionMaxForce) || !ValidForce(grip.RotationSpring) || !ValidForce(grip.RotationDamper) || !ValidForce(grip.RotationMaxForce)) throw new InvalidDataException("Invalid native grip drive");
        return grip;
    }
    private static bool ValidForce(float force) => Finite(force) && force >= 0;
    public static void Action(BinaryWriter w, GameplayAction a)
    {
        w.Write(a.EventId); w.Write(a.Entity); w.Write(a.Other); w.Write(a.Epoch); w.Write((byte)a.Kind);
        w.Write(a.Part); w.Write(a.Handle); w.Write(a.Side); w.Write(a.Value); Vector(w, a.Position); Vector(w, a.Direction); String(w, a.Text, 4096);
    }
    public static GameplayAction Action(BinaryReader r)
    {
        var a = new GameplayAction { EventId = r.ReadUInt64(), Entity = r.ReadUInt64(), Other = r.ReadUInt64(), Epoch = r.ReadUInt32(), Kind = (ActionKind)r.ReadByte(), Part = r.ReadInt32(), Handle = r.ReadInt32(), Side = r.ReadInt32(), Value = r.ReadSingle(), Position = Vector(r), Direction = Vector(r), Text = String(r, 4096) };
        if (a.EventId == 0 || a.Kind > ActionKind.ModEvent || !Finite(a.Value) || Math.Abs(a.Value) > 1e7f) throw new InvalidDataException("Invalid gameplay action");
        return a;
    }
}
