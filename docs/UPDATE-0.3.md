# v0.3.0 shared-world and native PvE update

This update targets PCVR 1.3.2.261012003. It retains the native ThunderScript loader, Harmony, LiteNetLib Direct IP transport and existing SteamNetworkingSockets/lobby transport. Protocol version5 is incompatible with earlier protocol4 mods; exact game/mod/core build admission still applies.

## Shared world and travel

SceneSynchronization intercepts native LevelManager entry points before unloading. Any admitted player can request a destination. The host coordinates preflight agreement with every admitted peer (including a peer joining during preflight); one proposal wins racing selections. Every peer verifies the native map/mode and resolved scene assets. A successful proposal commits one world revision, options and seed before unload. Repeated announcements do not restart an in-progress load, and old revision packets cannot roll the world back.

Local readiness includes the native player body and restored equipment. Clients announce their revision and receive a host-selected clear arrival slot. Placement uses native anchors, NavMesh or floor-ray sampling, standing capsule clearance and occupied player/slot checks. Slow load/readiness has explicit deadlines. Missing assets reject preflight without unloading. Native loading errors attempt the last stable map; a stuck loader disconnects with an explanation and retains pending local inventory.

Host scene item/creature/container spawners supply shared entities; client copies are suppressed/removed before catchup. Scene hierarchy identities include the scene and an index among same-named roots, avoiding unrelated runtime sibling offsets. Native item-book and inventory-retrieval spawns register even when unheld. Storage of a free remote item waits for ownership before native container addition/despawn. Departing players leave loose shared loot in place under host ownership; personal carried equipment leaves with their body.

TravelLoadout clones supported native container/custom data, captures worn entries, named holsters and each held physical item once, including both grips and orientation/axis. Generation guards prevent stale asynchronous callbacks from restoring a second copy. Only local PlayerSaveData is prepared. The host's PlayerContainerId is not imposed on guests. Client shared-world saving does not overwrite their personal Home containers, and host saving excludes guest-carried physical equipment.

## Native wave book and combat

Native UIWaveSpawner select/start/stop actions route through reliable requests with bounded command deduplication. Native host WaveSpawner keeps the original wave catalog, enemy tables, brains, gear, delays, queue generation, loop rules and completion logic. Client books mirror selection/progress without starting another AI simulation or invoking duplicate reward/gameplay events. Run/cleanup identities and callback generation guards stop canceled or old-map asynchronous spawns entering a new run. Native pooled creature objects have a fresh network identity per life, so kill deduplication does not discard later pooled enemies.

Remote humans retain native physical creatures/colliders/joints, without local Player/VR input. Narrow native AI queries recognize them as human targets and interpret a living network fighter correctly. Remote players do not count toward native NPC population limits. The existing native melee attack/defense target adaptations and owner-recorded weapon damage/penetration/cut events also apply to enemy weapons, avoiding a second delayed AI attack-window decision at the client.

NPC magic/ranged consequences use the native caster's reliable event identity and native victim damage path. Consequences survive a short-lived projectile's despawn. Native player damager multipliers/caps are applied once; player damage callbacks publish health/death normally. Projectile launch descriptors retain native damager/effect/caster metadata. Remote projectile visuals/trajectory follow source state; shadow magic does not independently run collision effects, damage, expiry or prop-breaking decisions. Native caster charge/bolt visuals and applicable native spell status events are reproduced separately. Fired native arrows register their source/caster at launch. A player taking a projectile retains their new native damage attribution; old NPC launch metadata cannot overwrite it.

Source-owned Item.DisallowDespawn remains under native nocking/firing/storage control. Network cleanup protection is applied/restored during remote ownership only. Source native cleanup publishes ordinary despawn; replicas wait for that removal. Remote NPC/player lifetime is protected from independent client cleaner decisions.

Native waves temporarily enable co-op team protection, restore the prior mode afterward and expose the shared friendly-fire host rule. Existing normal damage/head cuts and optional surviving limb loss remain in the same combat system.

## Menu and player guide

