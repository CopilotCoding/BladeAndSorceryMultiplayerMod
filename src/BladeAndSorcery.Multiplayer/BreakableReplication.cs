using System;
using System.Collections.Generic;
using System.Linq;
using BladeAndSorcery.Multiplayer.Core;
using Newtonsoft.Json;
using ThunderRoad;
using UnityEngine;
using UnityEngine.Events;

namespace BladeAndSorcery.Multiplayer;

internal sealed class BreakableState
{
    public bool Broken;
    public float Health;
    public int Pieces;
    public int[]? ActivePieces;
}

internal sealed partial class GameWorld
{
    private readonly HashSet<ulong> brokenRoots = new HashSet<ulong>();
    private readonly Dictionary<ulong,EntityDescriptor> pendingBreakFragments = new Dictionary<ulong,EntityDescriptor>();
    private readonly Dictionary<Breakable,UnityAction<float>> breakDamageListeners = new Dictionary<Breakable,UnityAction<float>>();
    internal bool IsBrokenRoot(ulong id) => brokenRoots.Contains(id);
    internal IEnumerable<EntityDescriptor> BrokenRootDescriptors => Registry.Entities.Where(d=>IsBrokenRoot(d.Id));
    private bool BindExistingBrokenRoot(EntityDescriptor descriptor)
    {
        if(descriptor.Kind!=EntityKind.Item || descriptor.Appearance.Length==0) return false;
        var state=JsonConvert.DeserializeObject<BreakableState>(descriptor.Appearance,new JsonSerializerSettings {TypeNameHandling=TypeNameHandling.None,MaxDepth=4});
        if(state==null || !state.Broken || state.Pieces<=0 || state.Pieces>128) return false;
        var prefix="break:"+descriptor.Id+":piece:";
        var existing=Item.allActive.Where(i=>i!=null && i.loaded && GameMath.SceneItemKey(i).StartsWith(prefix,StringComparison.Ordinal)).ToArray();
        var active=state.ActivePieces ?? Enumerable.Range(0,state.Pieces).ToArray();
        bool intactMapRoot=descriptor.SceneKey.Length>0 && Item.allActive.Any(i=>i!=null && i.loaded && GameMath.SceneItemKey(i)==descriptor.SceneKey && i.breakable!=null && !i.breakable.IsBroken);
        if(intactMapRoot || existing.Length!=active.Length || !existing.Select(GameMath.SceneItemKey).OrderBy(k=>k).SequenceEqual(active.Select(i=>prefix+i).OrderBy(k=>k)))
        {
            foreach(var item in existing) if(Find(item)==null) item.Despawn();
            return false;
        }
        // Rejoining the same server can reuse the native fragments retained in
        // the local scene. Recreating their parent would generate duplicates.
        Entities.Add(descriptor.Id,new ReplicatedEntity(this,descriptor));brokenRoots.Add(descriptor.Id);
        return true;
    }
    private static bool FragmentParent(string key,out ulong id)
    {
        id=0;
        if(!key.StartsWith("break:",StringComparison.Ordinal)) return false;
        var fields=key.Split(':');return fields.Length==4 && ulong.TryParse(fields[1],out id);
    }
    private bool ForeignUnboundFragment(Item item) => FragmentParent(GameMath.SceneItemKey(item),out var id) && Registry.TryGet(id,out var parent) && parent.Owner!=Session.LocalId;
    private bool WaitForBreakParent(EntityDescriptor descriptor)
    {
        if(!FragmentParent(descriptor.SceneKey,out var parent) || (IsBrokenRoot(parent) && !spawning.Contains(parent))) return false;
        pendingBreakFragments[descriptor.Id]=descriptor;return true;
    }
    private void SpawnPendingBreakFragments()
    {
        foreach(var descriptor in pendingBreakFragments.Values.ToArray())
            if(FragmentParent(descriptor.SceneKey,out var parent) && IsBrokenRoot(parent) && !spawning.Contains(parent))
            {pendingBreakFragments.Remove(descriptor.Id);SpawnRemote(descriptor);}
    }
    internal bool AllowNativeBreak(Breakable breakable)
    {
        if(Applying || disposed || !Session.Connected) return true;
        for(var current=breakable;current!=null;current=current.parentBreakable)
            if(Find(current.LinkedItem) is { } entity) return entity.Owned;
        return Session.IsHost;
    }
    internal void PrepareBreakable(ReplicatedEntity entity)
    {
        var item=entity.Item;
        var breakable=item != null ? item.breakable : null;
        if(breakable==null) return;
        PrepareFragmentKeys(entity,breakable,true);
        if(!breakDamageListeners.ContainsKey(breakable))
        {
            UnityAction<float> listener=_=>BreakDamageChanged(breakable);
            breakDamageListeners.Add(breakable,listener);breakable.onTakeDamage.AddListener(listener);
        }
        if(entity.Descriptor.Appearance.Length == 0)
            entity.Descriptor.Appearance=JsonConvert.SerializeObject(new BreakableState {Broken=breakable.IsBroken,Health=breakable.momentumHealth,Pieces=breakable.subBrokenItems.Count});
        else if(!entity.Owned)
        {
            var state=JsonConvert.DeserializeObject<BreakableState>(entity.Descriptor.Appearance,new JsonSerializerSettings {TypeNameHandling=TypeNameHandling.None,MaxDepth=4});
            if(state==null || !Protocol.Finite(state.Health)) return;
            breakable.momentumHealth=state.Health;
            if(state.Broken) ApplyBreak(entity,state);
        }
    }
    private static void PrepareFragmentKeys(ReplicatedEntity parent,Breakable breakable,bool pending)
    {
        for(int i=0;i<breakable.subBrokenItems.Count;i++)
            if(breakable.subBrokenItems[i]!=null) GameMath.SetFragmentKey(breakable.subBrokenItems[i],"break:"+parent.Descriptor.Id+":piece:"+i,pending);
    }
    private void BreakDamageChanged(Breakable breakable)
    {
        if(Applying || disposed || Find(breakable.LinkedItem) is not { } entity || !entity.Owned) return;
        float health=Protocol.Finite(breakable.momentumHealth) ? Mathf.Clamp(breakable.momentumHealth,-1000000,1000000) : 0;
        entity.Descriptor.Appearance=JsonConvert.SerializeObject(new BreakableState {Broken=breakable.IsBroken,Health=health,Pieces=breakable.subBrokenItems.Count});
        SendAction(entity,new GameplayAction {Kind=ActionKind.Break,Side=0,Value=health});
    }
    private void OnBreakStarted(Breakable breakable)
    {
        if(disposed || !Session.Connected || breakable.LinkedItem==null) return;
        var entity=Find(breakable.LinkedItem);
        if(entity==null && Session.IsHost && !Applying) entity=Register(breakable.LinkedItem);
        if(entity==null) return;
        brokenRoots.Add(entity.Descriptor.Id);
        entity.Descriptor.Pose=breakable.LinkedItem.transform.NetPose();
        entity.Descriptor.Appearance=JsonConvert.SerializeObject(new BreakableState {Broken=true,Health=0,Pieces=breakable.subBrokenItems.Count,ActivePieces=Enumerable.Range(0,breakable.subBrokenItems.Count).ToArray()});
        PrepareFragmentKeys(entity,breakable,true);
        // Send before native handle links can redirect a hand to a fragment.
        if(entity.Owned && !Applying)
        {
            var rotation=entity.Descriptor.Pose.Rotation;
            SendAction(entity,new GameplayAction {Kind=ActionKind.Break,Side=1,Position=entity.Descriptor.Pose.Position,Text=JsonConvert.SerializeObject(new[]{rotation.X,rotation.Y,rotation.Z,rotation.W})});
            foreach(var piece in breakable.subBrokenItems)
                if(piece!=null && piece.loaded && Find(piece)==null) Register(piece);
        }
    }
    private void OnBreakFinished(Breakable breakable,PhysicBody[] pieces)
    {
        var entity=Find(breakable.LinkedItem);
        if(entity==null || disposed) return;
        PrepareFragmentKeys(entity,breakable,false);
        if(Applying || !entity.Owned) return;
        foreach(var item in breakable.subBrokenItems)
            if(item!=null && item.loaded)
            {
                RememberLocalOrigin(item);
                if(Find(item)==null) Register(item);
            }
    }
    private void ApplyBreak(ReplicatedEntity entity,BreakableState? expected=null)
    {
        var item=entity.Item;
        if(item==null || item.breakable==null || item.breakable.IsBroken) return;
        // Restoring a broken scene prop for a newcomer must not leave the intact
        // map object behind. Recreate its native pieces before their descriptors.
        item.transform.SetPositionAndRotation(entity.Descriptor.Pose.Position.Game(),entity.Descriptor.Pose.Rotation.Game());
        item.physicBody.rigidBody.position=item.transform.position;item.physicBody.rigidBody.rotation=item.transform.rotation;
        applyDepth++;
        try
        {
            item.breakable.Break();
            if(expected?.ActivePieces!=null)
            {
                for(int i=0;i<item.breakable.subBrokenItems.Count;i++)
                    if(!expected.ActivePieces.Contains(i) && item.breakable.subBrokenItems[i]!=null) item.breakable.subBrokenItems[i].Despawn();
                entity.Descriptor.Appearance=JsonConvert.SerializeObject(expected);
            }
        }
        finally{applyDepth--;}
    }
    private void ForgetBreakFragment(ulong id)
    {
        if(!Registry.TryGet(id,out var fragment) || !FragmentParent(fragment.SceneKey,out var parent) || !Registry.TryGet(parent,out var root)) return;
        var fields=fragment.SceneKey.Split(':');
        if(!int.TryParse(fields[3],out int index)) return;
        var state=JsonConvert.DeserializeObject<BreakableState>(root.Appearance);
        if(state==null || !state.Broken) return;
        state.ActivePieces=(state.ActivePieces ?? Enumerable.Range(0,state.Pieces).ToArray()).Where(i=>i!=index).ToArray();
        root.Appearance=JsonConvert.SerializeObject(state);
    }
    private void ApplyBreakState(ReplicatedEntity entity,GameplayAction action)
    {
        int pieces=entity.Item!=null && entity.Item.breakable!=null ? entity.Item.breakable.subBrokenItems.Count :
            JsonConvert.DeserializeObject<BreakableState>(entity.Descriptor.Appearance)?.Pieces ?? 0;
        if(action.Side==1)
        {
            var rotation=JsonConvert.DeserializeObject<float[]>(action.Text)!;
            entity.Descriptor.Pose=new Core.Pose(action.Position,new System.Numerics.Quaternion(rotation[0],rotation[1],rotation[2],rotation[3]));
            brokenRoots.Add(entity.Descriptor.Id);
            entity.Descriptor.Appearance=JsonConvert.SerializeObject(new BreakableState {Broken=true,Health=0,Pieces=pieces});
            ApplyBreak(entity);
        }
        else
        {
            if(entity.Item?.breakable!=null) entity.Item.breakable.momentumHealth=action.Value;
            entity.Descriptor.Appearance=JsonConvert.SerializeObject(new BreakableState {Broken=brokenRoots.Contains(entity.Descriptor.Id),Health=action.Value,Pieces=pieces});
        }
    }
    private bool ValidateBreak(GameplayAction action)
    {
        if(!Registry.TryGet(action.Entity,out var entity) || entity.Kind!=EntityKind.Item || action.Side<0 || action.Side>1 || Math.Abs(action.Value)>1000000) return false;
        if(action.Side==0) return true;
        var rotation=JsonConvert.DeserializeObject<float[]>(action.Text,new JsonSerializerSettings {TypeNameHandling=TypeNameHandling.None,MaxDepth=4});
        return rotation!=null && rotation.Length==4 && rotation.All(Protocol.Finite) && rotation.Sum(v=>v*v)>.5f && rotation.Sum(v=>v*v)<1.5f;
    }
    private void ClearBreakables()
    {
        foreach(var pair in breakDamageListeners) if(pair.Key!=null) pair.Key.onTakeDamage.RemoveListener(pair.Value);
        breakDamageListeners.Clear();brokenRoots.Clear();pendingBreakFragments.Clear();
    }
}
