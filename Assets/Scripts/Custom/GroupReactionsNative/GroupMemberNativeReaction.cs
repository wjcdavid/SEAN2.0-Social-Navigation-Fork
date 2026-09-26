using System;
using System.Collections;
using UnityEngine;

namespace SEAN.Scenario.Agents
{
    // Logical slot stays in the original formation. The visible body can temporarily detach.
    // Run after the group spawners so their slot updates cannot drag a stopped/following body.
    [DefaultExecutionOrder(10000)]
    public sealed class GroupMemberNativeReaction : MonoBehaviour
    {
        private enum Phase { Formation, Holding, Native, Returning }
        [SerializeField] private string status = "Waiting for native scene";
        [SerializeField] private float formationOffset;
        private Phase phase;
        private GameObject body;
        private Animator animator;
        private GroupReactionSettings settings;
        private PedestrianModulator.PersonalityType personality;
        private PedestrianModulator mod;
        private GroupReactionSFAgent native;
        private OriginalInspectorReactionAgent player;
        private InspectorPedestrianDemo config;
        private GameObject settingsObject;
        private Func<Transform> targetProvider;
        private Func<bool> gate;
        private MonoBehaviour owner;
        private float trigger, release, beganAt, nextTargetAt, cooldownUntil;
        private bool ready, latch, detached, failed;
        private bool resumePending, restoreDetachedBody;
        private int resumePreparationUntilFrame;
        private IEnumerator preparation;
        private Vector3 heldPosition, returnOffset, lastBodyPosition;
        private float originalAnimationSpeed;
        private bool originalRootMotion;
        private int nativeFrames;
        public bool OwnsBody { get { return phase != Phase.Formation; } }
        internal MonoBehaviour Owner { get { return owner; } }
        internal bool Returning { get { return phase == Phase.Returning; } }
        internal bool NativeReaction { get { return phase == Phase.Native; } }
        internal bool UseNativeVelocity { get { return NativeReaction && settings.allowLeaveFormation && settings.HasNativeMovement(personality); } }
        private float MaxOffset { get { return Mathf.Max(0.05f, settings.maxFormationOffset); } }
        internal bool TargetOutsideRange { get { Transform t = Target(); return !t || Distance(t.position, body.transform.position) > release; } }

        public static GroupMemberNativeReaction Attach(GameObject person, GroupReactionSettings choices,
            PedestrianModulator.PersonalityType kind, MonoBehaviour group, Func<Transform> robot,
            Func<bool> enabledGate, float triggerDistance, float releaseDistance)
        {
            // No edit-mode instantiation of native lifecycle components or settings carriers.
            if (!Application.isPlaying || choices == null || !choices.enabled) return null;
            var slot = new GameObject("GroupReactionSlot_" + person.name);
            slot.transform.SetParent(person.transform.parent, false);
            slot.transform.SetPositionAndRotation(person.transform.position, person.transform.rotation);
            // Keep native perception colliders on a separate Rigidbody root. Otherwise the
            // old visible-body reporters also count the SFAgent's 2 m perception sphere as a hit.
            var bodyRoot = new GameObject("NativeReactionBody_" + person.name);
            bodyRoot.layer = person.layer;
            bodyRoot.transform.SetParent(slot.transform, false);
            person.transform.SetParent(bodyRoot.transform, true);
            var visibleRb = person.GetComponent<Rigidbody>();
            if (!visibleRb) visibleRb = person.AddComponent<Rigidbody>();
            visibleRb.isKinematic = true; visibleRb.useGravity = false;
            var result = slot.AddComponent<GroupMemberNativeReaction>();
            result.body = bodyRoot; result.settings = choices; result.personality = kind;
            result.owner = group; result.targetProvider = robot; result.gate = enabledGate;
            result.trigger = Mathf.Max(0.1f, triggerDistance);
            result.release = Mathf.Max(result.trigger + 0.01f, releaseDistance);
            result.lastBodyPosition = bodyRoot.transform.position;
            return result;
        }

