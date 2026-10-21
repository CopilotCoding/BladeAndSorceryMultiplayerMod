using System.Collections.Generic;
using System.Reflection;
using BladeAndSorcery.Multiplayer.Core;
using HarmonyLib;
using ThunderRoad;
using UnityEngine;

namespace BladeAndSorcery.Multiplayer;

internal sealed partial class ReplicatedEntity
{
    private readonly HashSet<RagdollPart> detachedPlayerParts = new HashSet<RagdollPart>();
    private readonly HashSet<RagdollPart> detachedPlayerRoots = new HashSet<RagdollPart>();
    private static readonly FieldInfo HandSnapBack = AccessTools.Field(typeof(PlayerHand), "_snapBack");

    internal bool IsDetachedPlayerPart(RagdollPart part) => part != null && part.isSliced && detachedPlayerParts.Contains(part);

    private void DetachSlicedPlayerBranch(RagdollPart root)
    {
        if (Descriptor.Kind != EntityKind.Player || !root.isSliced || !World.SurvivesLimbSlice(root)) return;
        // Use the actual completed native cut, not the disabled placeholder that
        // can redirect a slice to its child. Keep joints inside the fallen branch.
        var bones = new List<Ragdoll.Bone>();
        root.bone.GetAllChilds(bones);
        detachedPlayerRoots.Add(root);
        foreach (var bone in bones)
            if (bone.part != null) detachedPlayerParts.Add(bone.part);
        foreach (var bone in bones)
        {
            var part = bone.part;
            if (part == null) continue;
            if (part is RagdollHand hand)
            {
                ClearPhysicalHandGrip(hand.side);
                // Mark the whole branch first: native UnGrab normally reattaches
                // a player hand to its VR link at the end of this callback.
                if (hand.grabbedHandle != null) hand.UnGrab(false);
                if (hand.climb.isGripping) hand.climb.UnGrip();
                if (hand.playerHand is { } playerHand)
                {
                    if (HandSnapBack.GetValue(playerHand) is Coroutine snap)
                    {
                        playerHand.StopCoroutine(snap);
                        HandSnapBack.SetValue(playerHand, null);
                    }
                    ReleaseSeveredLink(playerHand.link, part);
                    hand.palmCollider.gameObject.SetActive(true);
                    hand.ragdoll.ik.SetHandAnchor(hand.side, playerHand.grip.transform);
                }
            }
            if (part is RagdollFoot foot && foot.playerFoot is { } playerFoot)
            {
                playerFoot.SetFootTracking(false);
                ReleaseSeveredLink(playerFoot.link, part);
            }
            part.SetBodyDamagerToDefault();
            FreeSeveredBody(part);
        }
        MaintainDetachedPlayerParts();
    }

    internal static void ReleaseSeveredLink(PlayerLink link, RagdollPart part)
    {
        // DetachRagdollPart recreates a character joint and makes a live player's
        // body kinematic. Only the low-level link release is appropriate here.
        link.Detach();
        part.collisionHandler.RemovePhysicModifier(link);
    }

    private static void FreeSeveredBody(RagdollPart part)
    {
        if (part.transform.parent != part.root) part.transform.SetParent(part.root, true);
        part.collisionHandler.RemovePhysicModifier(part.ragdoll);
        part.collisionHandler.active = true;
        part.physicBody.isKinematic = false;
        part.physicBody.rigidBody.useGravity = true;
        part.ResetCharJointLimit();
        var joint = part.bone.animationJoint;
        if (joint != null)
        {
            joint.xDrive = joint.yDrive = joint.zDrive = default;
            joint.angularXDrive = joint.angularYZDrive = joint.slerpDrive = default;
            joint.xMotion = joint.yMotion = joint.zMotion = ConfigurableJointMotion.Free;
            joint.angularXMotion = joint.angularYMotion = joint.angularZMotion = ConfigurableJointMotion.Free;
        }
    }

    private void MaintainDetachedPlayerParts()
    {
        // Native ragdoll mode changes can restore parenting/kinematic flags.
        // Do not reset poses or velocities: the owner simulates the fall, and
        // existing part snapshots replicate it including grabs and penetration.
        foreach (var root in detachedPlayerRoots)
            if (root != null && root.isSliced) root.DestroyCharJoint();
        foreach (var part in detachedPlayerParts)
            if (part != null && part.isSliced) FreeSeveredBody(part);
    }
}

internal static class DetachedPlayerLimb
{
    internal static bool Contains(RagdollPart? part) => part != null &&
        MultiplayerRuntime.Instance?.World?.Find(part.ragdoll.creature)?.IsDetachedPlayerPart(part) == true;
}

[HarmonyPatch]
internal static class SeveredPlayerLinkPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(PlayerLink), nameof(PlayerLink.AttachRagdollPart));
        yield return AccessTools.Method(typeof(PlayerLink), nameof(PlayerLink.DetachRagdollPart));
    }
    private static bool Prefix(PlayerLink __instance, RagdollPart ragdollPart)
    {
        if (!DetachedPlayerLimb.Contains(ragdollPart)) return true;
        ReplicatedEntity.ReleaseSeveredLink(__instance, ragdollPart);
        return false;
    }
}

[HarmonyPatch(typeof(PlayerHand), "ManagedUpdate")]
internal static class SeveredPlayerHandTrackingPatch
{
    private static bool Prefix(PlayerHand __instance) => !DetachedPlayerLimb.Contains(__instance.ragdollHand);
}

[HarmonyPatch(typeof(PlayerFoot), nameof(PlayerFoot.SetFootTracking))]
internal static class SeveredPlayerFootTrackingPatch
{
    private static bool Prefix(PlayerFoot __instance, bool active) => !active || !DetachedPlayerLimb.Contains(__instance.ragdollFoot);
}

[HarmonyPatch(typeof(RagdollHand), nameof(RagdollHand.Grab), new[] { typeof(Handle), typeof(HandlePose), typeof(float), typeof(bool), typeof(bool) })]
internal static class SeveredPlayerHandGrabPatch
{
    private static bool Prefix(RagdollHand __instance) => !DetachedPlayerLimb.Contains(__instance);
}

[HarmonyPatch]
internal static class SeveredRagdollHandInputPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(RagdollHand), "ManagedUpdate");
        yield return AccessTools.Method(typeof(RagdollHand), "ManagedFixedUpdate");
    }
    // A fallen physical hand cannot climb or swim on behalf of its owner.
    private static bool Prefix(RagdollHand __instance) => !DetachedPlayerLimb.Contains(__instance);
}
