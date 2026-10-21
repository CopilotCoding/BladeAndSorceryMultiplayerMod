# Blade & Sorcery Multiplayer

PCVR mod using the native ThunderScript loader, native physical replicas and Harmony. Target: Blade & Sorcery 1.3.2.261012003, Unity 2021.3.38f1, Steam AppID 629730. Index and Quest connected to a PC use the game's own VR input. Nomad is outside this project.

**Player setup and usage:** [mod/README.md](mod/README.md). The versioned ZIP ships that guide and START-HERE.txt at its top level. This README is for development.

## v0.3.0

- Safe arrival placement on playable maps using native anchors, navigation/floor checks, capsule clearance and per-player slots.
- Shared Home and props. Stable object identities, ownership epochs, native book-spawned items, storage handoffs, late-join catchup and departing-owner loose loot retention.
- Native map selection by any admitted player. All peers validate the requested map before unloading; first accepted selection wins. One world revision commits the options/seed, rejects old packets and survives native loading. Missing assets, slow peers and native load failures have explicit recovery.
- Each player carries held weapons, both grips, named holsters, worn armor and supported native container/custom data through travel and return Home. Personal character IDs/inventory stay local. A client does not save the host's shared placed world into its own Home stash.
- Native wave-book selection/start/stop and native host WaveSpawner simulation, including original enemy catalogs/loadouts, queues, progression, kills, loot and cleanup. Client books mirror state without running a second AI wave or awarding duplicate rewards.
- Native AI queries recognize remote human fighters without adding local Player/VR input to replicas. Native weapon-owner hits reach the victim owner instead of depending on a second delayed AI damage window. NPC spell outcomes, charge/bolt effects and projectile state use narrow replication hooks; player spellcasting is separate unfinished work.
- Unified native menu pages: Session, Steam, Direct IP, Player & hosting, Play, Diagnostics. The Multiplayer tab selector and live connection status remain visible. F8 uses the same organization. The legacy sword-wave action is diagnostic-only.

Existing physical melee PvP, native grabs/weapon contacts/penetration/cuts, death/respawn, optional surviving limb losses, Steam lobbies/password admission and separate Direct IP UDP remain in the same systems. Full base-game parity is not established.

## Build and distribute

For a fresh source checkout or source ZIP, follow [docs/BUILD-SOURCE.md](docs/BUILD-SOURCE.md), including the first restore. Create a separate versioned source archive with `./scripts/Package-Source.ps1`.

References live in local Managed/ and must match the installed game. Never distribute game assemblies or the isolated test game.

```powershell
./scripts/Build.ps1
./scripts/Install-Mod.ps1
./scripts/Package-Mod.ps1 -TestsSkippedByUser -RequireSharedWorld
```

Installation refuses to replace the mod while the installed game is running or references differ. Packaging uses an allowlist, matching versioned player guides and exact assembly identities. -TestsSkippedByUser records that the broad PvP/sword-cut suites remain stopped at the user's request; it does not bypass the focused shared-world/native-PvE/menu evidence required by -RequireSharedWorld.

Direct IP uses LiteNetLib UDP, default port 9050. Steam uses the game's initialized Steamworks.NET API and SteamNetworkingSockets, with public/friends/invite-only lobbies. Both share admission, ownership, reliable events, snapshots and 2-8 player capacity. Steam backend/relay and cross-account behavior still need real separate PCs.

## Headset-free checks

The ignored .runtime/game checkout uses MockHmd and separate saves. Start/stop scripts verify the recorded executable/PID/test arguments and do not target the normal installed VR process. Automation reaches actual native menu, wave-book, damage and map APIs. It never claims scripted kills prove sword combat.

```powershell
./scripts/Start-TestPair.ps1
./scripts/Invoke-SessionUiTests.ps1
./scripts/Invoke-NativePveTests.ps1 -KeepRunning
./scripts/Invoke-SharedWorldTests.ps1 -SkipLaunch -KeepRunning
./scripts/Invoke-NativeRangedTests.ps1
./scripts/Stop-TestInstances.ps1 -Names host,client,third
```

Focused networking checks: tests/SteamSessionChecks (12 cases, including real UDP loopback, password/replay/build/capacity checks and travel agreement races/timeouts). Actual loopback may require execution outside the restricted tool sandbox.

Reports in artifacts/ identify both assembly MVIDs and explicitly separate actual native combat from scripted progression fixtures. BUILD-INFO.txt lists only reports matching the packaged binaries. Historical reports are evidence for their recorded binaries, not newer source builds.

## Important limits

- Automated shared-world map coverage: Home, Arena, Canyon, Market, Citadel. Generalized code is not certification of every map, procedural dungeon, scripted encounter or third-party content.
- Local independent processes validate Direct IP replication. They cannot prove Steam invitations/discovery/relay across separate accounts or Internet latency.
- Native wave choices run their original AI/loadouts; enemy arrows/projectiles/effects use shared replication. Full multiplayer player spells, telekinesis, player bows, arbitrary spell/status mods, all non-Item scripted props and eight-player VR performance remain unverified or incomplete.
- Each machine runs Unity physics. Ownership, interpolation and consequence events reconcile a useful shared world; they do not make PhysX deterministic across PCs.
- No dedicated server or host migration. Host departure ends the session.
- Direct IP connection keys do not provide transport confidentiality. Saved multiplayer.json contains private connection preferences; never distribute it.
- Real Index + Quest-PCVR combat feel and full base-game regression remain manual verification work.

See TODO.txt and docs/IMPLEMENTATION.md for scope/evidence. Player controls, installation, passwords, ports and troubleshooting are in mod/README.md.
