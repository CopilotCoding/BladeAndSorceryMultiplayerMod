using System;
using HarmonyLib;
using ThunderRoad;

namespace BladeAndSorcery.Multiplayer;

// Keep the native kill/ragdoll/inventory events. The level's single-player
// defeat/reload coroutine must not compete with an active match's respawn.
[HarmonyPatch(typeof(Level),"OnCreatureKilled")]
internal static class SessionDeathFlowPatch
{
    private static bool Prefix(Level __instance,Creature creature,Player player,CollisionInstance collisionInstance,EventTime eventTime)
    {
        var world=MultiplayerRuntime.Instance?.World;
        if(world?.Session.Connected!=true || player==null || player!=Player.local || eventTime!=EventTime.OnEnd) return true;
        world.Match.RecordNativeDeath(__instance,creature,player,collisionInstance);
        return false;
    }
}

internal sealed partial class MatchController
{
    private Level? deathLevel;
    private Creature? deathCreature;
    private Player? deathPlayer;
    private CollisionInstance? deathHit;
    internal void RecordNativeDeath(Level level,Creature creature,Player player,CollisionInstance hit)
    {
        deathLevel=level;deathCreature=creature;deathPlayer=player;deathHit=hit;
        player.locomotion.MoveStop();player.locomotion.enabled=false;
    }
    private void ClearNativeDeath()
    {
        deathLevel=null;deathCreature=null;deathPlayer=null;deathHit=null;
    }
    internal Action? CaptureNativeDeathRestore()
    {
        if(respawning || GameManager.isQuitting || deathLevel==null || deathCreature==null || !deathCreature.isKilled || deathPlayer==null || deathPlayer.creature!=null || LevelManager.isLoadingLocked) return null;
        var level=deathLevel;var creature=deathCreature;var player=deathPlayer;var hit=deathHit;
        // Invoked after session patches are removed. Replay only the level's
        // original UI/defeat handler, without duplicating kill/score/skill events.
        return () =>
        {
            if(GameManager.isQuitting || level==null || level!=Level.current || creature==null || !creature.isKilled || player==null || player.creature!=null || LevelManager.isLoadingLocked) return;
            AccessTools.Method(typeof(Level),"OnCreatureKilled").Invoke(level,new object?[]{creature,player,hit,EventTime.OnEnd});
        };
    }
}
