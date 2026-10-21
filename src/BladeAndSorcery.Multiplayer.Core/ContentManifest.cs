using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BladeAndSorcery.Multiplayer.Core;

public sealed class ContentEntry
{
    public string Id = "", Name = "", Hash = "";
}

/// <summary>Only public mod identities and digests cross the wire, never paths or file contents.</summary>
public sealed class ContentManifest
{
    public const int MaxEntries = 128;
    private readonly ContentEntry[] entries;
    public int Count => entries.Length;
    public ContentManifest(IEnumerable<ContentEntry>? source = null)
    {
        entries = (source ?? Array.Empty<ContentEntry>()).Select(e => new ContentEntry { Id=e.Id, Name=e.Name, Hash=e.Hash }).OrderBy(e=>e.Id,StringComparer.Ordinal).ToArray();
        if (entries.Length > MaxEntries || entries.Select(e=>e.Id).Distinct(StringComparer.Ordinal).Count()!=entries.Length)
            throw new InvalidDataException("Too many or duplicate compatibility entries");
        foreach(var e in entries)
            if(string.IsNullOrWhiteSpace(e.Id) || System.Text.Encoding.UTF8.GetByteCount(e.Id)>128 || System.Text.Encoding.UTF8.GetByteCount(e.Name)>128 ||
                e.Hash.Length!=64 || e.Hash.Any(c=>!Uri.IsHexDigit(c))) throw new InvalidDataException("Invalid compatibility entry");
    }
    public void Write(BinaryWriter writer)
    {
        writer.Write((byte)entries.Length);
        foreach(var e in entries){Protocol.String(writer,e.Id);Protocol.String(writer,e.Name);Protocol.String(writer,e.Hash,64);}
    }
    public static ContentManifest Read(BinaryReader reader)
    {
        int count=reader.ReadByte();if(count>MaxEntries)throw new InvalidDataException("Too many compatibility entries");
        var entries=new ContentEntry[count];
        for(int i=0;i<count;i++)entries[i]=new ContentEntry{Id=Protocol.String(reader),Name=Protocol.String(reader),Hash=Protocol.String(reader,64)};
        return new ContentManifest(entries);
    }
    public string Difference(ContentManifest client)
    {
        var remote=client.entries.ToDictionary(e=>e.Id,StringComparer.Ordinal);var differences=new List<string>();
        foreach(var e in entries)
            if(!remote.TryGetValue(e.Id,out var other))differences.Add("Missing: "+e.Name);
            else if(!string.Equals(e.Hash,other.Hash,StringComparison.OrdinalIgnoreCase))differences.Add("Different files/settings/order: "+e.Name);
        var local=new HashSet<string>(entries.Select(e=>e.Id),StringComparer.Ordinal);
        foreach(var e in client.entries)if(!local.Contains(e.Id))differences.Add("Client-only: "+e.Name);
        return differences.Count==0 ? "" : "Mod mismatch. "+string.Join("; ",differences.Take(3))+ (differences.Count>3 ? "; and "+(differences.Count-3)+" more" : "")+". Match gameplay mods on both PCs.";
    }
}
