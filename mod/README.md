# Blade & Sorcery Multiplayer — v0.4.0

Share Home, travel together, fight PvP, or use the normal wave book for co-op. Everyone installs this same ZIP; one person hosts and everyone else joins them.

**Using other mods:** match gameplay mods, versions, files and load order on every PC. v0.4.0 checks content before joining and preserves supported native item/armor data. Special scripted abilities may need an adapter. See COMPATIBILITY.md inside the mod folder for coverage, diagnostics and limits. Older multiplayer versions cannot join this build.

## 1. Install it — everyone does this

You need **Blade & Sorcery on PC through Steam**, a PCVR headset, and this same mod version on every PC. The game version this build targets is **1.3.2.261012003**. An Index or a Quest connected to a PC uses the game's normal VR setup. Quest standalone / Nomad is not supported.

1. Close Blade & Sorcery.
2. Extract the ZIP. Open the extracted folder.
3. In Steam, right-click **Blade & Sorcery → Manage → Browse local files**.
4. Open **BladeAndSorcery_Data → StreamingAssets → Mods**. Create `Mods` if it is missing.
5. Copy the extracted **BladeAndSorceryMultiplayer** folder into `Mods`.
6. Check the result. The file must be here:

   ```text
   Blade & Sorcery/
     BladeAndSorcery_Data/
       StreamingAssets/
         Mods/
           BladeAndSorceryMultiplayer/
             manifest.json
             BladeAndSorcery.Multiplayer.dll
             ...
   ```

   If there is another `BladeAndSorceryMultiplayer` folder between that folder and `manifest.json`, move the inner folder up. Copy the whole mod folder, including all its DLLs.

7. Start the game through Steam with your usual PCVR setup. Load your **Home or Arena in Sandbox** using normal map selection.
8. Open the **in-game menu → Mods → Blade & Sorcery Multiplayer**. Use the **Multiplayer tab** arrows at the top to choose a page. **Connection status** stays visible on every page.

No building, scripts, BepInEx installation, or SDK is required.

## 2. Choose how to connect

Use the **same connection method** as your host:

| Method | What the joining player needs |
| --- | --- |
| **Steam** | A Steam invitation, or the host's lobby in this mod's lobby browser. Each person uses a separate online Steam account. |
| **Direct IP** | The host's reachable IP address and UDP port. Internet hosting requires a working port forward or an existing private network. |

Steam multiplayer still needs verification between separate PCs. If it fails, use Direct IP. Load a playable map before connecting; joining then loads the host's current map automatically. Home, Arena, Canyon, Market and Citadel have automated shared-world coverage.

## 3A. Connect through Steam

### Host — the person creating the session

1. In the mod menu, open **Steam**.
2. Set **Steam visibility (next lobby)** to **Friends only** for a normal friends session. **Public** makes the lobby discoverable in this mod's browser; **Invite only** requires an invitation.
3. Optional: before hosting, open **Player & hosting** and set **Maximum players (next host)**. The default is **4, including you**; the range is 2–8. Set **Edit server password** here if you want a password, and tell your friends what it is.
4. Return to **Steam** and press **Host Steam lobby**. Check **Steam status** for the result. **Host Direct IP** in the **Direct IP** tab is the separate Direct IP button.
5. Press **Invite Steam friends**. Steam's overlay opens; select your friends and send the invitations there.

### Friend — the person joining

1. Start Steam online on your own account, start the game, and load **Home or Arena in Sandbox**.
2. If the host set a password, open the mod's **Player & hosting → Edit server password**, enter it, and confirm with the keyboard's checkmark.
3. Accept the host's invitation in Steam. Once a playable map is loaded and you are out of any other session, the mod handles the invitation. If you need to retry, press **Steam → Join invited Steam lobby**.
4. Check **Connection status** at the top of any tab. Wait until it says **Joined** and the player count includes you and the host.

**Joining without an invitation:** open **Steam → Refresh Steam lobbies**, use the arrows on **Selected Steam lobby** to select your host, check **Selected lobby details**, then press **Join selected Steam lobby**. Invite-only lobbies require an invitation.