        // Advanced by Update, not a Unity-owned Start coroutine. Deactivating a GameObject
        // stops its Unity coroutines; retaining this enumerator lets preparation resume safely.
        IEnumerator Prepare()
        {
            while (owner && !Allowed()) yield return null;
            if (!owner) yield break;
            animator = IVI.AvatarAnimatorUtility.GetLocomotionAnimator(body);
            if (!animator || !animator.avatar || !animator.avatar.isHuman || !animator.avatar.isValid || !animator.runtimeAnimatorController) {
                Fail("This member needs a valid Humanoid Animator and its existing base controller. Its formation movement is retained."); yield break;
            }
            if (!body.GetComponentInChildren<SkinnedMeshRenderer>()) {
                Fail("Native Base.Start requires an active SkinnedMeshRenderer on the member."); yield break;
            }
            if (body.GetComponentInChildren<IVI.INavigable>(true)) {
                Fail("The visual prefab already contains an INavigable component. Use an avatar from AppearanceAvatar.avatars without a second navigation component."); yield break;
            }
            originalAnimationSpeed = animator.speed;
            originalRootMotion = animator.applyRootMotion;
            // SFAgent.Start assumes that the native SEAN robot exists. Wait, don't run half a lifecycle.
            while (owner && (!Allowed() || !NativeRobotReady())) {
                status = "Waiting: enable group/member reactions and select the native SEAN robot";
                yield return null;
            }
            if (!owner) yield break;
            native = body.AddComponent<GroupReactionSFAgent>();
            native.enabled = false;
            native.bridge = this;
            mod = body.GetComponent<PedestrianModulator>();
            if (!mod) mod = body.AddComponent<PedestrianModulator>();
            mod.enabled = true;
            mod.personality = personality;
            mod.rootMotionTranslationFrozen = true;
            mod.inspectorReactionTarget = Target();
            mod.detectRadius = mod.scaredRadius = mod.surpriseRadius = trigger;
            mod.detectExitMargin = Mathf.Max(1.01f, settings.curiousExitMultiplier);
            mod.followDist = Mathf.Max(0.1f, settings.followDistance);
            mod.freezeDuration = Mathf.Max(0.1f, settings.surpriseFreezeSeconds);
            mod.cooldownDuration = Mathf.Max(0f, settings.cooldownSeconds);
            native.enabled = true;
            yield return null;
            if (!native || !native.initialized) { Fail("Native SFAgent did not finish Start; inspect the preceding Console error."); yield break; }

            // OriginalInspectorReactionAgent accepts InspectorPedestrianDemo as its settings API.
            // Keep this carrier INACTIVE for its entire lifetime: its spawner-editing Awake never runs.
            settingsObject = new GameObject("GroupReactionSettings (inactive data only)");
            settingsObject.SetActive(false);
            settingsObject.hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSave;
            config = settingsObject.AddComponent<InspectorPedestrianDemo>();
            settings.CopyTo(config, personality, Target(), trigger, release);
            player = body.AddComponent<OriginalInspectorReactionAgent>();
            player.enabled = false; // Prepare the coroutine before allowing its first encounter Update.
            player.Configure(config, native, mod);
            // The current native runner prepares over two yielded frames.
            yield return null;
            yield return null;
            yield return null;
            ready = true;
            player.enabled = Allowed();
            status = "Formation: original walking controller retained";
        }

        bool Allowed() { return isActiveAndEnabled && owner && owner.isActiveAndEnabled && settings != null && settings.enabled && (gate == null || gate()); }
        static bool NativeRobotReady()
        {
            try { return SEAN.instance && SEAN.instance.robot; }
            catch (Exception) { return false; }
        }
        Transform Target()
        {
            Transform target = targetProvider == null ? null : targetProvider();
            if (target) return target;
            try { var r = SEAN.instance ? SEAN.instance.robot : null; return r ? (r.base_link ? r.base_link.transform : r.transform) : null; }
            catch (Exception) { return null; }
        }

