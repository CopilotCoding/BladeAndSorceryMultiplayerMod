# Procedural NPC integration — v0.5.1, API 1, protocol 8

API 1 is unchanged from v0.5.0. Protocol 8 adds a shared human-versus-human
weapon damage rule; procedural NPC attacks retain native player damage rules.
The v0.5.0 verification receipts below remain historical evidence for those
exact binaries. See the current release BUILD-INFO for current verification.

This is an opt-in C# API in `BladeAndSorcery.Multiplayer.dll`. It supplies
multiplayer integration for a separate procedural sword-fighting mod. It does
not implement AI decisions, attacks, guards, parries, navigation, virtual-hand
generation or a neural network. The separate mod owns those systems.

Reference the multiplayer DLL and matching game/Unity assemblies when building
your mod. Install the multiplayer distribution, including its Core/dependency
DLLs, normally. All peers need the same multiplayer build, creature/weapon
assets and matching gameplay mods under the existing content-admission policy.
Host-only simulation does not exempt a gameplay DLL from that file agreement.

## Authority and discovery

All API calls, callbacks and Unity objects are **main-thread only**.

| API on `MultiplayerModApi` | Meaning |
| --- | --- |
| `ProceduralNpcApiVersion` | Constant `1`; version your integration against this contract. |
| `ProceduralNpcAvailable` | Native procedural combat hooks installed safely. If false, check patch diagnostics; procedural registration is disabled while ordinary NPC behavior is retained. |
| `CanHostProceduralNpc` | Connected host, ready current scene, supported hooks. Use before spawning/registration and on every AI/controller tick. False offline, on clients and during loading. |
| `WorldReady`, `WorldRevision` | Current multiplayer scene readiness and revision. IDs/revisions are session-scoped; do not persist raw network IDs across reconnects. |
| `GetHumanPlayers()` | A fresh roster snapshot of admitted human players, including remote native physical replicas. Empty offline/during loading. NPCs are excluded. |
| `IsNetworkReplica(Creature)` | True for a creature simulated by another network owner. |
| `IsLocallyOwned(Creature)` | True for a registered creature owned by this peer. |
| `IsProceduralNpc(Creature)` | Explicit procedural mode for this creature's network life. Does not classify it as a human or set `Creature.isPlayer`. |

`MultiplayerHuman` exposes `NetworkId`, `PeerId`, `Name`, `Creature`, `IsLocal`,
`IsAlive`, and world-space `UnityEngine.Pose` values `Head`, `LeftHand`,
`RightHand`. The Creature reference belongs to this PC. A remote human has no
local `Player`, camera or VR input component. Do not use `Player.local` as your
only opponent or add a Player component to remote opponents. Requery the roster
to handle joining, leaving, deaths and respawns; do not retain destroyed creature
references. Remote poses have the normal network interpolation delay.

Keep your AI/controller components disabled on clients from their first Update
or FixedUpdate. Native prefab Awake/OnEnable can precede the multiplayer spawn
callback, so waiting for `IsNetworkReplica` alone is insufficient. The global
host gate must pass before a controller can run. Offline single-player behavior,
if desired, belongs to your own mod and must not call multiplayer registration.

## Register a host rig

```csharp
ProceduralNpcHandle handle = MultiplayerModApi.RegisterProceduralNpc(
    creature, leftVirtualGrip.transform, rightVirtualGrip.transform);
```

The creature must be a loaded, living native NPC on the host. Human players,
client registration, duplicate controllers and entity-capacity exhaustion are
rejected. Incompatible/oversized part layouts and unusable target poses are also
rejected. Existing ordinary host NPCs can opt in. Registration lasts for that
creature's current network life; it cannot be toggled back on a living replica.
Despawn and create an ordinary NPC to leave procedural mode.

Your mod owns the **host** physics/controller implementation. Stop the native
brain and any animation/locomotion forces that compete with it before taking
control. Keep or replace IK according to your rig; the API does not guess which
host IK solver you need. Native Creature/Ragdoll/Item/Handle components, damage
data, colliders, joints and native grab/release events must remain in use.

Both targets are **world-space native grip frames**, equivalent to the tracking
frames driving native VR grip joints. They are desired controller poses, not
weapon roots, wrist bone poses, local-space coordinates or cosmetic hand meshes.
Supply the frames actually used by your virtual-hand ConfigurableJoints.
The current native `RagdollHand.gripInfo.joint` must reference that grip joint
so its settings can be captured. Grabbing a real Handle and releasing it through
the native APIs preserves ownership, grip events, two-hand state and late joining.
Your rig must prevent native NPC hand-sync from fighting its virtual controllers.