Steam joining uses these lobby controls; you do not enter an IP or UDP port for it.

## 3B. Connect through Direct IP

### Host — the person creating the session

1. Load **Home or Arena in Sandbox**, open the mod menu and select the **Direct IP** tab.
2. Leave the UDP port at **9050**, or use **Edit UDP port** to choose another. Tell your friends the port you chose.
3. Optional: set **Player & hosting → Maximum players (next host)** and **Edit server password** before hosting.
4. Press **Host Direct IP** in the **Direct IP** tab. Check that **Connection status** says **Hosting**.
5. Give your friends your reachable address and port, for example `YOUR-HOST-ADDRESS:9050`. Replace `YOUR-HOST-ADDRESS` with your actual address.

**Which address does the host share?**

- **Same home network:** share the host PC's local IPv4 address. On that PC, open Command Prompt, run `ipconfig`, and find the **IPv4 Address** under the active Ethernet or Wi-Fi connection.
- **Different homes over the Internet:** share your router's public IP address. In your router's port-forwarding settings, forward **UDP 9050** to the host PC's local IPv4 address, using 9050 internally too. If you chose another port, use it throughout. Allow Blade & Sorcery through Windows Firewall on the network you are using.
- **An existing private network between your PCs:** use the host's address on that network and ensure it allows the selected UDP port.

Only the host sets up the port forward. There is no TCP port to forward. Some Internet connections, including CGNAT, prevent ordinary inbound port forwarding; if that applies, try Steam or an existing private network instead.

### Friend — the person joining

1. Load **Home or Arena in Sandbox** and open the mod menu.
2. On your desktop, copy the address your host gave you, including the port if provided.
3. In VR, select the **Direct IP** tab and press **Paste IP from clipboard**. This saves the address; it does not connect yet.
4. If the host sent only an IP, use **Edit UDP port** to set the host's port, normally **9050**. Alternatively, use **Edit host IP / hostname** to type the address manually. The keyboard's checkmark saves; X cancels.
5. If the host set a password, enter it under **Player & hosting → Edit server password**.
6. Press **Join Direct IP**. Wait for **Connection status** to say **Joined** and show both players.

`127.0.0.1` connects to your own PC. It will not connect to your friend's PC. When typing manually, enter the IP in the IP field and the port in the port field. Clipboard paste also accepts `IP:port`.

## 4. Start fighting

Once connected, you should see each other on the shared map. Spawn and equip weapons using the game's normal item book, then fight with your usual VR controls. **There is no separate Start PvP button.** The default game mode is **Free for all**.

- **Respawn:** deaths trigger an automatic respawn. To reset yourself manually, use **Play → Respawn player**.
- **Survive dismemberment:** the host can enable **Play → Survive limb loss (host)** for everyone. Arms, hands, legs and feet can be severed without forced instant death. Normal damage and head/neck cuts can still kill. Respawn to restore missing limbs.
- **Change game mode:** the host uses **Play → Game mode (host)**. For ordinary PvP, leave it on **Free for all**.
- **Leave:** use **Session → Leave session**. If the host leaves, the session ends for everyone. Leave before changing connection settings or joining a different host.
- **Desktop controls:** F8 opens an optional desktop panel. You can do the connection setup through the VR mod menu instead.

## 5. Home, maps and co-op

**Equip in Home:** use the normal item book, hands, holsters, armor and supported native inventory. Your character/save remains yours. The shared Home is the host's world; joining does not replace your personal Home save with theirs.

**Travel:** either player uses the normal map interface and selects the map/mode/options. Everyone agrees on the destination before unloading, then loads the same seed. The first accepted selection wins if two people choose at once. Keep the session running: held weapons, holsters, worn equipment and supported native inventory travel with you and return to Home. Loose props belong to the current map; carry or store loot you want to take.

**During loading:** wait for your own loading screen to finish. **Play → World status** explains the destination or problem. Missing assets cancel travel before unloading. A failed load attempts to return the session to its last stable map; a loader that stays stuck disconnects with a message. Custom map assets must be installed on every PC.

