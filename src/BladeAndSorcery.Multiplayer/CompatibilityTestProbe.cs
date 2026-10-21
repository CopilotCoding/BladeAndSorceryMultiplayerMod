using System;
using System.Linq;
using System.IO;
using System.Collections.Generic;
using BladeAndSorcery.Multiplayer.Core;
using Newtonsoft.Json.Linq;
using ThunderRoad;
using UnityEngine;

namespace BladeAndSorcery.Multiplayer;

// Representative mod state used only through the isolated AutomatedDriver.
internal sealed class CompatibilityProbeData : ContentCustomData { public int Value;public Vector3 Marker; }
internal sealed class CompatibilityProbeComponent : MonoBehaviour { public int Value,Events,Applied;public bool EchoBlocked; }
internal sealed class CompatibilityProbeAdapter : IMultiplayerItemAdapter
{
    public string Id=>"basmp.test-state";
    public int Version=>1;
    public bool AppliesTo(Item item)=>item.HasCustomData<CompatibilityProbeData>();
    private static CompatibilityProbeComponent Component(Item item)=>item.GetComponent<CompatibilityProbeComponent>() ?? item.gameObject.AddComponent<CompatibilityProbeComponent>();
    public byte[] CaptureState(Item item)=>BitConverter.GetBytes(Component(item).Value);
    public void ApplyState(Item item,byte[] state)
    {if(state.Length!=4)throw new ArgumentException("Test state size");var c=Component(item);c.Value=BitConverter.ToInt32(state,0);c.Applied++;c.EchoBlocked=!MultiplayerModApi.PublishEvent(item,Id,new byte[]{0});}
    public void ReceiveEvent(Item item,byte[] payload){if(payload.Length!=4)throw new ArgumentException("Test event size");Component(item).Events++;}
}
internal sealed partial class GameWorld
{
    internal void TestForgeAdapterEvent(ReplicatedEntity entity)
    {
        var action=new GameplayAction{EventId=((ulong)Session.LocalId<<32)|0xfffffff0,Entity=entity.Descriptor.Id,Epoch=entity.Descriptor.Epoch,Kind=ActionKind.ModEvent,
            Text="{\"Id\":\"basmp.test-state\",\"Version\":1,\"Data\":\"AQAAAA==\"}"};
        Session.Publish(MessageKind.Gameplay,Protocol.Encode(w=>Protocol.Action(w,action)));
    }
    internal void TestRepeatAdapterEvent(ReplicatedEntity entity)
    {
        if(!entity.Owned)throw new InvalidOperationException("Only owner may repeat fixture event");
        var action=new GameplayAction{EventId=((ulong)Session.LocalId<<32)|nextEvent++,Entity=entity.Descriptor.Id,Epoch=entity.Descriptor.Epoch,Kind=ActionKind.ModEvent,
            Text="{\"Id\":\"basmp.test-state\",\"Version\":1,\"Data\":\"AQAAAA==\"}"};
        var bytes=Protocol.Encode(w=>Protocol.Action(w,action));Session.Publish(MessageKind.Gameplay,bytes);Session.Publish(MessageKind.Gameplay,bytes);
    }
}
public sealed partial class MultiplayerRuntime
{
    internal bool TestMismatchContent;
    internal object CompatibilitySnapshot()=>new{ready=compatibility!=null,status=CompatibilityStatus,notice=connectionNotice,shared=compatibility?.Shared,localOnly=compatibility?.Local,adapters=MultiplayerModApi.Signatures()};
    internal string[] TestCompatibilityFiles()
    {
        string root=Path.Combine(TestDirectory,"compatibility-files-"+Guid.NewGuid().ToString("N"));
        string a=Path.Combine(root,"a"),b=Path.Combine(root,"b");Directory.CreateDirectory(a);Directory.CreateDirectory(b);
        foreach(var folder in new[]{a,b}){File.WriteAllText(Path.Combine(folder,"manifest.json"),"{\"Name\":\"Fixture\"}");File.WriteAllText(Path.Combine(folder,"weapon.json"),"{\"$type\":\"ItemData\",\"id\":\"Fixture\"}");File.WriteAllText(Path.Combine(folder,"code.dll"),"fixture-code");}
        var first=new CompatibilityMod{Root=a,Name="Fixture",Author="test",Version="1",HasCatalog=true};
        var second=new CompatibilityMod{Root=b,Name="Fixture",Author="test",Version="1",HasCatalog=true};
        var checks=new List<string>();
        ContentManifest Scan(CompatibilityMod mod)=>ScanMods(new[]{mod},Array.Empty<string>()).Manifest;
        void Check(bool condition,string name){if(!condition)throw new InvalidOperationException(name);checks.Add(name);}
        Check(Scan(first).Difference(Scan(second))=="","Same content in different absolute folders matches");
        File.WriteAllText(Path.Combine(a,"multiplayer.json"),"{\"Password\":\"fixture-private-A\"}");File.WriteAllText(Path.Combine(b,"multiplayer.json"),"{\"Password\":\"fixture-private-B\"}");
        Check(Scan(first).Difference(Scan(second))=="","Private untyped preferences excluded from content digest");
        File.WriteAllText(Path.Combine(b,"weapon.json"),"{\"$type\":\"ItemData\",\"id\":\"Changed\"}");Check(Scan(first).Difference(Scan(second)).Length>0,"Catalog modifications detected");
        File.Copy(Path.Combine(a,"weapon.json"),Path.Combine(b,"weapon.json"),true);
        File.WriteAllText(Path.Combine(b,"code.dll"),"changed-code");Check(Scan(first).Difference(Scan(second)).Length>0,"Script DLL modifications detected");File.Copy(Path.Combine(a,"code.dll"),Path.Combine(b,"code.dll"),true);
        foreach(var folder in new[]{a,b}){File.WriteAllText(Path.Combine(folder,"basmp-compatibility.json"),"{\"settings\":[\"shared.json\"]}");File.WriteAllText(Path.Combine(folder,"shared.json"),"{\"difficulty\":1}");}
        File.WriteAllText(Path.Combine(b,"shared.json"),"{\"difficulty\":2}");Check(Scan(first).Difference(Scan(second)).Length>0,"Declared gameplay settings included");
        File.WriteAllText(Path.Combine(a,"basmp-compatibility.json"),"{\"localOnly\":true}");first.HasCatalog=false;
        Check(Scan(first).Count==0,"Explicit pure local-only declaration supported");first.HasCatalog=true;Check(Scan(first).Count==1,"Catalog-changing mods cannot bypass admission through local-only declaration");
        File.WriteAllText(Path.Combine(a,"basmp-compatibility.json"),"{\"settings\":[\"../outside.json\"]}");File.WriteAllText(Path.Combine(root,"outside.json"),"{}");
        bool rejected=false;try{Scan(first);}catch(InvalidDataException){rejected=true;}Check(rejected,"Shared settings cannot escape their mod root");
        File.WriteAllText(Path.Combine(a,"basmp-compatibility.json"),"{}");File.WriteAllText(Path.Combine(b,"basmp-compatibility.json"),"{}");
        var other=new CompatibilityMod{Root=b,Name="Other fixture",Author="test",Version="1",HasCatalog=true};
        Check(ScanMods(new[]{first,other},Array.Empty<string>()).Manifest.Difference(ScanMods(new[]{other,first},Array.Empty<string>()).Manifest).Length>0,"Shared load order changes detected");
        var bytes=Protocol.Encode(w=>Scan(first).Write(w));string text=System.Text.Encoding.UTF8.GetString(bytes);
        Check(!text.Contains(root) && !text.Contains("fixture-private"),"Manifest wire contains no absolute paths or private preferences");
        return checks.ToArray();
    }
}
internal static class CompatibilityProbe
{
    internal static object? Execute(MultiplayerRuntime runtime,JObject command,Action<object> respond)
    {
        string op=(string)command["op"]!;
        if(op=="compatibility-status")return runtime.CompatibilitySnapshot();
        if(op=="compatibility-file-checks")return new{checks=runtime.TestCompatibilityFiles()};
        if(op=="compatibility-mismatch"){if(runtime.Session!=null)throw new InvalidOperationException("Disconnect first");runtime.TestMismatchContent=(bool?)command["enabled"] ?? false;return "test manifest set";}
        if(op=="compatibility-register")
        {
            if(runtime.Session!=null)throw new InvalidOperationException("Register before connecting");
            if(!MultiplayerModApi.Adapters.ContainsKey("basmp.test-state"))MultiplayerModApi.Register(new CompatibilityProbeAdapter());
            if(!Catalog.TryGetData<ItemData>("BASMP_Test_ModSword",out _,false))
            {var clone=(ItemData)Catalog.GetData<ItemData>("SwordShortCommon").Clone();clone.id="BASMP_Test_ModSword";if(!Catalog.LoadCatalogData(clone,"","","BASMPTest"))throw new InvalidOperationException("Test catalog registration failed");}
            return runtime.CompatibilitySnapshot();
        }
        if(op=="compatibility-spawn")
        {
            if(runtime.Session?.Connected!=true || runtime.World==null)throw new InvalidOperationException("Connect first");
            Catalog.GetData<ItemData>("BASMP_Test_ModSword").SpawnAsync(item=>
            {
                item.OverrideCustomData(new System.Collections.Generic.List<ContentCustomData>{new CompatibilityProbeData{Value=7,Marker=new Vector3(1,2,3)}});
                item.DisallowDespawn=true; // Durable late-join fixture, not a change to normal loot cleanup.
                item.gameObject.AddComponent<CompatibilityProbeComponent>().Value=11;
                var entity=runtime.World.Register(item);respond(new{entity=entity.Descriptor.Id.ToString()});
            },Player.local.creature.transform.position+Vector3.up*1.5f+Vector3.forward*2,Quaternion.identity,pooled:false);return null;
        }
        if(runtime.World==null || !ulong.TryParse((string?)command["entity"],out var id) || !runtime.World.Entities.TryGetValue(id,out var found) || found.Item==null)throw new InvalidOperationException("Expected a test network item");
        var target=found.Item;var component=target.GetComponent<CompatibilityProbeComponent>();
        if(op=="compatibility-set")
        {
            if(!found.Owned)throw new InvalidOperationException("Only owner may mutate fixture state");
            if(target.TryGetCustomData<CompatibilityProbeData>(out var data))data.Value=(int?)command["custom"] ?? data.Value;
            component!.Value=(int?)command["value"] ?? component.Value;
            return "fixture state changed";
        }
        if(op=="compatibility-event")return new{sent=MultiplayerModApi.PublishEvent(target,"basmp.test-state",BitConverter.GetBytes(1))};
        if(op=="compatibility-repeat"){runtime.World.TestRepeatAdapterEvent(found);return "same event sent twice";}
        if(op=="compatibility-forge"){runtime.World.TestForgeAdapterEvent(found);return "invalid owner event sent";}
        if(op=="compatibility-despawn"){if(!found.Owned)throw new InvalidOperationException("Expected owner");target.Despawn();return "despawned";}
        if(op=="compatibility-local-despawn"){if(found.Owned)throw new InvalidOperationException("Expected replica");target.Despawn();return "local native cleanup attempted";}
        if(op=="compatibility-destroy-replica"){if(found.Owned)throw new InvalidOperationException("Expected replica");UnityEngine.Object.Destroy(target.gameObject);return "test representation destroyed directly";}
        if(op=="compatibility-imbue")
        {
            if(!found.Owned)throw new InvalidOperationException("Expected owner");
            foreach(var imbue in target.imbues){imbue.UnloadCurrentSpell();imbue.Transfer(Catalog.GetData<SpellCastCharge>("Fire"),40);imbue.SetEnergyInstant(40);}
            return new{groups=target.imbues.Count};
        }
        if(op=="compatibility-inspect")return new{owner=found.Descriptor.Owner,catalog=target.data.id,custom=target.TryGetCustomData<CompatibilityProbeData>(out var data) ? data.Value : -1,marker=data!=null ? new[]{data.Marker.x,data.Marker.y,data.Marker.z} : null,
            adapter=component?.Value,events=component?.Events,applied=component?.Applied,echoBlocked=component?.EchoBlocked,imbues=target.imbues.Select(i=>new{spell=i.spellCastBase?.id,energy=i.energy}).ToArray()};
        throw new ArgumentException("Unknown compatibility command");
    }
}
