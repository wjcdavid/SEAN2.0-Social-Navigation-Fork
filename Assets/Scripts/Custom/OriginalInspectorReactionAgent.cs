using System.Collections;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using SEAN.AutoTrial;
using SEAN.Scenario.Agents;

// Native Base/SFAgent remain enabled. This component only installs clips and pauses
// through optional PedestrianModulator hooks during selected stop-and-gesture events.
[DefaultExecutionOrder(-9000)]
public class OriginalInspectorReactionAgent : MonoBehaviour
{
    [SerializeField] private string status = "Preparing";
    [SerializeField] private string lastError = "";
    [SerializeField] private float targetDistance;
    private InspectorPedestrianDemo cfg;
    private IVI.INavigable agent;
    private PedestrianModulator mod;
    private Animator animator;
    private RuntimeAnimatorController originalController;
    private RuntimeAnimatorController ownedController;
    private Transform target;
    private PlayableGraph graph;
    private AnimationClipPlayable clipPlayable;
    private AnimationMixerPlayable mixer;
    private bool ready, active, latch, crouch, observing;
    private float elapsed, duration, cooldownUntil, riseAt = -1f;
    private int[] states;
    private float[] times;
    private Vector3 bodyLocalPosition;
    private Quaternion bodyLocalRotation;
    private bool restoreApplyRootMotion;