        void Update()
        {
            // Hierarchy changes are safe here, after the entire OnEnable/OnDisable traversal.
            if (resumePending) ResumeAfterEnable();
            if (Time.frameCount < resumePreparationUntilFrame) return;
            if (!ready && !failed && body && Allowed()) {
                if (preparation == null) preparation = Prepare();
                if (!preparation.MoveNext()) preparation = null;
            }
            if (!ready || failed || !body || !native || !mod) return;
            if (!Allowed()) {
                if (player) player.enabled = false;
                if (phase != Phase.Formation) BeginReturn();
            } else if (player && phase != Phase.Returning) player.enabled = true;

            Transform target = Target();
            config.robotTrunk = target;
            mod.inspectorReactionTarget = target;
            float distance = target ? Distance(target.position, body.transform.position) : float.PositiveInfinity;
            if (distance > release) latch = false;
            bool movement = settings.allowLeaveFormation && settings.HasNativeMovement(personality);
            bool originalSurprise = personality == PedestrianModulator.PersonalityType.Surprised &&
                settings.surprisedReaction == InspectorPedestrianDemo.SurprisedVariant.OriginalClip;

            if (phase == Phase.Formation && Allowed()) {
                if (mod.inspectorHold) {
                    Detach(); phase = Phase.Holding; heldPosition = body.transform.position;
                    beganAt = Time.time; latch = true; status = "Native reaction player: stopped gesture / observation";
                } else if (!latch && target && distance <= trigger && Time.time >= cooldownUntil && (movement || originalSurprise)) {
                    Detach(); phase = Phase.Native; beganAt = Time.time; nativeFrames = 0; latch = true;
                    native.InitDest(transform.position); status = "Native PedestrianModulator reaction";
                }
            }
            if (phase == Phase.Holding && !mod.inspectorHold) {
                if (Allowed() && target && distance <= release && movement) {
                    phase = Phase.Native; beganAt = Time.time; nativeFrames = 0;
                    native.InitDest(transform.position);
                } else BeginReturn();
            }
            if (phase == Phase.Native) {
                nativeFrames++;
                bool timedOut = settings.nativeMovementSeconds > 0 && Time.time - beganAt >= settings.nativeMovementSeconds;
                float exit = personality == PedestrianModulator.PersonalityType.Curious
                    ? Mathf.Max(release, trigger * settings.curiousExitMultiplier) : release;
                bool surpriseDone = originalSurprise && nativeFrames > 2 && Time.time - beganAt >= settings.surpriseFreezeSeconds && !mod.IsRotationSuppressed();
                if ((!target || distance > exit || timedOut || surpriseDone) && !mod.inspectorHold) BeginReturn();
                else if (Time.time >= nextTargetAt && !(personality == PedestrianModulator.PersonalityType.Curious && mod.IsControllingDestination)) {
                    native.InitDest(transform.position); nextTargetAt = Time.time + 0.3f;
                }
            }
            native.SetSocialPresence(UseNativeVelocity || Returning);
        }

        void LateUpdate()
        {
            if (!ready || !body || !animator) return;
            mod.rootMotionTranslationFrozen = true;
            animator.applyRootMotion = false;
            if (phase == Phase.Formation) {
                body.transform.localPosition = Vector3.zero;
                body.transform.localRotation = Quaternion.identity;
            } else if (phase == Phase.Holding) {
                body.transform.position = heldPosition;
            }

            // Whole-body gestures must not hold a member behind a moving group indefinitely.
            // Check the CURRENT slot (also valid during the original group's reformation).
            // A short recovery margin avoids spending a frame beyond the configured envelope.
            formationOffset = Distance(body.transform.position, transform.position);
            if ((phase == Phase.Native || phase == Phase.Holding) && formationOffset >= MaxOffset * 0.9f) {
                BeginReturn();
                status = "Formation limit reached: personal reaction ended, rejoining slot";
            }
            if (phase == Phase.Returning) {
                // Recover an offset from the live slot, rather than chase a moving world-space
                // destination with social forces that can keep pushing the member away.
                returnOffset = Vector3.MoveTowards(returnOffset, Vector3.zero,
                    Mathf.Max(0.1f, settings.returnSpeed) * Time.deltaTime);
                body.transform.position = transform.position + returnOffset;
                body.transform.rotation = Quaternion.RotateTowards(body.transform.rotation, transform.rotation, 360f * Time.deltaTime);
                if (returnOffset.sqrMagnitude <= 0.000001f) Rejoin();
            }
            if (phase != Phase.Formation) {
                // Final frame bound, including endpoint turns, route resets and large frame times.
                Vector3 offset = body.transform.position - transform.position;
                offset.y = 0f;
                body.transform.position = transform.position + Vector3.ClampMagnitude(offset, MaxOffset);
            }
            formationOffset = Distance(body.transform.position, transform.position);
            float bodySpeed = Distance(body.transform.position, lastBodyPosition) / Mathf.Max(Time.deltaTime, 0.0001f);
            lastBodyPosition = body.transform.position;
            animator.applyRootMotion = false;
            if (phase == Phase.Formation) {
                // Group spawner has already supplied its original Forward / Strafe / Idling values.
                animator.speed = originalAnimationSpeed;
                return;
            }
            if (mod.inspectorHold) {
                // StandAndObserve has no PlayableGraph; explicitly idle the original controller.
                SetFloat("Forward", 0f); SetFloat("Strafe", 0f); SetBool("Idling", true);
                animator.speed = 1f;
                return; // Current native runner owns the gesture graph and playback rate.
            }
            float speed = UseNativeVelocity || Returning ? bodySpeed : 0f;
            SetFloat("Forward", speed / 0.6f);
            SetFloat("Strafe", 0f);
            SetBool("Idling", speed < 0.01f);
            animator.speed = speed > 0.01f ? Mathf.Max(0.1f, originalAnimationSpeed) : 1f;
        }

