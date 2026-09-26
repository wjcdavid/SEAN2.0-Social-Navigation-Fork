using System;
using UnityEngine;

namespace SEAN.Scenario.Agents
{
    // Reuses the current InspectorPedestrianDemo reaction enums. Deliberately has no gait selector.
    [Serializable]
    public sealed class GroupReactionSettings
    {
        public bool enabled = true;
        public InspectorPedestrianDemo.CuriousVariant curiousReaction = InspectorPedestrianDemo.CuriousVariant.StandAndObserve;
        public InspectorPedestrianDemo.ScaredVariant scaredReaction;
        public InspectorPedestrianDemo.SurprisedVariant surprisedReaction;
        public InspectorPedestrianDemo.AssertiveVariant assertiveReaction;
        public bool playGestureBeforeNativeBehavior;
        public AnimationClip customReactionClip;
        public AnimationClip crouchClipOverride;
        [Min(0.1f)] public float reactionHoldSeconds = 3f;
        [Min(0.1f)] public float reactionPlaybackSpeed = 1f;
        [Min(0.01f)] public float reactionBlendSeconds = 0.25f;
        [Min(0.1f)] public float crouchTransitionSeconds = 2.8f;
        [Min(0.1f)] public float crouchHoldSeconds = 3f;
        public bool kneelAtClipEnd = true;
        [Min(0.1f)] public float standUpIfCloserThan = 1.2f;
        [Min(0.1f)] public float followDistance = 1.8f;
        [Min(1.01f)] public float curiousExitMultiplier = 1.3f;
        [Min(0.1f)] public float surpriseFreezeSeconds = 1.5f;
        [Min(0f)] public float cooldownSeconds = 4f;
        [Tooltip("Allow native approach/flee within Max Formation Offset. If disabled, movement-only reactions stay in their slot; configured gestures still play.")]
        public bool allowLeaveFormation = true;
        [Tooltip("Maximum horizontal deviation in metres from this member's CURRENT formation slot, including while observing or playing a gesture. At the limit, end the personal reaction and return. This does not change the group's own AttractArc/SplitCorridor slots.")]
        [Min(0.05f)] public float maxFormationOffset = 0.6f;
        [Tooltip("Maximum native movement reaction time, then return to the moving formation slot. 0 = wait until the robot leaves the release range.")]
        [Min(0f)] public float nativeMovementSeconds = 15f;
        [Tooltip("Return pace relative to the moving formation slot, in metres per second. Uses the EXISTING walk controller; the group keeps its original route and pace.")]
        [Min(0.1f)] public float returnSpeed = 1.5f;

        public static GroupReactionSettings StationaryDefaults()
        {
            return new GroupReactionSettings { allowLeaveFormation = false };
        }

        internal bool HasNativeMovement(PedestrianModulator.PersonalityType personality)
        {
            return (personality == PedestrianModulator.PersonalityType.Curious && curiousReaction == InspectorPedestrianDemo.CuriousVariant.ApproachAndFollow)
                || personality == PedestrianModulator.PersonalityType.Scared
                || (personality == PedestrianModulator.PersonalityType.Assertive && assertiveReaction == InspectorPedestrianDemo.AssertiveVariant.OriginalBehavior);
        }

        internal void CopyTo(InspectorPedestrianDemo config, PedestrianModulator.PersonalityType personality,
            Transform target, float trigger, float release)
        {
            // IMPORTANT: default Original bypasses the runner's S41/S79 gait-installation path.
            config.walkAnimation = InspectorPedestrianDemo.WalkAnimation.Original;
            config.personality = (InspectorPedestrianDemo.Reaction)Enum.Parse(typeof(InspectorPedestrianDemo.Reaction), personality.ToString());
            config.robotTrunk = target;
            config.triggerDistance = Mathf.Max(0.1f, trigger);
            config.releaseMargin = Mathf.Max(0.01f, release - trigger);
            config.curiousReaction = curiousReaction;
            config.scaredReaction = scaredReaction;
            config.surprisedReaction = surprisedReaction;
            config.assertiveReaction = assertiveReaction;
            config.playGestureBeforeNativeBehavior = playGestureBeforeNativeBehavior;
            config.customReactionClip = customReactionClip;
            config.crouchClipOverride = crouchClipOverride;
            config.reactionHoldSeconds = Mathf.Max(0.1f, reactionHoldSeconds);
            config.reactionPlaybackSpeed = Mathf.Max(0.1f, reactionPlaybackSpeed);
            config.reactionBlendSeconds = Mathf.Max(0.01f, reactionBlendSeconds);
            config.crouchTransitionSeconds = Mathf.Max(0.1f, crouchTransitionSeconds);
            config.crouchHoldSeconds = Mathf.Max(0.1f, crouchHoldSeconds);
            config.kneelAtClipEnd = kneelAtClipEnd;
            config.standUpIfCloserThan = Mathf.Max(0.1f, standUpIfCloserThan);
            config.followDistance = Mathf.Max(0.1f, followDistance);
            config.curiousExitMultiplier = Mathf.Max(1.01f, curiousExitMultiplier);
            config.surpriseFreezeSeconds = Mathf.Max(0.1f, surpriseFreezeSeconds);
            config.cooldownSeconds = Mathf.Max(0f, cooldownSeconds);
        }
    }
}