    public void Configure(InspectorPedestrianDemo config, IVI.INavigable nativeAgent, PedestrianModulator nativeMod)
    {
        cfg = config; agent = nativeAgent; mod = nativeMod;
        if (mod) { mod.inspectorReactionTarget = cfg.robotTrunk; mod.inspectorSuppressNativeReaction = SuppressNative(); }
        StartCoroutine(Prepare());
    }
    IEnumerator Prepare()
    {
        // Base.Start initializes Animator and tracking. Do not replace its lifecycle.
        yield return null;
        if (!mod) { Error("Native PedestrianModulator missing; patrol must be configured."); yield break; }
        animator = IVI.AvatarAnimatorUtility.GetLocomotionAnimator(gameObject);
        if (!animator || !animator.avatar || !animator.avatar.isHuman || !animator.runtimeAnimatorController) {
            Error("A valid Humanoid Animator and base controller are required for this animation selector."); yield break;
        }
        originalController = animator.runtimeAnimatorController;
        if (cfg.walkAnimation != InspectorPedestrianDemo.WalkAnimation.Original) {
            var source = Resources.Load<RuntimeAnimatorController>(cfg.ClipName());
            AnimationClip gait = source ? S79GaitOverrideBuilder.ExtractGaitClip(source) : null;
            if (!gait) { Error("Gait source not found: " + cfg.ClipName()); yield break; }
            var applier = GetComponent<S41MixamoClipApplier>();
            if (!applier) applier = gameObject.AddComponent<S41MixamoClipApplier>();
            applier.clipControllerName = cfg.ClipName();
            applier.attachCarriedBox = cfg.carryBox && cfg.walkAnimation == InspectorPedestrianDemo.WalkAnimation.CarryAndWalk;
            float deadline = Time.realtimeSinceStartup + 10f;
            while (!applier.GaitInstalled && Time.realtimeSinceStartup < deadline) yield return null;
            if (!applier.GaitInstalled) { Error("S41 gait installation timed out."); yield break; }
            // Preserve native locomotion/reaction states for every selected travelling gait.
            // This opts this test agent out of the non-Kimodo wholesale-controller path;
            // S41 source code and all other agents remain unchanged.
            yield return null;
            string detail;
            var merged = S79GaitOverrideBuilder.Build(originalController, gait, S79GaitOverrideBuilder.DefaultForwardClipNames, out detail);
            if (!merged) { Error("Cannot preserve native controller with this gait: " + detail); yield break; }
            ownedController = merged;
            animator.runtimeAnimatorController = merged;
            Debug.Log("[OriginalInspector v6] " + name + " gait override: " + detail, this);
        }
        yield return null;
        bodyLocalPosition = animator.transform.localPosition;
        bodyLocalRotation = animator.transform.localRotation;
        ready = true;
        status = "Ready: native SFAgent movement";
    }
    bool OwnReaction()
    {
        switch (cfg.personality) {
            case InspectorPedestrianDemo.Reaction.Curious:
                return cfg.curiousReaction != InspectorPedestrianDemo.CuriousVariant.ApproachAndFollow || cfg.playGestureBeforeNativeBehavior;
            case InspectorPedestrianDemo.Reaction.Scared:
                return cfg.scaredReaction == InspectorPedestrianDemo.ScaredVariant.GestureThenMoveAway || cfg.playGestureBeforeNativeBehavior;
            case InspectorPedestrianDemo.Reaction.Surprised:
                return cfg.surprisedReaction != InspectorPedestrianDemo.SurprisedVariant.OriginalClip;
            case InspectorPedestrianDemo.Reaction.Assertive:
                return cfg.assertiveReaction != InspectorPedestrianDemo.AssertiveVariant.OriginalBehavior || cfg.playGestureBeforeNativeBehavior;
            default: return cfg.playGestureBeforeNativeBehavior;
        }
    }
    bool SuppressNative()
    {
        return (cfg.personality == InspectorPedestrianDemo.Reaction.Curious && cfg.curiousReaction != InspectorPedestrianDemo.CuriousVariant.ApproachAndFollow) ||
            (cfg.personality == InspectorPedestrianDemo.Reaction.Surprised && cfg.surprisedReaction != InspectorPedestrianDemo.SurprisedVariant.OriginalClip);
    }
    void Update()
    {
        if (!ready || !cfg || !mod) return;
        target = cfg.robotTrunk;
        if (!target) {
            try { var r = SEAN.SEAN.instance.robot; if (r) target = r.base_link ? r.base_link.transform : r.transform; }
            catch (System.Exception) { }
        }
        mod.inspectorReactionTarget = cfg.robotTrunk;
        mod.inspectorSuppressNativeReaction = SuppressNative();
        if (!target) { status = "Waiting for reaction target / active SEAN robot"; if (active) Finish(); return; }
        Vector3 delta = target.position - transform.position; delta.y = 0;
        targetDistance = delta.magnitude;
        if (targetDistance > cfg.triggerDistance + cfg.releaseMargin) latch = false;
        if (active) {
            elapsed += Time.deltaTime;
            FaceTarget(delta);
            if (crouch) {
                float downEnd = cfg.reactionBlendSeconds + cfg.crouchTransitionSeconds;
                if (riseAt < 0 && elapsed >= downEnd &&
                    (elapsed >= downEnd + cfg.crouchHoldSeconds || targetDistance <= cfg.standUpIfCloserThan)) riseAt = elapsed;
                if (riseAt >= 0 && elapsed >= riseAt + cfg.crouchTransitionSeconds + cfg.reactionBlendSeconds) Finish();
            } else if (elapsed >= duration) Finish();
            return;
        }
        if (OwnReaction() && !latch && Time.time >= cooldownUntil && targetDistance <= cfg.triggerDistance) {
            latch = true;
            Begin();
        }
    }
    void FaceTarget(Vector3 delta)
    {
        if (delta.sqrMagnitude > 0.0001f)
            transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(delta), 180f * Time.deltaTime);
    }
    AnimationClip SelectClip()
    {
        if (crouch) {
            if (cfg.crouchClipOverride) return cfg.crouchClipOverride;
            var source = Resources.Load<RuntimeAnimatorController>("S68_CuriousCrouch");
            if (source) foreach (var clip in source.animationClips)
                if (clip && !clip.name.StartsWith("__preview")) return clip;
            return null;
        }
        if (cfg.personality == InspectorPedestrianDemo.Reaction.Surprised && cfg.surprisedReaction == InspectorPedestrianDemo.SurprisedVariant.KimodoSurprised)
            return S79GaitOverrideBuilder.LoadB2Clip();
        if (cfg.personality == InspectorPedestrianDemo.Reaction.Assertive && cfg.assertiveReaction == InspectorPedestrianDemo.AssertiveVariant.OriginalGesture) {
            AnimationClip found = null;
            foreach (var clip in originalController.animationClips)
                if (clip && clip.name == "mixamo.com" && Mathf.Abs(clip.length - 3.6f) < 0.01f) {
                    if (found && found != clip) return null;
                    found = clip;
                }
            return found;
        }
        return cfg.customReactionClip;
    }
    void Begin()
    {
        observing = cfg.personality == InspectorPedestrianDemo.Reaction.Curious && cfg.curiousReaction == InspectorPedestrianDemo.CuriousVariant.StandAndObserve;
        crouch = cfg.personality == InspectorPedestrianDemo.Reaction.Curious && cfg.curiousReaction == InspectorPedestrianDemo.CuriousVariant.CrouchAndObserve;
        AnimationClip clip = observing ? null : SelectClip();
        if (!observing && (!clip || clip.legacy || !clip.humanMotion || clip.length <= 0)) {
            lastError = "Selected reaction clip is missing or not a non-Legacy Humanoid clip.";
            Debug.LogError("[OriginalInspector v6] " + name + ": " + lastError, this);
            cooldownUntil = Time.time + cfg.cooldownSeconds;
            return;
        }
        elapsed = 0; riseAt = -1; lastError = "";
        duration = observing ? cfg.reactionHoldSeconds : clip.length / cfg.reactionPlaybackSpeed + 2 * cfg.reactionBlendSeconds;
        states = new int[animator.layerCount]; times = new float[animator.layerCount];
        for (int i = 0; i < states.Length; i++) { var state = animator.GetCurrentAnimatorStateInfo(i); states[i] = state.fullPathHash; times[i] = state.normalizedTime; }
        restoreApplyRootMotion = animator.applyRootMotion;
        mod.inspectorHold = true;
        active = true;
        if (observing) { status = "Standing and observing"; return; }
        graph = PlayableGraph.Create("OriginalInspectorReaction:" + name);
        graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
        var idle = AnimatorControllerPlayable.Create(graph, animator.runtimeAnimatorController);
        foreach (var p in animator.parameters) {
            if ((p.name == "Forward" || p.name == "Strafe" || p.name == "Turn") && p.type == AnimatorControllerParameterType.Float) idle.SetFloat(p.name, 0);
            if (p.name == "OnGround" && p.type == AnimatorControllerParameterType.Bool) idle.SetBool(p.name, true);
        }
        clipPlayable = AnimationClipPlayable.Create(graph, clip);
        clipPlayable.SetSpeed(0); clipPlayable.SetApplyFootIK(false); clipPlayable.SetApplyPlayableIK(false);
        mixer = AnimationMixerPlayable.Create(graph, 2);
        graph.Connect(idle, 0, mixer, 0); graph.Connect(clipPlayable, 0, mixer, 1);
        var output = AnimationPlayableOutput.Create(graph, "Reaction", animator); output.SetSourcePlayable(mixer);
        graph.Play(); status = "Playing " + clip.name;
    }
    void LateUpdate()
    {
        if (!active || !animator) return;
        // Base.Move may set animator.speed to zero for the paused native velocity.
        animator.speed = 1f;
        if (!graph.IsValid()) return;
        float blend = Mathf.Max(0.01f, cfg.reactionBlendSeconds);
        float clipLength = clipPlayable.GetAnimationClip().length;
        float weight, u;
        if (crouch) {
            float stand = cfg.kneelAtClipEnd ? 0 : 1, kneel = 1 - stand;
            if (riseAt < 0) { u = Mathf.Lerp(stand, kneel, Mathf.Clamp01((elapsed - blend) / cfg.crouchTransitionSeconds)); weight = Mathf.Clamp01(elapsed / blend); }
            else { u = Mathf.Lerp(kneel, stand, Mathf.Clamp01((elapsed - riseAt) / cfg.crouchTransitionSeconds)); weight = Mathf.Clamp01((riseAt + cfg.crouchTransitionSeconds + blend - elapsed) / blend); }
        } else { u = Mathf.Clamp01((elapsed - blend) * cfg.reactionPlaybackSpeed / clipLength); weight = Mathf.Min(Mathf.Clamp01(elapsed / blend), Mathf.Clamp01((duration - elapsed) / blend)); }
        clipPlayable.SetTime(u * Mathf.Max(0, clipLength - 0.0001f));
        mixer.SetInputWeight(0, 1-weight); mixer.SetInputWeight(1, weight);
        // Manual evaluation must not translate the root; native root motion resumes afterwards.
        animator.applyRootMotion = false;
        graph.Evaluate(Time.deltaTime);
        if (animator.transform != transform) { animator.transform.localPosition = bodyLocalPosition; animator.transform.localRotation = bodyLocalRotation; }
    }
    void Finish()
    {
        bool wasActive = active;
        if (graph.IsValid()) graph.Destroy();
        if (animator && wasActive) {
            animator.applyRootMotion = restoreApplyRootMotion;
            animator.speed = 1f;
            if (states != null) for (int i = 0; i < states.Length && i < animator.layerCount; i++)
                if (states[i] != 0 && animator.HasState(i, states[i])) animator.Play(states[i], i, times[i]);
        }
        active = false;
        if (mod) mod.inspectorHold = false;
        if (cfg) cooldownUntil = Time.time + cfg.cooldownSeconds;
        status = "Native movement resumed";
    }
    void Error(string message) { lastError = message; status = "ERROR"; Debug.LogError("[OriginalInspector v6] " + name + ": " + message, this); enabled = false; }
    void OnDisable() { Finish(); if (mod) { mod.inspectorReactionTarget = null; mod.inspectorSuppressNativeReaction = false; } }
    void OnDestroy() { if (graph.IsValid()) graph.Destroy(); if (ownedController) Destroy(ownedController); }
}
