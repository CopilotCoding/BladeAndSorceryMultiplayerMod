using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using BladeAndSorcery.Multiplayer.Core;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ThunderRoad;

namespace BladeAndSorcery.Multiplayer;

internal sealed class CompatibilityMod
{
    internal string Root="", Name="", Author="", Version="";
    internal bool HasCatalog;
}
internal sealed class CompatibilityResult
{
    internal ContentManifest Manifest=new ContentManifest();
    internal string[] Shared=Array.Empty<string>(), Local=Array.Empty<string>();
}
public sealed partial class MultiplayerRuntime
{
    private Task<CompatibilityResult>? compatibilityScan;
    private CompatibilityResult? compatibility;
    private Action? compatibilityContinuation;
    internal string CompatibilityStatus="Checking installed mods...";
    private static readonly HashSet<string> optionalPatchWarnings=new HashSet<string>();
    internal static void ReportOptionalPatch(string warning)
    {if(optionalPatchWarnings.Add(warning)){UnityEngine.Debug.LogWarning("[BASMP] "+warning);Instance?.WriteLog(warning);}}
    internal void SavePatchCompatibilityReport()
    {
        try{File.WriteAllText(Path.Combine(ModFolder,"patch-compatibility-report.json"),JsonConvert.SerializeObject(new{warnings=optionalPatchWarnings.ToArray(),patches=PatchInventory()},Formatting.Indented));}
        catch(Exception error){WriteLog("Could not save patch compatibility report: "+error.Message);}
        if(optionalPatchWarnings.Count>0)connectionNotice=optionalPatchWarnings.Count+" optional AI patch warning(s). See patch-compatibility-report.json; client AI targeting may be affected.";
    }
    internal void BeginCompatibilityScan()
    {
        // Capture Unity-owned metadata on the game thread; file hashing happens off-thread.
        var mods=ModManager.loadedMods.Select(m=>new CompatibilityMod{Root=m.fullPath,Name=m.Name ?? m.folderName,Author=m.Author ?? "",Version=m.ModVersion ?? "",
            HasCatalog=m.ownedDatas.Count>0 || m.changedDatas.Count>0}).ToArray();
        var adapters=MultiplayerModApi.Signatures();
        compatibility=null;CompatibilityStatus="Checking installed mods...";
        compatibilityScan=Task.Run(()=>ScanMods(mods,adapters));
    }
    internal bool EnsureCompatibilityReady(Action continuation)
    {
        if(compatibility!=null)return true;
        if(compatibilityScan==null)BeginCompatibilityScan();
        if(compatibilityContinuation==null)compatibilityContinuation=continuation;
        WriteLog("Checking mod compatibility files before connecting...");
        return false;
    }
    private void TickCompatibilityScan()
    {
        if(compatibility!=null || compatibilityScan?.IsCompleted!=true)return;
        try
        {
            compatibility=compatibilityScan.GetAwaiter().GetResult();
            CompatibilityStatus=compatibility.Shared.Length+" shared mod(s), "+compatibility.Local.Length+" explicitly local-only. Files checked before admission; custom scripts may need an adapter.";
            try{File.WriteAllText(Path.Combine(ModFolder,"compatibility-report.json"),JsonConvert.SerializeObject(new{version="0.5.0",shared=compatibility.Shared,localOnly=compatibility.Local,adapters=MultiplayerModApi.Signatures(),patches=PatchInventory()},Formatting.Indented));}
            catch(Exception error){WriteLog("Could not save compatibility report: "+error.Message);}
            var continuation=compatibilityContinuation;compatibilityContinuation=null;continuation?.Invoke();
        }
        catch(Exception error)
        {
            CompatibilityStatus="Mod check failed: "+error.GetBaseException().Message;
            compatibilityContinuation=null;compatibilityScan=null;WriteLog(CompatibilityStatus);connectionNotice=CompatibilityStatus;
        }
        RefreshNativeText("Mod compatibility",CompatibilityStatus);
    }
    internal static object[] PatchInventory()=>Harmony.GetAllPatchedMethods().Select(method=>new{method=method.DeclaringType?.FullName+"."+method.Name,owners=Harmony.GetPatchInfo(method)?.Owners.ToArray() ?? Array.Empty<string>()})
        .Where(row=>row.owners.Contains("zsoul.bladeandsorcery.multiplayer") || row.owners.Contains("zsoul.bladeandsorcery.multiplayer.ui")).Cast<object>().ToArray();
    private static string Digest(byte[] bytes){using var sha=SHA256.Create();return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","");}
    private static string ShortName(string name)
    {while(Encoding.UTF8.GetByteCount(name)>120)name=name.Substring(0,name.Length-1);return name;}
    private static IEnumerable<string> ModFiles(string root)
    {
        var pending=new Stack<string>();pending.Push(root);
        while(pending.Count>0)
        {
            string directory=pending.Pop();
            foreach(var child in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes=File.GetAttributes(child);
                if((attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Linked entries are unsupported in mod compatibility scans");
                if((attributes&FileAttributes.Directory)!=0)pending.Push(child);else yield return child;
            }
        }
    }
    private static CompatibilityResult ScanMods(CompatibilityMod[] mods,string[] adapters)
    {
        var entries=new List<ContentEntry>();var shared=new List<string>();var local=new List<string>();
        foreach(var mod in mods)
        {
            string marker=Path.Combine(mod.Root,"basmp-compatibility.json");
            var options=File.Exists(marker) ? JObject.Parse(File.ReadAllText(marker)) : new JObject();
            // A catalog-changing mod cannot declare itself local-only through this shortcut.
            if((bool?)options["localOnly"]==true && !mod.HasCatalog){local.Add(mod.Name);continue;}
            var content=new StringBuilder();content.Append(mod.Version).Append('\n').Append(shared.Count).Append('\n');
            var settings=(options["settings"] as JArray)?.Values<string>().Where(p=>p!=null).ToArray() ?? Array.Empty<string>();
            var root=Path.GetFullPath(mod.Root).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)+Path.DirectorySeparatorChar;
            foreach(var file in ModFiles(root).OrderBy(f=>f,StringComparer.Ordinal))
            {
                if((File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Linked mod files are unsupported in compatibility scans: "+mod.Name);
                var relative=file.Substring(root.Length).Replace('\\','/');
                string extension=Path.GetExtension(file).ToLowerInvariant();
                bool include=extension==".dll" || extension==".bundle" || extension==".lua" || extension==".wasm" || extension==".bytes" || relative.Equals("manifest.json",StringComparison.OrdinalIgnoreCase) || relative=="basmp-compatibility.json" || settings.Contains(relative,StringComparer.Ordinal) || extension==".json" && Path.GetFileName(file).StartsWith("catalog",StringComparison.OrdinalIgnoreCase);
                if(!include && extension==".json")include=File.ReadAllText(file).Contains("\"$type\""); // Native catalog definitions; excludes private runtime preferences.
                if(!include)continue;
                using var sha=SHA256.Create();using var stream=File.OpenRead(file);
                content.Append(relative).Append(':').Append(BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","")).Append('\n');
            }
            foreach(var setting in settings)
            {
                var path=Path.GetFullPath(Path.Combine(root,setting));
                if(!path.StartsWith(root,StringComparison.OrdinalIgnoreCase) || !File.Exists(path))throw new InvalidDataException("Missing or invalid shared settings file for "+mod.Name);
            }
            entries.Add(new ContentEntry{Id="mod:"+Digest(Encoding.UTF8.GetBytes(mod.Name+"\n"+mod.Author)),Name=ShortName(mod.Name),Hash=Digest(Encoding.UTF8.GetBytes(content.ToString()))});
            shared.Add(mod.Name+" "+mod.Version);
        }
        foreach(var adapter in adapters)entries.Add(new ContentEntry{Id="adapter:"+Digest(Encoding.UTF8.GetBytes(adapter.Split('=')[0])),Name=ShortName(adapter),Hash=Digest(Encoding.UTF8.GetBytes(adapter))});
        return new CompatibilityResult{Manifest=new ContentManifest(entries),Shared=shared.ToArray(),Local=local.ToArray()};
    }
    [ModOption("Mod compatibility",saveValue=false,applyOnLoad=false)]
    [ModOptionTextDisplay("Checking installed mods...","")]
    [ModOptionCategory("Diagnostics",5),ModOptionOrder(10)]
    public static string ModCompatibilityStatus="";
    [ModOption("Rescan mods (offline)",valueSourceName=nameof(PressAction),saveValue=false,applyOnLoad=false)]
    [ModOptionButton,ModOptionCategory("Diagnostics",5),ModOptionOrder(11)]
    public static void RescanMods(int press=0)
    {
        var runtime=Instance;if(runtime==null)return;
        if(runtime.Session!=null || runtime.steamLobbies?.Busy==true || runtime.steamLobbies?.Current.IsValid()==true || runtime.compatibilityContinuation!=null)
        {runtime.connectionNotice="Leave the session/lobby before rescanning mods.";return;}
        runtime.BeginCompatibilityScan();
    }
}
