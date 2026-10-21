using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BladeAndSorcery.Multiplayer.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ThunderRoad;
using UnityEngine;
using UnityEngine.SpatialTracking;

namespace BladeAndSorcery.Multiplayer;

// This component only exists in an explicitly isolated automated test process.
internal sealed class AutomatedDriver : MonoBehaviour
{
    private readonly Dictionary<string,List<(object Target,System.Reflection.FieldInfo Field,object? Value)>> hiddenMapAssets=new Dictionary<string,List<(object,System.Reflection.FieldInfo,object?)>>();
    private MultiplayerRuntime runtime = null!;
    private string commands = "", responses = "";
    private float nextPoll;
    private bool configured, posing;
    private string testCaption="";
    private void OnGUI()
    {
        if(testCaption.Length==0) return;
        GUI.Box(new Rect(8,8,Screen.width-16,62),GUIContent.none);
        GUI.Label(new Rect(16,12,Screen.width-32,56),testCaption,new GUIStyle(GUI.skin.label){fontSize=21,wordWrap=true});
    }
    private Creature? lastMockBody;
    private int mockBodyResets;
    internal object PoseState => new {resets=mockBodyResets,motionActive=motionDuration>0,head=new[]{head.x,head.y,head.z},left=new[]{left.x,left.y,left.z},right=new[]{right.x,right.y,right.z}};
    private bool borrowedClipboard;
    private string previousClipboard="";
    private void RestoreClipboard()
    {
        if(!borrowedClipboard) return;
        GUIUtility.systemCopyBuffer=previousClipboard;previousClipboard="";borrowedClipboard=false;
    }
    private void OnDestroy() => RestoreClipboard();
    private Vector3 left, right, head;
    private Quaternion leftRotation, rightRotation, headRotation;
    private float motionStart, motionDuration;
    private Vector3 motionFrom, motionTo;
    private Quaternion motionRotationFrom, motionRotationTo;
    private Side motionSide;
    private bool motionBoth;
    private bool motionArc;
    private Vector3 motionPivot;
    private Vector3 motionOtherFrom;
    private Quaternion motionOtherRotation;
    private ulong observedItem;
    private ulong engagementTarget;
    private float engagementDistance = 2;
    private int engagementMoves;
    internal object Engagement => new { target=engagementTarget.ToString(),distance=engagementDistance,moves=engagementMoves };
    private readonly List<object> observedFrames = new List<object>();
    internal object ItemObservation => new {entity=observedItem.ToString(),frames=observedFrames.ToArray()};
    private void FixedUpdate()
    {
        if (observedItem == 0 || observedFrames.Count >= 256 || runtime.World == null ||
            !runtime.World.Entities.TryGetValue(observedItem,out var entity) || entity.Item == null) return;
        var item=entity.Item;var p=item.transform.position;var v=item.physicBody.velocity;
        observedFrames.Add(new {time=Time.realtimeSinceStartupAsDouble,position=new[]{p.x,p.y,p.z},velocity=new[]{v.x,v.y,v.z},held=item.handlers.Count,holstered=item.holder != null});
    }
    internal void Initialize(MultiplayerRuntime owner)
    {
        runtime = owner;
        commands = Path.Combine(owner.TestDirectory, "commands"); responses = Path.Combine(owner.TestDirectory, "responses");
        Directory.CreateDirectory(commands); Directory.CreateDirectory(responses);
    }
    private void Update()
    {
        if (!configured && GameManager.initialized && Player.local?.creature != null)
        {
            configured = true; Screen.SetResolution(800, 600, false); AudioListener.volume = 0;
            QualitySettings.vSyncCount=0;Application.targetFrameRate=90;
            foreach (var tracked in Player.local.GetComponentsInChildren<TrackedPoseDriver>()) tracked.enabled = false;
            runtime.WriteLog("Automated player ready");
        }
        CheckMockBody();
        if (Time.realtimeSinceStartup < nextPoll || commands.Length == 0) return;
        nextPoll = Time.realtimeSinceStartup + .05f;
        foreach (var path in Directory.GetFiles(commands, "*.json").OrderBy(p => p).Take(128))
        {
            string response = Path.Combine(responses, Path.GetFileName(path));
            if (File.Exists(response))
            {
                // Completed responses are retained as evidence. Requests are a
                // queue: rescanning thousands of completed files every frame
                // consumes the simulation's frame budget, especially on OneDrive.
                File.Delete(path);File.Delete(response+".pending");continue;
            }
            try
            {
                var command = JObject.Parse(File.ReadAllText(path));
                var result = Execute(command, response);
                if (result != null) Respond(response, true, result);
            }
            catch (Exception e) { Respond(response, false, e.ToString()); runtime.WriteLog("Test command failed: " + e.Message); }
        }
    }
    private object? Execute(JObject c, string response)
    {
        string op = (string?)c["op"] ?? throw new InvalidDataException("Command needs op");
        if(op.StartsWith("procedural-",StringComparison.Ordinal))return ProceduralNpcTestProbe.Execute(runtime,c,result=>Respond(response,true,result));
        if(op.StartsWith("compatibility-",StringComparison.Ordinal))return CompatibilityProbe.Execute(runtime,c,result=>Respond(response,true,result));
        if(op.StartsWith("pvp-probe-",StringComparison.Ordinal))return PvpDamageTestProbe.Execute(runtime,c);
        if(op=="world-audit")return new {
            connected=runtime.Session?.Connected==true,level=Level.current?.data?.id,seed=Level.seed,revision=runtime.Session?.WorldRevision,time=Time.realtimeSinceStartupAsDouble,corrections=runtime.World?.SettledItemCorrections ?? 0,recoveredItems=runtime.World?.RecoveredItemReplicas ?? 0,suppressedDespawns=runtime.World?.SuppressedItemDespawns ?? 0,
            items=Item.allActive.Where(item=>item!=null && item.loaded && item.data!=null).Select(item=>{var entity=runtime.World?.Find(item);var body=item.physicBody.rigidBody;return new {
                id=entity?.Descriptor.Id.ToString(),catalog=item.data.id,owner=entity?.Descriptor.Owner,epoch=entity?.Descriptor.Epoch,sceneKey=GameMath.SceneItemKey(item),
                position=new[]{item.transform.position.x,item.transform.position.y,item.transform.position.z},rotation=new[]{item.transform.rotation.x,item.transform.rotation.y,item.transform.rotation.z,item.transform.rotation.w},
                velocity=new[]{body.velocity.x,body.velocity.y,body.velocity.z},angular=new[]{body.angularVelocity.x,body.angularVelocity.y,body.angularVelocity.z},
                held=item.handlers.Count,holstered=item.holder!=null,penetrating=item.isPenetrating,item.worldAttached,body.isKinematic,sleeping=body.IsSleeping(),owned=entity?.Owned,
                snapshotAge=entity!=null && !entity.Owned ? runtime.World!.Now-entity.LastArrival : 0
                ,linkedBodies=entity?.AttachedBodyPaths(),sourceBodies=entity?.SourceBodyPaths
            };}).ToArray(),
            unmanagedBodies=UnityEngine.Object.FindObjectsOfType<Rigidbody>().Where(b=>!b.isKinematic && b.gameObject.scene.name==Level.current?.gameObject.scene.name && b.GetComponentInParent<Item>()==null && b.GetComponentInParent<RagdollPart>()==null)
                .Select(b=>new{key=GameMath.ScenePath(b.transform),position=new[]{b.position.x,b.position.y,b.position.z}}).ToArray()
        };
        if(op=="world-perturb-replicas")
        {
            if(runtime.World==null)throw new InvalidOperationException("Connect first");
            var candidates=runtime.World.Entities.Values.Where(e=>!e.Owned && e.Item!=null && e.Item.handlers.Count==0 && e.Item.holder==null && !e.Item.isPenetrating && !e.Item.worldAttached && e.Descriptor.SceneKey.StartsWith("spawner:",StringComparison.Ordinal)).Take(8).ToArray();
            foreach(var entity in candidates){var body=entity.Item!.physicBody.rigidBody;body.position+=new Vector3(.35f,.18f,.2f);body.rotation=Quaternion.AngleAxis(65,Vector3.up)*body.rotation;if(!body.isKinematic){body.velocity=Vector3.zero;body.angularVelocity=Vector3.zero;}}
            return new{count=candidates.Length,ids=candidates.Select(e=>e.Descriptor.Id.ToString()).ToArray()};
        }
        switch (op)
        {
            case "test-caption": testCaption=((string?)c["text"] ?? "").Substring(0,Math.Min(180,((string?)c["text"] ?? "").Length));return "test caption updated";
            case "status": return runtime.GetStatus();
            case "maps": return Catalog.GetDataList<LevelData>().Select(d=>new {d.id,modes=d.modes.Select(m=>m.name).ToArray()}).ToArray();
            case "native-waves":
                return new {books=UnityEngine.Object.FindObjectsOfType<UIWaveSpawner>(true).Where(b=>b.waveSpawner!=null).Select(b=>new {b.id,key=GameMath.ScenePath(b.waveSpawner.transform)}).ToArray(),waves=Catalog.GetDataList<WaveData>().Select(w=>new {w.id,w.title,w.alwaysAvailable,w.waveSelectors,w.minTotalCount,w.maxTotalCount,loop=w.loopBehavior.ToString(),groups=w.groups.Select(g=>new {g.referenceID,g.overrideContainerID,g.overrideBrainID,g.minMaxCount}).ToArray()}).ToArray(),state=runtime.World?.NativeWaveSnapshot()};
            case "native-wave-select":
                var waveBook=UnityEngine.Object.FindObjectsOfType<UIWaveSpawner>(true).First(b=>b.waveSpawner!=null && (c["book"]==null || b.id==(string)c["book"]!));
                var selectedWave=Catalog.GetData<WaveData>((string)c["wave"]!);
                var waveRow=waveBook.GetComponentsInChildren<UiWaveSpawnerWaveElement>(true).FirstOrDefault(row=>(HarmonyLib.AccessTools.Field(typeof(UiWaveSpawnerWaveElement),"waveData").GetValue(row) as WaveData)?.id==selectedWave.id);
                if(waveRow==null)throw new InvalidOperationException("Wave is not offered by this native book");
                var waveToggle=waveRow.GetComponent<UnityEngine.UI.Toggle>();waveToggle.SetIsOnWithoutNotify(true);waveBook.OnWaveSelectedChanged(waveToggle,selectedWave);return "native wave selection dispatched";
            case "native-wave-start":
                UnityEngine.Object.FindObjectsOfType<UIWaveSpawner>(true).First(b=>b.waveSpawner!=null).OnStartClick();return "native wave-book Start dispatched";
            case "native-wave-stop":
                UnityEngine.Object.FindObjectsOfType<UIWaveSpawner>(true).First(b=>b.waveSpawner!=null).OnStopClick();return "native wave-book Stop/Return dispatched";
            case "book-spawn":
                var nativeBook=UnityEngine.Object.FindObjectsOfType<UIItemSpawner>(true).FirstOrDefault() ?? throw new InvalidOperationException("No native item book on this map");
                nativeBook.SetSandboxTab();
                HarmonyLib.AccessTools.Property(typeof(UIItemSpawner),nameof(UIItemSpawner.selectedItemInfo)).SetValue(nativeBook,new UIItemSpawner.ItemInfo(new ItemContent((string)c["item"]!)));
                nativeBook.SpawnSelectedItem();return "native item-book Spawn button dispatched";
            case "native-store":
                RequirePlayer();
                if(runtime.World==null || !ulong.TryParse((string?)c["entity"],out var storedId) || !runtime.World.Entities.TryGetValue(storedId,out var stored) || stored.Item==null) throw new InvalidOperationException("Expected a shared item");
                Player.local.creature.container.AddItemContent(stored.Item,true);return "native inventory storage requested";
            case "map-assets":
                var hiddenMap=(string)c["level"]!;
                if((bool?)c["available"]==true)
                {
                    if(hiddenMapAssets.TryGetValue(hiddenMap,out var savedLocations)){foreach(var saved in savedLocations)saved.Field.SetValue(saved.Target,saved.Value);hiddenMapAssets.Remove(hiddenMap);}
                }
                else if(!hiddenMapAssets.ContainsKey(hiddenMap))
                {
                    var hiddenData=Catalog.GetData<LevelData>(hiddenMap);
                    var locations=new List<(object,System.Reflection.FieldInfo,object?)>();
                    var field=HarmonyLib.AccessTools.Field(typeof(LevelData),"sceneLocation");locations.Add((hiddenData,field,field.GetValue(hiddenData)));field.SetValue(hiddenData,null);
                    foreach(var variant in hiddenData.sceneVariants ?? new List<LevelData.SceneVariant>())
                    {var variantField=HarmonyLib.AccessTools.Field(variant.GetType(),"sceneLocation");locations.Add((variant,variantField,variantField.GetValue(variant)));variantField.SetValue(variant,null);}
                    hiddenMapAssets.Add(hiddenMap,locations);
                }
                return "isolated map asset availability changed";
            case "map-load-error": LoadingCamera.SetState(LoadingCamera.State.Error,"Isolated shared-world recovery fixture");return "native loading error reported";
            case "travel":
                LevelManager.LoadLevel((string)c["level"]!,(string?)c["mode"],c["options"]?.ToObject<Dictionary<string,string>>());return "native map selection dispatched";
            case "delay-scene-ready":
                if(runtime.World==null) throw new InvalidOperationException("Session required");
                runtime.World.DelaySceneReadyUntil=Time.realtimeSinceStartupAsDouble+((double?)c["seconds"] ?? 10);return "test loading delay enabled";
            case "holster-held":
                RequirePlayer();var holsterHand=Player.local.creature.GetHand((string?)c["side"]=="left" ? Side.Left : Side.Right);
                var holsterItem=holsterHand.grabbedHandle?.item ?? throw new InvalidOperationException("Hold an item first");
                var holster=Player.local.creature.holders.ElementAt((int?)c["index"] ?? 0);holsterHand.TryRelease();holster.Snap(holsterItem);return holster.name;
            case "holster-entity":
                RequirePlayer();
                if(runtime.World==null || !ulong.TryParse((string?)c["entity"],out var holsterId) || !runtime.World.Entities.TryGetValue(holsterId,out var storedLoot) || !storedLoot.Owned || storedLoot.Item==null)throw new InvalidOperationException("Expected locally owned loot");
                foreach(var lootHand in storedLoot.Item.handlers.ToArray())lootHand.TryRelease();
                Player.local.creature.holders.ElementAt((int?)c["index"] ?? 0).Snap(storedLoot.Item);return "native loot holster fixture applied";
            case "combat-trace":
                if(runtime.World==null) throw new InvalidOperationException("A test session is required");
                if((bool?)c["clear"]==true) runtime.World.NativeCombatTrace.Clear();
                return runtime.World.NativeCombatTrace.ToArray();
            case "combat-capture-status": return new {active=runtime.World?.CombatCaptureActive ?? false,count=runtime.World?.NativeCombatTrace.Count ?? 0,path=runtime.LastCombatCapturePath};
            case "steam-status": return runtime.SteamStatusSnapshot();
            case "steam-host":
                runtime.Configuration.SteamLobbyVisibility=Mathf.Clamp((int?)c["visibility"] ?? 2,0,2);
                MultiplayerRuntime.HostSteamAction();return runtime.SteamStatusSnapshot();
            case "steam-refresh": MultiplayerRuntime.RefreshSteamAction();return runtime.SteamStatusSnapshot();
            case "steam-join":
                if(!ulong.TryParse((string?)c["lobby"],out ulong lobbyId))throw new InvalidDataException("Expected a Steam lobby ID");
                runtime.JoinSteamAutomation(lobbyId);return runtime.SteamStatusSnapshot();
            case "steam-leave": runtime.Disconnect();return runtime.SteamStatusSnapshot();
            case "grip-collisions":
                if(runtime.World==null || !ulong.TryParse((string?)c["entity"],out var filterId) || !runtime.World.Entities.TryGetValue(filterId,out var filterEntity) || filterEntity.Creature==null) throw new InvalidOperationException("Expected a registered fighter");
                return new[]{filterEntity.Creature.handLeft,filterEntity.Creature.handRight}.Where(h=>h.grabbedHandle?.item!=null).Select(h=>new {
                    side=h.side.ToString(),item=runtime.World.Find(h.grabbedHandle.item)?.Descriptor.Id.ToString(),
                    parts=filterEntity.Creature.ragdoll.parts.Select(p=>new {
                        key=GameMath.PartKey(p),type=p.type.ToString(),
                        pairs=p.colliderGroup.colliders.Where(a=>a!=null).SelectMany(a=>h.grabbedHandle.item.colliderGroups.SelectMany(g=>g.colliders).Where(b=>b!=null).Select(b=>Physics.GetIgnoreCollision(a,b))).ToArray()
                    }).ToArray()
                }).ToArray();
            case "test-clipboard":
                // Never return or log the user's existing clipboard. Borrow it
                // only for this isolated native UI check, and restore afterwards.
                if((bool?)c["restore"]==true) RestoreClipboard();
                else
                {
                    if(!borrowedClipboard) { previousClipboard=GUIUtility.systemCopyBuffer;borrowedClipboard=true; }
                    GUIUtility.systemCopyBuffer=(string?)c["text"] ?? "";
                }
                return "test clipboard updated";
            case "catalog": return Catalog.GetDataList<ItemData>().Select(d => d.id).Where(id => id.IndexOf((string?)c["filter"] ?? "Sword", StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
            case "factions": return Catalog.gameData.factions.Select(f => new {f.id,f.name,behavior=f.attackBehaviour.ToString()}).ToArray();
            case "host": case "join":
                runtime.Configuration.PlayerName = (string?)c["name"] ?? Path.GetFileName(runtime.TestDirectory);
                runtime.Configuration.Address = (string?)c["address"] ?? "127.0.0.1";
                runtime.Configuration.Port = (int?)c["port"] ?? runtime.Configuration.Port;
                runtime.Configuration.TestLatencyMilliseconds = (int?)c["latency"] ?? 0;
                runtime.Configuration.TestPacketLossPercent = (int?)c["loss"] ?? 0;
                runtime.Connect(op == "host"); return new { started = runtime.Session != null };
            case "disconnect": runtime.Disconnect(); return "disconnected";
            case "reset-pose": RequirePlayer(); ResetMockPose(); return "neutral mock pose restored";
            case "snap-replicas":
                if(runtime.World==null) throw new InvalidOperationException("A test session is required");
                foreach(var replica in runtime.World.Entities.Values.Where(e=>!e.Owned && e.Creature!=null))
                {
                    if(replica.Creature!.handLeft.grabbedHandle!=null || replica.Creature.handRight.grabbedHandle!=null || replica.Creature.ragdoll.handlers.Count>0) throw new InvalidOperationException("Release test grips before snapping fixture replicas");
                    replica.SnapTestPose();
                }
                return "fixture replica poses snapped";
            case "teleport":
                RequirePlayer();
                motionDuration=0;
                var teleportPosition=Vector(c["position"]!);
                if((bool?)c["navmesh"] ?? false)
                {
                    if(!UnityEngine.AI.NavMesh.SamplePosition(teleportPosition,out var floor,1.5f,UnityEngine.AI.NavMesh.AllAreas)) throw new InvalidOperationException("No native navigation-mesh floor near the requested test position");
                    teleportPosition=floor.position+Vector3.up*.08f;
                }
                Player.local.Teleport(teleportPosition, Quaternion.Euler(0, (float?)c["yaw"] ?? 0, 0), false, false);
                if(posing) ApplyMockPose(true);
                return "teleported";
            case "pose":
                RequirePlayer(); BeginPosing();motionDuration=0;
                if (c["left"] != null) left = LocalPosition(c["left"]!); if (c["right"] != null) right = LocalPosition(c["right"]!); if (c["head"] != null) head = LocalPosition(c["head"]!);
                if (c["leftRotation"] != null) leftRotation = LocalRotation(c["leftRotation"]!);
                if (c["rightRotation"] != null) rightRotation = LocalRotation(c["rightRotation"]!);
                if (c["headRotation"] != null) headRotation = LocalRotation(c["headRotation"]!);
                ApplyMockPose((bool?)c["snap"] ?? true);
                return "pose set";
            case "motion":
                RequirePlayer(); BeginPosing(); motionSide = (string?)c["side"] == "left" ? Side.Left : Side.Right;
                motionFrom = motionSide == Side.Left ? left : right;
                motionArc=c["pivot"]!=null;motionPivot=motionArc ? LocalPosition(c["pivot"]!) : Vector3.zero;
                motionTo = c["to"]!=null ? LocalPosition(c["to"]!) : motionFrom;
                if(!motionArc && c["to"]==null) throw new InvalidDataException("Linear motion needs a destination");
                motionRotationFrom = motionSide == Side.Left ? leftRotation : rightRotation;
                motionRotationTo = c["rotation"] != null ? LocalRotation(c["rotation"]!) : motionRotationFrom;
                motionBoth=(bool?)c["both"] ?? false;
                motionOtherFrom=motionSide==Side.Left ? right : left;
                motionOtherRotation=motionSide==Side.Left ? rightRotation : leftRotation;
                motionStart = Time.realtimeSinceStartup; motionDuration = Mathf.Clamp((float?)c["duration"] ?? .4f, .05f, 10); return "motion started";
            case "face-cutting-edge":
                RequirePlayer();BeginPosing();
                var edge=Player.local.creature.handRight.grabbedHandle?.item?.collisionHandlers.SelectMany(h=>h.damagers).FirstOrDefault(d=>d.type==Damager.Type.Slash);
                if(edge==null) throw new InvalidOperationException("Hold a native cutting blade first");
                bool flip=edge.direction==Damager.Direction.Forward && Vector3.Dot(edge.transform.forward,Vector(c["direction"]!))<0;
                if(flip) rightRotation=Quaternion.Inverse(Player.local.transform.rotation)*Quaternion.AngleAxis(180,edge.transform.up)*Player.local.transform.rotation*rightRotation;
                var edgeEuler=(Player.local.transform.rotation*rightRotation).eulerAngles;
                return new {flipped=flip,direction=edge.direction.ToString(),rotation=new[]{edgeEuler.x,edgeEuler.y,edgeEuler.z}};
            case "prepare-neck-arc":
                RequirePlayer();BeginPosing();motionDuration=0;
                var arcBlade=Player.local.creature.handRight.grabbedHandle?.item?.collisionHandlers.SelectMany(h=>h.damagers).FirstOrDefault(d=>d.type==Damager.Type.Slash);
                if(arcBlade==null) throw new InvalidOperationException("Hold a native cutting blade first");
                var arcOrigin=Player.local.transform;
                var pivotWorld=Player.local.GetShoulderCenter();
                var pivotLocal=arcOrigin.InverseTransformPoint(pivotWorld);
                var shiftLocal=arcOrigin.InverseTransformVector(Vector(c["point"]!)-arcBlade.transform.position);
                float arcAngle=Mathf.Clamp((float?)c["angle"] ?? 45,15,70);
                var arcStart=Quaternion.Inverse(arcOrigin.rotation)*Quaternion.AngleAxis(arcAngle,Vector3.up)*arcOrigin.rotation;
                left=pivotLocal+arcStart*(left+shiftLocal-pivotLocal);right=pivotLocal+arcStart*(right+shiftLocal-pivotLocal);
                leftRotation=arcStart*leftRotation;rightRotation=arcStart*rightRotation;
                var arcEnd=Quaternion.Inverse(arcOrigin.rotation)*Quaternion.AngleAxis(-2*arcAngle,Vector3.up)*arcOrigin.rotation*rightRotation;
                var endEuler=(arcOrigin.rotation*arcEnd).eulerAngles;
                return new {pivot=new[]{pivotWorld.x,pivotWorld.y,pivotWorld.z},rotation=new[]{endEuler.x,endEuler.y,endEuler.z},degrees=2*arcAngle};
            case "aim-joint-slash":
                RequirePlayer();BeginPosing();
                if(runtime.World==null || !ulong.TryParse((string?)c["entity"],out var slashTargetId) || !runtime.World.Entities.TryGetValue(slashTargetId,out var slashTarget) || slashTarget.Creature==null) throw new InvalidOperationException("Expected a network fighter");
                var jointPart=GameMath.FindPart(slashTarget.Creature,(int)c["part"]!);
                var blade=Player.local.creature.handRight.grabbedHandle?.item;
                var cutting=blade?.collisionHandlers.SelectMany(h=>h.damagers).FirstOrDefault(d=>d.type==Damager.Type.Slash);
                if(jointPart==null || cutting==null) throw new InvalidOperationException("Expected a native joint and a held slash damager");
                jointPart.GetSlicePositionAndDirection(out var cutPoint,out var cutNormal);
                if(c["point"]!=null) cutPoint=Vector(c["point"]!);
                if(c["normal"]!=null) cutNormal=Vector(c["normal"]!);
                var lengthDirection=Vector3.ProjectOnPlane(cutPoint-Player.local.GetShoulderCenter(),cutNormal);
                if(lengthDirection.sqrMagnitude<.001f) lengthDirection=Vector3.ProjectOnPlane(Vector3.forward,cutNormal);
                lengthDirection.Normalize();var stroke=Vector3.Cross(cutNormal.normalized,lengthDirection).normalized;
                if(Vector3.Dot(stroke,cutPoint-slashTarget.Creature.transform.position)<0) stroke=-stroke;
                if((string?)c["stage"]=="orient")
                {
                    var desired=Quaternion.LookRotation(stroke,lengthDirection)*Quaternion.Inverse(cutting.transform.rotation)*Player.local.handRight.transform.rotation;
                    rightRotation=Quaternion.Inverse(Player.local.transform.rotation)*desired;
                    return "native controller orientation prepared";
                }
                var bladePoint=cutting.transform.position;
                var centered=Player.local.handRight.transform.position+cutPoint-bladePoint;
                float travel=Mathf.Clamp((float?)c["distance"] ?? .5f,.2f,.6f);
                var start=centered+stroke*travel;var end=centered-stroke*travel;
                right=Player.local.transform.InverseTransformPoint(start);
                var planned=Player.local.transform.rotation*rightRotation;
                return new {start=new[]{start.x,start.y,start.z},end=new[]{end.x,end.y,end.z},rotation=new[]{planned.eulerAngles.x,planned.eulerAngles.y,planned.eulerAngles.z},point=new[]{cutPoint.x,cutPoint.y,cutPoint.z}};
            case "grip":
                RequirePlayer(); var side = (string?)c["side"] == "left" ? Side.Left : Side.Right;
                PlayerControl.GetHand(side).InvokeButtonEvent(PlayerControl.Hand.Button.Grip, (bool?)c["pressed"] ?? true); return "input sent";
            case "release":
                RequirePlayer();
                var releasingHand = Player.local.creature.GetHand((string?)c["side"] == "left" ? Side.Left : Side.Right);
                if (releasingHand.grabbedHandle != null) releasingHand.UnGrab((bool?)c["throw"] ?? false);
                return "released";
            case "clear-loose-items":
                if (runtime.World == null) throw new InvalidOperationException("A test session is required");
                var loose = runtime.World.Entities.Values.Where(e => e.Owned && e.Item != null && !e.Descriptor.SceneKey.StartsWith("break:",StringComparison.Ordinal) && e.Item.handlers.Count == 0 && e.Item.holder == null && e.Item.spawner == null && !e.Item.worldAttached).Select(e => e.Item!).ToArray();
                foreach (var item in loose) item.Despawn(); return new {cleared=loose.Length};
            case "equip":
                RequirePlayer();
                if (File.Exists(response + ".pending")) return null;
                string id = (string?)c["item"] ?? throw new InvalidDataException("Missing item ID");
                if (!Catalog.TryGetData<ItemData>(id, out var data, false)) throw new InvalidDataException("Unknown item: " + id);
                var hand = Player.local.creature.GetHand((string?)c["side"] == "left" ? Side.Left : Side.Right);
                File.WriteAllText(response + ".pending", "spawning");
                data.SpawnAsync(item =>
                {
                    try { hand.TryRelease(); hand.Grab(item.GetMainHandle(hand.side), true); runtime.World?.Register(item); Respond(response, true, item.data.id); }
                    catch (Exception e) { Respond(response, false, e.ToString()); }
                }, hand.grip.position, hand.grip.rotation, pooled: false);
                return null;
            case "grab-entity":
                RequirePlayer();
                if (runtime.World == null || !ulong.TryParse((string?)c["entity"], out var grabbedId) || !runtime.World.Entities.TryGetValue(grabbedId, out var grabbed) || grabbed.Item == null) throw new InvalidOperationException("Expected a replicated item");
                var grabbingHand = Player.local.creature.GetHand((string?)c["side"] == "left" ? Side.Left : Side.Right);
                grabbingHand.TryRelease();
                var grabbedHandle=grabbed.Item.GetMainHandle(grabbingHand.side);
                grabbingHand.Grab(grabbedHandle,grabbedHandle.GetDefaultOrientation(grabbingHand.side),(float?)c["axis"] ?? grabbedHandle.GetDefaultAxisLocalPosition(),false);
                return "native grab requested";
            case "grab-body":
                RequirePlayer();
                if(runtime.World==null || !ulong.TryParse((string?)c["entity"],out var bodyId) || !runtime.World.Entities.TryGetValue(bodyId,out var bodyEntity) || bodyEntity.Creature==null) throw new InvalidOperationException("Expected a network fighter");
                var grabbedPart=GameMath.FindPart(bodyEntity.Creature,(int)c["part"]!);
                var bodyHandle=grabbedPart?.handles.ElementAtOrDefault((int?)c["handle"] ?? 0);
                if(bodyHandle==null) throw new InvalidOperationException("The requested native body handle does not exist");
                var bodyHand=Player.local.creature.GetHand((string?)c["side"]=="right" ? Side.Right : Side.Left);
                // Real VR grabs select the nearest pose/axis at the actual hand,
                // rather than forcing the prefab's default equipment grip pose.
                bodyHand.TryRelease();bodyHand.GrabRelative(bodyHandle);
                return new {held=bodyHand.grabbedHandle==bodyHandle,part=GameMath.PartKey(grabbedPart!),gripPressed=PlayerControl.GetHand(bodyHand.side).gripPressed,
                    teleportToHand=bodyHandle.data.alwaysTeleportToHand,
                    handGrip=new[]{bodyHand.grip.position.x,bodyHand.grip.position.y,bodyHand.grip.position.z},handleGrip=new[]{bodyHand.gripInfo.transform.position.x,bodyHand.gripInfo.transform.position.y,bodyHand.gripInfo.transform.position.z},
                    shoulderDistance=Vector3.Distance(Player.local.GetShoulderCenter(),bodyHand.gripInfo.transform.position),armReach=Player.local.creature.morphology.GetArmCenterToFingerTipLenght()};
            case "mod-options":
                return ModManager.loadedMods.Where(m=>m.fullPath==MultiplayerRuntime.ModFolder).SelectMany(m=>m.modOptions).Select(o=>new {o.name,o.category,o.order,interaction=o.interactionType.ToString(),o.applyOnLoad,o.saveValue,text=(o.parameterValues.FirstOrDefault() as ModOptionString)?.stringValue,ui=o.uiComponent?.GetType().Name,visible=(o.uiComponent as Component)?.gameObject.activeSelf,renderedText=(o.uiComponent as UITextDisplayModOption)?.textObj.text,values=o.parameterValues.Select(v=>v.title).ToArray()}).ToArray();
            case "mod-action":
                var option=ModManager.loadedMods.Where(m=>m.fullPath==MultiplayerRuntime.ModFolder).SelectMany(m=>m.modOptions).Single(o=>o.name==(string?)c["name"]);
                if(c["index"]!=null) option.Apply((int)c["index"]!);
                else if(option.uiComponent is UIButtonList nativeButton) nativeButton.NextValue();
                else throw new InvalidOperationException("Expected an initialized native option button");
                return "native mod option button advanced";
            case "menu":
                if(UIPlayerMenu.instance==null) throw new InvalidOperationException("Native player menu unavailable");
                UIPlayerMenu.instance.ToggleMenu((bool?)c["shown"] ?? true,false);
                if((bool?)c["mods"]==true && UIModsMenu.instance?.MenuToggle!=null) UIModsMenu.instance.MenuToggle.isOn=true;
                return "native menu toggled";
            case "keyboard-input":
                if(runtime.ConnectionKeyboard==null) throw new InvalidOperationException("Open a native connection keyboard first");
                if((bool?)c["clear"]==true) runtime.ConnectionKeyboard.vrkb.ClearText();
                if(c["text"]!=null) runtime.ConnectionKeyboard.SetTypedText((string)c["text"]!);
                if(c["key"]!=null)
                {
                    var key=runtime.ConnectionKeyboard.GetComponentsInChildren<VRKB.KeyBehaviour>().Single(k=>k.keyId==(string)c["key"]!);
                    // Match PointerInputModule's native event path, including
                    // the prefab's serialized handlers, rather than bypassing
                    // VR interaction by directly invoking KeyBehaviour.
                    var pointer=new ThunderRoad.PointerEventData(UnityEngine.EventSystems.EventSystem.current) {current=key.gameObject};
                    var down=UnityEngine.EventSystems.ExecuteEvents.ExecuteHierarchy(key.gameObject,pointer,UnityEngine.EventSystems.ExecuteEvents.pointerDownHandler);
                    var click=UnityEngine.EventSystems.ExecuteEvents.ExecuteHierarchy(key.gameObject,pointer,UnityEngine.EventSystems.ExecuteEvents.pointerClickHandler);
                    var up=UnityEngine.EventSystems.ExecuteEvents.ExecuteHierarchy(key.gameObject,pointer,UnityEngine.EventSystems.ExecuteEvents.pointerUpHandler);
                    if(down==null && click==null && up==null) throw new InvalidOperationException("Native keyboard key has no pointer event receiver: "+key.keyId);
                }
                if((bool?)c["confirm"]==true) runtime.ConnectionKeyboard.OnConfirm.Invoke(runtime.ConnectionKeyboard.vrkb.Text);
                else if((bool?)c["cancel"]==true) runtime.ConnectionKeyboard.OnCancel.Invoke(runtime.ConnectionKeyboard.vrkb.Text);
                return "native keyboard input dispatched";
            case "screenshot":
                var screenshot=Path.Combine(runtime.TestDirectory,"screenshot.png");ScreenCapture.CaptureScreenshot(screenshot);return screenshot;
            case "damage": RequirePlayer(); Player.local.creature.Damage((float?)c["amount"] ?? 10); return "native damage applied";
            case "invincibility": Player.invincibility = (bool?)c["enabled"] ?? false; return new {invincible=Player.IsInvincible};
            case "kill": RequirePlayer(); Player.local.creature.Kill(); return "native kill applied";
            case "slice":
                RequirePlayer(); var part = c["partKey"]!=null ? GameMath.FindPart(Player.local.creature,(int)c["partKey"]!) : Player.local.creature.ragdoll.GetPart((RagdollPart.Type)((int?)c["part"] ?? (int)RagdollPart.Type.LeftHand));
                return new { sliced = part != null && part.TrySlice() };
            case "respawn": RequirePlayer();ResetMockPose();runtime.World?.Match.Respawn(); return "respawn requested";
            case "mode": runtime.World?.Match.SetMode(Enum.Parse<MultiplayerMode>((string?)c["mode"] ?? "FreeForAll", true)); return "mode set";
            case "wave":
                for(int repeat=0;repeat<Mathf.Clamp((int?)c["repeat"] ?? 1,1,4);repeat++) runtime.World?.Match.StartWave();
                return "wave requested";
            case "engage-npc":
                if((bool?)c["enabled"] == false) { engagementTarget=0;return Engagement; }
                RequirePlayer();
                if(runtime.World == null || !ulong.TryParse((string?)c["entity"],out var engagementId) || !runtime.World.Entities.TryGetValue(engagementId,out var engaged) || engaged.Descriptor.Kind!=EntityKind.Creature || engaged.Creature==null) throw new InvalidOperationException("Expected a network NPC for scripted melee positioning");
                if(engagementTarget!=engagementId) engagementMoves=0;
                engagementTarget=engagementId;engagementDistance=Mathf.Clamp((float?)c["distance"] ?? 2,.5f,4);
                return Engagement;
            case "npc-teleport": case "npc-damage": case "npc-kill": case "npc-despawn": case "npc-perception": case "npc-ai":
                if(runtime.World == null || !ulong.TryParse((string?)c["entity"],out var npcId) || !runtime.World.Entities.TryGetValue(npcId,out var npc) || !npc.Owned || npc.Descriptor.Kind != EntityKind.Creature || npc.Creature == null) throw new InvalidOperationException("Expected a locally owned native NPC");
                if(op == "npc-teleport") npc.Creature.Teleport(Vector(c["position"]!),Quaternion.Euler(0,(float?)c["yaw"] ?? 0,0));
                else if(op == "npc-damage") npc.Creature.Damage((float?)c["amount"] ?? 5);
                else if(op == "npc-kill") npc.Creature.Kill();
                else if(op == "npc-perception") return runtime.World.Entities.Values.Where(e=>e.Descriptor.Kind==EntityKind.Player && e.Creature!=null).Select(e=>new {
                    id=e.Descriptor.Id.ToString(),loaded=e.Creature!.loaded,state=e.Creature.state.ToString(),
                    enemy=npc.Creature.IsEnemy(e.Creature,true),visible=npc.Creature.brain.CanSight(e.Creature),
                    velocity=new[]{e.Creature.currentLocomotion.physicBody.velocity.x,e.Creature.currentLocomotion.physicBody.velocity.y,e.Creature.currentLocomotion.physicBody.velocity.z},
                    rootKinematic=e.Creature.currentLocomotion.physicBody.isKinematic,
                    inFov=npc.Creature.brain.instance.GetModule<BrainModuleDetection>(false)?.CheckTransformInFOV(e.Creature.centerEyes,false,e.Creature.detectionFOVModifier),
                    inActiveList=Creature.allActive.Contains(e.Creature),faction=e.Creature.factionId
                }).ToArray();
                else if(op == "npc-ai")
                {
                    if((bool?)c["enabled"] ?? false)
                    {
                        if(npc.Creature.brain.instance!=null) npc.Creature.brain.instance.Start();
                        else npc.Creature.brain.Load(npc.Creature.data.brainId);
                        if(((bool?)c["holdPosition"] ?? false) && npc.Creature.brain.instance?.GetModule<BrainModuleMove>(false) is { } move) {move.StopMove();move.allowMove=false;}
                    }
                    else npc.Creature.brain.Stop();
                }
                else npc.Creature.Despawn();
                return "native NPC action applied";
            case "camera":
                if (Spectator.local == null) throw new InvalidOperationException("Native spectator camera unavailable");
                Spectator.local.SetViewFree();
                if(c["position"]!=null)Spectator.local.transform.position=Vector(c["position"]!);
                if(c["rotation"]!=null)Spectator.local.transform.rotation=Quaternion.Euler(Vector(c["rotation"]!));
                if(c["fov"]!=null)Spectator.local.cam.fieldOfView=(float)c["fov"]!;
                return "native free spectator enabled";
            case "item-velocity":
                if (runtime.World == null || !ulong.TryParse((string?)c["entity"], out var entityId) || !runtime.World.Entities.TryGetValue(entityId, out var itemEntity) || !itemEntity.Owned || itemEntity.Item == null) throw new InvalidOperationException("Expected a locally owned item");
                itemEntity.Item.physicBody.isKinematic = false; itemEntity.Item.physicBody.velocity = Vector(c["velocity"]!); return "native item velocity set";
            case "break-item":
                if(runtime.World == null || !ulong.TryParse((string?)c["entity"],out var brokenId) || !runtime.World.Entities.TryGetValue(brokenId,out var broken) || broken.Item?.breakable==null) throw new InvalidOperationException("Expected a native breakable item");
                broken.Item.breakable.Break();return new {broken=broken.Item.breakable.IsBroken};
            case "despawn-item":
                if(runtime.World == null || !ulong.TryParse((string?)c["entity"],out var despawnId) || !runtime.World.Entities.TryGetValue(despawnId,out var despawned) || !despawned.Owned || despawned.Item==null) throw new InvalidOperationException("Expected a locally owned native item");
                despawned.Item.Despawn();return "native item despawn requested";
            case "observe-item":
                if(runtime.World == null || !ulong.TryParse((string?)c["entity"],out var observedId) || !runtime.World.Entities.TryGetValue(observedId,out var watched) || watched.Item == null) throw new InvalidOperationException("Expected a replicated item");
                observedItem=observedId;observedFrames.Clear();return "native item observation started";
            case "quit": Respond(response, true, "quitting"); Application.Quit(); return null;
            default: throw new InvalidDataException("Unknown command: " + op);
        }
    }
    private static void Respond(string path, bool success, object result)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonConvert.SerializeObject(new { success, result }, Formatting.Indented));
        if (File.Exists(path)) return;
        File.Move(temp, path);
    }
    private static void RequirePlayer() { if (Player.local?.creature == null) throw new InvalidOperationException("Physical player not ready"); }
    private static Vector3 Vector(JToken value)
    {
        var array = value.ToObject<float[]>()!;
        if (array.Length != 3 || array.Any(v => float.IsNaN(v) || float.IsInfinity(v) || Mathf.Abs(v) > 10000)) throw new InvalidDataException("Invalid pose vector");
        return new Vector3(array[0], array[1], array[2]);
    }
    private void BeginPosing()
    {
        if (posing) return;
        var origin=Player.local.transform;
        // Match an XR origin: native locomotion, teleports and respawns carry
        // tracked local poses. Keeping them in world space fights root movement.
        posing = true; left = origin.InverseTransformPoint(Player.local.handLeft.transform.position); right = origin.InverseTransformPoint(Player.local.handRight.transform.position); head = origin.InverseTransformPoint(Player.local.head.transform.position);
        var rotation=Quaternion.Inverse(origin.rotation);
        leftRotation = rotation*Player.local.handLeft.transform.rotation; rightRotation = rotation*Player.local.handRight.transform.rotation; headRotation = rotation*Player.local.head.transform.rotation;
    }
    private static Vector3 LocalPosition(JToken value) => Player.local.transform.InverseTransformPoint(Vector(value));
    private static Quaternion LocalRotation(JToken value) => Quaternion.Inverse(Player.local.transform.rotation)*Quaternion.Euler(Vector(value));
    private void CheckMockBody()
    {
        var body=Player.local?.creature;
        if(!configured || body==null || body==lastMockBody) return;
        lastMockBody=body;ResetMockPose();mockBodyResets++;
    }
    private void ResetMockPose()
    {
        posing=true;motionDuration=0;motionStart=0;engagementTarget=0;
        head=new Vector3(0,1.75f,0);left=new Vector3(-.3f,1.25f,.25f);right=new Vector3(.3f,1.25f,.25f);
        headRotation=leftRotation=rightRotation=Quaternion.identity;
        foreach(var side in new[]{Side.Left,Side.Right})
        {
            PlayerControl.GetHand(side).InvokeButtonEvent(PlayerControl.Hand.Button.Grip,false);
            PlayerControl.GetHand(side).InvokeButtonEvent(PlayerControl.Hand.Button.Use,false);
        }
        var c=Player.local.creature;
        foreach(var part in c.ragdoll.parts)
            if(!part.physicBody.isKinematic) {part.physicBody.velocity=Vector3.zero;part.physicBody.angularVelocity=Vector3.zero;}
        if(!c.currentLocomotion.physicBody.isKinematic) {c.currentLocomotion.physicBody.velocity=Vector3.zero;c.currentLocomotion.physicBody.angularVelocity=Vector3.zero;}
        ApplyMockPose(true);
    }
    private void LateUpdate()
    {
        CheckMockBody();
        if (!posing || Player.local?.creature == null) return;
        UpdateEngagement();
        if (motionDuration > 0)
        {
            float t = Mathf.Clamp01((Time.realtimeSinceStartup - motionStart) / motionDuration);
            var p = Vector3.Lerp(motionFrom, motionTo, t); var q = Quaternion.Slerp(motionRotationFrom, motionRotationTo, t);
            if(motionArc) p=motionPivot+q*Quaternion.Inverse(motionRotationFrom)*(motionFrom-motionPivot);
            if (motionSide == Side.Left) { left = p; leftRotation = q; } else { right = p; rightRotation = q; }
            if(motionBoth)
            {
                var delta=q*Quaternion.Inverse(motionRotationFrom);
                var other=p+delta*(motionOtherFrom-motionFrom);
                var rotation=delta*motionOtherRotation;
                if(motionSide==Side.Left) {right=other;rightRotation=rotation;} else {left=other;leftRotation=rotation;}
            }
            if (t >= 1) motionDuration = 0;
        }
        ApplyMockPose();
    }
    private void ApplyMockPose(bool snapBody=false)
    {
        Player.local.handLeft.SetTracking(false); Player.local.handRight.SetTracking(false);
        var origin=Player.local.transform;
        Player.local.head.transform.SetPositionAndRotation(origin.TransformPoint(head), origin.rotation*headRotation);
        Player.local.handLeft.transform.SetPositionAndRotation(origin.TransformPoint(left), origin.rotation*leftRotation);
        Player.local.handRight.transform.SetPositionAndRotation(origin.TransformPoint(right), origin.rotation*rightRotation);
        var controlled=Player.local.creature;
        if(snapBody && controlled.handLeft.grabbedHandle==null && controlled.handRight.grabbedHandle==null && controlled.ragdoll.handlers.Count==0)
        {
            // Fixture setup is a discontinuity, unlike a combat trajectory. Reset
            // native IK's cached root/footstep state instead of blending from the
            // preceding test's body orientation and leg placement.
            var creature=Player.local.creature;
            var facing=Vector3.ProjectOnPlane(Player.local.head.transform.forward,Vector3.up);
            if(facing.sqrMagnitude>.001f) creature.transform.rotation=Quaternion.LookRotation(facing,Vector3.up);
            if(creature.ragdoll.ik is IKControllerFIK ik && ik.vrik!=null && ik.vrik.solver.initiated)
            {
                ik.vrik.solver.FixTransforms();ik.vrik.solver.Reset();ik.vrik.solver.Update();
                foreach(var part in creature.ragdoll.parts)
                    if(part.physicBody.isKinematic && part.bone?.animation!=null)
                    {
                        part.transform.SetPositionAndRotation(part.bone.animation.position,part.bone.animation.rotation);
                        part.physicBody.rigidBody.position=part.transform.position;part.physicBody.rigidBody.rotation=part.transform.rotation;
                    }
                Physics.SyncTransforms();
            }
        }
    }
    private void UpdateEngagement()
    {
        if(engagementTarget==0 || runtime.World==null || !runtime.World.Entities.TryGetValue(engagementTarget,out var enemy) || enemy.Creature==null || enemy.Creature.isKilled || Player.local.creature.isKilled) return;
        // Script only the mock fighter's positioning. Hold still throughout the
        // enemy's native wind-up/attack so the animation can actually reach it.
        if(enemy.Creature.brain.isAttacking || enemy.Creature.brain.canDamage) return;
        float distance=engagementDistance;
        if(enemy.Owned && enemy.Creature.brain.instance?.GetModule<BrainModuleMelee>(false) is { } melee)
        {
            melee.GetMeleeRanges(out float min,out float max,true);
            if(Protocol.Finite(min) && Protocol.Finite(max) && min>=.2f && max>min && max<=5) distance=(min+max)*.5f;
        }
        var actor=Player.local.creature.transform.position;
        var offset=actor-enemy.Creature.transform.position;offset.y=0;
        if(offset.sqrMagnitude<.001f || Mathf.Abs(offset.magnitude-distance)<.008f) return;
        var correction=enemy.Creature.transform.position+offset.normalized*distance-actor;correction.y=0;
        var destination=Player.local.transform.position+correction;
        if(!UnityEngine.AI.NavMesh.SamplePosition(destination,out var floor,.5f,UnityEngine.AI.NavMesh.AllAreas)) return;
        // Do not lift the mock player on every adjustment: it must finish a
        // native physics step grounded for the AI's ordinary attack predicate.
        float height=Player.local.transform.position.y;
        destination=floor.position;destination.y=height;
        if(height<floor.position.y-.2f || height>floor.position.y+.5f) destination.y=floor.position.y;
        Player.local.Teleport(destination,Player.local.transform.rotation,true,false);engagementMoves++;
    }
}