Replicas retain native physics-enabled creatures, colliders and ragdoll joints.
The multiplayer mod disables their native brain/animation forces and creates
two network tracking bodies, without running your AI. It captures the host's
body/root velocities, head/body poses, target poses and grip settings. Replicated
grips include connected anchor, position spring/damping/max force, angular drive
spring/damping/max force and drive selection. Other arbitrary joint graphs,
limits, custom solvers and custom articulated attachments are not a generic RPC.
Use the native grip reference convention. Additional custom joint target offsets,
mass/projection settings and solver parameters are not serialized by API 1; bake
desired pose offsets into your supplied grip frame or arrange a specific extension.

The expected rig is a compatible native humanoid, within the existing limit of
20 replicated ragdoll parts and stable native type/section keys. This API does
not add support for arbitrary skeletons. Weapons continue through ordinary item
replication and native catalogs; complex weapon scripts need the existing item
adapter API separately.

Owned and remote procedural grips use matching own-body collision exclusions,
with `creature.ragdoll.allowSelfDamage` controlling the self-collision choice.
Only the opted-in procedural holder receives this adjustment. Grabbing or
parrying another fighter's weapon still uses physical colliders/constraints.

## Minimal controller lifecycle example

This example defines an interface your **separate** AI mod would implement.
`IMyProceduralDriver` is not a multiplayer API type and contains no AI here.

```csharp
using System;
using BladeAndSorcery.Multiplayer;
using ThunderRoad;
using UnityEngine;

public interface IMyProceduralDriver
{
    Transform LeftGrip { get; }
    Transform RightGrip { get; }
    void Step(float dt, MultiplayerHuman[] humans);
    void StopAndReleaseResources(); // idempotent; stop forces/loops and free own targets
}

public sealed class MyMultiplayerNpcLink : MonoBehaviour
{
    private ProceduralNpcHandle handle;
    private IMyProceduralDriver driver;

    public void Attach(Creature npc, IMyProceduralDriver sourceDriver)
    {
        if (!MultiplayerModApi.CanHostProceduralNpc)
            throw new InvalidOperationException("Ready host required");
        driver = sourceDriver; // caller has already set up its native host rig
        handle = MultiplayerModApi.RegisterProceduralNpc(
            npc, driver.LeftGrip, driver.RightGrip);
        handle.Stopped += OnStopped;
    }

    private void FixedUpdate()
    {
        if (handle == null || !handle.CanSimulate) return;
        driver.Step(Time.fixedDeltaTime, MultiplayerModApi.GetHumanPlayers());
    }

    private void OnStopped(ProceduralNpcHandle npc, ProceduralNpcStopReason reason)
    {
        enabled = false;
        driver?.StopAndReleaseResources();
    }

    private void OnDestroy()
    {
        if (handle != null)
        {
            handle.Stopped -= OnStopped;
            handle.Despawn(); // safe no-op if the network life has already ended
        }
        driver?.StopAndReleaseResources();
    }
}
```

`CanSimulate` includes the world/host gate, live registration, life state and
target existence. Recheck it every tick. `IsRegistered` can remain true for a
corpse, while `CanSimulate` is false. `Stopped(handle, reason)` fires once for
`Killed`, `Despawned`, `WorldChanged`, `SessionEnded` or `TargetsLost`. A failed
subscriber is isolated/logged so other cleanup callbacks still run. The reason
is also retained in `StopReason` for code subscribing after a life ended.
`TargetsLost` also covers a target pose that becomes non-finite or unusable;
that controller fails closed instead of publishing malformed hand targets.

Stop your own coroutines, decisions and forces in that callback. Free only your
own source rig resources. A killed NPC keeps its ordinary networked corpse and
native dismemberment state. `handle.Despawn()` explicitly despawns the registered
NPC while the host world is available. Weapons/loot follow native lifecycle;
the API does not delete all related loose items. On map/session teardown the
procedural NPC itself is cleaned up on the host and clients. Normal NPCs retain
their previous cleanup rules. Native resurrection does not restart an ended
procedural controller: create a new network life for a new controller.

## Safe native spawning

```csharp
ProceduralNpcSpawnRequest request = MultiplayerModApi.SpawnProceduralNpcAsync(
    "HumanMale", spawnPosition, spawnYawDegrees,
    creature => {
        // Build your host rig here, then call RegisterProceduralNpc.
        // Do not start decisions or spawn another copy on clients.
        CreateYourRigAndRegister(creature);
    },
    error => Debug.LogWarning("Procedural NPC spawn: " + error));

// Before completion, when your spawner is disabled:
request.Cancel();
```