        void Detach()
        {
            if (detached) return;
            body.transform.SetParent(null, true);
            detached = true;
        }
        void BeginReturn()
        {
            if (phase == Phase.Returning || phase == Phase.Formation) return;
            if (player) player.enabled = false;
            mod.inspectorHold = false;
            returnOffset = body.transform.position - transform.position;
            returnOffset.y = 0f;
            returnOffset = Vector3.ClampMagnitude(returnOffset, MaxOffset);
            phase = Phase.Returning; nextTargetAt = 0f;
            status = "Returning to moving formation slot; original group route retained";
        }
        void Rejoin()
        {
            body.transform.SetParent(transform, true);
            body.transform.localPosition = Vector3.zero;
            body.transform.localRotation = Quaternion.identity;
            detached = false; phase = Phase.Formation;
            native.SetSocialPresence(false);
            cooldownUntil = Time.time + settings.cooldownSeconds;
            // Runner retains its own encounter latch/cooldown; do not recreate it every encounter.
            if (player) player.enabled = Allowed();
            status = "Formation: original walking controller retained";
        }
        void SetFloat(string parameter, float value)
        {
            foreach (var p in animator.parameters) if (p.name == parameter && p.type == AnimatorControllerParameterType.Float) { animator.SetFloat(parameter, value); break; }
        }
        void SetBool(string parameter, bool value)
        {
            foreach (var p in animator.parameters) if (p.name == parameter && p.type == AnimatorControllerParameterType.Bool) { animator.SetBool(parameter, value); break; }
        }
        static float Distance(Vector3 a, Vector3 b) { a.y = b.y = 0f; return Vector3.Distance(a, b); }
        void Fail(string message)
        {
            failed = true; status = "ERROR: " + message;
            Debug.LogError("[Group native reaction] " + (body ? body.name : name) + ": " + message, this);
            if (native) native.enabled = false;
        }
        void OnDisable()
        {
            resumePending = false;
            if (player) player.enabled = false;
            if (native) { native.SetSocialPresence(false); native.enabled = false; }
            if (mod) mod.inspectorHold = false;
            if (detached && body) {
                // This is a separate scene-root object. Hide it while its owning slot is off,
                // but NEVER parent it into a hierarchy that Unity is currently deactivating.
                restoreDetachedBody |= body.activeSelf;
                if (body.activeSelf) body.SetActive(false);
            }
            phase = Phase.Formation;
        }
        void OnEnable()
        {
            // Also called by AddComponent before Attach assigns body. That first call is a no-op.
            if (body) resumePending = true;
        }
        void ResumeAfterEnable()
        {
            resumePending = false;
            if (!body || !isActiveAndEnabled) return;
            if (detached) {
                body.transform.SetParent(transform, false);
                body.transform.localPosition = Vector3.zero;
                body.transform.localRotation = Quaternion.identity;
                detached = false;
                if (restoreDetachedBody) body.SetActive(true);
                restoreDetachedBody = false;
            }
            phase = Phase.Formation;
            returnOffset = Vector3.zero;
            formationOffset = 0f;
            lastBodyPosition = body.transform.position;
            if (mod) { mod.inspectorHold = false; mod.rootMotionTranslationFrozen = true; }
            if (animator) { animator.applyRootMotion = false; animator.speed = originalAnimationSpeed; }
            if (native && !failed) {
                native.SetSocialPresence(false);
                native.enabled = true;
            }
            // Let a newly enabled native component complete Start before preparation continues.
            resumePreparationUntilFrame = Time.frameCount + 1;
            if (!ready && player && config && native && mod && !failed) {
                // Inactivating the body may have stopped the native player's preparation coroutine.
                // Gait is fixed to Original, so replaying only this unfinished preparation is safe.
                player.StopAllCoroutines();
                player.Configure(config, native, mod);
                resumePreparationUntilFrame = Time.frameCount + 3;
            }
            if (ready && player && !failed) player.enabled = Allowed();
            if (ready && !failed) status = "Resumed: formation restored after activation";
        }
        void OnDestroy()
        {
            if (preparation is IDisposable disposable) disposable.Dispose();
            preparation = null;
            if (body) {
                IVI.SFAgent.GO2Agent.Remove(body);
                if (player) player.enabled = false;
                if (animator) animator.applyRootMotion = originalRootMotion;
                // Includes bodies detached to scene root when the group is cleared.
                Destroy(body);
            }
            if (settingsObject) Destroy(settingsObject);
        }
    }
}
