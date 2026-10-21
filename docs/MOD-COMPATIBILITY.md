# Mod integration — v0.5.0 / protocol 7

The opt-in procedural NPC API is documented separately in
[PROCEDURAL-NPC-API.md](PROCEDURAL-NPC-API.md). The v0.4 receipts below are
historical; current v0.5 evidence is in that guide and the release BUILD-INFO.

See mod/COMPATIBILITY.md for player guidance. The purpose of this release is
broad native-content support and an explicit extension boundary. It cannot infer
the networking semantics of arbitrary scripts.

## Admission and declarations

Shared mod identities are derived from their manifest Name/Author, with SHA-256
digests covering version, shared load order, relative file names and contents of
DLLs, bundles, native catalog JSON and manifests. Paths and file contents are
never transmitted. Duplicate identities and oversized manifests fail clearly.
File IO/hashing runs off the Unity thread; native metadata is captured on it.
Both Direct IP and Steam use the same pre-membership comparison. Password
authentication precedes mismatch reporting. Mod agreement does not provide
anti-cheat or prove runtime equivalence.

Optional `basmp-compatibility.json` in another mod's root:

```json
{"settings":["Settings/shared-gameplay.json"]}
```

Paths use forward slashes, must exist inside that mod, and their bytes are
fingerprinted. Do not declare files containing credentials or per-user settings;
only their hashes cross the wire, but personal differences would prevent joining.
Otherwise untyped runtime JSON is excluded. Mods that alter gameplay through
saved ModOptions or code must ensure those settings agree, declare an appropriate
shared file, or enforce their settings through an adapter.

`{"localOnly":true}` is accepted only when that mod has no owned/changed catalog
data. The declaration is a trust decision for strictly local UI/visual behavior.
It does not make gameplay modifications safe to omit. Restart after changing
files; the offline rescan action does not hot-reload mods.

## Native saved state

Item descriptors contain a bounded item-state envelope. ContentCustomData entries
carry a locally installed type identifier and plain JSON. Only concrete subclasses
from the game and loaded mod assemblies can be constructed. JSON TypeNameHandling
is disabled; a peer cannot select arbitrary assemblies or CLR types. Native Unity
value structs have explicit converters; Unity object/delegate/reflection members
are excluded. Unsupported complex data is reported once and needs an adapter.

Initial data is passed into native ItemData.SpawnAsync before spawn callbacks.
Later changes use Item.OverrideCustomData without replaying OnSpawn. Owner state
is checked every 0.5 seconds; unchanged state is not republished. Native imbue
and wardrobe data use their existing game APIs. Custom item data is limited to
32 records, adapter state to 4096 bytes per adapter, and the complete item
envelope to 24000 UTF-8 bytes. Over-budget/failed captures are logged and retain
the last good descriptor rather than removing the item from the session.

Callbacks in arbitrary mods may spawn effects or damage. Such scripts must
distinguish local ownership and remote application to avoid duplicate outcomes.
Do not independently run a second attack/damage simulation for remote items.

## Item adapter API

Reference BladeAndSorcery.Multiplayer.dll and game references. Implement
IMultiplayerItemAdapter and register one stable instance using
MultiplayerModApi.Register(adapter) in your ThunderScript's enable/load path,
before connecting. Register the same ID/version on all peers. Registration
changes invalidate the offline manifest; live registration is refused.

```csharp
public sealed class ModeAdapter : IMultiplayerItemAdapter
{
    public string Id => "example.weapon-mode";
    public int Version => 1;
    public bool AppliesTo(Item item) => item.GetComponent<MyWeapon>() != null;
    public byte[] CaptureState(Item item) =>
        System.BitConverter.GetBytes(item.GetComponent<MyWeapon>().Mode);
    public void ApplyState(Item item, byte[] state)
    {
        if (state.Length != 4) return;
        item.GetComponent<MyWeapon>().SetModeWithoutFiring(
            System.BitConverter.ToInt32(state, 0));
    }
    public void ReceiveEvent(Item item, byte[] payload)
    {
        // Validate your own payload. Play the corresponding remote effect.
        // This callback already belongs to an authenticated item owner.
    }
}
```

MyWeapon is an example component supplied by your mod, not part of this API.
CaptureState must be quick, deterministic for unchanged state, and free of side
effects. ApplyState must be idempotent: it may run on reconstruction, catch-up
or repeated state and must not fire an attack. Persistent mode/ammo/charge state
belongs here. Empty bytes are valid. All callbacks run on Unity's main thread.

After performing an action locally, its current owner can call:

```csharp
if (MultiplayerModApi.IsLocallyOwned(item))
    MultiplayerModApi.PublishEvent(item, "example.weapon-mode", payload);
```

Events are limited to 2048 bytes, reliable and deduplicated through normal
gameplay events. Host validation requires the item's current owner and epoch,
a registered adapter and matching version. Events wait for asynchronous item
creation. They are not retained for late join: use CaptureState for lasting
consequences. ApplyState/ReceiveEvent run under ApplyingRemoteState, which blocks
publication loops. Your event's internal meaning still needs application-specific
validation. This is cooperative owner-based networking, not a secure competitive
server or a generic RPC facility.

Ownership follows the existing item handoff system. The new owner captures the
current native item state. Scene unloading removes old entities/state and the
world revision rejects old packets. Descriptors supply current state to late
joiners. For persistence across save/load or map travel, store lasting adapter
state in your mod's native ContentCustomData too; the adapter envelope is not a
replacement save format. Disconnect before unregistering when unloading your mod.