`CreateYourRigAndRegister` is your own setup method. The callback receives a real
native creature; it must opt in explicitly. You can also use your own native
spawner and register its result, provided you guard its callbacks against scene
changes. The helper guards the current multiplayer-world generation, requires
the ready host, times out after 60 seconds and rejects invalid catalogs/poses.
It discards late native completions after cancellation, map change or shutdown.
Cancellation/failure reports once; `IsPending` and `Error` expose the request
state. Cancel after completion does nothing. Exceptions in setup clean up the
native creature and reach the failure callback. Protect any additional async
weapon/asset callbacks in your mod with the resulting handle's `CanSimulate`.

## Combat and synchronization contract

- Use the game's native weapon collision, Damager, penetration, health, kill,
  slice and grab/release paths. Physical velocities, materials, blade angles and
  native damage data still determine valid contacts. The opt-in hook bypasses
  only the NPC animation damage window; it does not invent successful strikes.
- Do not send a second hit RPC, directly mutate remote health, or run the same
  AI on clients. Existing source-weapon/target-owner rules, ownership epochs,
  reliable event deduplication and replay guards prevent duplicate consequences.
- `ApplyingRemoteState` is available to other scripted effects. Avoid creating
  new attacks or network events from callbacks caused by remote state replay.
- Held weapons use normal shared identities/ownership. Drop/grab through native
  hands rather than replacing items with visual copies. A player can claim a
  genuinely released weapon through the existing ownership path.
- Late joining restores the procedural descriptor, native body/corpse state,
  equipment, held weapon IDs/grabs, target snapshots and grip drives. It does
  not instantiate your AI controller on clients or replay historical decisions.
- Travel removes old NPC identities and invalidates handles. Spawn/register
  desired AI for the new ready scene; procedural NPCs are not player loadouts
  and do not automatically persist through travel or saves.

Use the existing item adapters for persistent custom weapon mode/state and
deduplicated effects. AI neural state, observations and decisions remain local
to the host. They are neither required for replicas nor serialized by this API.

## Verification and limits

`scripts/Invoke-ProceduralNpcTests.ps1` exercises the public API with a test-only
virtual grip rig and a dynamic native sword. The source fixture freezes its body
to make target/weapon comparisons stable. It tests native health/slice/death
events and a deliberately constructed native contact for network delivery and
duplicate suppression. Those are integration tests, not proof of realistic AI,
real sword contact/parry quality or autonomous blade-cut reliability. No AI
decision loop is included in the multiplayer distribution.

Exact DLL identities and results are in the local procedural NPC report and
the player ZIP's BUILD-INFO. Earlier v0.4 receipts apply to their old binaries.
Real separate-PC latency, Index/Quest-PCVR feel, an actual third-party AI mod,
custom skeletons/solvers and eight-player performance need separate validation.

### Exact release evidence — 2026-10-21

Mod MVID `7f01559f82b94c558de696fa18d01572`; Core MVID
`309f5126ca3b45a39b5ad9cba7e2cca3`. These identify the tested distribution;
rebuilding source produces its own binaries and requires fresh validation.

| Receipt | Passed | Scope |
| --- | ---: | --- |
| procedural-npc-report.json | 26 | Host gate/roster, rejected player/client/duplicate registration, cancellation, unchanged ordinary NPC window, body/target/grip/sword replication, scripted contact delivery/duplicate suppression, health, third-peer catch-up, native slice/death/despawn, target loss, map/session cleanup |
| procedural-api-consumer-report.json | 1 compile | Separate assembly using only public signatures in the actual distribution DLLs |
| native-pve-report.json | 38 | Real ordinary native enemy weapon damage to both players, native book/wave choices and finite progression, loot/cleanup, live-wave late join and return Home |
| teleport-report.json | 19 | Existing human-player grips and long/short/rotation-only teleports, including held swords, in both directions |
| steam-session-checks.json | 17 | Shared admission/router, content/password/build/capacity, travel agreement and actual local UDP |
| procedural-protocol-checks.log | 10 | Serialization/limits, procedural host-only NPC descriptors, ordering/interpolation/teleports/ownership/deduplication and actual UDP |

All game receipts match both DLLs and the networking receipt matches Core.
The procedural contact/cut fixtures are explicitly scripted native events;
the native-PvE regression separately measured real AI weapon contact damage.
Native asset/apparel callbacks and a native dismemberment renderer mismatch
logged during fixture cleanup/cutting. Body-part state and tested lifecycle
converged; this is not a clean-log or complete mesh/weapon-quality certification.
The broad PvP/practice/sword/neck suites remain stopped at the user's request.
