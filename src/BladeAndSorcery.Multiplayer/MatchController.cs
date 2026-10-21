using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BladeAndSorcery.Multiplayer.Core;
using ThunderRoad;
using UnityEngine;

namespace BladeAndSorcery.Multiplayer;

public enum MultiplayerMode : byte { FreeForAll, Duel, Teams, Coop }
internal sealed class Score
{
    public int Kills, Deaths, Team;
}
internal sealed partial class MatchController
{
    private readonly GameWorld world;
    internal MultiplayerMode Mode = MultiplayerMode.FreeForAll;
    internal readonly Dictionary<ushort, Score> Scores = new Dictionary<ushort, Score>();
    internal bool FriendlyFire;
    internal bool SurviveLimbLoss;
    internal PvpWeaponDamageRule PvpWeaponDamage;
    internal float RespawnSeconds = 4;
    internal int Wave;
    internal int PendingWaveSpawns { get; private set; }
    private uint waveToken;
    private double respawnAt;
    private bool respawning;
    internal MatchController(GameWorld world) { this.world = world; }
    internal void OnDeath(ReplicatedEntity entity, CollisionInstance hit)
    {
        var attacker = hit.sourceColliderGroup?.collisionHandler?.item?.lastHandler?.creature ?? hit.casterHand?.mana?.creature ?? entity.Creature?.lastInteractionCreature;
        ReportDeath(entity, world.Find(attacker)?.Descriptor.Id ?? 0);
    }
    internal void ReportDeath(ReplicatedEntity entity, ulong killer)
    {
        if (entity.Descriptor.Kind != EntityKind.Player) return;
        if (entity.Owned) respawnAt = world.Now + RespawnSeconds;
        if (!world.Session.IsHost) return;
        var victimScore = Get(entity.Descriptor.Owner); victimScore.Deaths++;
        if (killer != 0 && world.Registry.TryGet(killer, out var source) && source.Kind == EntityKind.Player && source.Owner != entity.Descriptor.Owner) Get(source.Owner).Kills++;
        Broadcast();
    }
    private Score Get(ushort id)
    {
        if (!Scores.TryGetValue(id, out var score)) Scores[id] = score = new Score { Team = id % 2 };
        return score;
    }
    internal bool AllowHit(Creature victim, CollisionInstance hit)
    {
        if ((Mode != MultiplayerMode.Teams && Mode != MultiplayerMode.Coop) || FriendlyFire) return true;
        var sourceCreature = hit.sourceColliderGroup?.collisionHandler?.item?.lastHandler?.creature ?? hit.casterHand?.mana?.creature;
        var a = world.Find(sourceCreature); var b = world.Find(victim);
        if (Mode == MultiplayerMode.Coop)
            return a == null || b == null || a.Descriptor.Kind != EntityKind.Player || b.Descriptor.Kind != EntityKind.Player || a.Descriptor.Owner == b.Descriptor.Owner;
        return a == null || b == null || a.Descriptor.Owner == b.Descriptor.Owner || Get(a.Descriptor.Owner).Team != Get(b.Descriptor.Owner).Team;
    }
    internal void Tick(double now)
    {
        if (Player.local?.creature != null && !Player.local.creature.isKilled) ClearNativeDeath();
        if (respawnAt > 0 && now >= respawnAt && !respawning && PlayerSpawner.current != null)
        {
            if (Player.local?.creature != null && !Player.local.creature.isKilled) { respawnAt = 0; return; }
            respawning = true; respawnAt = 0;
            PlayerSpawner.current.SpawnAsync(() => respawning = false);
        }
    }
    internal void Respawn()
    {
        if (!world.Session.Connected || PlayerSpawner.current == null || respawning) return;
        respawning = true;
        if (Player.local?.creature != null && !Player.local.creature.isKilled) Player.local.creature.Kill();
        respawnAt = 0;
        PlayerSpawner.current.SpawnAsync(() => respawning = false);
    }
    internal void SetMode(MultiplayerMode mode)
    {
        if (!world.Session.IsHost) return;
        waveToken++; PendingWaveSpawns = 0;
        Mode = mode; Wave = 0; Scores.Clear(); Broadcast();
    }
    internal void Clear()
    {
        ClearNativeDeath();
        waveToken++; PendingWaveSpawns = 0; Wave = 0;
        respawnAt = 0; respawning = false; Scores.Clear();
    }
    internal void StartWave()
    {
        if (!world.Session.IsHost || Mode != MultiplayerMode.Coop || Player.local?.creature == null || PendingWaveSpawns > 0) return;
        if (world.Entities.Values.Any(e => e.Descriptor.Kind == EntityKind.Creature && e.Creature && !e.Creature!.isKilled)) return;
        var players = world.Entities.Values.Where(e => e.Descriptor.Kind == EntityKind.Player && e.Creature && !e.Creature!.isKilled).OrderBy(e => e.Descriptor.Owner).Select(e => e.Creature!.transform.position).ToArray();
        if (players.Length == 0) return;
        if (!Catalog.TryGetData<CreatureData>("HumanMale", out var data, false)) return;
        // Supply this wave's loadout once, instead of also loading HumanMale's
        // random catalog container while our explicit sword is being spawned.
        var waveData = (CreatureData)data.Clone(); waveData.containerID = "None";
        Wave++; int count = Math.Min(8, 1 + Wave);
        PendingWaveSpawns = count;
        int generation = world.Generation; uint token = ++waveToken;
        bool Current() => world.IsCurrent(generation) && token == waveToken && Mode == MultiplayerMode.Coop;
        for (int i = 0; i < count; i++)
        {
            var origin = players[i % players.Length];
            float angle = i * Mathf.PI * 2 / count;
            var point = origin + new Vector3(Mathf.Sin(angle), 0, Mathf.Cos(angle)) * 5;
            if (UnityEngine.AI.NavMesh.SamplePosition(point, out var nav, 5, UnityEngine.AI.NavMesh.AllAreas)) point = nav.position;
            waveData.SpawnAsync(point, angle * Mathf.Rad2Deg + 180, pooled: false, callback: c =>
            {
                if (!Current()) { if (c != null) c.Despawn(); return; }
                PendingWaveSpawns--;
                if (c == null) return;
                c.SetFaction(3); world.Register(c, EntityKind.Creature);
                var sword = Catalog.GetDataList<ItemData>().FirstOrDefault(d => d.id == "SwordShortCommon" || d.id == "SwordShort");
                sword?.SpawnAsync(item =>
                {
                    if (item == null) return;
                    if (!Current() || c == null || c.isKilled) { item.Despawn(); return; }
                    c.handRight.Grab(item.GetMainHandle(Side.Right), true); world.Register(item);
                }, c.handRight.grip.position, pooled: false);
            });
        }
        Broadcast();
    }
    internal void Broadcast()
    {
        if (!world.Session.IsHost || !world.Session.Connected) return;
        foreach (var id in Scores.Keys.Where(id => !world.Session.Peers.ContainsKey(id)).ToArray()) Scores.Remove(id);
        foreach (var peer in world.Session.Peers.Keys) Get(peer);
        world.Session.Publish(MessageKind.Match, Protocol.Encode(w =>
        {
            w.Write((byte)Mode); w.Write(FriendlyFire); w.Write(SurviveLimbLoss); w.Write((byte)PvpWeaponDamage); w.Write(RespawnSeconds); w.Write(Wave); w.Write((byte)Scores.Count);
            foreach (var entry in Scores) { w.Write(entry.Key); w.Write(entry.Value.Kills); w.Write(entry.Value.Deaths); w.Write(entry.Value.Team); }
        }));
    }
    internal void Receive(BinaryReader reader)
    {
        var mode = (MultiplayerMode)reader.ReadByte(); bool ff = reader.ReadBoolean(); bool survive = reader.ReadBoolean(); var damage=(PvpWeaponDamageRule)reader.ReadByte(); float delay = reader.ReadSingle(); int wave = reader.ReadInt32(); int count = reader.ReadByte();
        if (mode > MultiplayerMode.Coop || damage>PvpWeaponDamageRule.NpcStyle || !Protocol.Finite(delay) || delay < 0 || delay > 120 || count > Protocol.MaxPeers) throw new InvalidDataException("Invalid match state");
        var scores = new Dictionary<ushort, Score>();
        for (int i = 0; i < count; i++) scores.Add(reader.ReadUInt16(), new Score { Kills = reader.ReadInt32(), Deaths = reader.ReadInt32(), Team = reader.ReadInt32() });
        Protocol.End(reader);
        Mode = mode; FriendlyFire = ff; SurviveLimbLoss=survive; PvpWeaponDamage=damage; RespawnSeconds = delay; Wave = wave; Scores.Clear(); foreach (var s in scores) Scores.Add(s.Key, s.Value);
    }
}
