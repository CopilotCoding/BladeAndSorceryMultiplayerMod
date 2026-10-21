using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ThunderRoad;

namespace BladeAndSorcery.Multiplayer;

/// <summary>Optional owner-driven item integration. All callbacks run on Unity's main thread.</summary>
public interface IMultiplayerItemAdapter
{
    string Id { get; }
    int Version { get; }
    bool AppliesTo(Item item);
    byte[] CaptureState(Item item);
    void ApplyState(Item item, byte[] state);
    void ReceiveEvent(Item item, byte[] payload);
}

public static class MultiplayerModApi
{
    internal static readonly Dictionary<string,IMultiplayerItemAdapter> Adapters=new Dictionary<string,IMultiplayerItemAdapter>(StringComparer.Ordinal);
    public static bool Connected => MultiplayerRuntime.Instance?.Session?.Connected==true;
    public static bool IsHost => MultiplayerRuntime.Instance?.Session?.IsHost==true;
    public static bool ApplyingRemoteState => MultiplayerRuntime.Instance?.World?.Applying==true;
    public static bool IsLocallyOwned(Item item)=>MultiplayerRuntime.Instance?.World?.Find(item)?.Owned==true;
    public static void Register(IMultiplayerItemAdapter adapter)
    {
        if(adapter==null)throw new ArgumentNullException(nameof(adapter));
        if(MultiplayerRuntime.Instance?.Session!=null)throw new InvalidOperationException("Register adapters before connecting");
        string id=adapter.Id ?? "";
        if(!Regex.IsMatch(id,"^[a-z0-9][a-z0-9._-]{0,63}$") || adapter.Version<1 || Adapters.Count>=16)throw new ArgumentException("Invalid adapter ID/version or too many adapters");
        Adapters.Add(id,adapter);MultiplayerRuntime.Instance?.BeginCompatibilityScan();
    }
    public static void Unregister(IMultiplayerItemAdapter adapter)
    {
        if(MultiplayerRuntime.Instance?.Session!=null)throw new InvalidOperationException("Disconnect before unregistering adapters");
        if(Adapters.TryGetValue(adapter.Id,out var current) && ReferenceEquals(current,adapter)){Adapters.Remove(adapter.Id);MultiplayerRuntime.Instance?.BeginCompatibilityScan();}
    }
    /// <summary>Publish after applying the action locally. Only the current item owner may send; maximum 2048 bytes.</summary>
    public static bool PublishEvent(Item item,string adapterId,byte[] payload)
    {
        if(payload==null || payload.Length>2048)throw new ArgumentException("Adapter events are limited to 2048 bytes");
        return MultiplayerRuntime.Instance?.World?.PublishModEvent(item,adapterId,payload)==true;
    }
    internal static string[] Signatures()=>Adapters.Values.OrderBy(a=>a.Id,StringComparer.Ordinal).Select(a=>a.Id+"="+a.Version).ToArray();
}
