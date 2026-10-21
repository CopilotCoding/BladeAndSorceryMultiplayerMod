# Building the v0.3.0 source release

This is the source archive, separate from the ready-to-install PCVR mod ZIP.
For playing without compiling, use `BladeAndSorceryMultiplayer-v0.3.0-PCVR.zip`
and its START-HERE.txt. Player instructions are also in `mod/README.md`.

## Requirements

- Windows, PowerShell 7 (`pwsh`), and a .NET SDK that supports net8.0.
  Development used SDK 9.0.305; the developer tools and console checks target net8.0.
  Running those tools requires the .NET 8 runtime as well.
- Your own Blade & Sorcery **PCVR** installation matching the references used for
  this release: 1.3.2.261012003, Unity 2021.3.38f1. Nomad is unsupported.
- Internet access for the first NuGet restore. Dependencies are pinned in the
  project files (Harmony 2.4.1, LiteNetLib 1.3.1, Mono.Cecil 0.11.5).

## Build and install in one command

Extract the ZIP to a writable folder. Open PowerShell 7 in the extracted project
folder (the one containing Directory.Build.props, src, and scripts).

Close Blade & Sorcery, then run:

```powershell
./scripts/Build-And-Install.ps1
```

The script finds the game in Steam's libraries, copies its Managed folder into
this workspace, removes stale top-level reference DLLs, restores NuGet packages,
builds, and installs the mod. It stops if copying or building fails. It does not
launch or terminate the game. To select a particular installation:

```powershell
$gamePath = 'D:\SteamLibrary\steamapps\common\Blade & Sorcery'
./scripts/Build-And-Install.ps1 -GamePath $gamePath
```

`-Configuration Debug` is available; Release is the default. If installation
fails with access denied, fix write access to that game's Mods folder and rerun
the install command below. Game assemblies, assets, saves, binaries and private
connection settings remain excluded from the source ZIP.

## Build separately

After references have been copied, use `./scripts/Build.ps1 -Restore` for a first
build, or `./scripts/Build.ps1` when packages are already restored. For manual
setup, copy the contents of your game's `BladeAndSorcery_Data/Managed` into
`Managed` at this project root; `Managed/ThunderRoad.dll` must exist directly
there. The build script builds the mod and the isolated-runtime patcher and puts
the installable mod in:

```text
artifacts/mod/BladeAndSorceryMultiplayer/
```

This mod uses the game's native ThunderScript loader. BepInEx is not required.
The build selects Harmony's net472 runtime assembly for Unity Mono.

## Install your build

Close the installed game, then run:

```powershell
./scripts/Install-Mod.ps1 -GamePath $gamePath
```

Set `$gamePath` to your installation first, or omit `-GamePath $gamePath` for the
default Steam location under `C:\Program Files (x86)\Steam`.

The installer checks that ThunderRoad.dll matches your build references and
refuses to replace the mod while that installed game is running. See
`mod/README.md` for the in-game menus, Steam lobbies and Direct IP setup.

## Checks and optional game automation

The networking console checks can run without a headset or game process:

```powershell
dotnet run --project ./tests/SteamSessionChecks -c Release
dotnet run --project ./tests/ProtocolTests -c Release
```

These commands perform their own initial restore. They do not establish real
headset combat quality or cross-account Steam connectivity.

For optional headset-free game automation, first build, then create the separate
local test runtime from your own installed game:

```powershell
./scripts/New-TestRuntime.ps1 -GamePath $gamePath
./scripts/Start-TestPair.ps1
./scripts/Invoke-SessionUiTests.ps1
./scripts/Stop-TestInstances.ps1 -Names host,client,third
```

New-TestRuntime uses directory junctions for large game assets and copies the
startup/managed files it modifies. Keep the installed game at its original path
while using that runtime. Never distribute `.runtime`. It uses MockHmd and
separate saves. Tests may launch two local game windows and require substantial
memory. Additional focused checks and their limitations are documented in
`README.md` and `docs/UPDATE-0.3.md`. Historical results are not test results for
your newly compiled DLLs.

## Create another source ZIP

```powershell
./scripts/Package-Source.ps1
```

The version comes from `mod/manifest.json`. The script uses a source allowlist,
includes a SHA-256 manifest, and verifies every archived file against its source.
Output: `artifacts/BladeAndSorceryMultiplayer-v<version>-Source.zip`.
The separate Package-Mod.ps1 produces player ZIPs and has validation requirements
described in the root README.

See TODO.txt for unfinished features and known validation gaps. Third-party
notices and licenses are in `mod/THIRD_PARTY_NOTICES.md` and `mod/licenses/`.
