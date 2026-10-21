using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Emit;
using BladeAndSorcery.Multiplayer.Core;
using HarmonyLib;
using Newtonsoft.Json;
using ThunderRoad;
using UnityEngine;

namespace BladeAndSorcery.Multiplayer;

public enum PvpWeaponDamageRule : byte { NativePlayer, NpcStyle }

public sealed partial class MultiplayerRuntime
{
    private const string PvpDamageOptionName="PvP weapon damage (host)";
    private bool refreshingPvpDamageOption;
    public static ModOptionInt[] PvpDamageChoices() => new[] {
        new ModOptionInt("Native player damage",0),new ModOptionInt("NPC-style weapon damage",1)
    };
    [ModOption(PvpDamageOptionName,tooltip="Native keeps the game's player weapon damage reduction and caps. NPC-style removes those protections for human-versus-human weapons only. Armor, invincibility, friendly fire and NPC combat keep their normal rules.",valueSourceName=nameof(PvpDamageChoices),defaultValueIndex=0,saveValue=false,applyOnLoad=false)]
    [ModOptionCategory("Play",4),ModOptionOrder(4)]
    public static void PvpDamageOption(int choice=0) => Instance?.SetPvpDamageRule(choice);
    private void SetPvpDamageRule(int choice)
    {
        if(refreshingPvpDamageOption || choice<0 || choice>1)return;
        if(Session!=null && !Session.IsHost){connectionNotice="Only the host can change PvP weapon damage.";WriteLog(connectionNotice);return;}
        var previous=Configuration.PvpWeaponDamage;
        Configuration.PvpWeaponDamage=(PvpWeaponDamageRule)choice;
        try{File.WriteAllText(configPath,JsonConvert.SerializeObject(Configuration,Formatting.Indented));}
        catch(Exception error) when(error is IOException || error is UnauthorizedAccessException)
        {Configuration.PvpWeaponDamage=previous;connectionNotice="Could not save PvP damage setting: "+error.Message;WriteLog(connectionNotice);return;}
        if(World!=null && Session?.IsHost==true){World.Match.PvpWeaponDamage=Configuration.PvpWeaponDamage;World.Match.Broadcast();}
        connectionNotice="PvP weapons: "+(choice==0 ? "native player damage." : "NPC-style damage.");WriteLog(connectionNotice);
    }
    private void RefreshPvpDamageOption()
    {
        int index=(int)(Session!=null && World!=null ? World.Match.PvpWeaponDamage : Configuration.PvpWeaponDamage);
        refreshingPvpDamageOption=true;
        try
        {
            foreach(var option in ModManager.loadedMods.Where(m=>m.fullPath==ModFolder).SelectMany(m=>m.modOptions).Where(o=>o.name==PvpDamageOptionName))
                if(option.parameterValues!=null && option.parameterValues.Length>0 && option.currentValueIndex!=index){option.Apply(index);option.uiComponent?.Reload();}
        }
        finally{refreshingPvpDamageOption=false;}
    }
}

internal sealed partial class GameWorld
{
    private CollisionInstance? npcStylePvpHit;
    private bool IsHumanWeaponAttacker(ulong actor,ushort owner)
        => actor!=0 && Registry.TryGet(actor,out var descriptor) && descriptor.Kind==EntityKind.Player && descriptor.Owner==owner;
    internal bool UseNpcStylePvpDamage(Creature creature,CollisionInstance hit)
    {
        if(!Session.Connected || Match.PvpWeaponDamage!=PvpWeaponDamageRule.NpcStyle ||
            Find(creature) is not {Descriptor.Kind:EntityKind.Player} victim)return false;
        if(ReferenceEquals(npcStylePvpHit,hit))return true;
        var item=hit.damageStruct.damager?.collisionHandler?.item ?? hit.sourceColliderGroup?.collisionHandler?.item;
        var actor=Find(item?.mainHandler?.creature ?? item?.lastHandler?.creature);
        return actor?.Descriptor.Kind==EntityKind.Player && actor.Descriptor.Owner!=victim.Descriptor.Owner;
    }
}

// Keep native damage events, armor, creature multipliers, grip disruption and
// invincibility. Only the player-only weapon scaling/cap is selectable.
[HarmonyPatch(typeof(Creature),nameof(Creature.Damage),new[]{typeof(CollisionInstance)})]
internal static class PvpWeaponDamageScalingPatch
{
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var scale=AccessTools.Field(typeof(DamagerData.Tier),nameof(DamagerData.Tier.playerDamageMultiplier));
        var clamp=AccessTools.Method(typeof(Mathf),nameof(Mathf.Clamp),new[]{typeof(float),typeof(float),typeof(float)});
        int scales=0,clamps=0;
        foreach(var instruction in instructions)
        {
            if(instruction.Calls(clamp))
            {
                instruction.opcode=OpCodes.Ldarg_0;instruction.operand=null;yield return instruction;
                yield return new CodeInstruction(OpCodes.Ldarg_1);
                yield return new CodeInstruction(OpCodes.Call,AccessTools.Method(typeof(PvpWeaponDamageScalingPatch),nameof(Clamp)));
                clamps++;continue;
            }
            yield return instruction;
            if(!instruction.LoadsField(scale))continue;
            yield return new CodeInstruction(OpCodes.Ldarg_0);yield return new CodeInstruction(OpCodes.Ldarg_1);
            yield return new CodeInstruction(OpCodes.Call,AccessTools.Method(typeof(PvpWeaponDamageScalingPatch),nameof(Multiplier)));scales++;
        }
        if(scales!=1 || clamps!=1)throw new InvalidOperationException("Native player damage scaling changed; refusing an incompatible PvP damage patch.");
    }
    private static float Multiplier(float native,Creature victim,CollisionInstance hit)
        => MultiplayerRuntime.Instance?.World?.UseNpcStylePvpDamage(victim,hit)==true ? 1 : native;
    private static float Clamp(float damage,float min,float max,Creature victim,CollisionInstance hit)
        => MultiplayerRuntime.Instance?.World?.UseNpcStylePvpDamage(victim,hit)==true ? damage : Mathf.Clamp(damage,min,max);
}
