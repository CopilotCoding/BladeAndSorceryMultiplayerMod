using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BladeAndSorcery.Multiplayer.Core;
using HarmonyLib;
using ThunderRoad;
using UnityEngine;

namespace BladeAndSorcery.Multiplayer;

internal sealed class SharedScene
{
    internal string Level = "", Mode = "";
    internal Dictionary<string, string> Options = new Dictionary<string, string>();
}

internal sealed partial class GameWorld
{
    private bool sceneReady;
    private bool applyingScene;
    private SharedScene? expectedScene, proposedScene, stableScene;
    private readonly TravelAgreement travelAgreement = new TravelAgreement();
    private uint nextTravelId, proposedTravelId;
    private double sceneDeadline, nextSceneSignal;
    private bool localSceneLoaded, sceneReadySent, recoveringScene;
    private uint preparedClientWorld;
    private readonly HashSet<ushort> loadedPeers = new HashSet<ushort>();
    internal string TravelStatus { get; private set; } = "";
    internal bool SceneUsable => sceneReady && !LevelManager.isLoadingLocked;
    internal double DelaySceneReadyUntil;
    internal object SceneSnapshot()=>new {ready=sceneReady,loaded=localSceneLoaded,status=TravelStatus,revision=Session.WorldRevision,loading=LevelManager.isLoadingLocked,expected=expectedScene?.Level,proposal=travelAgreement.Active,seed=Level.seed,options=Level.current?.options,loadedPeers=loadedPeers.ToArray(),pendingEquipment=travelLoadout?.Pending ?? 0};
    internal void InitializeScene(bool host)
    {
        sceneReady = host; localSceneLoaded = host;
        if(host){stableScene=expectedScene=CurrentScene();loadedPeers.Add(Session.LocalId);}
    }
    private SharedScene CurrentScene()
    {
        var scene = new SharedScene { Level = Level.current.data.id, Mode = Level.current.mode.name, Options = Level.current.options != null ? new Dictionary<string, string>(Level.current.options) : new Dictionary<string, string>() };
        scene.Options["Seed"] = Level.seed.ToString(CultureInfo.InvariantCulture);
        scene.Options.Remove(LevelOption.PlayerContainerId.Get());
        return scene;
    }
    private static void WriteScene(BinaryWriter writer, SharedScene scene)
    {
        Protocol.String(writer, scene.Level); Protocol.String(writer, scene.Mode); writer.Write((byte)scene.Options.Count);
        foreach (var option in scene.Options.OrderBy(o => o.Key)) { Protocol.String(writer, option.Key); Protocol.String(writer, option.Value, 512); }
    }
    private static SharedScene ReadScene(BinaryReader reader)
    {
        var scene = new SharedScene { Level = Protocol.String(reader), Mode = Protocol.String(reader) };
        int count = reader.ReadByte(); if (count > 64) throw new InvalidDataException("Too many level options");
        for (int i = 0; i < count; i++) scene.Options.Add(Protocol.String(reader), Protocol.String(reader, 512));
        return scene;
    }
    private static string CheckScene(SharedScene scene)
    {
        if(!Catalog.TryGetData<LevelData>(scene.Level,out var data,false) || scene.Level.Equals("Master",StringComparison.OrdinalIgnoreCase) || scene.Level==ThunderRoadSettings.current.game.mainMenuLevelId) return "Map is unavailable: "+scene.Level;
        if(!data.TryGetMode(scene.Mode,out _)) return "Map mode is unavailable: "+scene.Mode;
        scene.Options.TryGetValue(LevelOption.SceneVariantId.Get(),out var variant);
        if(data.GetSceneLocationOrDefault(variant)==null) return "Map assets are unavailable: "+scene.Level;
        if(scene.Options.Count>64 || scene.Options.Any(p=>p.Key.Length==0)) return "Invalid map options";
        return "";
    }
    internal bool InterceptNativeTravel(LevelData data, LevelData.Mode? mode, Dictionary<string,string>? options)
    {
        if(applyingScene || disposed || !Session.Connected) return true;
        if(data==null) return true;
        if(data.id==ThunderRoadSettings.current.game.mainMenuLevelId || data.id.Equals("Master",StringComparison.OrdinalIgnoreCase))
        {
            if(sceneDeadline>0) FailScene("The game could not load the destination map.");
            else MultiplayerRuntime.Instance?.RequestDisconnect("Left the multiplayer world.");
            return true;
        }
        var scene=new SharedScene {Level=data.id,Mode=(mode ?? data.GetMode()).name,Options=options!=null ? new Dictionary<string,string>(options) : new Dictionary<string,string>()};
        scene.Options.Remove(LevelOption.PlayerContainerId.Get());
        if(!scene.Options.ContainsKey("Seed")) scene.Options["Seed"]=UnityEngine.Random.Range(1,int.MaxValue).ToString(CultureInfo.InvariantCulture);
        if(Session.IsHost) ProposeScene(scene);
        else if(sceneDeadline==0 && !travelAgreement.Active)
        {
            Session.Publish(MessageKind.SceneRequest,Protocol.Encode(w=>WriteScene(w,scene)));
            SetTravelStatus("Requested "+scene.Level+". Waiting for the session.");
        }
        else SetTravelStatus("A map transition is already in progress.");
        return false;
    }
    private void SetTravelStatus(string text){TravelStatus=text;Log?.Invoke(text);}
    private void NoticeScene(string text)
    {
        SetTravelStatus(text);
        if(Session.IsHost) Session.Publish(MessageKind.SceneNotice,Protocol.Encode(w=>Protocol.String(w,text,1024)));
    }
    private void UpdateArrivalStatus()
    {
        if(!Session.IsHost || !sceneReady)return;
        int waiting=Session.Peers.Keys.Count(p=>!loadedPeers.Contains(p));
        SetTravelStatus(waiting==0 ? "Everyone arrived in "+Level.current.data.id+"." : "Arrived in "+Level.current.data.id+". Waiting for "+waiting+" player(s) to finish loading.");
    }
    private void ProposeScene(SharedScene scene)
    {
        if(!Session.IsHost) return;
        if(sceneDeadline>0 || travelAgreement.Active || LevelManager.isLoadingLocked)
        {NoticeScene("A map transition is already in progress; later selections were ignored.");return;}
        var error=CheckScene(scene);if(error.Length>0){NoticeScene(error);return;}
        proposedTravelId=++nextTravelId;
        if(!travelAgreement.Begin(proposedTravelId,Session.Peers.Keys,Now)) return;
        proposedScene=scene;
        SetTravelStatus("Checking "+scene.Level+" with all players before loading.");
        Session.Publish(MessageKind.ScenePrepare,Protocol.Encode(w=>{w.Write(proposedTravelId);WriteScene(w,scene);}));
    }
    private void ReceiveScenePrepare(ushort source,BinaryReader reader)
    {
        uint proposal=reader.ReadUInt32();var scene=ReadScene(reader);Protocol.End(reader);
        if(source!=1 || Session.IsHost) return;
        var error=CheckScene(scene);
        if(LevelManager.isLoadingLocked || sceneDeadline>0) error="Already loading a map";
        Session.Publish(MessageKind.ScenePrepared,Protocol.Encode(w=>{w.Write(proposal);w.Write(error.Length==0);Protocol.String(w,error,1024);}));
        SetTravelStatus(error.Length==0 ? "Preparing to travel to "+scene.Level : error);
    }
    private void ReceiveScenePrepared(ushort source,BinaryReader reader)
    {
        uint proposal=reader.ReadUInt32();bool accepted=reader.ReadBoolean();string reason=Protocol.String(reader,1024);Protocol.End(reader);
        if(Session.IsHost) travelAgreement.Reply(proposal,source,accepted,reason);
    }
    private void SendScene(ushort peer)
    {
        if(!Session.IsHost || expectedScene==null) return;
        Session.SendTo(peer, MessageKind.Scene, Protocol.Encode(w=>WriteScene(w,expectedScene)));
    }
    private bool SceneMatches(SharedScene scene)
    {
        if(Level.current?.data?.id!=scene.Level || Level.current.mode?.name!=scene.Mode) return false;
        if(scene.Options.TryGetValue("Seed",out var seed) && seed!=Level.seed.ToString(CultureInfo.InvariantCulture)) return false;
        return scene.Options.Where(p=>p.Key!="Seed").All(p=>Level.current.options!=null && Level.current.options.TryGetValue(p.Key,out var value) && value==p.Value);
    }
    private void ReceiveScene(ushort source, BinaryReader reader)
    {
        var scene=ReadScene(reader);Protocol.End(reader);
        if(source!=1 || Session.IsHost) return;
        var error=CheckScene(scene);
        if(error.Length>0){FailScene(error);return;}
        // Repeated host announcements for slow/late joiners never restart a load.
        if(sceneDeadline>0 && expectedScene!=null && SameScene(expectedScene,scene)) return;
        if(SceneMatches(scene) && !LevelManager.isLoadingLocked)
        {
            expectedScene=scene;localSceneLoaded=true;sceneReady=false;
            PrepareClientWorld();
            sceneDeadline=Now+240;
            if(localArenaSpawn.HasValue) sceneReady=true;
            SignalSceneReady();
        }
        else Travel(scene);
    }
    private static bool SameScene(SharedScene a,SharedScene b)=>a.Level==b.Level && a.Mode==b.Mode && a.Options.Count==b.Options.Count && a.Options.All(p=>b.Options.TryGetValue(p.Key,out var value) && value==p.Value);
    private void Travel(SharedScene scene)
    {
        CaptureTravelLoadout();
        if(travelLoadout!=null){travelLoadout.Restoring=false;travelLoadout.Pending=0;travelLoadout.RestoreGeneration=-1;}
        sceneReady=false;localSceneLoaded=false;sceneReadySent=false;loadedPeers.Clear();
        Clear();expectedScene=scene;sceneDeadline=Now+240;nextSceneSignal=Now+2;
        SetTravelStatus("Loading "+scene.Level+". Waiting for players to arrive.");
        applyingScene=true;
        try{LevelManager.LoadLevel(scene.Level,scene.Mode,new Dictionary<string,string>(scene.Options));}
        catch(Exception error){FailScene("Map loading failed: "+error.Message);}
        finally{applyingScene=false;}
    }
    private void SignalSceneReady()
    {
        if(Now<DelaySceneReadyUntil || !localSceneLoaded || LevelManager.isLoadingLocked || Player.local?.creature==null || !RestoreTravelLoadout()) return;
        if(!Session.IsHost) PrepareClientWorld();
        if(Session.IsHost)
        {
            sceneReady=true;loadedPeers.Add(Session.LocalId);sceneDeadline=0;stableScene=expectedScene;recoveringScene=false;
            UpdateArrivalStatus();
            if(!sceneReadySent){sceneReadySent=true;foreach(var peer in Session.Peers.Keys.Where(p=>p!=Session.LocalId).ToArray()) SendScene(peer);}
        }
        else
        {
            Session.Publish(MessageKind.SceneReady,Protocol.Encode(w=>w.Write(Session.WorldRevision)));
            sceneReadySent=true;
            if(localArenaSpawn.HasValue){sceneReady=true;sceneDeadline=0;stableScene=expectedScene;recoveringScene=false;}
        }
    }
    private void ReceiveSceneReady(ushort source,BinaryReader reader)
    {
        uint revision=reader.ReadUInt32();Protocol.End(reader);
        if(!Session.IsHost || revision!=Session.WorldRevision) return;
        loadedPeers.Add(source);
        UpdateArrivalStatus();
        if(SceneUsable && Player.local?.creature!=null){SendArenaSpawn(source);SendCatchup(source);}
    }
    private void TickSceneTravel()
    {
        if(!Session.Connected) return;
        if(travelAgreement.Active)
        {
            if(travelAgreement.Failure.Length>0 || travelAgreement.TimedOut(Now))
            {var reason=travelAgreement.Failure.Length>0 ? travelAgreement.Failure : "A player did not confirm the map in time. No maps were unloaded.";travelAgreement.End();NoticeScene(reason);proposedScene=null;}
            else if(travelAgreement.Ready && proposedScene!=null)
            {
                var scene=proposedScene;proposedScene=null;travelAgreement.End();
                Session.SetWorldRevision(Session.WorldRevision+1);
                // Publish the new revision before either machine starts unloading.
                expectedScene=scene;
                Session.Publish(MessageKind.Scene,Protocol.Encode(w=>WriteScene(w,scene)));
                Travel(scene);
            }
        }
        if(sceneDeadline>0)
        {
            if(LoadingCamera.state==LoadingCamera.State.Error || Now>=sceneDeadline)
            {FailScene("Loading "+expectedScene?.Level+" failed or timed out.");return;}
            if(localSceneLoaded && Now>=nextSceneSignal){nextSceneSignal=Now+2;SignalSceneReady();}
        }
        if(Session.IsHost && SceneUsable && Now>=nextSceneSignal)
        {
            nextSceneSignal=Now+2;
            foreach(var peer in Session.Peers.Keys.Where(p=>p!=Session.LocalId).ToArray())
                if(!loadedPeers.Contains(peer)) SendScene(peer);
                else if(!arenaSpawnSlots.ContainsKey(peer)){SendArenaSpawn(peer);SendCatchup(peer);}
        }
        if(!Session.IsHost && sceneReadySent && !sceneReady && Now>=nextSceneSignal)
        {nextSceneSignal=Now+2;SignalSceneReady();}
    }
    private void FailScene(string reason)
    {
        sceneDeadline=0;SetTravelStatus(reason);
        if(Session.IsHost && !recoveringScene && stableScene!=null)
        {
            recoveringScene=true;Session.SetWorldRevision(Session.WorldRevision+1);expectedScene=stableScene;
            Session.Publish(MessageKind.Scene,Protocol.Encode(w=>WriteScene(w,stableScene)));
            // Let a native fallback finish before starting the recovery load.
            MultiplayerRuntime.Instance!.StartCoroutine(RecoverScene(stableScene));
        }
        else MultiplayerRuntime.Instance?.RequestDisconnect(reason+" Your character save was kept local; reload a playable map before reconnecting.");
    }
    private IEnumerator RecoverScene(SharedScene scene)
    {
        yield return null;
        double deadline=Time.realtimeSinceStartupAsDouble+30;
        while(LevelManager.isLoadingLocked && Time.realtimeSinceStartupAsDouble<deadline) yield return null;
        if(disposed) yield break;
        if(LevelManager.isLoadingLocked){MultiplayerRuntime.Instance?.RequestDisconnect("Native map loader is stuck. Restart the map before reconnecting.");yield break;}
        Travel(scene);
    }
    private void OnLevelLoaded(LevelData data, LevelData.Mode mode, EventTime time)
    {
        if(!Session.Connected || time!=EventTime.OnEnd || data==null || data.id=="Master") return;
        if(expectedScene==null || expectedScene.Level!=data.id) return;
        if(!SceneMatches(expectedScene)){FailScene("The loaded map/options do not match the shared destination.");return;}
        localSceneLoaded=true;nextSceneSignal=Now;SignalSceneReady();
    }
}

[HarmonyPatch(typeof(LevelManager),nameof(LevelManager.LoadLevel),new[]{typeof(LevelData),typeof(LevelData.Mode),typeof(Dictionary<string,string>),typeof(LoadingCamera.State)})]
internal static class SharedNativeMapSelectionPatch
{
    private static bool Prefix(LevelData levelData,LevelData.Mode mode,Dictionary<string,string> options)
        =>MultiplayerRuntime.Instance?.World?.InterceptNativeTravel(levelData,mode,options) ?? true;
}

[HarmonyPatch(typeof(LevelManager),nameof(LevelManager.LoadLevelCoroutine),new[]{typeof(LevelData),typeof(LevelData.Mode),typeof(Dictionary<string,string>),typeof(LoadingCamera.State)})]
internal static class SharedNativeMapCoroutinePatch
{
    private static bool Prefix(LevelData levelData,LevelData.Mode mode,Dictionary<string,string> options,ref IEnumerator __result)
    {
        if(MultiplayerRuntime.Instance?.World?.InterceptNativeTravel(levelData,mode,options)!=false) return true;
        __result=Empty();return false;
    }
    private static IEnumerator Empty(){yield break;}
}
