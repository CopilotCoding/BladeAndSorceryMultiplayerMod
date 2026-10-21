using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using ThunderRoad;

namespace BladeAndSorcery.Multiplayer;

public sealed partial class MultiplayerRuntime
{
    private const string LimbLossOptionName="Survive limb loss (host)";
    private bool refreshingLimbLossOption;

    [ModOption(LimbLossOptionName,tooltip="Host rule for all players. Survive arm, hand, leg and foot severing. Normal damage and head/neck cuts can still kill. NPCs keep native rules.",defaultValueIndex=0,saveValue=false,applyOnLoad=false)]
    [ModOptionCategory("Play",4),ModOptionOrder(3)]
    public static void LimbLossOption(bool enabled=false) => Instance?.SetLimbLossRule(enabled);

    private void SetLimbLossRule(bool enabled)
    {
        if(refreshingLimbLossOption) return;
        if(Session!=null && !Session.IsHost)
        {
            connectionNotice="Only the host can change Survive limb loss.";
            WriteLog(connectionNotice);return;
        }
        bool previous=Configuration.SurviveLimbLoss;
        Configuration.SurviveLimbLoss=enabled;
        try {File.WriteAllText(configPath,JsonConvert.SerializeObject(Configuration,Formatting.Indented));}
        catch(Exception error) when(error is IOException || error is UnauthorizedAccessException)
        {
            Configuration.SurviveLimbLoss=previous;
            connectionNotice="Could not save limb loss setting: "+error.Message;WriteLog(connectionNotice);return;
        }
        if(World!=null && Session?.IsHost==true)
        {
            World.Match.SurviveLimbLoss=enabled;World.Match.Broadcast();
        }
        connectionNotice="Survive limb loss "+(enabled ? "enabled" : "disabled")+". Head/neck cuts and normal damage remain lethal.";
        WriteLog(connectionNotice);
    }

    private void RefreshLimbLossOption()
    {
        int index=(Session!=null && World!=null ? World.Match.SurviveLimbLoss : Configuration.SurviveLimbLoss) ? 1 : 0;
        refreshingLimbLossOption=true;
        try
        {
            foreach(var option in ModManager.loadedMods.Where(m=>m.fullPath==ModFolder).SelectMany(m=>m.modOptions).Where(o=>o.name==LimbLossOptionName))
                if(option.parameterValues!=null && option.parameterValues.Length>0 && option.currentValueIndex!=index) {option.Apply(index);option.uiComponent?.Reload();}
        }
        finally {refreshingLimbLossOption=false;}
    }
}

internal sealed partial class GameWorld
{
    internal bool ShouldForceKillSlice(RagdollPart? part)
    {
        if(part==null) return false;
        bool native=part.data.sliceForceKill || part.sliceChildAndDisableSelf?.data.sliceForceKill==true;
        return native && !SurvivesLimbSlice(part);
    }

    internal bool SurvivesLimbSlice(RagdollPart part)
    {
        if(!Session.Connected || !Match.SurviveLimbLoss || Find(part.ragdoll.creature)?.Descriptor.Kind!=BladeAndSorcery.Multiplayer.Core.EntityKind.Player) return false;
        var type=(part.sliceChildAndDisableSelf ?? part).type;
        const RagdollPart.Type limbs=RagdollPart.Type.LeftArm|RagdollPart.Type.RightArm|RagdollPart.Type.LeftHand|RagdollPart.Type.RightHand|
            RagdollPart.Type.LeftLeg|RagdollPart.Type.RightLeg|RagdollPart.Type.LeftFoot|RagdollPart.Type.RightFoot;
        return (type&limbs)!=0 && (type&~limbs)==0;
    }
}
