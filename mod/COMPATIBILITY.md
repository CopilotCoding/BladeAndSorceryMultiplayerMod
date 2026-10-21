# Mod compatibility — v0.5.1

Install the same gameplay mods, versions, files and load order on every PC.
Everyone also needs this same multiplayer build. Protocol 8 cannot join older
multiplayer versions. The mod does not download or distribute other mods.

Before connecting, a background check hashes mod DLLs, bundles, native catalog
JSON, manifests and explicitly declared shared settings. Passwords, IP settings,
logs and saves are not sent. A mismatch is rejected before network world
catch-up, with names of missing, different or client-only mods. Matching files
are a prerequisite; they do not certify that a script's gameplay synchronizes.
Restart the game after adding/updating/removing mods so the loaded code and
catalogs match the files. Diagnostics -> Rescan mods can refresh the file check
while offline; it does not reload DLLs or assets.

## What this update improves

v0.5 adds an opt-in procedural NPC API: a separate AI mod runs only on the host,
queries every human player and supplies virtual hand targets/native grip joints.
Clients replicate the physical NPC and weapon state through existing systems.
Normal NPCs retain their native control and animation damage windows. This is
integration support, not bundled AI or automatic AI-overhaul compatibility.
Developer guide: docs/PROCEDURAL-NPC-API.md in the source archive.

Existing v0.4 compatibility features:

- Ordinary mod weapons continue to use their native catalog, colliders, grips,
  damage and penetration settings. No weapon-ID allowlist is imposed.
- Supported native ContentCustomData is provided when remote items spawn and
  updated through the game's OverrideCustomData event. This covers compatible
  saved item state; scripts must actually use that native mechanism.
- Native imbue spell/energy is included in initial and late-join item state.
- Native teleports snap remote fighters and teleported items to their new poses
  instead of interpolating them across the map. Regular movement stays smooth.
- Loose props reconcile their resting positions/rotations after settling, while
  held, penetrating and actively contacted items keep their native constraints.
- Attached props also align after settling. Non-owning peers wait for the
  owner's cleanup; an unexpectedly destroyed replica can recover its shared
  identity and state without resurrecting an item the owner removed.
- Custom data on worn wardrobe contents is preserved when reconstructing armor.
- Scripted-item adapters can synchronize bounded owner state, late-join state
  and reliable deduplicated events. Adapter IDs/versions must match.
- A changed optional AI patch leaves the other mod's method in place instead
  of aborting the whole session. Client AI behavior then needs verification.

Diagnostics shows the content-check status. compatibility-report.json lists
shared/local-only mods and adapter versions. patch-compatibility-report.json
lists overlapping patches and optional AI warnings. Reports contain public mod
identities and method names; they are diagnostic tools, not compatibility certificates.

## Limits

Arbitrary spells, guns, special weapon abilities, custom damage/AI/physics
overhauls, scripted map objectives and unusual creature skeletons still need
specific verification or adapters. Spawning an object and synchronizing its
position cannot reproduce an unknown script's behavior. Existing limits on
player magic, telekinesis and bows remain. Dismemberment assumes compatible
native ragdoll parts; this update does not add arbitrary skeletal replication.

A pure local UI/visual script with no owned or changed catalog data may include
`basmp-compatibility.json` containing `{"localOnly":true}`. This is an explicit
author/operator declaration, not automatic detection. Never use it for gameplay
scripts. Catalog-changing mods cannot opt out using that declaration. Runtime
gameplay settings outside catalog JSON must be declared by their author or
configured consistently by players; arbitrary private state is not fingerprinted.

The source archive includes docs/MOD-COMPATIBILITY.md with the adapter API and
shared-settings declaration. No third-party mod pack is certified by this release.
