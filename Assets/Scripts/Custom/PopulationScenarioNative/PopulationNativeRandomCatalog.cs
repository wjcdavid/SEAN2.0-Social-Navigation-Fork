using System;
using System.Collections.Generic;
using UnityEngine;
using SEAN.AutoTrial;

namespace SEAN.Scenario.Agents
{
    // Read-only resource availability for the composer. Never changes assets/controllers or
    // native behaviour. Resource names match InspectorPedestrianDemo.ClipName().
    public static class PopulationNativeRandomCatalog
    {
        public sealed class Snapshot
        {
            public readonly List<InspectorPedestrianDemo.WalkAnimation> walks = new List<InspectorPedestrianDemo.WalkAnimation>();
            public readonly List<string> unavailableWalks = new List<string>();
            public bool crouch, kimodoSurprised, assertiveGesture;
        }

        public static string WalkResource(InspectorPedestrianDemo.WalkAnimation value)
        {
            switch (value) {
                case InspectorPedestrianDemo.WalkAnimation.KimodoRelaxed: return "kimodo_relaxed_walk";
                case InspectorPedestrianDemo.WalkAnimation.KimodoElderly: return "kimodo_elderly_shuffle";
                case InspectorPedestrianDemo.WalkAnimation.OldManWalk: return "Old_Man_Walk";
                case InspectorPedestrianDemo.WalkAnimation.DrunkWalk: return "Drunk_Walk";
                case InspectorPedestrianDemo.WalkAnimation.CarryAndWalk: return "carry_and_walk";
                case InspectorPedestrianDemo.WalkAnimation.PacingPhone: return "Pacing_And_Talking_On_A_Phone";
                default: return "";
            }
        }

        public static Snapshot Read(GameObject appearancePrefab)
        {
            var result = new Snapshot();
            var bases = BaseControllers(appearancePrefab);
            bool canOverride = bases.Count > 0;
            foreach (var controller in bases) canOverride &= HasForwardSlot(controller);
            foreach (InspectorPedestrianDemo.WalkAnimation walk in Enum.GetValues(typeof(InspectorPedestrianDemo.WalkAnimation))) {
                if (walk == InspectorPedestrianDemo.WalkAnimation.Original) continue;
                var source = Resources.Load<RuntimeAnimatorController>(WalkResource(walk));
                // ExtractGaitClip in the existing S79 runner selects the first controller clip.
                var clips = source ? source.animationClips : null;
                if (canOverride && clips != null && clips.Length > 0 && Valid(clips[0])) result.walks.Add(walk);
                else result.unavailableWalks.Add(walk.ToString());
            }
            // Exact first-non-preview selection used by the native player; no custom override assigned.
            var crouchController = Resources.Load<RuntimeAnimatorController>("S68_CuriousCrouch");
            result.crouch = bases.Count > 0 && crouchController && Valid(FirstPlayable(crouchController.animationClips, false));
            result.kimodoSurprised = bases.Count > 0 && Valid(FirstPlayable(Resources.LoadAll<AnimationClip>(S79GaitOverrideBuilder.B2ResourcePath), true));
            result.assertiveGesture = bases.Count > 0;
            foreach (var controller in bases) result.assertiveGesture &= HasNativeAssertiveGesture(controller);
            return result;
        }
        static List<RuntimeAnimatorController> BaseControllers(GameObject source)
        {
            var result = new List<RuntimeAnimatorController>();
            if (!source) return result;
            var appearance = source.GetComponentInChildren<AppearanceAvatar>(true);
            if (!appearance) return result;
            if (appearance.animationController) { result.Add(appearance.animationController); return result; }
            if (appearance.avatars == null || appearance.avatars.Length == 0) return result;
            foreach (var prefab in appearance.avatars) {
                var animator = prefab ? IVI.AvatarAnimatorUtility.GetLocomotionAnimator(prefab) : null;
                if (!animator || !animator.runtimeAnimatorController) { result.Clear(); return result; }
                if (!result.Contains(animator.runtimeAnimatorController)) result.Add(animator.runtimeAnimatorController);
            }
            return result;
        }
        static bool HasForwardSlot(RuntimeAnimatorController source)
        {
            foreach (var name in S79GaitOverrideBuilder.DefaultForwardClipNames) {
                var matches = new HashSet<AnimationClip>();
                foreach (var clip in source.animationClips) if (clip && clip.name == name) matches.Add(clip);
                if (matches.Count == 1) return true;
            }
            return false;
        }
        static bool HasNativeAssertiveGesture(RuntimeAnimatorController controller)
        {
            AnimationClip selected = null;
            foreach (var clip in controller.animationClips) {
                if (!clip || clip.name != "mixamo.com" || Mathf.Abs(clip.length - 3.6f) >= 0.01f) continue;
                if (selected && selected != clip) return false;
                selected = clip;
            }
            return Valid(selected);
        }
        static AnimationClip FirstPlayable(AnimationClip[] clips, bool allowFirstFallback)
        {
            if (clips == null || clips.Length == 0) return null;
            foreach (var clip in clips) if (clip && !clip.name.StartsWith("__preview")) return clip;
            return allowFirstFallback ? clips[0] : null;
        }
        static bool Valid(AnimationClip clip) { return clip && !clip.legacy && clip.humanMotion && clip.length > 0; }
    }
}