public sealed partial class MultiplayerRuntime
{
    internal object GetStatus()
    {
        if(LevelManager.isLoadingLocked)
            return new {game=GameSettings.GetVersionString(),level=Level.current?.data?.id,creature=false,scene=World?.SceneSnapshot(),processId=System.Diagnostics.Process.GetCurrentProcess().Id,time=Time.realtimeSinceStartup,connected=Session?.Connected ?? false,peers=Session?.Peers.Count ?? 0,peer=Session?.LocalId ?? 0,modBuild=typeof(MultiplayerRuntime).Assembly.ManifestModule.ModuleVersionId.ToString("N"),coreBuild=typeof(NetworkSession).Assembly.ManifestModule.ModuleVersionId.ToString("N")};
        static float[] Position(Vector3 p) => new[] { p.x, p.y, p.z };
        static object? GripDrive(RagdollHand? hand)
        {
            var joint=hand?.gripInfo?.joint; if (joint == null) return null;
            return new {positionSpring=joint.xDrive.positionSpring,positionDamper=joint.xDrive.positionDamper,positionMax=joint.xDrive.maximumForce,slerpSpring=joint.slerpDrive.positionSpring,xSpring=joint.angularXDrive.positionSpring,yzSpring=joint.angularYZDrive.positionSpring,anchor=Position(joint.connectedAnchor),bodyAnchor=Position(joint.anchor),controllerPosition=Position(joint.transform.position),controllerRotation=Position(joint.transform.rotation.eulerAngles),itemAnchorWorld=joint.connectedBody != null ? Position(joint.connectedBody.transform.TransformPoint(joint.connectedAnchor)) : null,
                handGrip=Position(hand!.grip.position),handGripRotation=Position(hand.grip.rotation.eulerAngles),handleGrip=Position(hand.gripInfo!.transform.position),handleGripRotation=Position(hand.gripInfo.transform.rotation.eulerAngles),
                gripSeparation=Vector3.Distance(hand.grip.position,hand.gripInfo.transform.position),gripAngle=Quaternion.Angle(hand.grip.rotation,hand.gripInfo.transform.rotation),
                physicalGripLocal=Position(hand.grip.localPosition),physicalGripLocalRotation=Position(hand.grip.localRotation.eulerAngles),gripType=hand.gripInfo.type.ToString()};
        }
        static object? MeleeRange(Creature creature)
        {
            var melee=creature.brain.instance?.GetModule<BrainModuleMelee>(false);
            if(melee==null) return null;
            melee.GetMeleeRanges(out float min,out float max,true);
            return new {min,max,distance=creature.brain.currentTarget != null ? (float?)creature.brain.GetHorizontalDistance(creature.brain.currentTarget.transform) : null,scale=Position(creature.transform.lossyScale),reach=melee.GetWeaponReach(),maxDelta=melee.maxRangeDelta,idle=melee.meleeIdle?.id,motions=melee.meleeIdle?.attackMotions?.Select(m=>new {m.id,m.weight,m.skipChance,m.minRange,m.maxRange,m.includeWeaponReach,reach=melee.GetWeaponReach(m.attackSide)}).ToArray()};
        }
        var activeItems = Item.allActive.Where(i => i != null && i.loaded && i.data != null).ToArray();
        var skippedEntities = new List<string>();
        return new
        {
            game = GameSettings.GetVersionString(), level = Level.current?.data?.id, player = Player.local != null, creature = Player.local?.creature != null,
            scene=World?.SceneSnapshot(),
            nativeWaves=World?.NativeWaveSnapshot(),
            npcReplication=World!=null ? new {effectsSent=World.NpcEffectsSent,effectsReceived=World.NpcEffectsReceived,magicHits=World.NpcMagicHits,projectilesSent=World.NpcProjectilesSent,projectilesReceived=World.NpcProjectilesReceived} : null,
            localInventory=Player.local?.creature?.container.contents.Select(c=>new {c.referenceID,type=c.GetType().Name,state=(c as ItemContent)?.state?.GetType().Name,quantity=(c as ItemContent)?.quantity ?? 1}).ToArray(),
            localHolders=Player.local?.creature?.holders.Select(h=>new {h.name,items=h.items.Select(i=>i.data.id).ToArray()}).ToArray(),
            character=Player.characterData?.ID,
            modBuild = typeof(MultiplayerRuntime).Assembly.ManifestModule.ModuleVersionId.ToString("N"),
            coreBuild = typeof(BladeAndSorcery.Multiplayer.Core.NetworkSession).Assembly.ManifestModule.ModuleVersionId.ToString("N"),
            shoulder = Player.local?.creature != null ? Position(Player.local.GetShoulderCenter()) : null,
            armReach = Player.local?.creature?.morphology.GetArmCenterToFingerTipLenght(),
            networkControllers = GetComponentsInChildren<Rigidbody>().Length,
            gameplayPatches = harmony != null,
            automation=GetComponent<AutomatedDriver>()?.PoseState,
            nativeSpawnStarts = testSpawnStarts, nativeSpawnEnds = testSpawnEnds,
            processId = System.Diagnostics.Process.GetCurrentProcess().Id,
            xr = PlayerControl.loader.ToString(), time = Time.realtimeSinceStartup, connected = Session?.Connected ?? false,
            frameDelta=Time.unscaledDeltaTime,simulationDelta=Time.deltaTime,maximumDelta=Time.maximumDeltaTime,
            eyeTextureScale=UnityEngine.XR.XRSettings.eyeTextureResolutionScale,
            timeScale = Time.timeScale, fixedDeltaTime = Time.fixedDeltaTime, fixedTime = Time.fixedTimeAsDouble,
            slowMotionState = TimeManager.slowMotionState.ToString(), timeStopped = TimeManager.timeStopped, paused = TimeManager.isPaused,
            locomotionEnabled = Player.local?.locomotion?.enabled,
            endGameMenu = ThunderBehaviourSingleton<UIEndGameMenu>.HasInstance && ThunderBehaviourSingleton<UIEndGameMenu>.Instance.gameObject.activeInHierarchy,
            playerMenuBlocked = UIPlayerMenu.instance != null && UIPlayerMenu.instance.IsOpeningBlocked,
            connectionEditor = new {field=ConnectionEditField,loading=ConnectionKeyboardLoading,visible=ConnectionKeyboard!=null && ConnectionKeyboard.gameObject.activeInHierarchy,text=ConnectionKeyboard?.vrkb.PasswordMode==true ? "<hidden>" : ConnectionKeyboard?.vrkb.Text,passwordMode=ConnectionKeyboard?.vrkb.PasswordMode,menuShown=UIPlayerMenu.instance?.IsShownToPlayer,
                keys=ConnectionKeyboard?.GetComponentsInChildren<VRKB.KeyBehaviour>().Select(k=>new {id=k.keyId,label=k.Config.Labels.FirstOrDefault(),action=k.Config.Action.Type.ToString(),arg=k.Config.Action.Arg}).ToArray()},
            connectionSettings = new {address=Configuration.Address,port=Configuration.Port,name=Configuration.PlayerName,passwordSet=Configuration.Password.Length>0},
            peer = Session?.LocalId ?? 0, peers = Session?.Peers.Count ?? 0, sent = Session?.SentBytes ?? 0, received = Session?.ReceivedBytes ?? 0,
            rejected = Session?.RejectedPackets ?? 0, remote = World?.RemoteCount ?? 0, contacts = World?.ContactCount ?? 0, weaponContacts = World?.WeaponContacts ?? 0,
            actions = World?.AppliedActions ?? 0, snapshotsSent = World?.SentSnapshots ?? 0, snapshotsReceived = World?.ReceivedSnapshots ?? 0,
            root = Player.local != null ? Position(Player.local.transform.position) : null,
            originRotation = Player.local != null ? Position(Player.local.transform.rotation.eulerAngles) : null,
            head = Player.local?.head != null ? Position(Player.local.head.transform.position) : null,
            left = Player.local?.handLeft != null ? Position(Player.local.handLeft.transform.position) : null,
            right = Player.local?.handRight != null ? Position(Player.local.handRight.transform.position) : null,
            leftItem = Player.local?.creature?.handLeft?.grabbedHandle?.item?.data?.id,
            rightItem = Player.local?.creature?.handRight?.grabbedHandle?.item?.data?.id,
            bodyGrips = Player.local?.creature!=null ? new {left=Player.local.creature.handLeft.grabbedHandle?.name,right=Player.local.creature.handRight.grabbedHandle?.name,leftPressed=PlayerControl.GetHand(Side.Left).gripPressed,rightPressed=PlayerControl.GetHand(Side.Right).gripPressed} : null,
            localHealth = Player.local?.creature?.currentHealth,
            match = World != null ? new { mode=World.Match.Mode.ToString(), wave=World.Match.Wave, pendingWaveSpawns=World.Match.PendingWaveSpawns, friendlyFire=World.Match.FriendlyFire, surviveLimbLoss=World.Match.SurviveLimbLoss,pvpWeaponDamage=World.Match.PvpWeaponDamage.ToString(), scores=World.Match.Scores.Select(s => new {peer=s.Key,kills=s.Value.Kills,deaths=s.Value.Deaths,team=s.Value.Team}).ToArray() } : null,
            invincible = Player.IsInvincible,
            manualInvincibility = Player.invincibility,
            itemObservation = GetComponent<AutomatedDriver>()?.ItemObservation,
            engagement = GetComponent<AutomatedDriver>()?.Engagement,
            localMaxHealth = Player.local?.creature?.maxHealth,
            cachedGrips = World?.ActiveGrips.Select(g=>new {entity=g.Entity.ToString(),other=g.Other.ToString(),side=g.Side}).ToArray(),
            brokenRoots = World?.BrokenRootDescriptors.Select(d=>new {id=d.Id.ToString(),owner=d.Owner,epoch=d.Epoch,catalog=d.CatalogId}).ToArray(),
            bodyContactsTrace = World?.BodyContactTrace.Select(t => new {time=t.Time,item=t.Item.ToString(),victim=t.Victim.ToString(),part=t.Part,speed=t.Speed}).ToArray(),
            damageTrace = World?.DamageTrace.Select(t => new {time=t.Time,victim=t.Victim.ToString(),damage=t.Damage,allowed=t.Allowed,damager=t.Damager,part=t.Part,speed=t.Speed,point=Position(t.Point)}).ToArray(),
            sliceTrace = World?.SliceTrace.Select(t => new {time=t.Time,victim=t.Victim.ToString(),part=t.Part,speed=t.Speed,sliced=t.Sliced}).ToArray(),
            nativeItems = activeItems.Length,
            unregisteredItems = World != null ? activeItems.Count(i => World.Find(i) == null) : activeItems.Length,
            unregistered = World != null ? activeItems.Where(i => World.Find(i) == null).Select(i => new { nativeId=i.GetInstanceID(), catalog=i.data.id, position=Position(i.transform.position), sceneKey=GameMath.SceneItemKey(i) }).ToArray() : null,
            contactsTrace = World?.ContactTrace.Select(t => new {time=t.Time,a=t.A.ToString(),b=t.B.ToString(),speed=t.Speed,impulse=t.Impulse,gripError=t.GripError}).ToArray(),
            physicsWeaponContacts = World?.PhysicsWeaponContacts ?? 0,
            physicsContactsTrace = World?.PhysicsContactTrace.Select(t => new { time=t.Time,a=t.A.ToString(),b=t.B.ToString(),impulse=t.Impulse,gripForce=t.GripForce }).ToArray(),
            skippedEntities,
            entities = World?.Entities.Values.Where(e=>e.Transform != null).Select(e => {
                try { return new {
                id = e.Descriptor.Id.ToString(), owner = e.Descriptor.Owner, epoch = e.Descriptor.Epoch,
                nativeId = e.Item?.GetInstanceID() ?? e.Creature?.GetInstanceID(),
                kind = e.Descriptor.Kind.ToString(), catalog = e.Descriptor.CatalogId,
                sceneKey = e.Descriptor.SceneKey,
                position = e.Transform != null ? Position(e.Transform.position) : null,
                health = e.Creature != null ? (float?)e.Creature.currentHealth : null,
                height=e.Creature?.GetHeight(),baseHeight=e.Creature?.morphology.height,scale=e.Creature!=null ? Position(e.Creature.transform.lossyScale) : null,ragdollState=e.Creature?.ragdoll.state.ToString(),
                dead = e.Creature?.isKilled, bodies = e.Creature?.ragdoll.parts.Count,
                nativeWaveMember=e.Creature!=null && World.NativeWaveMember(e.Creature),
                initialPoseSnapped=e.InitialPoseSnapped,
                proceduralNpc=e.Descriptor.ProceduralNpc,
                poseRevision=e.PoseRevision,teleportSnaps=e.TeleportSnaps,teleportAudit=e.LastTeleportAudit,
                ai = e.Creature != null ? new {
                    enabled=e.Creature.brain.enabled, active=e.Creature.brain.instance?.isActive,
                    brain=e.Creature.brain.instance?.id, state=e.Creature.brain.state.ToString(),
                    faction=e.Creature.factionId, grounded=e.Creature.currentLocomotion.isGrounded, sourceAlive=e.SourceAlive,
                    target=World.Find(e.Creature.brain.currentTarget)?.Descriptor.Id.ToString(),
                    canDamage=e.Creature.brain.canDamage, attacking=e.Creature.brain.isAttacking,
                    attacks=e.Creature.brain.instance?.GetModule<BrainModuleMelee>(false)?.attackCount,
                    spells=new[]{e.Creature.handRight.caster,e.Creature.handLeft.caster}.Select(c=>new{spell=c.spellInstance?.id,c.isFiring,c.isSpraying}).ToArray(),
                    meleeRange=MeleeRange(e.Creature),
                    evaluation=World.AiEvaluations.TryGetValue(e.Descriptor.Id,out var evaluation) ? evaluation : null,
                    sightable=e.Creature.brain.instance?.GetModule<BrainModuleSightable>(false) != null
                } : null,
                appearanceUpdating = e.AppearanceUpdating, pendingSlices = e.PendingRemoteSlices.Count, apparelPending = e.Creature?.equipment.GetPendingApparelLoading(),
                held = e.Item?.handlers.Count, physics = e.Item != null ? (bool?)!e.Item.physicBody.isKinematic : e.Creature?.ragdoll.IsPhysicsEnabled()
                ,lastHandler=World.Find(e.Item?.lastHandler?.creature)?.Descriptor.Id.ToString()
                ,holder=e.Item?.holder?.name,holderActor=World.Find(e.Item?.holder?.GetRootHolder().creature)?.Descriptor.Id.ToString()
                ,npcProjectile=e.Item!=null && e.Descriptor.Appearance.StartsWith("{\"Kind\":\"npc-projectile\"",StringComparison.Ordinal)
                ,penetrating = e.Item?.isPenetrating
                ,penetrations = e.Item?.collisionHandlers.SelectMany(h=>h.collisions).Where(hit=>hit.active && hit.damageStruct.penetrationJoint!=null).Select(hit=>new {target=(World.Find(hit.damageStruct.hitRagdollPart?.ragdoll.creature) ?? World.Find(hit.damageStruct.hitItem))?.Descriptor.Id.ToString(),part=hit.damageStruct.hitRagdollPart!=null ? GameMath.PartKey(hit.damageStruct.hitRagdollPart) : 0,mode=hit.damageStruct.penetration.ToString(),damager=hit.damageStruct.damager?.data?.id}).ToArray()
                ,breakable = e.Item != null && e.Item.breakable != null ? new {broken=e.Item.breakable.IsBroken,health=e.Item.breakable.momentumHealth,pieces=e.Item.breakable.subBrokenItems.Count,otherBodies=e.Item.breakable.subBrokenBodies.Count} : null
                ,holster = e.Item?.holder?.name
                ,snapshotAge = e.Owned ? 0 : Time.realtimeSinceStartupAsDouble - e.LastArrival
                ,parryPoint = e.Item?.parryPoint != null ? Position(e.Item.parryPoint.position) : null
                ,rotation = e.Transform != null ? Position(e.Transform.rotation.eulerAngles) : null
                ,rightHand = e.Creature != null ? Position(e.Creature.handRight.transform.position) : null
                ,leftHand = e.Creature != null ? Position(e.Creature.handLeft.transform.position) : null
                ,rightHeld = e.Creature?.handRight.grabbedHandle?.item != null ? World.Find(e.Creature.handRight.grabbedHandle.item)?.Descriptor.Id.ToString() : null
                ,leftHeld = e.Creature?.handLeft.grabbedHandle?.item != null ? World.Find(e.Creature.handLeft.grabbedHandle.item)?.Descriptor.Id.ToString() : null
                ,rightDrive = GripDrive(e.Creature?.handRight)
                ,leftDrive = GripDrive(e.Creature?.handLeft)
                ,handles = e.Item?.handles.Select((h,i)=>(Handle:h,Index:i)).Where(v=>v.Handle != null && v.Handle.transform != null).Select(v=>new {index=v.Index,axisLength=v.Handle.axisLength,defaultAxis=v.Handle.GetDefaultAxisLocalPosition(),position=Position(v.Handle.transform.position),up=Position(v.Handle.transform.up)}).ToArray()
                ,damagers = e.Item?.collisionHandlers.Where(h=>h!=null).SelectMany(h=>h.damagers).Where(d=>d!=null && d.transform!=null).Distinct().Select(d=>new {id=d.data?.id,type=d.type.ToString(),position=Position(d.transform.position),forward=Position(d.transform.forward),up=Position(d.transform.up),canSlice=d.data?.dismembermentAllowed,minSliceVelocity=d.data?.dismembermentMinVelocity,colliders=d.colliderGroup != null ? d.colliderGroup.colliders.Where(c=>c!=null).Select(c=>new {center=Position(c.bounds.center),size=Position(c.bounds.size)}).ToArray() : null}).ToArray()
                ,sliced = e.Creature?.ragdoll.parts.Where(p => p.isSliced).Select(p => (int)p.type).ToArray()
                ,velocity = e.Item != null ? Position(e.Item.physicBody.velocity) : e.Creature != null ? Position(e.Creature.currentLocomotion.physicBody.velocity) : null
                ,angularVelocity = e.Item != null ? Position(e.Item.physicBody.angularVelocity) : e.Creature != null ? Position(e.Creature.currentLocomotion.physicBody.angularVelocity) : null
                ,parts = e.Creature?.ragdoll.parts.Where(p=>p!=null && p.transform!=null && p.meshBone!=null && p.colliderGroup!=null).Select(p => {
                    p.GetSlicePositionAndDirection(out var slicePosition,out var sliceDirection);
                    return new { key=GameMath.PartKey(p), type=p.type.ToString(), typeValue=(int)p.type,sliceChild=p.sliceChildAndDisableSelf!=null ? (int?)p.sliceChildAndDisableSelf.type : null,forceKill=p.data.sliceForceKill,position=Position(p.transform.position), velocity=Position(p.physicBody.velocity), handles=p.handles.Count,isGrabbed=p.isGrabbed,sliceAllowed=p.sliceAllowed, sliceWidth=p.sliceWidth, slicePosition=Position(slicePosition), sliceDirection=Position(sliceDirection), colliders=p.colliderGroup.colliders.Where(c=>c!=null).Select(c => new {enabled=c.enabled,trigger=c.isTrigger,layer=c.gameObject.layer,center=Position(c.bounds.center),size=Position(c.bounds.size)}).ToArray() };
                }).ToArray()
                }; }
                catch(Exception ex) when(ex is MissingReferenceException || ex is NullReferenceException)
                {
                    // Unity destruction can invalidate child handles before the
                    // owning entity's despawn event has removed its registry entry.
                    skippedEntities.Add(e.Descriptor.Id+":"+ex.GetType().Name); return null;
                }
            }).Where(e=>e!=null).ToArray()
        };
    }
}