## Resting world props

Item snapshots identify held, holstered, penetrating, attached and resting states.
Moving replicas retain native physics steering. After a free owner's target stays
still, a slow replica can align exactly to its resting position/rotation and sleep.
Local grips, holsters, penetrations, recent owned-object contacts and nearby local
player colliders block that resting correction. This addresses force-only steering
leaving settled props at different angles; it does not make moving PhysX simulations
identical. The focused world audit compares all native item identities and settled
positions/rotations, and deliberately disturbs loose replicas to test recovery.

World-attached items also participate in resting alignment. Their connected-joint
graph can carry up to 20 additional native bodies, matched by scene path and
restricted to that item's connected non-player/non-creature bodies. Arbitrary
joint configuration changes and joint break events are not replicated by this
addition. In Arena, the observed hanging hook mismatch was resolved by including
attached items in resting alignment; its audited connected-body list was empty.

Native cleanup on a non-owning peer waits for the owner's despawn event, including
after ownership transfer. A directly destroyed remote Item representation is
reconstructed from its retained descriptor/snapshot/state. This does not respawn
objects the owner intentionally removed. Scene unload, application shutdown and
explicit network replay retain their normal cleanup paths.

## Patch coexistence and validation

Native Creature/Player and Item teleport events increment a persistent snapshot
pose revision. Capture waits until the following frame so native tracking/IK
can finish the teleport. A new revision discards pre-teleport interpolation
history and snaps the received body poses; the revision persists across packet
loss. Reordered snapshots and stale revisions cannot restore the previous path.
Native creature teleport callbacks carry equipped items before body placement;
item teleports also carry their own revision. Normal movement within a revision
retains interpolation. Scripts directly changing transforms without native
teleport events must use the game's teleport APIs for this discontinuity handling.

Optional AI attack/defense adaptations check the incoming IL pattern before any
rewrite. Changed patterns retain the incoming method, with a warning. Critical
damage/ownership patches retain their existing checks; they are not silently
disabled to claim compatibility. The patch report lists owners, not automatic
conflict verdicts. Two patches on one method can coexist; contradictory behavior
still requires an explicit compatibility fix.

Focused checks live in tests/SteamSessionChecks and
scripts/Invoke-ModCompatibilityTests.ps1. The game fixture registers a synthetic
native sword catalog, ContentCustomData and adapter through the isolated test
driver. Its late-join sword is a durable test fixture; normal loot expiration is
unchanged. It is representative integration coverage, not a downloaded third-party
mod or proof of arbitrary gun/spell/AI-overhaul compatibility. Check the report's
DLL identities before applying its results to a build.

## v0.4.0 release checks (2026-10-21)

Mod MVID: `2b88666119c04674a7fb373d27a5cdf9`.
Core MVID: `0b6128d56bfc42ca81ec9968aab75f7e`.
These identities describe the distributed binaries, not a future source rebuild.

| Receipt | Passed checks | Coverage |
| --- | ---: | --- |
| mod-compatibility-report.json | 30 | File/settings/order fingerprints, named rejection, all settled Arena items, deliberate drift recovery, native custom/adapter/imbue state, deduplication, late joining, replica recovery, owner transfer and removal |
| teleport-report.json | 19 | Native long/short/rotation-only player teleports in both directions, carried swords, immediate received root/bone placement and discarded interpolation history |
| shared-world-report.json | 29 | Home/Arena/Canyon/Market/Citadel, loadouts/save identities, native storage, prop movement/ownership, travel races/missing assets/slow loads/recovery, joining during travel and owner departure |
| native-pve-report.json | 38 | Real native NPC weapon damage to host and client, normal book choices/progression/cleanup, loot, live-wave late join/stop and return Home |
| native-ranged-report.json | 19 | Native caster effects/projectiles and damage, native arrows and damage, fired-arrow pickup/ownership and player attribution |
| session-ui-report.json | 12 | Six native pages, explicit buttons, visibility, connection/status/shared rule and leaving |
| steam-session-checks.json | 17 | Shared session admission/router, content/password/build/capacity rejection, travel agreement and real local UDP |
| compatibility-protocol-checks.log | 9 | Wire limits/round trips, ordering, interpolation, teleport packet loss/stale revisions/rollover, ownership, deduplication and real UDP |

All six game reports are fresh successful runs matching both DLLs. No older
binary results are reused. The final Arena resting-pose audit checked 30 items:
maximum position difference 0.542 mm and rotation difference 0.086 degrees.
This is one settled-world sample, not equality of moving PhysX simulations.
Eight deliberately displaced/rotated replicas also recovered. Native teleport
audits recorded zero root/interpolation error and under 0.001 mm bone-position
error immediately after applying each received teleport.

NPC melee fixtures use test-only extra enemy health against incidental mock
controller contacts; player damage is native. Finite-wave progression uses
explicit native kills, separately from measured real AI damage. Native unload
logs can still include AudioPortal/asset callback errors; tested loads/recovery
completed. This is not a clean-log or complete base-game certification.

Broad PvP/practice/sword/neck tests remain stopped at the user's request.
Separate-account Steam/relay, real Index/Quest-PCVR feel, arbitrary community
mod packs, procedural dungeons and eight-player performance remain unverified.
