using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BladeAndSorcery.Multiplayer.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using ThunderRoad;
using UnityEngine;

namespace BladeAndSorcery.Multiplayer;

internal sealed class NativeCustomRecord { public string Type="", Json=""; }
internal sealed class AdapterRecord { public string Id="";public int Version;public byte[] Data=Array.Empty<byte>(); }
internal sealed class NativeImbueRecord { public int Index;public string Spell="";public float Energy; }
internal sealed class NativeItemState
{
    public NativeCustomRecord[] Custom=Array.Empty<NativeCustomRecord>();
    public NativeImbueRecord[] Imbues=Array.Empty<NativeImbueRecord>();
    public AdapterRecord[] Adapters=Array.Empty<AdapterRecord>();
    public string[] Bodies=Array.Empty<string>();
}
internal sealed class PlainContentResolver : DefaultContractResolver
{
    protected override JsonProperty CreateProperty(MemberInfo member,MemberSerialization serialization)
    {
        var property=base.CreateProperty(member,serialization);var type=property.PropertyType;
        if(type!=null && (typeof(UnityEngine.Object).IsAssignableFrom(type) || typeof(Delegate).IsAssignableFrom(type) || typeof(MemberInfo).IsAssignableFrom(type)))property.Ignored=true;
        return property;
    }
}
internal sealed class NativeValueConverter : JsonConverter
{
    public override bool CanConvert(Type t)=>t==typeof(Vector2) || t==typeof(Vector3) || t==typeof(Quaternion) || t==typeof(Color);
    public override void WriteJson(JsonWriter writer,object? value,JsonSerializer serializer)
    {
        float[] a=value is Vector3 v ? new[]{v.x,v.y,v.z} : value is Vector2 u ? new[]{u.x,u.y} : value is Quaternion q ? new[]{q.x,q.y,q.z,q.w} : value is Color c ? new[]{c.r,c.g,c.b,c.a} : Array.Empty<float>();
        if(a.Any(x=>!Protocol.Finite(x)))throw new JsonSerializationException("Nonfinite native value");
        serializer.Serialize(writer,a);
    }
    public override object ReadJson(JsonReader reader,Type type,object? existingValue,JsonSerializer serializer)
    {
        var a=serializer.Deserialize<float[]>(reader) ?? Array.Empty<float>();
        int count=type==typeof(Vector2) ? 2 : type==typeof(Vector3) ? 3 : 4;
        if(a.Length!=count || a.Any(x=>!Protocol.Finite(x)))throw new JsonSerializationException("Invalid native value");
        if(type==typeof(Vector2))return new Vector2(a[0],a[1]);if(type==typeof(Vector3))return new Vector3(a[0],a[1],a[2]);
        if(type==typeof(Quaternion))return new Quaternion(a[0],a[1],a[2],a[3]);return new Color(a[0],a[1],a[2],a[3]);
    }
}
internal static class NativeCustomState
{
    internal static readonly JsonSerializerSettings Settings=new JsonSerializerSettings{TypeNameHandling=TypeNameHandling.None,MaxDepth=12,ContractResolver=new PlainContentResolver(),Converters={new NativeValueConverter()}};
    private static Dictionary<string,Type>? types;
    internal static string TypeId(Type type)=>type.Assembly.GetName().Name+":"+type.FullName;
    private static Dictionary<string,Type> Types()
    {
        if(types!=null)return types;
        types=new Dictionary<string,Type>(StringComparer.Ordinal);
        foreach(var assembly in ModManager.loadedMods.SelectMany(m=>m.assemblies).Concat(new[]{typeof(ContentCustomData).Assembly,typeof(NativeCustomState).Assembly}).Distinct())
        {
            Type[] found;try{found=assembly.GetTypes();}catch(ReflectionTypeLoadException error){found=error.Types.Where(t=>t!=null).Cast<Type>().ToArray();}
            foreach(var type in found)if(!type.IsAbstract && !type.ContainsGenericParameters && typeof(ContentCustomData).IsAssignableFrom(type))types[TypeId(type)]=type;
        }
        return types;
    }
    internal static NativeCustomRecord[] Capture(IEnumerable<ContentCustomData>? data,Action<string,Exception>? warning=null)
    {
        var result=new List<NativeCustomRecord>();int count=0;
        foreach(var value in data ?? Enumerable.Empty<ContentCustomData>())
        {
            if(value==null)continue;string type=TypeId(value.GetType());
            try
            {
                if(++count>32)throw new InvalidDataException("Custom data is limited to 32 records");
                string json=JsonConvert.SerializeObject(value,Settings);
                if(type.Length>512 || Encoding.UTF8.GetByteCount(json)>12000)throw new InvalidDataException("Native custom record exceeds its size limit");
                result.Add(new NativeCustomRecord{Type=type,Json=json});
            }
            catch(Exception error){warning?.Invoke(type,error);}
        }
        return result.ToArray();
    }
    internal static List<ContentCustomData> Read(NativeCustomRecord[] records)
    {
        var result=new List<ContentCustomData>();
        foreach(var record in records)
        {
            if(!Types().TryGetValue(record.Type,out var type))throw new InvalidDataException("Unsupported installed custom-data type: "+record.Type);
            if(JsonConvert.DeserializeObject(record.Json,type,Settings) is ContentCustomData value)result.Add(value);
        }
        return result;
    }
}
internal sealed partial class GameWorld
{
    private double nextCompatibilityItems;
    private readonly HashSet<string> compatibilityWarnings=new HashSet<string>();
    internal void CompatibilityWarning(string key,Exception error)
    {if(compatibilityWarnings.Add(key))Log?.Invoke("Compatibility: "+key+": "+error.Message);}
    private string CaptureItemState(ReplicatedEntity entity)
    {
        var item=entity.Item;if(item==null)return "";
        var records=new List<AdapterRecord>();
        foreach(var adapter in MultiplayerModApi.Adapters.Values)
        {
            try
            {
                if(!adapter.AppliesTo(item))continue;
                var bytes=adapter.CaptureState(item) ?? Array.Empty<byte>();if(bytes.Length>4096)throw new InvalidDataException("Adapter state exceeds 4096 bytes");
                records.Add(new AdapterRecord{Id=adapter.Id,Version=adapter.Version,Data=bytes});
            }
            catch(Exception error){CompatibilityWarning(adapter.Id,error);}
        }
        try
        {
            var state=new NativeItemState{Custom=NativeCustomState.Capture(item.contentCustomData,CompatibilityWarning),Adapters=records.OrderBy(a=>a.Id,StringComparer.Ordinal).ToArray(),Bodies=entity.AttachedBodyPaths(),
                Imbues=item.imbues.Select((imbue,index)=>new NativeImbueRecord{Index=index,Spell=imbue.spellCastBase?.id ?? "",Energy=imbue.spellCastBase==null ? 0 : Mathf.Round(imbue.energy)}).ToArray()};
            string json=JsonConvert.SerializeObject(state,NativeCustomState.Settings);ReadItemState(json);return json;
        }
        catch(Exception error){CompatibilityWarning(item.data.id,error);return entity.Descriptor.ItemState;}
    }
    private static NativeItemState ReadItemState(string json)
    {
        if(Encoding.UTF8.GetByteCount(json)>24000)throw new InvalidDataException("Item custom state exceeds 24000 bytes");
        var state=json.Length==0 ? new NativeItemState() : JsonConvert.DeserializeObject<NativeItemState>(json,NativeCustomState.Settings) ?? throw new InvalidDataException("Missing item state");
        if(state.Custom==null || state.Imbues==null || state.Adapters==null || state.Bodies==null || state.Custom.Length>32 || state.Imbues.Length>64 || state.Adapters.Length>16 || state.Bodies.Length>Protocol.MaxParts || state.Bodies.Any(p=>p==null || p.Length>1024) || state.Bodies.Distinct().Count()!=state.Bodies.Length)throw new InvalidDataException("Too much custom item state");
        if(state.Custom.Any(c=>c==null || c.Type==null || c.Json==null || c.Type.Length>512 || Encoding.UTF8.GetByteCount(c.Json)>12000) ||
            state.Imbues.Any(i=>i==null || i.Spell==null || i.Spell.Length>128 || i.Index<0 || i.Index>=64 || !Protocol.Finite(i.Energy) || i.Energy<0 || i.Energy>1000000) ||
            state.Adapters.Any(a=>a==null || a.Id==null || a.Data==null || a.Data.Length>4096 || !MultiplayerModApi.Adapters.TryGetValue(a.Id,out var adapter) || adapter.Version!=a.Version) ||
            state.Imbues.Select(i=>i.Index).Distinct().Count()!=state.Imbues.Length || state.Adapters.Select(a=>a.Id).Distinct().Count()!=state.Adapters.Length)
            throw new InvalidDataException("Invalid custom item state");
        return state;
    }
    private bool ValidateItemState(EntityDescriptor descriptor)
    {if(descriptor.Kind!=EntityKind.Item)return descriptor.ItemState.Length==0;ReadItemState(descriptor.ItemState);return true;}
    private List<ContentCustomData> ReadCompatibleCustomData(NativeCustomRecord[] records)
    {
        var result=new List<ContentCustomData>();
        foreach(var record in records)
            try{result.AddRange(NativeCustomState.Read(new[]{record}));}
            catch(Exception error){CompatibilityWarning(record?.Type ?? "missing custom type",error);}
        return result;
    }
    private List<ContentCustomData> SpawnCustomData(EntityDescriptor descriptor)=>ReadCompatibleCustomData(ReadItemState(descriptor.ItemState).Custom);
    internal void ApplyItemState(ReplicatedEntity entity,bool customAlreadyLoaded=false)
    {
        var item=entity.Item;if(entity.Owned || item==null || entity.Descriptor.ItemState.Length==0)return;
        applyDepth++;
        try
        {
            var state=ReadItemState(entity.Descriptor.ItemState);
            entity.SourceBodyPaths=state.Bodies;
            string custom=JsonConvert.SerializeObject(state.Custom,NativeCustomState.Settings);
            if(!customAlreadyLoaded && entity.LastCustomData!=custom)item.OverrideCustomData(ReadCompatibleCustomData(state.Custom));
            entity.LastCustomData=custom;
            foreach(var record in state.Imbues)
            {
                var imbue=item.imbues.ElementAtOrDefault(record.Index);if(imbue==null)continue;
                if(record.Spell.Length==0 || record.Energy<=0){if(imbue.spellCastBase!=null)imbue.UnloadCurrentSpell();continue;}
                if(imbue.spellCastBase?.id!=record.Spell)
                {
                    if(!Catalog.TryGetData<SpellCastCharge>(record.Spell,out var spell,false))continue;
                    imbue.UnloadCurrentSpell();imbue.Transfer(spell,Mathf.Min(record.Energy,imbue.maxEnergy));
                }
                imbue.SetEnergyInstant(Mathf.Min(record.Energy,imbue.maxEnergy));
            }
            foreach(var record in state.Adapters)
                if(MultiplayerModApi.Adapters.TryGetValue(record.Id,out var adapter))
                    try{if(adapter.AppliesTo(item))adapter.ApplyState(item,(byte[])record.Data.Clone());}catch(Exception error){CompatibilityWarning(adapter.Id,error);}
        }
        catch(Exception error){CompatibilityWarning(item.data.id,error);}
        finally{applyDepth--;}
    }
    private void TickCompatibleItems()
    {
        if(Now<nextCompatibilityItems)return;nextCompatibilityItems=Now+.5;
        foreach(var entity in Entities.Values.ToArray())
            if(entity.Owned && entity.Item!=null)
            {var state=CaptureItemState(entity);if(state!=entity.Descriptor.ItemState){entity.Descriptor.ItemState=state;PublishSpawn(entity.Descriptor);}}
    }
    internal bool PublishModEvent(Item item,string id,byte[] payload)
    {
        var entity=Find(item);
        if(Applying || !SceneUsable || entity?.Owned!=true || !MultiplayerModApi.Adapters.TryGetValue(id,out var adapter) || !adapter.AppliesTo(item))return false;
        SendAction(entity,new GameplayAction{Kind=ActionKind.ModEvent,Text=JsonConvert.SerializeObject(new AdapterRecord{Id=id,Version=adapter.Version,Data=payload},NativeCustomState.Settings)});return true;
    }
    private bool ValidateModEvent(GameplayAction action)
    {
        var record=JsonConvert.DeserializeObject<AdapterRecord>(action.Text,NativeCustomState.Settings);
        return action.Other==0 && Registry.TryGet(action.Entity,out var entity) && entity.Kind==EntityKind.Item && record?.Id!=null && record.Data!=null && record.Data.Length<=2048 &&
            MultiplayerModApi.Adapters.TryGetValue(record.Id,out var adapter) && adapter.Version==record.Version;
    }
    private void ApplyModEvent(ReplicatedEntity entity,GameplayAction action)
    {
        if(entity.Item==null || !ValidateModEvent(action))return;
        var record=JsonConvert.DeserializeObject<AdapterRecord>(action.Text,NativeCustomState.Settings)!;
        var adapter=MultiplayerModApi.Adapters[record.Id];
        try{if(adapter.AppliesTo(entity.Item))adapter.ReceiveEvent(entity.Item,(byte[])record.Data.Clone());}catch(Exception error){CompatibilityWarning(adapter.Id,error);}
    }
}