**Co-op:** use the map's **normal wave book**. Either player selects an enemy/wave and presses its normal **Start** button. Both books show the same selection/progress. Enemies and equipment spawn once, kills advance the same wave, and completion/Stop/cleanup are shared. Players can join a running wave. Use the normal **Stop** or **Return** button when finished. Native waves temporarily enable co-op team protection and restore the previous PvP mode afterward. The host can change **Play → Friendly fire (host)**.

**Return Home:** select Home through the normal map interface. Everyone travels back with their own carried equipment and local character identity.

The old **Experimental sword wave** button is under **Diagnostics** for development. Use the normal wave book for ordinary co-op.

| Multiplayer tab | Use it for |
| --- | --- |
| **Session** | Instructions, players, Leave session. |
| **Steam** | Visibility, host/invite, browse/join, invitation retry. |
| **Direct IP** | Address/UDP port, edit/paste, Host Direct IP, Join Direct IP. |
| **Player & hosting** | Your name, optional password for either method, next host's player limit. |
| **Play** | World/wave status, match rules, friendly fire, survive limb loss, respawn. |
| **Diagnostics** | Mod compatibility status, offline rescan, combat captures and the experimental legacy wave. |

F8 opens a desktop panel with the same pages. The VR menu works without it.

## If something goes wrong

| Problem | What to check |
| --- | --- |
| The mod is missing from the in-game Mods menu | Close the game and check the folder layout above. The manifest must be directly inside `Mods/BladeAndSorceryMultiplayer`. Check that you launched PCVR, not Nomad. |
| Steam lobby is missing | Confirm the host used **Host Steam lobby**. Press **Refresh Steam lobbies** again. For Friends only, use Steam friends accounts; for Invite only, ask for an invitation. Read **Steam status** for errors. |
| Invitation does nothing | Load Home or Arena first, leave any existing session, then press **Join invited Steam lobby**. Check the password if required. |
| Direct IP will not join | Confirm the host says **Hosting**, both sides use the same UDP port, the password matches, and the address belongs to the host. For Internet play, check the host's UDP port forward and firewall. |
| A different-build error appears | Everyone must use the same game build and this same mod ZIP. Replace older copies and restart the game. |
| A missing/different/client-only mod error appears | Match the named gameplay mods, versions, files and load order on every PC, then restart. Diagnostics shows the content-check status. Read COMPATIBILITY.md for local-only declarations and scripted-ability limits. |
| Connected, but you cannot find each other | Check the player count and **Play → World status**. Wait for loading and a safe arrival. If necessary, leave and rejoin the host's current map. |
| You cannot damage your friend | Check that the host selected **Free for all**, rather than Co-op or a mode that protects teammates. |
| Someone wants to host again after a session with broken props | Leave and reload the current map before creating the next session. |

For a combat bug, use **Diagnostics → Record combat capture**, reproduce it, then press **Save combat capture**. Send the files from `Mods/BladeAndSorceryMultiplayer/Diagnostics` to the person helping you. Record on both PCs if possible.

## What to expect from this build

This is a development build. Shared-world checks cover Home, Arena, Canyon, Market and Citadel, loadouts, ownership, late joining and load recovery. Native wave-book co-op uses native enemy choices and the same combat replication. This does not establish every map, procedural dungeon, third-party mod or base-game interaction. Player spellcasting, telekinesis and bow mechanics still need dedicated multiplayer work. Steam across separate accounts/PCs and real Index + Quest-PCVR combat feel remain unverified. See BUILD-INFO.txt for checks matching this exact build.

## Updating or uninstalling

Close the game first. To update, replace the installed `BladeAndSorceryMultiplayer` folder with the new one from the ZIP, then restart. Everyone in the session must update. If you want to keep your saved name, address and password, back up `multiplayer.json` from the old mod folder and copy it into the new folder. Keep that file private; it contains your saved connection settings.

To uninstall, delete `Mods/BladeAndSorceryMultiplayer`.
