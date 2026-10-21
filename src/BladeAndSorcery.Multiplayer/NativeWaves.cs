using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BladeAndSorcery.Multiplayer.Core;
using HarmonyLib;
using ThunderRoad;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BladeAndSorcery.Multiplayer;

internal enum NativeWaveCommand : byte { Select, Start, Stop }
internal sealed class NativeWaveRecord
{
    internal string Key="",Selected="",Wave="";
    internal uint Run,Cleanup;
    internal bool Running;
    internal byte Outcome;
    internal int Round,Alive,Queued,Kills;
    internal float Delay;
    internal WaveSpawner? Spawner;
    internal readonly HashSet<ulong> Killed=new HashSet<ulong>();
}

internal sealed partial class GameWorld
{
    private readonly Dictionary<string,NativeWaveRecord> nativeWaves=new Dictionary<string,NativeWaveRecord>();
    private readonly HashSet<ulong> nativeWaveCommands=new HashSet<ulong>();
    private readonly Dictionary<UIWaveSpawner,uint> dismissedWaveBooks=new Dictionary<UIWaveSpawner,uint>();
    private readonly Dictionary<string,uint> receivedWaveCleanups=new Dictionary<string,uint>();
    private uint nativeWaveRequest, nextNativeRun;
    private double nextNativeWaveSync;
    private double nextNativeWaveUi;
    internal bool ApplyingNativeWaves {get;private set;}
    internal bool ApplyingNativeWaveCleanup {get;private set;}
    private MultiplayerMode? beforeNativeWaveMode;
    private static readonly FieldInfo BookWaveData=AccessTools.Field(typeof(UIWaveSpawner),"waveData");
    private static readonly FieldInfo BookStartDelay=AccessTools.Field(typeof(UIWaveSpawner),"startDelay");
    private NativeWaveRecord NativeWave(WaveSpawner spawner)
    {
        string key=GameMath.ScenePath(spawner.transform);
        if(!nativeWaves.TryGetValue(key,out var state)) nativeWaves[key]=state=new NativeWaveRecord{Key=key,Spawner=spawner};
        state.Spawner=spawner;return state;
    }
    internal object NativeWaveSnapshot()=>nativeWaves.Values.Select(s=>new{key=s.Key,selected=s.Selected,wave=s.Wave,run=s.Run,running=s.Running,outcome=s.Outcome,round=s.Round,alive=s.Alive,queued=s.Queued,kills=s.Kills,delay=s.Delay,cleanup=s.Cleanup}).ToArray();
    internal void SelectNativeWave(UIWaveSpawner book,WaveData? data)
    {
        if(ApplyingNativeWaves || !SceneUsable || book.waveSpawner==null) return;
        RequestNativeWave(book.waveSpawner,NativeWaveCommand.Select,data?.id ?? "",0);
    }
    internal bool UseNativeWaveBook(UIWaveSpawner book,bool start)
    {
        if(ApplyingNativeWaves || disposed || !Session.Connected) return true;
        var spawner=book.waveSpawner;
        if(spawner==null || !SceneUsable) return false;
        var state=NativeWave(spawner);
        if(!start && !state.Running)
        {
            dismissedWaveBooks[book]=state.Run;
            ApplyingNativeWaves=true;
            try {book.OnStopClick();}finally{ApplyingNativeWaves=false;}
            return false;
        }
        var data=BookWaveData.GetValue(book) as WaveData;
        RequestNativeWave(spawner,start ? NativeWaveCommand.Start : NativeWaveCommand.Stop,data?.id ?? state.Selected,start ? (float)BookStartDelay.GetValue(book) : 0);
        return false;
    }
    private void RequestNativeWave(WaveSpawner spawner,NativeWaveCommand command,string wave,float delay)
    {
        ulong token=((ulong)Session.LocalId<<32)|++nativeWaveRequest;
        var payload=Protocol.Encode(w=>{w.Write(token);w.Write((byte)command);Protocol.String(w,GameMath.ScenePath(spawner.transform),2048);Protocol.String(w,wave);w.Write(delay);});
        if(Session.IsHost){using var reader=Protocol.Read(payload);ReceiveNativeWaveRequest(Session.LocalId,reader);}
        else Session.Publish(MessageKind.NativeWaveRequest,payload);
    }
    private bool ValidateNativeWaveRequest(ushort source,BinaryReader reader)
    {
        ulong token=reader.ReadUInt64();var command=(NativeWaveCommand)reader.ReadByte();Protocol.String(reader,2048);Protocol.String(reader);float delay=reader.ReadSingle();Protocol.End(reader);
        return (ushort)(token>>32)==source && command<=NativeWaveCommand.Stop && Protocol.Finite(delay) && delay>=0 && delay<=60;
    }
    private void ReceiveNativeWaveRequest(ushort source,BinaryReader reader)
    {
        ulong token=reader.ReadUInt64();var command=(NativeWaveCommand)reader.ReadByte();string key=Protocol.String(reader,2048),wave=Protocol.String(reader);float delay=reader.ReadSingle();Protocol.End(reader);
        if(!Session.IsHost || !SceneUsable || nativeWaveCommands.Contains(token)) return;
        if(nativeWaveCommands.Count>2048) nativeWaveCommands.Clear();nativeWaveCommands.Add(token);
        var spawner=WaveSpawner.instances.FirstOrDefault(s=>s!=null && GameMath.ScenePath(s.transform)==key);
        if(spawner==null){NoticeScene("This wave book is not available on the shared map.");return;}
        var state=NativeWave(spawner);
        if(command==NativeWaveCommand.Stop){spawner.StopWave(false);nextNativeWaveSync=0;return;}
        if(state.Running || spawner.isRunning) return;
        if(wave.Length==0 && command==NativeWaveCommand.Select){state.Selected="";nextNativeWaveSync=0;return;}
        if(!Catalog.TryGetData<WaveData>(wave,out var data,false)) return;
        bool allowed=UnityEngine.Object.FindObjectsOfType<UIWaveSpawner>(true).Where(b=>b.waveSpawner==spawner).Any(b=>data.alwaysAvailable || data.waveSelectors?.Any(id=>string.Equals(id,b.id,StringComparison.OrdinalIgnoreCase))==true);
        if(!allowed){NoticeScene("That wave is not offered by this map's native book.");return;}
        state.Selected=data.id;
        if(command==NativeWaveCommand.Start) spawner.StartWave(data,delay,true);
        nextNativeWaveSync=0;
    }
    internal bool BeginNativeWave(WaveSpawner spawner,WaveData data)
    {
        if(disposed || !Session.Connected) return true;
        // Client level scripts must not start their own enemy simulation.
        // Explicit client book input is routed separately above.
        if(!Session.IsHost) return false;
        if(data==null || spawner.isRunning) return true;
        var state=NativeWave(spawner);state.Run=++nextNativeRun;state.Selected=data.id;state.Wave=data.id;state.Outcome=0;state.Kills=0;state.Killed.Clear();
        if(!beforeNativeWaveMode.HasValue){beforeNativeWaveMode=Match.Mode;if(Match.Mode!=MultiplayerMode.Coop)Match.SetMode(MultiplayerMode.Coop);}
        nextNativeWaveSync=0;return true;
    }
    internal void EndNativeWave(WaveSpawner spawner,bool success)
    {
        if(!Session.IsHost || disposed || !SceneUsable) return;
        var state=NativeWave(spawner);state.Outcome=success ? (byte)1 : (byte)2;state.Running=false;nextNativeWaveSync=0;
    }
    internal void NativeWaveCleaned(WaveSpawner spawner)
    {
        if(Session.IsHost && !disposed && SceneUsable){NativeWave(spawner).Cleanup++;nextNativeWaveSync=0;}
    }
    internal Action<Creature> GuardNativeWaveSpawn(WaveSpawner spawner,Action<Creature> callback)
    {
        var state=NativeWave(spawner);uint run=state.Run;int generation=Generation;
        return creature=>
        {
            if(!IsCurrent(generation) || spawner==null || !spawner.isRunning || state.Run!=run)
            {if(creature!=null)creature.Despawn();return;}
            callback(creature);
        };
    }
    private void OnNativeWaveKill(Creature creature,Player player,CollisionInstance hit,EventTime time)
    {
        if(!Session.IsHost || time!=EventTime.OnStart || Find(creature)?.Descriptor.Kind==EntityKind.Player) return;
        foreach(var state in nativeWaves.Values)
            if(state.Spawner!=null && state.Spawner.spawnedCreatures.Contains(creature))
            {
                // Native pooling reuses a Creature instance for different lives.
                // Network identity, rather than Unity object identity, deduplicates kills.
                var entity=Find(creature) ?? Register(creature,EntityKind.Creature);
                if(state.Killed.Add(entity.Descriptor.Id)){state.Kills++;nextNativeWaveSync=0;}
            }
    }
    internal bool NativeWaveMember(Creature creature)=>nativeWaves.Values.Any(s=>s.Spawner!=null && s.Spawner.spawnedCreatures.Contains(creature));
    private static void WriteNativeWave(BinaryWriter w,NativeWaveRecord s)
    {
        Protocol.String(w,s.Key,2048);Protocol.String(w,s.Selected);Protocol.String(w,s.Wave);w.Write(s.Run);w.Write(s.Cleanup);w.Write(s.Running);w.Write(s.Outcome);w.Write(s.Round);w.Write(s.Alive);w.Write(s.Queued);w.Write(s.Kills);w.Write(s.Delay);
    }
    private void ReceiveNativeWaveState(ushort source,BinaryReader reader)
    {
        int count=reader.ReadByte();if(count>32)throw new InvalidDataException("Too many native wave books");
        for(int i=0;i<count;i++)
        {
            var s=new NativeWaveRecord{Key=Protocol.String(reader,2048),Selected=Protocol.String(reader),Wave=Protocol.String(reader),Run=reader.ReadUInt32(),Cleanup=reader.ReadUInt32(),Running=reader.ReadBoolean(),Outcome=reader.ReadByte(),Round=reader.ReadInt32(),Alive=reader.ReadInt32(),Queued=reader.ReadInt32(),Kills=reader.ReadInt32(),Delay=reader.ReadSingle()};
            if(s.Outcome>3 || s.Round<0 || s.Alive<0 || s.Alive>512 || s.Queued<0 || s.Queued>100000 || s.Kills<0 || !Protocol.Finite(s.Delay) || s.Delay<0 || s.Delay>60)throw new InvalidDataException("Invalid native wave progress");
            if(source==1 && !Session.IsHost) nativeWaves[s.Key]=s;
        }
        Protocol.End(reader);
    }
    private void TickNativeWaves()
    {
        if(!SceneUsable) return;
        if(Session.IsHost)
        {
            foreach(var spawner in WaveSpawner.instances.Where(s=>s!=null).ToArray())
            {
                var state=NativeWave(spawner);
                if(spawner.isRunning && state.Run==0){state.Run=++nextNativeRun;state.Selected=spawner.waveData?.id ?? "";state.Wave=state.Selected;}
                state.Running=spawner.isRunning;state.Round=spawner.waveCount;state.Delay=Mathf.Max(0,spawner.delayedStartTimeLeft);
                state.Alive=spawner.spawnedCreatures.Count(c=>c!=null && !c.isKilled);state.Queued=spawner.creatureQueue.Count;
            }
            if(beforeNativeWaveMode.HasValue && nativeWaves.Values.All(s=>!s.Running))
            {var previous=beforeNativeWaveMode.Value;beforeNativeWaveMode=null;if(Match.Mode==MultiplayerMode.Coop && previous!=MultiplayerMode.Coop)Match.SetMode(previous);}
            if(Now>=nextNativeWaveSync)
            {
                nextNativeWaveSync=Now+.5;
                Session.Publish(MessageKind.NativeWaveState,Protocol.Encode(w=>{w.Write((byte)nativeWaves.Count);foreach(var state in nativeWaves.Values)WriteNativeWave(w,state);}));
            }
        }
        else foreach(var state in nativeWaves.Values)
        {
            var spawner=WaveSpawner.instances.FirstOrDefault(s=>s!=null && GameMath.ScenePath(s.transform)==state.Key);if(spawner==null)continue;state.Spawner=spawner;
            if(receivedWaveCleanups.TryGetValue(state.Key,out uint cleanup) && cleanup!=state.Cleanup)
            {
                ApplyingNativeWaveCleanup=true;
                try{foreach(var entity in Entities.Values)entity.RefreshItemLifetime();spawner.Clean();}finally{ApplyingNativeWaveCleanup=false;}
            }
            receivedWaveCleanups[state.Key]=state.Cleanup;
            spawner.isRunning=state.Running;spawner.waveCount=state.Round;spawner.delayedStartTimeLeft=state.Delay;
            spawner.waveData=Catalog.TryGetData<WaveData>(state.Wave,out var data,false) ? data : null;
        }
        if(Now<nextNativeWaveUi)return;
        nextNativeWaveUi=Now+.25;
        ApplyingNativeWaves=true;
        try
        {
            foreach(var book in UnityEngine.Object.FindObjectsOfType<UIWaveSpawner>(true))
            {
                if(book.waveSpawner==null || !nativeWaves.TryGetValue(GameMath.ScenePath(book.waveSpawner.transform),out var state))continue;
                RenderNativeWaveBook(book,state);
            }
        }
        finally{ApplyingNativeWaves=false;}
    }
    private static T BookField<T>(UIWaveSpawner book,string name)=> (T)AccessTools.Field(typeof(UIWaveSpawner),name).GetValue(book);
    private void RenderNativeWaveBook(UIWaveSpawner book,NativeWaveRecord state)
    {
        var data=Catalog.TryGetData<WaveData>(state.Selected,out var selected,false) ? selected : null;
        BookWaveData.SetValue(book,data);
        BookField<UIText>(book,"selectWaveText").gameObject.SetActive(data==null);
        BookField<UIText>(book,"waveTitle").gameObject.SetActive(data!=null);BookField<UIText>(book,"waveDescription").gameObject.SetActive(data!=null);
        BookField<TextMeshProUGUI>(book,"npcMaxAliveCount").gameObject.SetActive(data!=null);BookField<TextMeshProUGUI>(book,"npcTotalCount").gameObject.SetActive(data!=null);
        if(data!=null)
        {
            AccessTools.Method(typeof(UIWaveSpawner),"SetLocalizedFields").Invoke(book,null);
            BookField<TextMeshProUGUI>(book,"npcMaxAliveCount").text=data.GetMaxAlive().ToString();
            BookField<TextMeshProUGUI>(book,"npcTotalCount").text=data.loopBehavior==WaveData.LoopBehavior.NoLoop ? data.minTotalCount+"-"+data.maxTotalCount : "∞";
        }
        foreach(var row in book.GetComponentsInChildren<UiWaveSpawnerWaveElement>(true))
        {
            var wave=AccessTools.Field(typeof(UiWaveSpawnerWaveElement),"waveData").GetValue(row) as WaveData;
            var toggle=row.GetComponent<Toggle>();if(toggle!=null)toggle.SetIsOnWithoutNotify(wave!=null && wave.id==state.Selected);
        }
        bool showProgress=state.Running || state.Outcome!=0 && (!dismissedWaveBooks.TryGetValue(book,out uint dismissed) || dismissed!=state.Run);
        BookField<UIScrollController>(book,"wavesScroll").gameObject.SetActive(!showProgress);BookField<GameObject>(book,"fightProgress").SetActive(showProgress);
        BookField<UIText>(book,"leftPageTitle").SetLocalizationIds("Default",showProgress ? "{Fight}" : "{Waves}");
        BookField<UICustomisableButton>(book,"startButton").IsInteractable=data!=null && !state.Running;
        var stop=BookField<UICustomisableButton>(book,"stopButton");stop.labels[0].GetComponent<UIText>().SetLocalizationIds("Default",state.Running ? "{Stop}" : "{Return}");
        BookField<UIText>(book,"fightState").SetLocalizedText(state.Running ? (state.Delay>0 ? "Starting in "+Mathf.CeilToInt(state.Delay)+"..." : "Wave "+state.Round+" | "+state.Alive+" alive | "+state.Queued+" waiting | "+state.Kills+" defeated") : state.Outcome==1 ? "Wave complete — "+state.Kills+" defeated" : "Wave stopped");
    }
    private void ClearNativeWaves()
    {
        if(Session.IsHost && beforeNativeWaveMode.HasValue && Match.Mode==MultiplayerMode.Coop && beforeNativeWaveMode!=MultiplayerMode.Coop)Match.SetMode(beforeNativeWaveMode.Value);
        if(!Session.IsHost)
            foreach(var state in nativeWaves.Values)
                if(state.Spawner!=null){state.Spawner.isRunning=false;state.Spawner.waveData=null;state.Spawner.spawnedCreatures.Clear();state.Spawner.creatureQueue.Clear();}
        nativeWaves.Clear();nativeWaveCommands.Clear();dismissedWaveBooks.Clear();receivedWaveCleanups.Clear();beforeNativeWaveMode=null;nextNativeWaveSync=0;nextNativeWaveUi=0;
    }
}

