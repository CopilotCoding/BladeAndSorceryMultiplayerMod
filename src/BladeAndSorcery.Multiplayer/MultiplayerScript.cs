using System;
using System.IO;
using ThunderRoad;
using UnityEngine;

namespace BladeAndSorcery.Multiplayer;

public sealed class MultiplayerScript : ThunderScript
{
    private GameObject? root;
    public override void ScriptEnable()
    {
        if (root != null) return;
        MultiplayerRuntime.ModFolder = ModData.fullPath;
        root = new GameObject("BladeAndSorcery.Multiplayer");
        UnityEngine.Object.DontDestroyOnLoad(root);
        root.AddComponent<MultiplayerRuntime>();
    }
    public override void ScriptDisable()
    {
        if (root != null) UnityEngine.Object.Destroy(root);
        root = null;
    }
    public override void ScriptUnload() => ScriptDisable();
}

public sealed partial class MultiplayerRuntime : MonoBehaviour
{
    internal static MultiplayerRuntime? Instance { get; private set; }
    internal static string ModFolder = "";
    internal string TestDirectory = "";
    private float nextStatus;
    private int testSpawnStarts, testSpawnEnds;
    private void Awake()
    {
        Instance = this;
        TestDirectory = Argument("-mp-test-dir") ?? "";
        Debug.Log("[BASMP] Native mod loaded; game=" + GameSettings.GetVersionString() + "; minModVersion=" + ThunderRoadSettings.current.game.minModVersion);
        if (TestDirectory.Length > 0)
        {
            Directory.CreateDirectory(TestDirectory);
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BASMP_TEST_SAVE_ROOT")))
                throw new InvalidOperationException("Automated runtime requires isolated saves.");
            ThunderRoadSettings.current.pauseOnVRPresence = false;
            EventManager.onGameLoad += OnTestGameLoaded;
            PlayerSpawner.onSpawn += OnTestPlayerSpawn;
            if (Player.characterData != null) Player.characterData.calibration.calibrated = true;
            Debug.Log("[BASMP] Test mode active; saves=" + Environment.GetEnvironmentVariable("BASMP_TEST_SAVE_ROOT"));
        }
        InitializeControls();
    }
    private void Update()
    {
        NetworkUpdate();
        if (TestDirectory.Length == 0 || Time.realtimeSinceStartup < nextStatus) return;
        nextStatus = Time.realtimeSinceStartup + 1;
        var status = GetStatus();
        File.WriteAllText(Path.Combine(TestDirectory, "status.json"), Newtonsoft.Json.JsonConvert.SerializeObject(status));
    }
    private void OnTestGameLoaded()
    {
        if (Player.characterData != null) Player.characterData.calibration.calibrated = true;
    }
    private void OnTestPlayerSpawn(PlayerSpawner spawner, EventTime time)
    {
        if (time == EventTime.OnStart) testSpawnStarts++;
        else if (time == EventTime.OnEnd) testSpawnEnds++;
    }
    private void OnDestroy()
    {
        EventManager.onGameLoad -= OnTestGameLoaded;
        PlayerSpawner.onSpawn -= OnTestPlayerSpawn;
        Disconnect();
        steamLobbies?.Dispose();steamLobbies=null;
        uiHarmony?.UnpatchAll("zsoul.bladeandsorcery.multiplayer.ui");uiHarmony=null;
        if (Instance == this) Instance = null;
    }
    internal static string? Argument(string name)
    {
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i + 1 < args.Length; i++) if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }
}
