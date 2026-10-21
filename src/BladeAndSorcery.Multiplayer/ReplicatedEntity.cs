using System;
using System.Collections.Generic;
using System.Linq;
using BladeAndSorcery.Multiplayer.Core;
using ThunderRoad;
using UnityEngine;
using Pose = BladeAndSorcery.Multiplayer.Core.Pose;

namespace BladeAndSorcery.Multiplayer;

internal sealed partial class ReplicatedEntity : IDisposable
{
    internal readonly GameWorld World;
    internal readonly EntityDescriptor Descriptor;
    internal readonly SnapshotBuffer Buffer = new SnapshotBuffer();
    internal Creature? Creature;
    internal Item? Item;
    internal uint Sequence;
    internal double LastArrival;
    internal bool SpawnedRemotely;
    internal bool SourceSelfCollision = Player.selfCollision;
    internal bool SourceAlive = true;
    internal bool SourceInvincible;
    internal readonly Dictionary<RagdollPart,RagdollPart> SliceOrigins = new Dictionary<RagdollPart,RagdollPart>();
    internal readonly HashSet<RagdollPart> RemoteSliceTargets = new HashSet<RagdollPart>();
    internal readonly Queue<RagdollPart> PendingRemoteSlices = new Queue<RagdollPart>();
    internal bool AppearanceUpdating;
    internal double NextRemoteSliceAt;
    private bool oldAnimatorEnabled, oldLocomotionEnabled, oldRootMotion;
    private Ragdoll.State oldRagdollState;
    private bool oldDisallowDespawn;
    private bool oldCountTowardsMaxAlive;
    private readonly Dictionary<HandleRagdoll,HandleRagdollData> bodyHandleData = new Dictionary<HandleRagdoll,HandleRagdollData>();
    private Rigidbody? leftController, rightController;
    private bool? sourcePhysicalRagdoll;
    private readonly Dictionary<Side,ConfigurableJoint> physicalHandGrips = new Dictionary<Side,ConfigurableJoint>();
    private readonly List<WeaponCollisionProbe> collisionProbes = new List<WeaponCollisionProbe>();
    private readonly List<Damager> damagers = new List<Damager>();
    private float oldPinPositionSpring, oldPinPositionDamper, oldPinRotationSpring, oldPinRotationDamper, oldMaxPosition, oldMaxRotation;
    internal bool Owned => Descriptor.Owner == World.Session.LocalId;
    internal Transform? Transform => Creature ? Creature!.transform : Item ? Item!.transform : null;
    internal ReplicatedEntity(GameWorld world, EntityDescriptor descriptor) { World = world; Descriptor = descriptor; }
    internal void Bind(Creature creature, bool remote)
    {
        Creature = creature; SpawnedRemotely = remote;
        if(Descriptor.Kind==EntityKind.Player)
            foreach(var handle in creature.ragdoll.parts.SelectMany(p=>p.handles))
            {
                bodyHandleData[handle]=handle.handleRagdollData;
                var data=(HandleRagdollData)handle.handleRagdollData.Clone();
                // Human tracking already supplies IK targets. NPC grapple IK
                // replaces those targets and clears them on release.
                data.useIK=false;data.moveStep=false;
                handle.handleRagdollData=data;
            }
        if (remote) NextRemoteSliceAt = World.Now + .1;
        creature.OnKillEvent += OnKilled; creature.OnHealEvent += OnHealed; creature.OnDamageEvent += OnDamaged;
        creature.OnResurrectEvent += OnResurrected; creature.OnDespawnEvent += OnCreatureDespawn;
        creature.ragdoll.OnSliceEvent += OnSlice;
        creature.equipment.onCreaturePartChanged += OnAppearanceChanged;
        creature.handLeft.OnGrabEvent += OnHandGrab; creature.handRight.OnGrabEvent += OnHandGrab;
        creature.handLeft.OnUnGrabEvent += OnHandRelease; creature.handRight.OnUnGrabEvent += OnHandRelease;
        if (remote) ConfigureRemoteCreature();
    }
    internal void Bind(Item item, bool remote)
    {
        Item = item; SpawnedRemotely = remote;
        oldDisallowDespawn = item.DisallowDespawn; RefreshItemLifetime();
        World.PrepareBreakable(this);
        item.OnDespawnEvent += OnItemDespawn;
        item.OnSnapEvent += OnSnap; item.OnUnSnapEvent += OnUnsnap;
        foreach (var handler in item.collisionHandlers) handler.OnCollisionStartEvent += OnCollision;
        foreach (var damager in item.collisionHandlers.SelectMany(h => h.damagers).Distinct())
        {
            damagers.Add(damager); damager.OnPenetrateEvent += OnPenetrated; damager.OnUnpenetrateEvent += OnUnpenetrated;
        }
        if (!string.IsNullOrEmpty(MultiplayerRuntime.Instance?.TestDirectory))
            foreach (var handler in item.collisionHandlers)
            {
                var probe = handler.gameObject.AddComponent<WeaponCollisionProbe>(); probe.Entity = this; collisionProbes.Add(probe);
            }
    }
    private void ConfigureRemoteCreature()
    {
        var c = Creature!;
        oldCountTowardsMaxAlive=c.countTowardsMaxAlive;
        oldAnimatorEnabled = c.animator.enabled; oldRootMotion = c.animator.applyRootMotion;
        oldLocomotionEnabled = c.locomotion.enabled; oldRagdollState = c.ragdoll.state;
        oldPinPositionSpring = c.ragdoll.springPositionForce; oldPinPositionDamper = c.ragdoll.damperPositionForce;
        oldPinRotationSpring = c.ragdoll.springRotationForce; oldPinRotationDamper = c.ragdoll.damperRotationForce;
        oldMaxPosition = c.ragdoll.maxPositionForce; oldMaxRotation = c.ragdoll.maxRotationForce;
        c.brain.Stop(); c.brain.enabled = false;
        // Damager checks this flag for any creature without a local Player. A
        // stopped AI must not suppress collisions from a human-controlled replica.
        c.brain.canDamage = Descriptor.Kind == EntityKind.Player;
        if (c.brain.navMeshAgent) c.brain.navMeshAgent.enabled = false;
        c.ragdoll.forcePhysic.Add(this);
        c.ragdoll.SetState(Ragdoll.State.Inert);
        c.animator.applyRootMotion = false; c.animator.enabled = false;
        c.ragdoll.ik.enabled = false;
        c.locomotion.enabled = false;
        c.ragdoll.SetPinForce(0, 0, 0, 0, 0, 0);
        foreach (var part in c.ragdoll.parts)
        {
            part.physicBody.rigidBody.interpolation = RigidbodyInterpolation.Interpolate;
            part.physicBody.rigidBody.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        }
        if (Descriptor.Kind == EntityKind.Player)
        {
            c.countTowardsMaxAlive=false;
            leftController = CreateController(c.handLeft); rightController = CreateController(c.handRight);
            PrepareTrackedSkeleton();
        }
    }
    private Rigidbody CreateController(RagdollHand hand)
    {
        var target = new GameObject("NetworkController." + Descriptor.Id + "." + hand.side);
        target.transform.SetParent(MultiplayerRuntime.Instance!.transform, true);
        target.transform.SetPositionAndRotation(hand.grip.position, hand.grip.rotation);
        var body = target.AddComponent<Rigidbody>(); body.isKinematic = true; body.useGravity = false;
        return body;
    }
    internal Pose HandTarget(Side side)
    {
        var hand=Creature!.GetHand(side);
        if (Owned && hand.playerHand != null) return hand.playerHand.grip.transform.NetPose();
        var state=Buffer.Sample(World.Now);
        return state != null ? (side == Side.Left ? state.LeftHand : state.RightHand) : hand.grip.NetPose();
    }
    internal void AttachNetworkGrip(RagdollHand hand,Pose target)
    {
        if (IsDetachedPlayerPart(hand)) return;
        var controller = hand.side == Side.Left ? leftController : rightController;
        var handle = hand.grabbedHandle;
        if (controller == null || handle == null || hand.gripInfo == null) return;
        var old = hand.gripInfo.joint;
        var connected = old != null ? old.connectedBody : (handle.customPhysicBody ?? handle.physicBody).rigidBody;
        if (connected == null) return;
        var anchor = old != null ? old.connectedAnchor : connected.transform.InverseTransformPoint(hand.gripInfo.transform.position);
        ClearPhysicalHandGrip(hand.side);
        // The controller drives the weapon. A passive native hand constraint makes
        // the visible physical hand follow that weapon, just as the owner's IK does.
        // Do not also drive this held hand toward an independent pose snapshot.
        if (handle.item != null)
        {
            if (old != null) physicalHandGrips[hand.side] = old;
            else
            {
                var handBody=hand.physicBody.rigidBody;
                var handPosition=hand.transform.position;var handRotation=hand.transform.rotation;
                var gripRotation=hand.gripInfo.transform.rotation*Quaternion.Inverse(hand.grip.rotation)*handRotation;
                var gripPosition=hand.gripInfo.transform.position+gripRotation*Quaternion.Inverse(handRotation)*(handPosition-hand.grip.position);
                hand.transform.SetPositionAndRotation(gripPosition,gripRotation);
                handBody.position=gripPosition;handBody.rotation=gripRotation;
                var passive=hand.gameObject.AddComponent<ConfigurableJoint>();
                passive.autoConfigureConnectedAnchor=false;passive.anchor=hand.transform.InverseTransformPoint(hand.grip.position);
                passive.connectedBody=connected;passive.connectedAnchor=anchor;
                passive.xMotion=passive.yMotion=passive.zMotion=ConfigurableJointMotion.Locked;
                passive.angularXMotion=passive.angularYMotion=passive.angularZMotion=ConfigurableJointMotion.Locked;
                physicalHandGrips[hand.side]=passive;
                hand.transform.SetPositionAndRotation(handPosition,handRotation);
                handBody.position=handPosition;handBody.rotation=handRotation;
            }
            // The owner's visible hand follows the physical weapon through IK;
            // it does not add an NPC arm's inertia to the controller grip. Apply
            // the same direction of influence to this passive constraint only.
            // Weapon/world/other-weapon contacts still use its real body mass.
            physicalHandGrips[hand.side].connectedMassScale=0;
        }
        else if (old != null)
        {
            old.xMotion = old.yMotion = old.zMotion = ConfigurableJointMotion.Free;
            old.angularXMotion = old.angularYMotion = old.angularZMotion = ConfigurableJointMotion.Free;
            UnityEngine.Object.Destroy(old);
        }
        // Joint construction samples Transform rotations immediately, before the next
        // physics step. Rigidbody-only setters can leave the previous controller
        // Transform here and bake an arbitrary rotation offset into a fresh grip.
        controller.transform.SetPositionAndRotation(hand.gripInfo.transform.position, hand.gripInfo.transform.rotation);
        controller.position = controller.transform.position; controller.rotation = controller.transform.rotation;
        if (connected != null) { connected.position=connected.transform.position; connected.rotation=connected.transform.rotation; }
        var joint = controller.gameObject.AddComponent<ConfigurableJoint>();
        joint.autoConfigureConnectedAnchor = false; joint.anchor = Vector3.zero;
        joint.connectedBody = connected; joint.connectedAnchor = anchor; joint.enableCollision = true;
        handle.SetJointConfig(joint, hand, Vector2.one, Vector2.one, hand.creature.data.forceMaxPosition, hand.creature.data.forceMaxRotation, handle.data.rotationDrive);
        hand.gripInfo.joint = joint; hand.gripInfo.hasPlayerJoint = false;
        controller.transform.SetPositionAndRotation(target.Position.Game(),target.Rotation.Game());
        controller.position=controller.transform.position; controller.rotation=controller.transform.rotation;
    }
    private void ClearPhysicalHandGrip(Side side)
    {
        if (!physicalHandGrips.TryGetValue(side,out var joint)) return;
        physicalHandGrips.Remove(side);
        if (joint==null) return;
        joint.xMotion=joint.yMotion=joint.zMotion=ConfigurableJointMotion.Free;
        joint.angularXMotion=joint.angularYMotion=joint.angularZMotion=ConfigurableJointMotion.Free;
        UnityEngine.Object.Destroy(joint);
    }
    internal Transform ControllerTarget(Side side) => (side==Side.Left ? leftController : rightController)?.transform ?? Creature!.GetHand(side).grip;
    internal EntitySnapshot Capture(double now)
    {
        var identity = new Pose(System.Numerics.Vector3.Zero, System.Numerics.Quaternion.Identity);
        var s = new EntitySnapshot { Id = Descriptor.Id, Epoch = Descriptor.Epoch, Sequence = ++Sequence, Time = now, Head = identity, LeftHand = identity, RightHand = identity };
        if (Item) s.Root = GameMath.Capture(Item!.physicBody);
        else if (Creature)
        {
            var c = Creature!;
            s.Root = GameMath.Capture(c.currentLocomotion.physicBody);
            s.Root.Pose = c.transform.NetPose();
            s.Head = c == Player.local?.creature ? Player.local.head.transform.NetPose() : c.ragdoll.headPart.transform.NetPose();
            bool local = c == Player.local?.creature;
            s.LeftHand = local ? Player.local!.handLeft.grip.transform.NetPose() : c.handLeft.grip.NetPose();
            s.RightHand = local ? Player.local!.handRight.grip.transform.NetPose() : c.handRight.grip.NetPose();
            if (local) { s.LeftGrip = CaptureGrip(c.handLeft); s.RightGrip = CaptureGrip(c.handRight); }
            s.Parts = c.ragdoll.parts.Take(Protocol.MaxParts).Select(p => GameMath.Capture(p.physicBody, GameMath.PartKey(p))).ToArray();
            s.Flags = (ushort)((c.isKilled ? 1 : 0) | (c.handLeft.grabbedHandle ? 2 : 0) | (c.handRight.grabbedHandle ? 4 : 0) | (c.brain.canDamage ? 8 : 0) | (c.state == Creature.State.Alive ? 16 : 0) | (c.currentLocomotion.isGrounded ? 32 : 0) | (local && Player.selfCollision ? 64 : 0) | (c.brain.isAttacking ? 128 : 0) | (Ragdoll.IsPhysicalState(c.ragdoll.state) ? 256 : 0) | (local && Player.IsInvincible ? 512 : 0));
        }
        return s;
    }
    private static GripState CaptureGrip(RagdollHand hand)
    {
        if (hand.gripInfo?.joint == null) return default;
        var joint = hand.gripInfo.joint;
        bool Enabled(JointDrive drive) => drive.positionSpring > 0 || drive.positionDamper > 0;
        float Force(float value) => float.IsPositiveInfinity(value) ? float.MaxValue : Mathf.Max(0, value);
        var angular = Enabled(joint.slerpDrive) ? joint.slerpDrive : Enabled(joint.angularXDrive) ? joint.angularXDrive : joint.angularYZDrive;
        return new GripState
        {
            Active = true, ConnectedAnchor = joint.connectedAnchor.Net(),
            PositionSpring = Force(joint.xDrive.positionSpring), PositionDamper = Force(joint.xDrive.positionDamper), PositionMaxForce = Force(joint.xDrive.maximumForce),
            RotationSpring = Force(angular.positionSpring), RotationDamper = Force(angular.positionDamper), RotationMaxForce = Force(angular.maximumForce),
            AngularMask = (byte)((Enabled(joint.angularXDrive) ? 1 : 0) | (Enabled(joint.angularYZDrive) ? 2 : 0) | (Enabled(joint.slerpDrive) ? 4 : 0) | (joint.rotationDriveMode == RotationDriveMode.Slerp ? 8 : 0))
        };
    }
    private void ApplyGrip(RagdollHand hand, GripState grip)
    {
        if (!grip.Active || hand.gripInfo?.joint == null) return;
        var joint = hand.gripInfo.joint;
        joint.connectedAnchor = grip.ConnectedAnchor.Game();
        if (physicalHandGrips.TryGetValue(hand.side,out var passive) && passive!=null) passive.connectedAnchor=joint.connectedAnchor;
        joint.xDrive = joint.yDrive = joint.zDrive = new JointDrive { positionSpring=grip.PositionSpring, positionDamper=grip.PositionDamper, maximumForce=grip.PositionMaxForce };
        var angular = new JointDrive { positionSpring=grip.RotationSpring, positionDamper=grip.RotationDamper, maximumForce=grip.RotationMaxForce };
        joint.rotationDriveMode = (grip.AngularMask & 8) != 0 ? RotationDriveMode.Slerp : RotationDriveMode.XYAndZ;
        joint.angularXDrive = (grip.AngularMask & 1) != 0 ? angular : default;
        joint.angularYZDrive = (grip.AngularMask & 2) != 0 ? angular : default;
        joint.slerpDrive = (grip.AngularMask & 4) != 0 ? angular : default;
    }
    internal void FixedTick(double now)
    {
        RefreshItemLifetime();
        MaintainDetachedPlayerParts();
        if (Owned || World.IsBrokenRoot(Descriptor.Id) || LastArrival == 0 || now - LastArrival > .5) return;
        var s = Buffer.Sample(now); if (s == null) return;
        RefreshPhysicalHandMass();
        if (Creature != null && leftController != null) { ApplyGrip(Creature.handLeft, s.LeftGrip); ApplyGrip(Creature.handRight, s.RightGrip); }
        if (leftController != null) SetControllerPose(leftController,s.LeftHand);
        if (rightController != null) SetControllerPose(rightController,s.RightHand);
        if (Item)
        {
            // Native grip/holster/penetration joints are the controller while constrained.
            if (Item!.handlers.Count == 0 && Item.holder == null && !Item.isPenetrating) GameMath.Drive(Item.physicBody, s.Root, 45, 120);
        }
        else if (Creature)
        {
            var c = Creature!;
            if (Descriptor.Kind == EntityKind.Player)
            {
                bool physical = (s.Flags & 257) != 0;
                UpdateTrackedSkeleton(s,physical);
                if (sourcePhysicalRagdoll != physical)
                {
                    // Native tracked players disable angular ragdoll limits.
                    // Leaving NPC limits active resists the replicated wrist pose
                    // and visibly separates the hand from its controller-driven grip.
                    foreach (var part in c.ragdoll.parts)
                        if (physical || IsDetachedPlayerPart(part)) part.ResetCharJointLimit(); else part.DisableCharJointLimit();
                    sourcePhysicalRagdoll = physical;
                }
            }
            // Locomotion is disabled on replicas. Native melee AI still needs
            // the owner's grounded state to decide whether it can attack them.
            c.currentLocomotion.isGrounded = (s.Flags & 32) != 0;
            SourceAlive = (s.Flags & 16) != 0;
            SourceInvincible = (s.Flags & 512) != 0;
            c.brain.canDamage = Descriptor.Kind == EntityKind.Player || (s.Flags & 8) != 0;
            if (Descriptor.Kind == EntityKind.Creature) c.brain.isAttacking = (s.Flags & 128) != 0;
            if (Descriptor.Kind == EntityKind.Player && SourceSelfCollision != ((s.Flags & 64) != 0))
            {
                SourceSelfCollision=(s.Flags & 64) != 0;
                c.handLeft.grabbedHandle?.item?.RefreshCollision(); c.handRight.grabbedHandle?.item?.RefreshCollision();
            }
            c.transform.SetPositionAndRotation(s.Root.Pose.Position.Game(), s.Root.Pose.Rotation.Game());
            if (!c.currentLocomotion.physicBody.isKinematic)
            {
                c.currentLocomotion.physicBody.velocity = s.Root.Velocity.Game();
                c.currentLocomotion.physicBody.angularVelocity = s.Root.AngularVelocity.Game();
            }
            foreach (var body in s.Parts)
            {
                var part = GameMath.FindPart(c, body.Part);
                if(part!=null && BodyGripControls(part)) continue;
                if ((part==c.handLeft && physicalHandGrips.ContainsKey(Side.Left)) ||
                    (part==c.handRight && physicalHandGrips.ContainsKey(Side.Right))) continue;
                if (part != null) GameMath.Drive(part.physicBody, body, 45, part.isSliced ? 50 : 160);
            }
        }
    }
    private static void SetControllerPose(Rigidbody controller,Pose target)
    {
        // These bodies are tracking references without colliders. Native player
        // tracking sets its grip frame directly; MovePosition/MoveRotation instead
        // supplies kinematic body motion to the joint solver. Keep the reference
        // frame current before solving, while the held weapon remains dynamic.
        controller.transform.SetPositionAndRotation(target.Position.Game(),target.Rotation.Game());
        controller.position=controller.transform.position;controller.rotation=controller.transform.rotation;
    }
    private void OnCollision(CollisionInstance hit) => World.RecordContact(this, hit);
    private void OnPenetrated(Damager damager, CollisionInstance hit, EventTime time) { if (time == EventTime.OnEnd) World.PenetrationChanged(damager,hit,true); }
    private void OnUnpenetrated(Damager damager, CollisionInstance hit, bool wentThrough, EventTime time) { if (time == EventTime.OnStart) World.PenetrationChanged(damager,hit,false,wentThrough); }
    private void OnKilled(CollisionInstance hit, EventTime time)
    {
        if (time != EventTime.OnEnd || !Owned || World.Applying) return;
        var attacker = hit.sourceColliderGroup?.collisionHandler?.item?.lastHandler?.creature ?? hit.casterHand?.mana?.creature ?? Creature?.lastInteractionCreature;
        World.SendAction(this, new GameplayAction { Kind = ActionKind.Death, Other = World.Find(attacker)?.Descriptor.Id ?? 0 });
        // Local events skip replay in ApplyAction, including host publication.
        // Every owner must schedule its own respawn here. The host also records
        // this local death's score; other deaths arrive through ApplyAction.
        World.Match.OnDeath(this, hit);
    }
    private void OnHealed(float amount, Creature healer, EventTime time) { if (time == EventTime.OnEnd && Owned && !World.Applying) World.SendHealth(this); }
    private void OnDamaged(CollisionInstance hit, EventTime time) { if (time == EventTime.OnEnd && Owned && !World.Applying) World.SendHealth(this); }
    private void OnResurrected(float health, Creature resurrector, EventTime time)
    {
        if (time == EventTime.OnEnd) { SliceOrigins.Clear(); RemoteSliceTargets.Clear(); PendingRemoteSlices.Clear(); }
        if (time == EventTime.OnEnd && Owned && !World.Applying) World.SendAction(this, new GameplayAction { Kind = ActionKind.Respawn, Value = health });
    }
    private void OnSlice(RagdollPart part, EventTime time)
    {
        if (time != EventTime.OnEnd) return;
        DetachSlicedPlayerBranch(part);
        if (World.Applying) return;
        if (Owned)
        {
            // Some native parts slice a child while disabling their own colliders.
            // Replicate that original request, so both native side effects run.
            var requested = SliceOrigins.TryGetValue(part,out var origin) ? origin : part;
            SliceOrigins.Remove(part);
            World.SendAction(this, new GameplayAction { Kind = ActionKind.Slice, Part = GameMath.PartKey(requested) });
        }
    }
    private void OnAppearanceChanged(EventTime time)
    {
        AppearanceUpdating = time == EventTime.OnStart;
        if (!AppearanceUpdating) NextRemoteSliceAt = World.Now + .1;
        if(!AppearanceUpdating && Owned && !World.Applying) World.PublishUpdatedAppearance(this);
    }
    private void OnHandGrab(Side side, Handle handle, float axis, HandlePose orientation, EventTime time)
    {
        if (Owned && !World.Applying && time == EventTime.OnEnd) World.HandGrab(this, side, handle, axis, orientation);
    }
    private void OnHandRelease(Side side, Handle handle, bool throwing, EventTime time)
    {
        if (time == EventTime.OnStart) ClearPhysicalHandGrip(side);
        if (Owned && !World.Applying && time == EventTime.OnEnd) World.HandRelease(this, side, handle, throwing);
    }
    internal void ClearRemotePinForces() => Creature!.ragdoll.SetPinForce(0,0,0,0,0,0,sourcePhysicalRagdoll ?? true);
    internal void RefreshPhysicalHandMass()
    {
        foreach(var pair in physicalHandGrips)
            if(pair.Value!=null)
                // A grapple must be able to move the held weapon against its
                // controller spring. Treating that endpoint as infinitely heavy
                // overconstrains the arm between two independent VR controllers.
                pair.Value.connectedMassScale=BodyGripControls(Creature!.GetHand(pair.Key)) ? 1f : 0f;
    }
    private bool BodyGripControls(RagdollPart part)
    {
        if(Descriptor.Kind!=EntityKind.Player) return false;
        // A native grapple controls this limb. Pose springs on the same limb
        // would fight its grip joint and inject energy into the whole skeleton.
        for(var ancestor=part;ancestor!=null;ancestor=ancestor.parentPart)
        {
            if(ancestor.isGrabbed) return true;
            if(ancestor.isSliced && detachedPlayerRoots.Contains(ancestor)) break;
        }
        return false;
    }
    private void OnSnap(Holder holder) { if (Owned && !World.Applying) World.ItemSnap(this, holder, true); }
    private void OnUnsnap(Holder holder) { if (Owned && !World.Applying) World.ItemSnap(this, holder, false); }
    private void OnItemDespawn(EventTime time) { if (time == EventTime.OnStart && Owned && !World.Applying) World.Despawn(this); }
    private void OnCreatureDespawn(EventTime time) { if (time == EventTime.OnStart && Owned && !World.Applying) World.Despawn(this); }
    public void Dispose()
    {
        detachedPlayerParts.Clear(); detachedPlayerRoots.Clear();
        RestoreTrackedSkeleton();
        ClearPhysicalHandGrip(Side.Left);ClearPhysicalHandGrip(Side.Right);
        foreach(var pair in bodyHandleData) if(pair.Key!=null) pair.Key.handleRagdollData=pair.Value;
        bodyHandleData.Clear();
        SliceOrigins.Clear(); RemoteSliceTargets.Clear(); PendingRemoteSlices.Clear();
        foreach (var probe in collisionProbes) if (probe != null) UnityEngine.Object.Destroy(probe);
        collisionProbes.Clear();
        foreach (var damager in damagers) if (damager != null) { damager.OnPenetrateEvent -= OnPenetrated; damager.OnUnpenetrateEvent -= OnUnpenetrated; }
        damagers.Clear();
        if (leftController != null) UnityEngine.Object.Destroy(leftController.gameObject);
        if (rightController != null) UnityEngine.Object.Destroy(rightController.gameObject);
        if (Creature)
        {
            var c = Creature!;
            c.OnKillEvent -= OnKilled; c.OnHealEvent -= OnHealed; c.OnDamageEvent -= OnDamaged; c.OnResurrectEvent -= OnResurrected;
            c.OnDespawnEvent -= OnCreatureDespawn; c.ragdoll.OnSliceEvent -= OnSlice;
            c.equipment.onCreaturePartChanged -= OnAppearanceChanged;
            c.handLeft.OnGrabEvent -= OnHandGrab; c.handRight.OnGrabEvent -= OnHandGrab;
            c.handLeft.OnUnGrabEvent -= OnHandRelease; c.handRight.OnUnGrabEvent -= OnHandRelease;
            if (SpawnedRemotely)
            {
                c.countTowardsMaxAlive=oldCountTowardsMaxAlive;
                c.ragdoll.forcePhysic.Remove(this);
                c.animator.enabled = oldAnimatorEnabled; c.animator.applyRootMotion = oldRootMotion;
                c.locomotion.enabled = oldLocomotionEnabled;
                c.ragdoll.SetPinForce(oldPinPositionSpring, oldPinPositionDamper, oldPinRotationSpring, oldPinRotationDamper, oldMaxPosition, oldMaxRotation);
            }
        }
        if (Item)
        {
            if(!Owned && Item!.GetComponent<ItemMagicProjectile>() is { } projectile)projectile.effectInstance?.End();
            if(itemLifetimeProtected)Item!.DisallowDespawn = oldDisallowDespawn;
            Item.OnDespawnEvent -= OnItemDespawn; Item.OnSnapEvent -= OnSnap; Item.OnUnSnapEvent -= OnUnsnap;
            foreach (var handler in Item.collisionHandlers) handler.OnCollisionStartEvent -= OnCollision;
        }
    }
}

// Test instrumentation observes the actual Unity collision solver separately from
// ThunderRoad's hit callbacks, which can also be generated by swept damager checks.
internal sealed class WeaponCollisionProbe : MonoBehaviour
{
    internal ReplicatedEntity Entity = null!;
    private void OnCollisionEnter(Collision collision) => Entity.World.RecordPhysicsContact(Entity, collision);
    private void OnCollisionStay(Collision collision) { if (collision.impulse.sqrMagnitude > .000001f) Entity.World.RecordPhysicsContact(Entity,collision); }
}
