using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BladeAndSorcery.Multiplayer.Core;
using Newtonsoft.Json;
using ThunderRoad;
using UnityEngine;
using Pose = BladeAndSorcery.Multiplayer.Core.Pose;

namespace BladeAndSorcery.Multiplayer;

internal sealed class AppearanceData
{
    public int Faction = 2;
    public string EthnicGroup = "";
    public float[] Skin = Array.Empty<float>(), Hair = Array.Empty<float>(), HairSecondary = Array.Empty<float>(), HairSpecular = Array.Empty<float>(), Iris = Array.Empty<float>(), Sclera = Array.Empty<float>();
    public string[] Wardrobes = Array.Empty<string>();
    public NativeWardrobeState[] WardrobeContents = Array.Empty<NativeWardrobeState>();
    internal static float[] Components(Color color) => new[] { color.r, color.g, color.b, color.a };
    internal static Color Color(float[] c) => c.Length == 4 && c.All(Protocol.Finite) ? new Color(c[0], c[1], c[2], c[3]) : UnityEngine.Color.clear;
}

internal sealed class GripEventData
{
    public int Orientation;
    public bool SelfCollision;
    public float[] Rotation = Array.Empty<float>();
}

internal sealed partial class GameWorld : IDisposable
{
    internal readonly NetworkSession Session;
    internal readonly OwnershipTable Registry = new OwnershipTable();
    internal readonly Dictionary<ulong, ReplicatedEntity> Entities = new Dictionary<ulong, ReplicatedEntity>();
    internal readonly MatchController Match;
    private readonly Dictionary<Creature, ReplicatedEntity> creatures = new Dictionary<Creature, ReplicatedEntity>();
    private readonly Dictionary<Item, ReplicatedEntity> items = new Dictionary<Item, ReplicatedEntity>();
    private readonly Dictionary<ulong, EntitySnapshot> earlySnapshots = new Dictionary<ulong, EntitySnapshot>();
    private readonly List<(ushort Source, GameplayAction Action, double Expiry)> deferred = new List<(ushort, GameplayAction, double)>();
    private readonly EventWindow events = new EventWindow();
    private readonly HashSet<ulong> spawning = new HashSet<ulong>();
    private readonly Dictionary<ulong, GameplayAction> grips = new Dictionary<ulong, GameplayAction>();
    private readonly Dictionary<ulong, GameplayAction> snaps = new Dictionary<ulong, GameplayAction>();
    private readonly Dictionary<ulong, List<GameplayAction>> slices = new Dictionary<ulong, List<GameplayAction>>();
    private uint nextEntity = 1, nextEvent = 1;
    private double nextDiscover, nextSnapshot, nextHeartbeat;
    private double retryResyncAt;
    private int applyDepth;
    private bool disposed;
    internal bool Applying => applyDepth > 0;
    internal int Generation => clearGeneration;
    internal bool IsCurrent(int generation) => !disposed && generation == clearGeneration && Session.Connected;
    internal double Now;
    internal int ContactCount, WeaponContacts, AppliedActions, SentSnapshots, ReceivedSnapshots;
    internal readonly List<(double Time, ulong A, ulong B, float Speed, float Impulse, float GripError)> ContactTrace = new List<(double, ulong, ulong, float, float, float)>();
    internal readonly List<(double Time, ulong A, ulong B, float Impulse, float GripForce)> PhysicsContactTrace = new List<(double, ulong, ulong, float, float)>();
    internal int PhysicsWeaponContacts;
    internal readonly List<(double Time, ulong Item, ulong Victim, int Part, float Speed)> BodyContactTrace = new List<(double, ulong, ulong, int, float)>();
    internal readonly List<(double Time, ulong Victim, float Damage, bool Allowed, string Damager, int Part, float Speed, Vector3 Point)> DamageTrace = new List<(double, ulong, float, bool, string, int, float, Vector3)>();
    internal readonly Dictionary<ulong,string> AiEvaluations = new Dictionary<ulong,string>();
    private readonly Dictionary<(ulong, ulong), double> lastContact = new Dictionary<(ulong, ulong), double>();
    internal int RemoteCount => Entities.Values.Count(e => !e.Owned);
    internal event Action<string>? Log;
    internal GameWorld(NetworkSession session)
    {
        Session = session; Match = new MatchController(this);
        session.Validate = Validate;
        session.Message += Receive; session.PeerJoined += OnPeerJoined; session.PeerLeft += OnPeerLeft;
        EventManager.onLevelUnload += OnLevelUnload;
        EventManager.onItemSpawnEquip += OnEquipped;
        EventManager.OnStashItemSpawn += OnNativeBookItem;
        UIInventory.OnItemRetrieved += OnNativeInventoryItem;
        EventManager.onCreatureKill += OnNativeWaveKill;
        EventManager.onLevelLoad += OnLevelLoaded;
        EventManager.OnItemBrokenStart += OnBreakStarted;
        EventManager.OnItemBrokenEnd += OnBreakFinished;
    }
    internal ReplicatedEntity? Find(Creature? c) => c != null && creatures.TryGetValue(c, out var e) ? e : null;
    internal ReplicatedEntity? Find(Item? item) => item != null && items.TryGetValue(item, out var e) ? e : null;
    internal void Tick(double now)
    {
        Now = now;
        TickProceduralNpcs();
        TickSceneTravel();
        if (!Session.Connected || !sceneReady || LevelManager.isLoadingLocked || !GameManager.initialized || Level.current == null) return;
        if (retryResyncAt > 0 && now >= retryResyncAt)
        {
            retryResyncAt=0; Session.Publish(MessageKind.Resync,Array.Empty<byte>());
        }
        if (now >= nextDiscover)
        {
            nextDiscover = now + .25;
            RecoverMissingRemotePlayers();
            RecoverMissingRemoteItems();
            RemoveForeignSceneSpawns();
            if (Player.local?.creature != null && Find(Player.local.creature) == null) { PrepareLocalPlayerSpawn(); Register(Player.local.creature, EntityKind.Player); localPlayerRegistered = true; }
            if (spawning.Count == 0)
            {
                foreach (var item in Item.allActive.ToArray())
                {
                    if (item == null || !item.loaded || item.data == null || Find(item) != null) continue;
                    if(ForeignUnboundFragment(item)) continue;
                    bool localHeld = item.handlers.Any(h => h.creature == Player.local?.creature);
                    bool localHolstered = item.holder != null && item.holder.GetRootHolder().creature == Player.local?.creature;
                    if ((Session.IsHost || localHeld || localHolstered || HasLocalOrigin(item)) && Registry.Count < Protocol.MaxEntities - Protocol.MaxPeers) Register(item);
                }
                if (Session.IsHost)
                    foreach (var c in Creature.allActive.ToArray())
                        if (c != null && c.loaded && c != Player.local?.creature && Find(c) == null) Register(c, EntityKind.Creature);
            }
            ReconcileOwnedGrips();
        }
        if (now >= nextSnapshot)
        {
            nextSnapshot = now + 1.0 / 30;
            foreach (var e in Entities.Values.ToArray())
            {
                if (!e.Owned || e.Transform == null || !e.TeleportPoseReady || IsBrokenRoot(e.Descriptor.Id)) continue;
                var snapshot = e.Capture(now);
                var bytes = Protocol.Encode(w => Protocol.Snapshot(w, snapshot));
                if (bytes.Length + Protocol.HeaderBytes <= Protocol.MaxSnapshot) { Session.Publish(MessageKind.State, bytes, false); SentSnapshots++; }
            }
        }
        foreach (var descriptor in pendingSceneSpawns.Values.ToArray()) { pendingSceneSpawns.Remove(descriptor.Id); SpawnRemote(descriptor); }
        SpawnPendingBreakFragments();
        TickCompatibleItems();
        if (now >= nextHeartbeat)
        {
            nextHeartbeat = now + 1;
            foreach (var e in Entities.Values.ToArray()) if (e.Owned && e.Creature) SendHealth(e);
            SyncImbues();
        }
        for (int i = 0; i < deferred.Count;)
        {
            var d = deferred[i];
            if (d.Expiry < now) { deferred.RemoveAt(i); Log?.Invoke("Expired action waiting for entity " + d.Action.Entity); continue; }
            if (Ready(d.Action)) { deferred.RemoveAt(i); ApplyAction(d.Source, d.Action); }
            else i++;
        }
        TickPlayerCutRequests();TickMirroredPenetrations();TickPlayerWeaponDamage();TickRemoteSlices();
        Match.Tick(now);
        TickNativeItemStorage();
        TickNativeWaves();
        TickNpcEffects();
    }
    internal void FixedTick(double now)
    {
        foreach (var entity in Entities.Values.ToArray()) entity.FixedTick(now);
    }
    private EntityDescriptor NewDescriptor(EntityKind kind, string catalogId, Transform transform) => new EntityDescriptor
    {
        Id = ((ulong)Session.LocalId << 32) | nextEntity++, Owner = Session.LocalId, Epoch = 1,
        Kind = kind, CatalogId = catalogId, Pose = transform.NetPose()
    };
    internal ReplicatedEntity Register(Creature creature, EntityKind kind,bool proceduralNpc=false)
    {
        var existing = Find(creature); if (existing != null) return existing;
        var d = NewDescriptor(kind, creature.data.id, creature.transform);
        d.ProceduralNpc=proceduralNpc;
        d.Health = creature.currentHealth; d.MaxHealth = creature.maxHealth; d.Killed = creature.isKilled;
        d.Height = Mathf.Clamp(creature.GetHeight(), .3f, 4);
        var custom = kind == EntityKind.Player ? Player.characterData?.customization : null;
        d.Appearance = JsonConvert.SerializeObject(new AppearanceData
        {
            Faction = creature.factionId,
            EthnicGroup = custom?.ethnicGroupId ?? "",
            Skin = custom != null ? AppearanceData.Components(custom.skinColor) : Array.Empty<float>(),
            Hair = custom != null ? AppearanceData.Components(custom.hairColor) : Array.Empty<float>(),
            HairSecondary = custom != null ? AppearanceData.Components(custom.hairSecondaryColor) : Array.Empty<float>(),
            HairSpecular = custom != null ? AppearanceData.Components(custom.hairSpecularColor) : Array.Empty<float>(),
            Iris = custom != null ? AppearanceData.Components(custom.eyesIrisColor) : Array.Empty<float>(),
            Sclera = custom != null ? AppearanceData.Components(custom.eyesScleraColor) : Array.Empty<float>(),
            Wardrobes = creature.equipment.GetWornContents().Select(w => w.referenceID).ToArray(),
            WardrobeContents = CaptureWardrobeContents(creature)
        });
        var entity = new ReplicatedEntity(this, d); Registry.Add(d); Entities.Add(d.Id, entity); creatures.Add(creature, entity);
        entity.Bind(creature, false); PublishSpawn(d);
        // Include native existing grips in catch-up after the item descriptors exist.
        if (creature.handLeft.grabbedHandle) HandGrab(entity, Side.Left, creature.handLeft.grabbedHandle, creature.handLeft.gripInfo?.axisPosition ?? 0, creature.handLeft.gripInfo?.orientation);
        if (creature.handRight.grabbedHandle) HandGrab(entity, Side.Right, creature.handRight.grabbedHandle, creature.handRight.gripInfo?.axisPosition ?? 0, creature.handRight.gripInfo?.orientation);
        return entity;
    }
    internal ReplicatedEntity Register(Item item)
    {
        var existing = Find(item); if (existing != null) return existing;
        RememberLocalOrigin(item);
        var d = NewDescriptor(EntityKind.Item, item.data.id, item.transform);
        d.SceneKey = GameMath.SceneItemKey(item);
        var entity = new ReplicatedEntity(this, d); Registry.Add(d); Entities.Add(d.Id, entity); items.Add(item, entity);
        entity.Bind(item, false); PublishSpawn(d);
        if (item.holder != null) ItemSnap(entity, item.holder, true);
        return entity;
    }
    private void PublishSpawn(EntityDescriptor d)
    {
        if (Entities.TryGetValue(d.Id,out var entity))
        {
            if (entity.Transform != null) d.Pose=entity.Transform.NetPose();
            if (entity.Creature != null)
            {
                d.Health=entity.Creature.currentHealth; d.MaxHealth=entity.Creature.maxHealth; d.Killed=entity.Creature.isKilled;
                d.SlicedParts=entity.Creature.ragdoll.parts.Where(p=>p.isSliced).Select(GameMath.PartKey).ToArray();
            }
            if(entity.Item!=null && entity.Owned)d.ItemState=CaptureItemState(entity);
        }
        Session.Publish(MessageKind.Spawn, Protocol.Encode(w => Protocol.Descriptor(w, d)));
    }
    private void PublishOwnedCatchup()
    {
        foreach(var entity in Entities.Values.ToArray()) if(entity.Owned) PublishSpawn(entity.Descriptor);
        foreach(var cached in grips.Values.Concat(snaps.Values).Concat(penetrations.Values).ToArray())
            if(Entities.TryGetValue(cached.Entity,out var actor) && actor.Owned)
            {
                if(cached.Kind == ActionKind.Grab)
                {
                    var pose=actor.HandTarget((Side)cached.Side); var data=JsonConvert.DeserializeObject<GripEventData>(cached.Text)!;
                    cached.Position=pose.Position;cached.Text=GripEvent(data.Orientation,pose.Rotation,actor.GripSelfCollision);
                }
                SendAction(actor,cached);
            }
    }
    private bool Validate(ushort source, MessageKind kind, byte[] payload)
    {
        try
        {
            using var r = Protocol.Read(payload);
            switch (kind)
            {
                case MessageKind.Spawn:
                    var d = Protocol.Descriptor(r); Protocol.End(r);
                    bool known = Registry.TryGet(d.Id, out var previous);
                    return ValidateItemState(d) && d.Owner == source && (known ? previous.Owner == source && previous.Epoch == d.Epoch && previous.Kind == d.Kind && previous.CatalogId == d.CatalogId : (ushort)(d.Id >> 32) == source && d.Epoch == 1) &&
                        (d.Kind != EntityKind.Creature || source == 1) && (!known || !previous.ProceduralNpc || d.ProceduralNpc) && Registry.Count < Protocol.MaxEntities;
                case MessageKind.State:
                    var s = Protocol.Snapshot(r); Protocol.End(r); return Registry.Owns(s.Id, source, s.Epoch);
                case MessageKind.Despawn:
                    ulong id = r.ReadUInt64(); uint epoch = r.ReadUInt32(); Protocol.End(r); return Registry.Owns(id, source, epoch);
                case MessageKind.OwnershipRequest:
                    ulong requested = r.ReadUInt64(); uint expected = r.ReadUInt32(); Protocol.End(r);
                    return Registry.TryGet(requested, out var target) && target.Kind == EntityKind.Item && target.Epoch == expected;
                case MessageKind.OwnershipGrant:
                    return source == 1;
                case MessageKind.Gameplay:
                    var a = Protocol.Action(r); Protocol.End(r);
                    return (ushort)(a.EventId >> 32) == source && Registry.Owns(a.Entity, source, a.Epoch) &&
                        (a.Kind != ActionKind.DamageRequest || ValidatePlayerDamageRequest(a)) && (a.Kind != ActionKind.Spell || ValidateNpcEffect(a)) && a.Kind != ActionKind.Telekinesis &&
                        (a.Kind != ActionKind.Break || ValidateBreak(a)) &&
                        (a.Kind != ActionKind.ModEvent || ValidateModEvent(a)) &&
                        (a.Kind != ActionKind.Grab || ValidateGrip(a)) &&
                        (a.Kind != ActionKind.Penetration || ValidatePenetration(a));
                case MessageKind.Resync:
                    Protocol.End(r); return true;
                case MessageKind.Match:
                    return source == 1;
                case MessageKind.Scene: return source == 1;
                case MessageKind.SceneRequest: ReadScene(r); Protocol.End(r); return true;
                case MessageKind.SceneReady: r.ReadUInt32(); Protocol.End(r); return true;
                case MessageKind.ScenePrepare: return source==1;
                case MessageKind.ScenePrepared: r.ReadUInt32();r.ReadBoolean();Protocol.String(r,1024);Protocol.End(r);return true;
                case MessageKind.SceneNotice: return source==1;
                case MessageKind.NativeWaveRequest: return ValidateNativeWaveRequest(source,r);
                case MessageKind.NativeWaveState: return source==1;
                case MessageKind.ArenaSpawn: return source == 1;
                default: return false;
            }
        }
        catch (Exception e) when (e is IOException || e is InvalidDataException || e is ArgumentException || e is JsonException) { Log?.Invoke("Invalid game message: " + e.Message); return false; }
    }
    private void Receive(ushort source, MessageKind kind, byte[] bytes)
    {
        if (disposed) return;
        try
        {
            using var r = Protocol.Read(bytes);
            switch (kind)
            {
                case MessageKind.Scene: ReceiveScene(source, r); break;
                case MessageKind.SceneRequest:
                    var requestedScene = ReadScene(r); Protocol.End(r);
                    if (Session.IsHost) ProposeScene(requestedScene); break;
                case MessageKind.SceneReady:
                    ReceiveSceneReady(source,r);break;
                case MessageKind.ScenePrepare: ReceiveScenePrepare(source,r);break;
                case MessageKind.ScenePrepared: ReceiveScenePrepared(source,r);break;
                case MessageKind.SceneNotice: if(source==1)SetTravelStatus(Protocol.String(r,1024));Protocol.End(r);break;
                case MessageKind.NativeWaveRequest: ReceiveNativeWaveRequest(source,r);break;
                case MessageKind.NativeWaveState: ReceiveNativeWaveState(source,r);break;
                case MessageKind.ArenaSpawn: ReceiveArenaSpawn(source,r); break;
                case MessageKind.Spawn:
                    var d = Protocol.Descriptor(r); Protocol.End(r);
                    if (Registry.TryGet(d.Id, out var existingDescriptor))
                    {
                        if(existingDescriptor.Owner!=d.Owner || existingDescriptor.Epoch!=d.Epoch)break;
                        if(d.ProceduralNpc && !existingDescriptor.ProceduralNpc)
                        {
                            existingDescriptor.ProceduralNpc=true;
                            if(Entities.TryGetValue(d.Id,out var proceduralEntity))proceduralEntity.EnableProceduralReplica();
                        }
                        if(existingDescriptor.ItemState!=d.ItemState)
                        {
                            existingDescriptor.ItemState=d.ItemState;
                            if(Entities.TryGetValue(d.Id,out var stateEntity) && !stateEntity.Owned)ApplyItemState(stateEntity);
                        }
                        if(existingDescriptor.Appearance!=d.Appearance && existingDescriptor.Owner==source)
                        {
                            existingDescriptor.Appearance=d.Appearance;
                            if(Entities.TryGetValue(d.Id,out var appearanceEntity) && !appearanceEntity.Owned)
                            {if(appearanceEntity.Item!=null)ApplyNpcProjectile(appearanceEntity);else ApplyUpdatedAppearance(appearanceEntity);}
                        }
                        break;
                    }
                    if (!Registry.Add(d)) break;
                    if (sceneReady) SpawnRemote(d); else pendingSceneSpawns[d.Id] = d; break;
                case MessageKind.State:
                    var s = Protocol.Snapshot(r); Protocol.End(r);
                    if (!Registry.Owns(s.Id, source, s.Epoch)) break;
                    if (Entities.TryGetValue(s.Id, out var e) && !e.Owned)
                    {
                        if (e.ReceiveSnapshot(s, Now)) ReceivedSnapshots++;
                    }
                    else if (!Entities.ContainsKey(s.Id) && earlySnapshots.Count < Protocol.MaxEntities) earlySnapshots[s.Id] = s;
                    break;
                case MessageKind.Despawn:
                    ulong id = r.ReadUInt64(); uint epoch = r.ReadUInt32(); Protocol.End(r);
                    if (Registry.Owns(id, source, epoch)) Remove(id, source != Session.LocalId); break;
                case MessageKind.Gameplay:
                    var action = Protocol.Action(r); Protocol.End(r);
                    if (!Registry.Owns(action.Entity, source, action.Epoch) || !events.Accept(action.EventId)) break;
                    if (!Ready(action)) deferred.Add((source, action, Now + 15)); else ApplyAction(source, action);
                    break;
                case MessageKind.OwnershipRequest:
                    ulong requested = r.ReadUInt64(); uint requestedEpoch = r.ReadUInt32(); Protocol.End(r);
                    if (Session.IsHost && Registry.TryGet(requested, out var owned) && owned.Epoch == requestedEpoch)
                    {
                        bool free = !Entities.TryGetValue(requested, out var itemEntity) || itemEntity.Item == null || itemEntity.Item.handlers.All(h => Find(h.creature)?.Descriptor.Owner == source);
                        if (free) Registry.Transfer(requested, source, requestedEpoch);
                        Session.Publish(MessageKind.OwnershipGrant, Protocol.Encode(w => { w.Write(requested); w.Write(owned.Owner); w.Write(owned.Epoch); }));
                    }
                    break;
                case MessageKind.OwnershipGrant:
                    if (source != 1) break;
                    ulong granted = r.ReadUInt64(); ushort owner = r.ReadUInt16(); uint newEpoch = r.ReadUInt32(); Protocol.End(r);
                    Registry.ApplyTransfer(granted, owner, newEpoch);
                    if (Entities.TryGetValue(granted, out var changed)) changed.Buffer.Clear();
                    break;
                case MessageKind.Resync:
                    if (Session.IsHost) { if(source != Session.LocalId) SendCatchup(source); }
                    else if(source == 1) PublishOwnedCatchup();
                    break;
                case MessageKind.Match:
                    if (source == 1) Match.Receive(r); break;
            }
        }
        catch (Exception exception) { Log?.Invoke("Message failed: " + exception); }
    }
    private void SpawnRemote(EntityDescriptor descriptor)
    {
        if(WaitForBreakParent(descriptor)) return;
        if (!spawning.Add(descriptor.Id)) return;
        try { BeginRemoteSpawn(descriptor); }
        catch(Exception exception) { SpawnFailed(descriptor,"Remote spawn setup failed: " + exception); }
    }
    private void BeginRemoteSpawn(EntityDescriptor descriptor)
    {
        int generation = clearGeneration;
        if (descriptor.Kind == EntityKind.Item)
        {
            string spawnedItemState=descriptor.ItemState;
            if(BindExistingBrokenRoot(descriptor)) {spawning.Remove(descriptor.Id);return;}
            if (!Catalog.TryGetData<ItemData>(descriptor.CatalogId, out var data, false)) { SpawnFailed(descriptor, "Item catalog entry missing"); return; }
            // Spawner identity survives settling physics and avoids duplicate map props
            // when the two native scenes start with slightly different positions.
            var match = Item.allActive.FirstOrDefault(i => i != null && i.loaded && i.data != null && Find(i) == null &&
                (descriptor.SceneKey.Length > 0 ? GameMath.SceneItemKey(i) == descriptor.SceneKey :
                    i.data.id == descriptor.CatalogId && i.handlers.Count == 0 && i.holder == null && Vector3.Distance(i.transform.position, descriptor.Pose.Position.Game()) < .2f));
            if (match != null && match.data.id != descriptor.CatalogId) { match.Despawn(); match = null; }
            if (match != null) { BindItem(match); return; }
            data.SpawnAsync(BindItem, descriptor.Pose.Position.Game(), descriptor.Pose.Rotation.Game(), pooled: false,customDataList:SpawnCustomData(descriptor));
            void BindItem(Item item)
            {
                spawning.Remove(descriptor.Id);
                if(item == null) { SpawnFailed(descriptor,"Native item spawn returned no item"); return; }
                if (disposed || generation != clearGeneration || !Registry.TryGet(descriptor.Id, out _)) { item.Despawn(); return; }
                var entity = new ReplicatedEntity(this, descriptor); Entities.Add(descriptor.Id, entity); items[item] = entity;
                entity.Bind(item, match == null);ApplyItemState(entity,match==null && descriptor.ItemState==spawnedItemState); ApplyEarly(entity);
                if(descriptor.SceneKey.Length>0) GameMath.SetFragmentKey(item,descriptor.SceneKey,false);
            }
        }
        else
        {
            if (!Catalog.TryGetData<CreatureData>(descriptor.CatalogId, out var data, false)) { SpawnFailed(descriptor, "Creature catalog entry missing"); return; }
            // Equipment comes from network descriptors. Loading the catalog's default
            // container here would create a second, unrelated set of inventory items.
            var replicaData = (CreatureData)data.Clone(); replicaData.containerID = "None";
            replicaData.SpawnAsync(descriptor.Pose.Position.Game(), descriptor.Pose.Rotation.Game().eulerAngles.y, pooled: false, callback: c =>
            {
                spawning.Remove(descriptor.Id);
                if (disposed || generation != clearGeneration || !Registry.TryGet(descriptor.Id, out _)) { c.Despawn(); return; }
                try
                {
                    c.SetHeight(descriptor.Height);
                    // Observe native apparel updates before applying appearance.
                    // Mesh cuts wait for their completion and run one at a time.
                    var entity = new ReplicatedEntity(this, descriptor); Entities.Add(descriptor.Id, entity); creatures[c] = entity;
                    entity.Bind(c, true); ApplyEarly(entity);
                    var appearance = JsonConvert.DeserializeObject<AppearanceData>(descriptor.Appearance, new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.None, MaxDepth = 8 });
                    if (appearance != null && !string.IsNullOrEmpty(appearance.EthnicGroup))
                    {
                        c.SetEthnicGroupFromId(appearance.EthnicGroup);
                        c.SetColor(AppearanceData.Color(appearance.Skin), Creature.ColorModifier.Skin);
                        c.SetColor(AppearanceData.Color(appearance.Hair), Creature.ColorModifier.Hair);
                        c.SetColor(AppearanceData.Color(appearance.HairSecondary), Creature.ColorModifier.HairSecondary);
                        c.SetColor(AppearanceData.Color(appearance.HairSpecular), Creature.ColorModifier.HairSpecular);
                        c.SetColor(AppearanceData.Color(appearance.Iris), Creature.ColorModifier.EyesIris);
                        c.SetColor(AppearanceData.Color(appearance.Sclera), Creature.ColorModifier.EyesSclera, true);
                    }
                    if (appearance != null)
                    {
                        c.SetFaction(appearance.Faction);
                        c.equipment.UnequipAllWardrobes();
                        foreach (var wardrobe in appearance.Wardrobes.Take(32))
                            if (Catalog.TryGetData<ItemData>(wardrobe, out _, false)) c.equipment.EquipWardrobe(WardrobeContent(appearance,wardrobe));
                    }
                    c.maxHealth = descriptor.MaxHealth; c.currentHealth = descriptor.Health;
                    applyDepth++;
                    try
                    {
                        foreach (var part in descriptor.SlicedParts.Select(k => GameMath.FindPart(c,k)).Where(p => p != null).OrderByDescending(p => p!.sliceChildAndDisableSelf != null))
                            ApplyNativeSlice(entity,part!);
                        if (descriptor.Killed && !c.isKilled) c.Kill();
                    }
                    finally { applyDepth--; }
                    // A repaired player replica must regain its reliable grips;
                    // otherwise its owned weapons remain detached locally.
                    foreach(var grip in grips.Values.Where(g=>g.Entity==descriptor.Id).ToArray())
                        if(Ready(grip)) ApplyAction(descriptor.Owner,grip);
                    Log?.Invoke("Spawned remote " + descriptor.Kind + " " + descriptor.Id);
                }
                catch (Exception ex)
                {
                    if(Entities.ContainsKey(descriptor.Id)) Remove(descriptor.Id,true); else c.Despawn();
                    SpawnFailed(descriptor,"Remote creature setup failed: " + ex);
                }
            });
        }
    }
    private void SpawnFailed(EntityDescriptor descriptor, string reason)
    {
        spawning.Remove(descriptor.Id); Registry.Remove(descriptor.Id); Log?.Invoke(reason + ": " + descriptor.CatalogId);
        if(retryResyncAt == 0) retryResyncAt=Now+1;
    }
    private void ApplyEarly(ReplicatedEntity entity)
    {
        if (earlySnapshots.TryGetValue(entity.Descriptor.Id, out var state))
        {
            entity.ReceiveSnapshot(state,Now);earlySnapshots.Remove(entity.Descriptor.Id);
        }
    }
    private bool Ready(GameplayAction a) => Entities.ContainsKey(a.Entity) && (a.Other == 0 || Entities.ContainsKey(a.Other));
    internal void SendAction(ReplicatedEntity entity, GameplayAction action)
    {
        if (Applying || !entity.Owned || disposed) return;
        if (nextEvent == uint.MaxValue) throw new InvalidOperationException("Gameplay event sequence exhausted; reconnect to create a new session identity");
        action.EventId = ((ulong)Session.LocalId << 32) | nextEvent++;
        action.Entity = entity.Descriptor.Id; action.Epoch = entity.Descriptor.Epoch;
        RememberConstraint(action);
        Session.Publish(MessageKind.Gameplay, Protocol.Encode(w => Protocol.Action(w, action)));
    }
    internal void SendHealth(ReplicatedEntity entity)
    {
        if (entity.Creature) SendAction(entity, new GameplayAction { Kind = ActionKind.Health, Value = entity.Creature!.currentHealth, Other = 0 });
    }
    internal void HandGrab(ReplicatedEntity actor, Side side, Handle handle, float axis, HandlePose? orientation)
    {
        var heldItem=handle.item;
        if(heldItem!=null && Find(heldItem)==null && ForeignUnboundFragment(heldItem)) return;
        ReplicatedEntity? target = heldItem != null ? Find(heldItem) ?? Register(heldItem) : handle is HandleRagdoll hr ? Find(hr.ragdollPart.ragdoll.creature) : null;
        if (target == null) return;
        if (target.Item && !target.Owned && target.Item!.handlers.All(h => h.creature == actor.Creature))
            Session.Publish(MessageKind.OwnershipRequest, Protocol.Encode(w => { w.Write(target.Descriptor.Id); w.Write(target.Descriptor.Epoch); }));
        int handleIndex = target.Item ? target.Item!.handles.IndexOf(handle) : ((HandleRagdoll)handle).ragdollPart.handles.IndexOf((HandleRagdoll)handle);
        int orientationIndex = handle.orientations.IndexOf(orientation!);
        var pose = actor.HandTarget(side);
        var action = new GameplayAction { Kind = ActionKind.Grab, Other = target.Descriptor.Id, Side = (int)side, Handle = handleIndex, Part = handle is HandleRagdoll ragdollHandle ? GameMath.PartKey(ragdollHandle.ragdollPart) : 0, Value = axis, Position=pose.Position, Text = GripEvent(orientationIndex,pose.Rotation,actor.GripSelfCollision) };
        grips[(actor.Descriptor.Id << 1) | (uint)side] = action;
        SendAction(actor, action);
    }
    internal void HandRelease(ReplicatedEntity actor, Side side, Handle handle, bool throwing)
    {
        grips.Remove((actor.Descriptor.Id << 1) | (uint)side);
        var target = handle.item ? Find(handle.item) : null;
        SendAction(actor, new GameplayAction { Kind = ActionKind.Release, Other = target?.Descriptor.Id ?? 0, Side = (int)side, Value = throwing ? 1 : 0 });
    }
    internal void ItemSnap(ReplicatedEntity item, Holder holder, bool snapped)
    {
        var parent = holder.creature ? Find(holder.creature) : Find(holder.parentItem);
        if (parent == null) return;
        var holders = holder.creature ? holder.creature.holders : holder.parentItem.GetComponentsInChildren<Holder>().ToList();
        SendAction(item, new GameplayAction { Kind = snapped ? ActionKind.Snap : ActionKind.Unsnap, Other = parent.Descriptor.Id, Handle = holders.IndexOf(holder) });
    }
    private void ApplyAction(ushort source, GameplayAction a)
    {
        if (!Entities.TryGetValue(a.Entity, out var entity)) return;
        RememberConstraint(a);
        if (a.Kind == ActionKind.Slice)
        {
            if (!slices.TryGetValue(a.Entity, out var list)) slices[a.Entity] = list = new List<GameplayAction>();
            if (list.All(s => s.Part != a.Part)) list.Add(a);
        }
        if (a.Kind == ActionKind.Respawn) slices.Remove(a.Entity);
        if (source == Session.LocalId) return;
        // A validated contact request is input to the victim owner's native
        // combat. Keep its resulting slice/death callbacks outside replay
        // suppression so the owner publishes those consequences exactly once.
        if(a.Kind==ActionKind.DamageRequest){ApplyPlayerDamageRequest(a);return;}
        if(a.Kind==ActionKind.Spell){ApplyNpcEffect(entity,a);return;}
        applyDepth++;
        try
        {
            AppliedActions++;
            var creature = entity.Creature;
            switch (a.Kind)
            {
                case ActionKind.ModEvent: ApplyModEvent(entity,a);break;
                case ActionKind.Break: ApplyBreakState(entity,a); break;
                case ActionKind.Penetration: QueuePenetration(a); break;
                case ActionKind.Grab:
                    if (!creature || !Entities.TryGetValue(a.Other, out var target)) break;
                    var hand = a.Side == (int)Side.Left ? creature!.handLeft : creature!.handRight;
                    Handle? handle = target.Item ? target.Item!.handles.ElementAtOrDefault(a.Handle) : target.Creature != null ? GameMath.FindPart(target.Creature, a.Part)?.handles.ElementAtOrDefault(a.Handle) : null;
                    if (handle == null || hand.grabbedHandle == handle) break;
                    hand.TryRelease();
                    var grip = JsonConvert.DeserializeObject<GripEventData>(a.Text)!;
                    entity.SourceSelfCollision=grip.SelfCollision;
                    var orientation = handle.orientations.ElementAtOrDefault(grip.Orientation);
                    var controllerPose = new Pose(a.Position,new System.Numerics.Quaternion(grip.Rotation[0],grip.Rotation[1],grip.Rotation[2],grip.Rotation[3]));
                    var body = (handle.customPhysicBody ?? handle.physicBody)?.rigidBody;
                    var beforePose = body != null ? body.transform.NetPose() : default;
                    var beforeVelocity = body != null ? body.velocity : Vector3.zero; var beforeAngular = body != null ? body.angularVelocity : Vector3.zero;
                    bool beforeKinematic=body!=null && body.isKinematic;
                    bool detect = body != null && body.detectCollisions;
                    if (body != null) body.detectCollisions = false;
                    try
                    {
                        hand.Grab(handle, orientation ?? handle.GetDefaultOrientation(hand.side), a.Value, false);
                        entity.AttachNetworkGrip(hand,controllerPose);
                    }
                    finally
                    {
                        // Match native VR gripping: construct the joint in an
                        // aligned temporary pose, then restore the physical item.
                        // NPC alignment must not sweep a sword through somebody
                        // before its network controller target arrives.
                        if (body != null)
                        {
                            // Native NPC gripping makes its target dynamic. A
                            // replicated grab must not switch an owner's tracked
                            // player limb out of the native player physics mode.
                            if(target.Descriptor.Kind==EntityKind.Player) body.isKinematic=beforeKinematic;
                            body.transform.SetPositionAndRotation(beforePose.Position.Game(),beforePose.Rotation.Game());
                            body.position=body.transform.position; body.rotation=body.transform.rotation;
                            if (!body.isKinematic) { body.velocity=beforeVelocity; body.angularVelocity=beforeAngular; }
                            body.detectCollisions=detect;
                        }
                    }
                    break;
                case ActionKind.Release:
                    if (creature)
                    {
                        var releasingHand = a.Side == (int)Side.Left ? creature!.handLeft : creature!.handRight;
                        if (releasingHand.grabbedHandle != null) releasingHand.UnGrab(a.Value > .5f);
                    }
                    break;
                case ActionKind.Health:
                    entity.Descriptor.Health=Mathf.Clamp(a.Value,0,entity.Descriptor.MaxHealth);
                    if (creature && !creature!.isKilled) creature.currentHealth = entity.Descriptor.Health; break;
                case ActionKind.Death:
                    entity.Descriptor.Killed=true;
                    if (creature && !creature!.isKilled) creature.Kill();
                    if (Session.IsHost) Match.ReportDeath(entity, a.Other);
                    break;
                case ActionKind.Respawn:
                    entity.Descriptor.Killed=false;entity.Descriptor.Health=Mathf.Max(1,a.Value);
                    if (creature) { creature!.Resurrect(Mathf.Max(1, a.Value), null); entity.Buffer.Clear(); } break;
                case ActionKind.Slice:
                    var part = creature != null ? GameMath.FindPart(creature, a.Part) : null;
                    if (part != null) ApplyNativeSlice(entity,part); break;
                case ActionKind.Snap:
                case ActionKind.Unsnap:
                    if (!entity.Item || !Entities.TryGetValue(a.Other, out var parent)) break;
                    var holders = parent.Creature ? parent.Creature!.holders : parent.Item?.GetComponentsInChildren<Holder>().ToList();
                    var holder = holders?.ElementAtOrDefault(a.Handle);
                    if (holder != null) { if (a.Kind == ActionKind.Snap) holder.Snap(entity.Item!, true); else holder.UnSnap(entity.Item!, true); }
                    break;
                case ActionKind.Imbue:
                    var imbue = entity.Item?.imbues.ElementAtOrDefault(a.Part);
                    if (imbue == null) break;
                    if (string.IsNullOrEmpty(a.Text)) imbue.UnloadCurrentSpell();
                    else if (Catalog.TryGetData<SpellCastCharge>(a.Text, out var spell, false))
                    {
                        if (imbue.spellCastBase?.id != a.Text) { imbue.UnloadCurrentSpell(); imbue.Transfer(spell, Mathf.Max(.1f, a.Value)); }
                        imbue.SetEnergyInstant(a.Value);
                    }
                    break;
            }
        }
        finally { applyDepth--; }
    }
    private static string GripEvent(int orientation,System.Numerics.Quaternion rotation,bool? selfCollision=null) => JsonConvert.SerializeObject(new GripEventData {Orientation=orientation,SelfCollision=selfCollision ?? Player.selfCollision,Rotation=new[]{rotation.X,rotation.Y,rotation.Z,rotation.W}});
    private bool ValidateGrip(GameplayAction action)
    {
        if (action.Side < 0 || action.Side > 1 || action.Handle < 0 || action.Handle >= 128 || !Registry.TryGet(action.Other,out _)) return false;
        var data=JsonConvert.DeserializeObject<GripEventData>(action.Text,new JsonSerializerSettings {TypeNameHandling=TypeNameHandling.None,MaxDepth=4});
        return data != null && data.Orientation >= -1 && data.Orientation < 128 && data.Rotation != null && data.Rotation.Length == 4 && data.Rotation.All(Protocol.Finite) && data.Rotation.Sum(v=>v*v) > .5f && data.Rotation.Sum(v=>v*v) < 1.5f;
    }
    private void RememberConstraint(GameplayAction action)
    {
        if (action.Kind == ActionKind.Grab)
        {
            grips[(action.Entity << 1) | (uint)action.Side] = action;
            if(Registry.TryGet(action.Entity,out var actor) && actor.Kind==EntityKind.Player &&
                Registry.TryGet(action.Other,out var held) && held.Kind==EntityKind.Item)
                playerPenetrationWeapons.Add(held.Id);
        }
        if (action.Kind == ActionKind.Release) grips.Remove((action.Entity << 1) | (uint)action.Side);
        if (action.Kind == ActionKind.Snap) snaps[action.Entity] = action;
        if (action.Kind == ActionKind.Unsnap) snaps.Remove(action.Entity);
        if (action.Kind == ActionKind.Penetration)
        {
            string key = PenetrationKey(action);
            if (action.Side != 0) penetrations[key] = action; else penetrations.Remove(key);
        }
    }
    private void SyncImbues()
    {
        foreach (var entity in Entities.Values.ToArray())
            if (entity.Owned && entity.Item)
                for (int i = 0; i < entity.Item!.imbues.Count; i++)
                {
                    var imbue = entity.Item.imbues[i];
                    SendAction(entity, new GameplayAction { Kind = ActionKind.Imbue, Part = i, Value = imbue.energy, Text = imbue.spellCastBase?.id ?? "" });
                }
    }
    internal bool AllowDamage(Creature creature) => Applying || Find(creature) == null || Find(creature)!.Owned;
    internal void TraceDamage(Creature creature, CollisionInstance hit, bool allowed)
    {
        if (string.IsNullOrEmpty(MultiplayerRuntime.Instance?.TestDirectory) || Applying || Find(creature) is not { } victim) return;
        DamageTrace.Add((Now, victim.Descriptor.Id, hit.damageStruct.damage, allowed, hit.damageStruct.damager?.data?.id ?? "", hit.damageStruct.hitRagdollPart != null ? GameMath.PartKey(hit.damageStruct.hitRagdollPart) : 0, hit.impactVelocity.magnitude, hit.contactPoint));
        if (DamageTrace.Count > 100) DamageTrace.RemoveAt(0);
    }
    internal bool AllowSlice(Ragdoll ragdoll) => Applying || Find(ragdoll.creature) == null || Find(ragdoll.creature)!.Owned;
    internal void RecordContact(ReplicatedEntity entity, CollisionInstance hit)
    {
        ContactCount++;
        var body = hit.targetColliderGroup?.collisionHandler?.ragdollPart;
        if (entity.Item != null && body != null && Find(body.ragdoll.creature) is { } victim && victim.Descriptor.Owner != entity.Descriptor.Owner)
        {
            BodyContactTrace.Add((Now, entity.Descriptor.Id, victim.Descriptor.Id, GameMath.PartKey(body), hit.impactVelocity.magnitude));
            if (BodyContactTrace.Count > 100) BodyContactTrace.RemoveAt(0);
        }
        var other = Find(hit.targetColliderGroup?.collisionHandler?.item);
        if (entity.Item == null || other?.Item == null || entity.Descriptor.Owner == other.Descriptor.Owner) return;
        if (!entity.Item.handlers.Any(h => Find(h.creature)?.Descriptor.Kind == EntityKind.Player) || !other.Item.handlers.Any(h => Find(h.creature)?.Descriptor.Kind == EntityKind.Player)) return;
        var pair = (Math.Min(entity.Descriptor.Id, other.Descriptor.Id), Math.Max(entity.Descriptor.Id, other.Descriptor.Id));
        double time = Time.fixedTimeAsDouble;
        if (lastContact.TryGetValue(pair, out double previous) && time - previous < .03) return;
        lastContact[pair] = time; WeaponContacts++;
        float gripError = 0;
        foreach (var hand in entity.Item.handlers.Concat(other.Item.handlers))
            if (hand.playerHand != null && hand.gripInfo?.transform != null)
                gripError = Mathf.Max(gripError, Vector3.Distance(hand.playerHand.grip.transform.position, hand.gripInfo.transform.position));
        ContactTrace.Add((Now, pair.Item1, pair.Item2, hit.impactVelocity.magnitude, hit.startingCollision?.impulse.magnitude ?? 0, gripError));
        if (ContactTrace.Count > 100) ContactTrace.RemoveAt(0);
    }
    internal void RecordPhysicsContact(ReplicatedEntity entity, Collision hit)
    {
        var other = Find(hit.collider.GetComponentInParent<Item>());
        if (entity.Item == null || other?.Item == null || entity.Descriptor.Owner == other.Descriptor.Owner) return;
        if (!entity.Item.handlers.Any(h => Find(h.creature)?.Descriptor.Kind == EntityKind.Player) || !other.Item.handlers.Any(h => Find(h.creature)?.Descriptor.Kind == EntityKind.Player)) return;
        var a = Math.Min(entity.Descriptor.Id, other.Descriptor.Id); var b = Math.Max(entity.Descriptor.Id, other.Descriptor.Id);
        double now = Time.fixedTimeAsDouble;
        if (PhysicsContactTrace.Any(t => t.A == a && t.B == b && t.Time == now)) return;
        float force = 0;
        foreach (var hand in entity.Item.handlers.Concat(other.Item.handlers))
            if (hand.gripInfo?.joint != null) force = Mathf.Max(force,hand.gripInfo.joint.currentForce.magnitude);
        PhysicsWeaponContacts++; PhysicsContactTrace.Add((now, a, b, hit.impulse.magnitude, force));
        if (PhysicsContactTrace.Count > 100) PhysicsContactTrace.RemoveAt(0);
    }
    internal void Despawn(ReplicatedEntity entity)
    {
        if (!entity.Owned || Applying) return;
        // Keep a scene tombstone after native delayed cleanup. Newcomers need
        // the original prop's broken state before matching its fragment IDs.
        if (IsBrokenRoot(entity.Descriptor.Id)) return;
        Session.Publish(MessageKind.Despawn, Protocol.Encode(w => { w.Write(entity.Descriptor.Id); w.Write(entity.Descriptor.Epoch); }));
        if (!Session.IsHost) Remove(entity.Descriptor.Id, false);
    }
    private void Remove(ulong id, bool destroy, bool preserveSceneObjects=false)
    {
        if(Entities.TryGetValue(id,out var removing)) ReleaseEntityPenetrations(removing,destroy && (!preserveSceneObjects || removing.SpawnedRemotely));
        ForgetBreakFragment(id);
        Registry.Remove(id); earlySnapshots.Remove(id); spawning.Remove(id); slices.Remove(id); pendingSceneSpawns.Remove(id); pendingBreakFragments.Remove(id); ownershipRetryAt.Remove(id);
        snaps.Remove(id);
        foreach (var key in penetrations.Where(p => p.Value.Entity == id || p.Value.Other == id).Select(p => p.Key).ToArray()) penetrations.Remove(key);
        foreach (var key in grips.Where(g => g.Value.Entity == id || g.Value.Other == id).Select(g => g.Key).ToArray()) grips.Remove(key);
        if (!Entities.TryGetValue(id, out var entity)) return;
        if (entity.Item != null) ClearPlayerItemCollision(entity.Item);
        Entities.Remove(id); entity.Dispose();
        if (entity.Creature) creatures.Remove(entity.Creature!); if (entity.Item) items.Remove(entity.Item!);
        if (!destroy || (preserveSceneObjects && !entity.SpawnedRemotely && !entity.Descriptor.ProceduralNpc)) return;
        applyDepth++;
        try { if (entity.Creature) entity.Creature!.Despawn(); if (entity.Item) entity.Item!.Despawn(); }
        finally { applyDepth--; }
    }
    private void OnEquipped(Item item)
    {
        if (!Applying && SceneUsable && Session.Connected && spawning.Count == 0 && Find(item) == null) Register(item);
    }
    private void OnPeerJoined(PeerInfo peer)
    {
        if(Session.IsHost && travelAgreement.Active && proposedScene!=null)
        {
            travelAgreement.Add(peer.Id);
            Session.SendTo(peer.Id,MessageKind.ScenePrepare,Protocol.Encode(w=>{w.Write(proposedTravelId);WriteScene(w,proposedScene);}));
        }
        else if (Session.IsHost) SendScene(peer.Id);
        // Each owner resends its own descriptors, preserving the actual source identity.
        if(SceneUsable) PublishOwnedCatchup();
    }
    private void SendCatchup(ushort peer)
    {
        foreach (var e in Entities.Values.OrderBy(e=>IsBrokenRoot(e.Descriptor.Id) ? 0 : 1).ToArray())
        {
            if(e.Owned && e.Item!=null)e.Descriptor.ItemState=CaptureItemState(e);
            if (e.Transform != null) e.Descriptor.Pose = e.Transform.NetPose();
            if (e.Creature)
            {
                e.Descriptor.Health = e.Creature!.currentHealth; e.Descriptor.MaxHealth = e.Creature.maxHealth;
                e.Descriptor.Killed = e.Creature.isKilled;
                e.Descriptor.SlicedParts = e.Creature.ragdoll.parts.Where(p => p.isSliced).Select(GameMath.PartKey).ToArray();
            }
            // The coordinator supplies the complete registry, including transferred items.
            Session.SendTo(peer, MessageKind.Spawn, Protocol.Encode(w => Protocol.Descriptor(w, e.Descriptor)));
        }
        // Replay current constraints from every owner to this joining peer. Reusing
        // their original event IDs makes repeated catch-up idempotent.
        foreach (var a in snaps.Values.Concat(grips.Values).Concat(penetrations.Values).ToArray())
            if (Registry.TryGet(a.Entity, out var actor) && actor.Epoch == a.Epoch)
            {
                var current=a;
                if (a.Kind == ActionKind.Grab && Entities.TryGetValue(a.Entity,out var creature))
                {
                    var target=creature.HandTarget((Side)a.Side); var data=JsonConvert.DeserializeObject<GripEventData>(a.Text)!;
                    current=new GameplayAction {EventId=a.EventId,Entity=a.Entity,Other=a.Other,Epoch=a.Epoch,Kind=a.Kind,Part=a.Part,Handle=a.Handle,Side=a.Side,Value=a.Value,Position=target.Position,Text=GripEvent(data.Orientation,target.Rotation,creature.GripSelfCollision)};
                }
                Session.SendTo(peer, MessageKind.Gameplay, Protocol.Encode(w => Protocol.Action(w, current)), originalSource: actor.Owner);
            }
        Match.Broadcast();
        nextNativeWaveSync=0;
    }
    private void OnPeerLeft(ushort peer)
    {
        travelAgreement.Remove(peer);loadedPeers.Remove(peer);
        arenaSpawnSlots.Remove(peer);
        var leaving=Entities.Values.Where(e=>e.Descriptor.Owner==peer).ToArray();
        bool Carried(ReplicatedEntity entity)=>grips.Values.Any(g=>g.Other==entity.Descriptor.Id && Registry.TryGet(g.Entity,out var actor) && actor.Owner==peer) ||
            snaps.TryGetValue(entity.Descriptor.Id,out var snap) && Registry.TryGet(snap.Other,out var holder) && holder.Kind==EntityKind.Player && holder.Owner==peer ||
            entity.Item!=null && (entity.Item.handlers.Any(h=>Find(h.creature)?.Descriptor.Owner==peer) || Find(entity.Item.holder?.GetRootHolder().creature)?.Descriptor.Owner==peer);
        var persistent=leaving.Where(e=>e.Descriptor.Kind==EntityKind.Item && (e.Descriptor.SceneKey.Length>0 || !Carried(e))).ToArray();
        var sharedBrokenItems=Entities.Values.Where(e=>e.Descriptor.Kind==EntityKind.Item && e.Descriptor.SceneKey.Length>0).Concat(persistent).Distinct().ToArray();
        // Shared props and loose loot stay in the world when their simulation
        // owner leaves. Personal carried loadouts leave with that player.
        if(Session.IsHost)
            foreach(var entity in persistent)
                if(Registry.Transfer(entity.Descriptor.Id,Session.LocalId,entity.Descriptor.Epoch))
                {
                    entity.SpawnedRemotely=false;entity.Buffer.Clear();
                    Session.Publish(MessageKind.OwnershipGrant,Protocol.Encode(w=>{w.Write(entity.Descriptor.Id);w.Write(entity.Descriptor.Owner);w.Write(entity.Descriptor.Epoch);}));
                }
        applyDepth++;
        try
        {
            foreach(var entity in sharedBrokenItems)
            {
                var item=entity.Item;if(item==null) continue;
                foreach(var hand in item.handlers.ToArray())
                    if(Find(hand.creature)?.Descriptor.Owner==peer) hand.TryRelease();
                if(item.holder?.GetRootHolder().creature is { } creature && Find(creature)?.Descriptor.Owner==peer) item.holder.UnSnap(item,true);
            }
        }
        finally{applyDepth--;}
        var kept=new HashSet<ulong>(persistent.Select(e=>e.Descriptor.Id));
        foreach(var descriptor in Registry.Entities.Where(e=>e.Owner==peer).ToArray())
            if(!kept.Contains(descriptor.Id)) Remove(descriptor.Id,true,true);
    }
    private int clearGeneration;
    private void OnLevelUnload(LevelData data, LevelData.Mode mode, EventTime time)
    {
        if (time != EventTime.OnStart) return;
        CaptureTravelLoadout();
        sceneReady = false;
        localSceneLoaded=false;
        Clear();
    }
    private void Clear()
    {
        ClearProceduralSpawns();
        Match.Clear();
        ClearNativeWaves();
        ClearNpcEffects();
        ClearBreakables();
        clearGeneration++;
        foreach (var e in Entities.Values.ToArray()) Remove(e.Descriptor.Id, true,true);
        Registry.Clear(); spawning.Clear(); earlySnapshots.Clear(); deferred.Clear(); grips.Clear(); snaps.Clear(); slices.Clear(); events.Clear();
        penetrations.Clear(); mirroredPenetrations.Clear(); penetrationBindings.Clear();playerPenetrationWeapons.Clear();pendingMirroredPenetrations.Clear(); worldColliders.Clear();pendingPlayerWeaponDamage.Clear();
        nextPlayerCutRequest.Clear();pendingPlayerCuts.Clear();
        arenaSpawnSlots.Clear(); pendingSceneSpawns.Clear(); localArenaSpawn = null; localPlayerRegistered = false;
        playerItemCollisionOverrides.Clear();
        retryResyncAt=0;
        ownershipRetryAt.Clear();
        pendingNativeStorage.Clear();
        lastContact.Clear();
    }
    public void Dispose()
    {
        PreservePendingTravelInventory();
        disposed = true; Session.Validate = null;
        Session.Message -= Receive; Session.PeerJoined -= OnPeerJoined; Session.PeerLeft -= OnPeerLeft;
        EventManager.onLevelUnload -= OnLevelUnload; EventManager.onItemSpawnEquip -= OnEquipped;
        EventManager.onLevelLoad -= OnLevelLoaded;
        EventManager.OnStashItemSpawn -= OnNativeBookItem;
        UIInventory.OnItemRetrieved -= OnNativeInventoryItem;
        EventManager.onCreatureKill -= OnNativeWaveKill;
        EventManager.OnItemBrokenStart -= OnBreakStarted;
        EventManager.OnItemBrokenEnd -= OnBreakFinished;
        Clear();
    }
}