[HarmonyPatch(typeof(UIWaveSpawner),nameof(UIWaveSpawner.OnWaveSelectedChanged))]
internal static class SharedNativeWaveSelectionPatch
{
    private static void Postfix(UIWaveSpawner __instance,Toggle toggle,WaveData waveData)
        =>MultiplayerRuntime.Instance?.World?.SelectNativeWave(__instance,toggle.isOn ? waveData : null);
}
[HarmonyPatch(typeof(UIWaveSpawner),nameof(UIWaveSpawner.OnStartClick))]
internal static class SharedNativeWaveStartButtonPatch
{
    private static bool Prefix(UIWaveSpawner __instance)=>MultiplayerRuntime.Instance?.World?.UseNativeWaveBook(__instance,true) ?? true;
}
[HarmonyPatch(typeof(UIWaveSpawner),nameof(UIWaveSpawner.OnStopClick))]
internal static class SharedNativeWaveStopButtonPatch
{
    private static bool Prefix(UIWaveSpawner __instance)=>MultiplayerRuntime.Instance?.World?.UseNativeWaveBook(__instance,false) ?? true;
}
[HarmonyPatch(typeof(WaveSpawner),nameof(WaveSpawner.StartWave),new[]{typeof(WaveData),typeof(float),typeof(bool)})]
internal static class SharedNativeWaveStartPatch
{
    private static bool Prefix(WaveSpawner __instance,WaveData waveData)=>MultiplayerRuntime.Instance?.World?.BeginNativeWave(__instance,waveData) ?? true;
}
[HarmonyPatch(typeof(WaveSpawner),nameof(WaveSpawner.StopWave))]
internal static class SharedNativeWaveStopPatch
{
    private static bool Prefix(WaveSpawner __instance,out bool __state)
    {__state=__instance.isRunning;return MultiplayerRuntime.Instance?.World is not { } world || world.Session.IsHost;}
    private static void Postfix(WaveSpawner __instance,bool success,bool __state)
    {if(__state && !__instance.isRunning)MultiplayerRuntime.Instance?.World?.EndNativeWave(__instance,success);}
}
[HarmonyPatch(typeof(WaveSpawner),nameof(WaveSpawner.Clean))]
internal static class SharedNativeWaveCleanupPatch
{
    private static bool Prefix()=>MultiplayerRuntime.Instance?.World is not { } world || world.Session.IsHost || world.ApplyingNativeWaveCleanup;
    private static void Postfix(WaveSpawner __instance)=>MultiplayerRuntime.Instance?.World?.NativeWaveCleaned(__instance);
}
[HarmonyPatch(typeof(CreatureData),nameof(CreatureData.SpawnCoroutine))]
internal static class SharedNativeWaveSpawnCallbackPatch
{
    private static void Prefix(ref Action<Creature> callback)
    {
        var world=MultiplayerRuntime.Instance?.World;
        if(world==null || !world.Session.IsHost || callback?.Target==null) return;
        var owner=callback.Target.GetType().GetFields(BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public).FirstOrDefault(f=>f.FieldType==typeof(WaveSpawner))?.GetValue(callback.Target) as WaveSpawner;
        if(owner!=null)callback=world.GuardNativeWaveSpawn(owner,callback);
    }
}
