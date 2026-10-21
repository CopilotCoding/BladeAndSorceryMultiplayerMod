using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BladeAndSorcery.Multiplayer.Core;
using HarmonyLib;
using Newtonsoft.Json;
using ThunderRoad;
using UnityEngine;

namespace BladeAndSorcery.Multiplayer;

public sealed class MultiplayerConfiguration
{
    public string PlayerName = "Player";
    public string Address = "127.0.0.1";
    public int Port = 9050;
    public int MaxPlayers = 4;
    public bool SurviveLimbLoss;
    public int SteamLobbyVisibility=1;
    public string Password = "";
    public int TestLatencyMilliseconds, TestPacketLossPercent;
}

public sealed partial class MultiplayerRuntime
{
    internal NetworkSession? Session;
    internal GameWorld? World;
    private Harmony? harmony;
    private Harmony? uiHarmony;
    private bool previousBackground;
    private bool showPanel;
    private Rect panel = new Rect(15, 15, 540, 530);
    private Vector2 panelScroll;
    private string message = "Disconnected";
    private string configPath = "";
    private float nextNativeStatus;
    internal MultiplayerConfiguration Configuration = new MultiplayerConfiguration();
    private readonly Queue<string> logLines = new Queue<string>();
    internal void InitializeControls()
    {
        // Native OnEnable runs before Instantiate binds modOption. Guard only
        // this mod's text components during that binding window.
        uiHarmony=new Harmony("zsoul.bladeandsorcery.multiplayer.ui");
        uiHarmony.Patch(AccessTools.Method(typeof(UITextDisplayModOption),nameof(UITextDisplayModOption.LoadText)),prefix:new HarmonyMethod(typeof(NativeModTextInitialization),nameof(NativeModTextInitialization.AllowLoad)));
        uiHarmony.Patch(AccessTools.Method(typeof(StoreSteam),nameof(StoreSteam.Unload)),prefix:new HarmonyMethod(typeof(NativeSteamShutdown),nameof(NativeSteamShutdown.Prefix)));
        uiHarmony.Patch(AccessTools.Method(typeof(UIModsMenuContentArea),nameof(UIModsMenuContentArea.Setup)),postfix:new HarmonyMethod(typeof(NativeMultiplayerPages),nameof(NativeMultiplayerPages.Postfix)));
        // Native mods are loaded from bytes, so Assembly.Location is empty.
        configPath = Path.Combine(ModFolder, "multiplayer.json");
        try
        {
            if (File.Exists(configPath)) Configuration = JsonConvert.DeserializeObject<MultiplayerConfiguration>(File.ReadAllText(configPath)) ?? new MultiplayerConfiguration();
            else File.WriteAllText(configPath, JsonConvert.SerializeObject(Configuration, Formatting.Indented));
        }
        catch (Exception e) { WriteLog("Configuration error: " + e.Message); }
        Configuration.SteamLobbyVisibility=Mathf.Clamp(Configuration.SteamLobbyVisibility,0,2);
        if (TestDirectory.Length > 0)
        {
            // Isolated game pairs must not contend with a live VR host on 9050.
            Configuration.Port = 19050;
            QualitySettings.vSyncCount = 0; Application.targetFrameRate = 90;
            gameObject.AddComponent<AutomatedDriver>().Initialize(this);
        }
        InitializeConnectionFields();
        BeginCompatibilityScan();
    }
    internal string GameBuildIdentity=>GameSettings.GetVersionString()+"|"+typeof(MultiplayerRuntime).Assembly.ManifestModule.ModuleVersionId.ToString("N")+"|"+typeof(NetworkSession).Assembly.ManifestModule.ModuleVersionId.ToString("N");
    internal void Connect(bool host,ISessionTransport? transport=null,string? steamHost=null)
    {
        if (Session != null) { WriteLog("Disconnect before starting another session"); return; }
        if(transport==null && (steamLobbies?.Busy==true || steamLobbies?.Current.IsValid()==true)){WriteLog("Leave the pending Steam lobby before starting Direct IP.");return;}
        if (Configuration.Port < 1024 || Configuration.Port > 65535) { WriteLog("Port must be 1024-65535"); return; }
        if (Level.current?.data == null || Player.local?.creature == null) { WriteLog("Load a playable level before connecting"); return; }
        if(!EnsureCompatibilityReady(()=>Connect(host,transport,steamHost)))return;
        CloseConnectionKeyboard();
        try
        {
            Session = new NetworkSession(transport);
            Session.SetContentManifest(compatibility!.Manifest);
            if(TestDirectory.Length>0 && TestMismatchContent)Session.SetContentManifest(new ContentManifest(new[]{new ContentEntry{Id="isolated-test-mismatch",Name="Isolated test content mismatch",Hash=new string('A',64)}}));
            Session.Log += WriteLog;
            Session.Closed += reason => { WriteLog(reason);connectionNotice=reason;if(Session?.TransportName=="Steam P2P")steamLobbies?.NoteConnectionClosed(reason); disconnectRequested = true; };
            World = new GameWorld(Session); World.Log += WriteLog;
            World.Match.SurviveLimbLoss=host && Configuration.SurviveLimbLoss;
            harmony = new Harmony("zsoul.bladeandsorcery.multiplayer"); harmony.PatchAll(typeof(MultiplayerRuntime).Assembly);
            SavePatchCompatibilityReport();
            previousBackground = Application.runInBackground; Application.runInBackground = true;
            string build = GameBuildIdentity;
            if (host) Session.Host(Configuration.PlayerName, build, Configuration.Port, Configuration.Password, Configuration.MaxPlayers);
            else Session.Join(steamHost ?? Configuration.Address, Configuration.PlayerName, build, Configuration.Port, Configuration.Password);
            World.InitializeScene(host);
            Session.SetImpairment(Configuration.TestLatencyMilliseconds, Configuration.TestPacketLossPercent);
            WriteLog(host ? "Hosting through "+Session.TransportName : "Joining through "+Session.TransportName);
        }
        catch (Exception e) { WriteLog("Connection failed: " + e); Disconnect(); }
    }
    private bool disconnectRequested;
    internal void RequestDisconnect(string reason){connectionNotice=reason;WriteLog(reason);disconnectRequested=true;}
    internal void NetworkUpdate()
    {
        TickCompatibilityScan();
        TickSteamLobbies();
        TickConnectionKeyboard();
        if(Time.realtimeSinceStartup>=nextNativeStatus){nextNativeStatus=Time.realtimeSinceStartup+.5f;RefreshNativeStatus();}
        if (disconnectRequested) { disconnectRequested = false; Disconnect(); }
        bool openPanel = UnityEngine.InputSystem.Keyboard.current?.f8Key.wasPressedThisFrame == true;
        if (legacyKeyboardAvailable)
        {
            try { openPanel |= Input.GetKeyDown(KeyCode.F8); }
            catch (InvalidOperationException) { legacyKeyboardAvailable = false; }
        }
        if (openPanel) showPanel = !showPanel;
        try
        {
            Session?.Poll(Time.realtimeSinceStartupAsDouble);
            World?.Tick(Time.realtimeSinceStartupAsDouble);
        }
        catch (Exception exception)
        {
            WriteLog("Session stopped after an integration error: " + exception);
            Disconnect();
        }
    }
    private void FixedUpdate() => World?.FixedTick(Time.realtimeSinceStartupAsDouble);
    internal void Disconnect()
    {
        compatibilityContinuation=null;
        CloseConnectionKeyboard();
        if(World?.CombatCaptureActive==true) SaveCombatCapture();
        var restoreNativeDeath = World?.Match.CaptureNativeDeathRestore();
        World?.Dispose(); World = null; Session?.Dispose(); Session = null;
        steamLobbies?.Leave();
        harmony?.UnpatchAll("zsoul.bladeandsorcery.multiplayer"); harmony = null;
        if (TestDirectory.Length == 0) Application.runInBackground = previousBackground;
        try { restoreNativeDeath?.Invoke(); }
        catch (Exception error) { WriteLog("Native death flow restoration failed: " + error); }
        WriteLog("Disconnected");
    }
    internal void WriteLog(string text)
    {
        message = text; Debug.Log("[BASMP] " + text);
        logLines.Enqueue(DateTime.UtcNow.ToString("o") + " " + text); while (logLines.Count > 12) logLines.Dequeue();
        if (TestDirectory.Length > 0) File.AppendAllText(Path.Combine(TestDirectory, "multiplayer.log"), DateTime.UtcNow.ToString("o") + " " + text + Environment.NewLine);
    }
    private void OnGUI()
    {
        if (!showPanel) return;
        panel = GUI.Window(629730, panel, _ =>
        {
            GUILayout.Label("Blade & Sorcery Multiplayer");
            GUILayout.Label(ConnectionStatus);
            int tab=GUILayout.Toolbar(SelectedMenuTab,MenuTabs);
            if(tab!=SelectedMenuTab)MenuTab(tab);
            panelScroll=GUILayout.BeginScrollView(panelScroll);
            if(SelectedMenuTab==0)
            {
                GUILayout.Label("Choose Steam for lobbies/invites or Direct IP for an address and UDP port. Set shared connection preferences under Player & hosting.");
                GUILayout.Label(SessionPlayers);
                if((Session!=null || steamLobbies?.Busy==true || steamLobbies?.Current.IsValid()==true) && GUILayout.Button("Leave session"))Disconnect();
            }
            else if(SelectedMenuTab==1)
            {
                GUILayout.Label(SteamStatus);
                int visibility=GUILayout.Toolbar(Configuration.SteamLobbyVisibility,new[]{"Public","Friends only","Invite only"});
                if(visibility!=Configuration.SteamLobbyVisibility)SteamVisibilityChoice(visibility);
                if(GUILayout.Button("Host Steam lobby"))HostSteamAction();
                if(GUILayout.Button("Invite Steam friends"))InviteSteamAction();
                if(GUILayout.Button("Join invited Steam lobby"))JoinSteamInviteAction();
                if(GUILayout.Button("Refresh Steam lobbies"))RefreshSteamAction();
                var rows=steamLobbies?.Results;
                if(rows!=null)foreach(var row in rows.ToArray())if(GUILayout.Button(row.Name+" | "+row.Map+" | "+row.Members+"/"+row.Limit+(row.Password ? " | password" : "")))
                {steamSelection=row.Id;JoinSteamAction();}
            }
            else if(SelectedMenuTab==2)
            {
                GUILayout.Label(ServerAddress);GUILayout.Label(ConnectionHelp);
                GUILayout.Label("Host IP / hostname");addressInput=GUILayout.TextField(addressInput,128);
                GUILayout.Label("UDP port");portInput=GUILayout.TextField(portInput,5);
                if(GUILayout.Button("Paste IP from clipboard"))PasteConnectionEndpoint();
                if(GUILayout.Button("Host Direct IP") && ApplyDesktopEndpoint())Connect(true);
                if(GUILayout.Button("Join Direct IP") && ApplyDesktopEndpoint())Connect(false);
                if(GUILayout.Button("Save address and port") && ApplyDesktopEndpoint())SaveConnectionSettings();
            }
            else if(SelectedMenuTab==3)
            {
                GUILayout.Label(SavedProfile);
                GUI.enabled=Session==null;
                GUILayout.Label("Player name"); Configuration.PlayerName = GUILayout.TextField(Configuration.PlayerName, 32);
                GUILayout.Label("Maximum players: " + Configuration.MaxPlayers); Configuration.MaxPlayers = Mathf.RoundToInt(GUILayout.HorizontalSlider(Configuration.MaxPlayers, 2, 8));
                GUILayout.Label("Session password"); Configuration.Password = GUILayout.PasswordField(Configuration.Password, '*', 64);
                if (GUILayout.Button("Save profile")) SaveConnectionSettings();
                GUI.enabled=true;
            }
            else if(SelectedMenuTab==4)
            {
                GUILayout.Label(WorldStatus);GUILayout.Label(NativeWaveStatus);
                GUILayout.Label("Choose maps and equipment normally. Use the map's normal wave book for co-op.");
                if (Session?.IsHost==true && World != null)
                {
                    GUILayout.BeginHorizontal();
                    foreach (MultiplayerMode mode in Enum.GetValues(typeof(MultiplayerMode))) if (GUILayout.Button(mode.ToString())) World.Match.SetMode(mode);
                    GUILayout.EndHorizontal();
                    bool friendly=GUILayout.Toggle(World.Match.FriendlyFire,"Friendly fire");if(friendly!=World.Match.FriendlyFire)FriendlyFireOption(friendly);
                    bool limbs=GUILayout.Toggle(World.Match.SurviveLimbLoss,"Survive limb loss");if(limbs!=World.Match.SurviveLimbLoss)SetLimbLossRule(limbs);
                }
                if (World != null)
                {
                    GUILayout.Label("Mode: " + World.Match.Mode);
                    foreach (var p in Session!.Peers.Values)
                    {
                        World.Match.Scores.TryGetValue(p.Id, out var score);
                        GUILayout.Label($"{p.Name}: {score?.Kills ?? 0} kills / {score?.Deaths ?? 0} deaths");
                    }
                }
                if (GUILayout.Button("Respawn")) World?.Match.Respawn();
            }
            else
            {
                GUILayout.Label(CompatibilityStatus);
                if(Session==null && GUILayout.Button("Rescan mods (offline)"))RescanMods();
                if(GUILayout.Button("Record combat capture"))BeginCombatCapture();
                if(GUILayout.Button("Save combat capture"))SaveCombatCapture();
                GUILayout.Label("Experimental legacy wave: prefer the normal wave book for co-op.");
                if(GUILayout.Button("Experimental sword wave"))World?.Match.StartWave();
            }
            GUILayout.EndScrollView();
            GUILayout.Label(message.Length > 120 ? message.Substring(0, 120) : message);
            GUI.DragWindow(new Rect(0, 0, 540, 22));
        }, "Multiplayer — F8 to close");
    }
    private bool legacyKeyboardAvailable = true;
    [ModOption("Connection status", defaultValueIndex=0, saveValue=false, applyOnLoad=false)]
    [ModOptionTextDisplay("Offline — choose Steam or Direct IP to connect.", "")]
    [ModOptionOrder(-99)]
    public static string ConnectionStatus = "Offline — choose Steam or Direct IP to connect.";
    [ModOption("Connection help", defaultValueIndex=0, saveValue=false, applyOnLoad=false)]
    [ModOptionTextDisplay("Edit host IP and UDP port here. Confirm saves. Then press Join Direct IP.", "")]
    [ModOptionCategory("Direct IP",2),ModOptionOrder(1)]
    public static string ConnectionHelp = "Edit host IP and UDP port here. Confirm saves. Then press Join Direct IP.";
    [ModOption("World status",saveValue=false,applyOnLoad=false)]
    [ModOptionTextDisplay("Choose a map normally while connected.", "")]
    [ModOptionCategory("Play",4),ModOptionOrder(-1)]
    public static string WorldStatus="Choose a map normally while connected.";
    [ModOption("Server address",defaultValueIndex=0,saveValue=false,applyOnLoad=false)]
    [ModOptionTextDisplay("127.0.0.1:9050", "")]
    [ModOptionCategory("Direct IP",2),ModOptionOrder(0)]
    public static string ServerAddress = "127.0.0.1:9050";
    private void RefreshNativeStatus()
    {
        RefreshNativeText("Mod compatibility",CompatibilityStatus);
        string status=Session==null ? "Offline — choose the Steam or Direct IP tab to connect." :
            !Session.Connected ? "Connecting to "+Configuration.Address+"..." :
            (Session.IsHost ? "Hosting" : "Joined")+" | "+Session.Peers.Count+" players | "+(Level.current?.data?.id ?? "loading")+" | "+Session.TransportName+" | "+Session.Ping+" ms";
        ConnectionStatus=status;ServerAddress=Configuration.Address+":"+Configuration.Port;
        ConnectionHelp=connectionNotice.Length>0 ? connectionNotice : "Edit host IP and UDP port here. Confirm saves. Then press Join Direct IP.";
        RefreshNativeText("Connection status",ConnectionStatus);RefreshNativeText("Server address",ServerAddress);RefreshNativeText("Connection help",ConnectionHelp);
        RefreshLimbLossOption();
        WorldStatus=World?.TravelStatus ?? "Choose a map normally while connected.";
        RefreshNativeText("World status",WorldStatus);
        RefreshSteamMenu();
        RefreshUnifiedMenu();
    }
    private static void RefreshNativeText(string name,string value)
    {
        foreach(var option in ModManager.loadedMods.Where(m=>m.fullPath==ModFolder).SelectMany(m=>m.modOptions).Where(o=>o.name==name))
        {
            if(option.parameterValues.FirstOrDefault() is not ModOptionString text) continue;
            bool changed=text.stringValue!=value;
            text.stringValue=value;
            if(option.uiComponent is UITextDisplayModOption display && display.isActiveAndEnabled && (changed || display.textObj.text!=value)) display.Reload();
        }
    }
    // A native ButtonList runs the NEXT value. Each action therefore gets its
    // own one-value list: every press executes exactly the label on the button.
    public static ModOptionInt[] PressAction() => new[] { new ModOptionInt("Press", 0) };
    [ModOption("Host Direct IP",tooltip="Host through Direct IP / UDP. Steam lobbies have separate controls under Steam multiplayer.", valueSourceName = nameof(PressAction), saveValue = false, applyOnLoad = false)]
    [ModOptionButton]
    [ModOptionCategory("Direct IP",2),ModOptionOrder(5)]
    public static void HostAction(int press = 0) => Instance?.Connect(true);
    [ModOption("Join Direct IP",tooltip="Join the saved Direct IP / UDP address. Steam lobbies have separate controls under Steam multiplayer.", valueSourceName = nameof(PressAction), saveValue = false, applyOnLoad = false)]
    [ModOptionButton,ModOptionCategory("Direct IP",2),ModOptionOrder(6)]
    public static void JoinAction(int press = 0) => Instance?.Connect(false);
    [ModOption("Leave session", valueSourceName = nameof(PressAction), saveValue = false, applyOnLoad = false)]
    [ModOptionButton,ModOptionCategory("Session",0),ModOptionOrder(2)]
    public static void LeaveAction(int press = 0) => Instance?.Disconnect();
    [ModOption("Respawn player", valueSourceName = nameof(PressAction), saveValue = false, applyOnLoad = false)]
    [ModOptionButton, ModOptionCategory("Play", 4), ModOptionOrder(5)]
    public static void RespawnAction(int press = 0) => Instance?.World?.Match.Respawn();
    [ModOption("Experimental sword wave", tooltip="Legacy test wave. For normal co-op, use the map's native wave book; either player can select, start and stop it.", valueSourceName = nameof(PressAction), saveValue = false, applyOnLoad = false)]
    [ModOptionButton, ModOptionCategory("Diagnostics", 5), ModOptionOrder(20)]
    public static void WaveAction(int press = 0) => Instance?.World?.Match.StartWave();
    public static ModOptionInt[] ModeChoices() => new[] { new ModOptionInt("Free for all", 0), new ModOptionInt("Duel", 1), new ModOptionInt("Teams", 2), new ModOptionInt("Co-op", 3) };
    [ModOption("Game mode (host)", valueSourceName = nameof(ModeChoices), saveValue = false, applyOnLoad = false)]
    [ModOptionCategory("Play", 4), ModOptionOrder(1)]
    public static void ModeAction(int mode = 0) { if (mode >= 0 && mode <= 3 && Instance?.refreshingMatchOptions==false) Instance.World?.Match.SetMode((MultiplayerMode)mode); }
    [ModOption("Maximum players (next host)", defaultValueIndex = 2)]
    [ModOptionIntValues(2, 8, 1)]
    [ModOptionCategory("Player & hosting", 3), ModOptionOrder(4)]
    public static void PlayerLimit(int value = 4) { if (Instance != null) Instance.Configuration.MaxPlayers = Mathf.Clamp(value, 2, 8); }
}

internal static class NativeModTextInitialization
{
    internal static bool AllowLoad(UITextDisplayModOption __instance)
        => __instance.modOption!=null || __instance.GetComponentInParent<UIModsMenuContentArea>()?.ModData?.fullPath!=MultiplayerRuntime.ModFolder;
}
