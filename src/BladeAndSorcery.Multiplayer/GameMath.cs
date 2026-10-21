using BladeAndSorcery.Multiplayer.Core;
using ThunderRoad;
using System.Linq;
using UnityEngine;
using NVector = System.Numerics.Vector3;
using NQuaternion = System.Numerics.Quaternion;
using Pose = BladeAndSorcery.Multiplayer.Core.Pose;

namespace BladeAndSorcery.Multiplayer;

internal static class GameMath
{
    private sealed class SceneIdentity { internal string Key=""; internal float SpawnTime; internal bool Pending; }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Item,SceneIdentity> sceneIdentities = new System.Runtime.CompilerServices.ConditionalWeakTable<Item,SceneIdentity>();
    internal static void SetFragmentKey(Item item,string key,bool pending)
    {
        var identity=sceneIdentities.GetValue(item,_=>new SceneIdentity());
        identity.Key=key;identity.SpawnTime=item.spawnTime;identity.Pending=pending;
    }
    private static readonly System.Reflection.FieldInfo SpawnerItems = HarmonyLib.AccessTools.Field(typeof(ItemSpawner), "spawnedItems");
    internal static string SceneItemKey(Item item)
    {
        if(sceneIdentities.TryGetValue(item,out var identity))
        {
            if(identity.Pending || identity.SpawnTime == item.spawnTime) return identity.Key;
            sceneIdentities.Remove(item);
        }
        if (item.spawner != null && SpawnerItems?.GetValue(item.spawner) is System.Collections.Generic.List<Item> spawned)
        {
            int index=spawned.IndexOf(item);
            if(index>=0) return "spawner:" + ScenePath(item.spawner.transform) + ":" + index;
        }
        return item.worldAttached ? "attached:" + ScenePath(item.transform) : "";
    }
    internal static string ScenePath(Transform transform)
    {
        var parts = new System.Collections.Generic.List<string>();
        for (var t = transform; t != null; t = t.parent)
        {
            int index=t.GetSiblingIndex();
            if(t.parent==null)
            {
                index=0;
                foreach(var root in t.gameObject.scene.GetRootGameObjects())
                {if(root.transform==t)break;if(root.name==t.name)index++;}
            }
            parts.Add(t.name+"["+index+"]");
        }
        // Unity root sibling indices include unrelated runtime-created objects.
        // Scene-local paths must survive a different player/UI spawn order.
        parts.Reverse(); return transform.gameObject.scene.name+":"+string.Join("/", parts);
    }
    internal static int PartKey(RagdollPart p) => ((int)p.type << 2) | (int)p.section;
    internal static RagdollPart? FindPart(Creature creature, int key) => creature.ragdoll.parts.FirstOrDefault(p => PartKey(p) == key);
    internal static NVector Net(this Vector3 v) => new NVector(v.x, v.y, v.z);
    internal static Vector3 Game(this NVector v) => new Vector3(v.X, v.Y, v.Z);
    internal static NQuaternion Net(this Quaternion q) => new NQuaternion(q.x, q.y, q.z, q.w);
    internal static Quaternion Game(this NQuaternion q) => new Quaternion(q.X, q.Y, q.Z, q.W);
    internal static Pose NetPose(this Transform t) => new Pose(t.position.Net(), t.rotation.Net());
    internal static BodyState Capture(PhysicBody body, int part = 0) => new BodyState { Part = part, Pose = body.transform.NetPose(), Velocity = body.velocity.Net(), AngularVelocity = body.angularVelocity.Net() };
    internal static void Drive(PhysicBody body, BodyState target, float gain = 60f, float force = 160f)
    {
        var rb = body.rigidBody;
        if (!rb) return;
        if (rb.isKinematic)
        {
            rb.MovePosition(target.Pose.Position.Game()); rb.MoveRotation(target.Pose.Rotation.Game()); return;
        }
        var error = target.Pose.Position.Game() - rb.position;
        var acceleration = error * gain * gain * .25f + (target.Velocity.Game() - rb.velocity) * gain;
        if (rb.useGravity) acceleration -= Physics.gravity;
        rb.AddForce(Vector3.ClampMagnitude(acceleration, force), ForceMode.Acceleration);
        var rotationError = target.Pose.Rotation.Game() * Quaternion.Inverse(rb.rotation);
        if (rotationError.w < 0) rotationError = new Quaternion(-rotationError.x, -rotationError.y, -rotationError.z, -rotationError.w);
        rotationError.ToAngleAxis(out float angle, out var axis);
        if (!float.IsNaN(axis.x) && angle > .01f)
        {
            var angular = axis * (angle * Mathf.Deg2Rad * gain * .5f) + target.AngularVelocity.Game();
            rb.AddTorque(Vector3.ClampMagnitude((angular - rb.angularVelocity) * gain, force * 2), ForceMode.Acceleration);
        }
    }
}