Native pages: Session, Steam, Direct IP, Player & hosting, Play, Diagnostics. A native Multiplayer tab selector and connection status appear on every page; inactive pages are hidden. Steam and Direct IP actions have explicit separate names. World/wave status and normal map/wave-book guidance sit under Play. The old custom sword wave is under Diagnostics. F8 uses the same page organization.

The archive has a player README and plain-text quick start at its top level and in the mod folder where appropriate. It includes installation layout, complete host/join instructions for each method, clipboard IP entry, ports/passwords/player limits, normal Home/travel/native-book use and honest limitations. Packaging accepts only allowlisted mod files/licenses and records reports matching both DLL identities.

## Validation boundaries

Authoritative receipts are artifacts/shared-world-report.json, native-pve-report.json, native-ranged-report.json, session-ui-report.json and steam-session-checks.json. Their MVIDs must match the DLLs in the ZIP. BUILD-INFO.txt is generated from matching receipts only.

Combat measurements require actual native AI weapon/caster/arrow contacts and decreased native player health; they do not inject a hit or force an attack animation. The automated melee opponent gets test-only extra health to survive incidental mock-controller/body contacts. This fixture is not evidence about normal enemy health balance. Finite-wave progression uses explicit native Kill calls, clearly separated from combat measurement. Ranged fixtures leave an active native aiming brain/coroutine running and place the test player in front of it. Loot is claimed before normal unclaimed-loot expiration, then tested through native cleanup and return Home.

The broad PvP/practice/sword/neck consistency suites remain stopped at the user's request. Shared-world coverage is Home, Arena, Canyon, Market and Citadel with two/three independent processes, including joining during travel, ownership/flight, native storage, equipment/container/character identity, missing assets, races, slow readiness and native load-error fallback. Generalized code does not certify every map, procedural dungeon, custom mod, encounter or base-game interaction.

Full multiplayer player spellcasting/telekinesis/bow mechanics, arbitrary spell/status mods, non-Item scripted props, real Index+Quest-PCVR feel and eight-player performance remain outside current proof. Steam backend discovery/invites/relay across separate actual accounts/PCs remains unverified; actual local UDP loopback and single-account APIs cannot establish it.

## Final exact-build receipts

Final mod: `95a0ffc706c8470c866a75d7904112e3`; core: `faca4bc8d58e4f79b9cb896b50b9ed75`.

| Receipt | Passed assertions | Scope |
| --- | ---: | --- |
| shared-world-report.json | 29 | Five base map types, loadouts/save IDs, storage, props/flight/ownership, races/slow/missing/failed loads, joining during travel, owner departure |
| native-pve-report.json | 37 | Real native enemy weapon damage to both players; native finite progression, claimed loot, cleanup, live-wave late join/stop and return Home |
| native-ranged-report.json | 19 | Native caster effects/projectiles/client damage, native archer damage, fired-arrow ownership and new-player attribution, return Home |
| session-ui-report.json | 12 | Six page membership/visibility, native single-action buttons, connect/live status/shared rule/leave |
| steam-session-checks.json | 12 | Shared router/admission/security/capacity, travel agreement, actual Direct IP UDP loopback |

All four game receipts are fresh successful runs matching both final DLLs, with no retained older-build cases. The unchanged core receipt matches its final DLL. These are local independent processes, not real separate-PC Steam or headset-feel verification. Some native asynchronous AudioSource asset callbacks logged during map unload; the tested loads and recovery completed. Source-side scripted progression and durable melee fixtures are recorded explicitly rather than described as normal combat balance tests.

Final archive: artifacts/BladeAndSorceryMultiplayer-v0.3.0-PCVR.zip (SHA256 `3F5387323032476074942D96FEA9F64D546CFE22A60199C9DD70E28B36DB8179`). Installed in the normal Steam mod folder; installed assembly hashes match artifacts/package-info.json. Owned isolated games were stopped. Native UI control/text validation passed; VR-only menu readability remains a real-headset check because the desktop spectator capture excludes it.
