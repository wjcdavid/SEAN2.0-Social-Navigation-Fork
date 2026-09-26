using System.Collections;
using SEAN.Scenario.Agents;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Animations;
using UnityEngine.Playables;

// Version 2: resolve AppearanceAvatar.avatars and animationController without running its movement setup.
// Independent visual pedestrian. Does not register with SEAN/SFM/ROS.
// Source assets are never modified. Native behaviours are removed from the clone
// while inactive, before Awake/Start can run on those behaviours.
[DefaultExecutionOrder(10000)]
public class StationaryInspectorPedestrian : MonoBehaviour
{
    public enum Personality { Indifferent, Curious, Scared, Surprised, Assertive }
    public enum CuriousReaction { StandAndObserve, CustomGesture, CrouchAndObserve }
    public enum StationaryReaction { None, FaceRobot, CustomGesture, KimodoSurprised }

    [Header("References — set before Play")]
    [Tooltip("SimpleAppearanceAgent with AppearanceAvatar, or a complete character prefab. AppearanceAvatar avatar selection and controller assignment are supported without starting SF/ORCA movement.")]
    public GameObject simpleAppearanceAgent;
    public Transform robotTrunk;
    public Transform spawnPoint;
    [Tooltip("Optional relative path to the intended Animator, if the prefab contains multiple valid Humanoid Animators.")]
    public string animatorPath = "";
    [Tooltip("Optional standing idle clip. If empty, use the prefab's Animator Controller with movement parameters set to zero.")]
    public AnimationClip idleClip;

    [Header("Personality and reaction")]
    public Personality personality = Personality.Curious;
    public CuriousReaction curiousReaction = CuriousReaction.StandAndObserve;
    public StationaryReaction reaction = StationaryReaction.CustomGesture;
    [Tooltip("FBX's non-Legacy Humanoid AnimationClip, not the FBX GameObject. For crouching choose a standing-to-crouching clip: it plays forward, holds, then reverses.")]
    public AnimationClip customReactionClip;
    public bool faceRobot = true;
    [Min(0)] public float turnSpeed = 180;
    [Min(0.01f)] public float triggerDistance = 3;
    [Min(0)] public float releaseMargin = 1;
    [Min(0)] public float cooldownSeconds = 3;
    [Tooltip("Off: robot must leave Trigger Distance + Release Margin before retriggering. On: repeat after cooldown even if robot stays close.")]
    public bool repeatWhileNear;
    [Min(0.01f)] public float reactionPlaybackSpeed = 1;
    [Min(0)] public float reactionBlendSeconds = 0.25f;
    [Min(0)] public float observationSeconds = 3;
    [Min(0)] public float crouchHoldSeconds = 3;
    [Tooltip("If disabled, keep the final facing direction after the reaction.")]
    public bool returnToSpawnRotation;

    [SerializeField] private string status = "Not started";
    [SerializeField] private float targetDistance;
    [SerializeField] private GameObject spawnedCharacter;
    private GameObject staging;
    private Animator animator;
    private PlayableGraph graph;
    private AnimationMixerPlayable mixer;
    private AnimationClipPlayable reactionPlayable, idlePlayable;
    private float idleTime;
    private AnimatorControllerPlayable controllerPlayable;
    private AnimationClip selectedClip;
    private Vector3 fixedPosition, animatorLocalPosition;
    private Quaternion initialRotation, animatorLocalRotation;
    private float elapsed, nextAllowed, blend, playSeconds, totalSeconds;
    private bool ready, reacting, latched, crouching, clipMode;

    IEnumerator Start()
    {
        if (!simpleAppearanceAgent || !spawnPoint || !robotTrunk) {
            Fail("Assign Simple Appearance Agent, Spawn Point and Robot Trunk before Play."); yield break;
        }
        if (idleClip && !ValidClip(idleClip)) {
            Fail("Idle Clip must be a non-Legacy Humanoid AnimationClip."); yield break;
        }
        crouching = personality == Personality.Curious && curiousReaction == CuriousReaction.CrouchAndObserve;
        clipMode = personality == Personality.Curious
            ? curiousReaction != CuriousReaction.StandAndObserve
            : reaction == StationaryReaction.CustomGesture || reaction == StationaryReaction.KimodoSurprised;
        selectedClip = customReactionClip;
        if (personality != Personality.Curious && reaction == StationaryReaction.KimodoSurprised) {
            selectedClip = null;
            foreach (var clip in Resources.LoadAll<AnimationClip>("kimodo_b2_surprised")) {
                if (ValidClip(clip) && !clip.name.StartsWith("__preview")) {
                    if (selectedClip) { Fail("Multiple Kimodo surprised clips found. Use Custom Gesture and choose the clip explicitly."); yield break; }
                    selectedClip = clip;
                }
            }
        }
        if (clipMode && !ValidClip(selectedClip)) {
            Fail("Selected reaction needs a non-Legacy Humanoid AnimationClip. Fill Custom Reaction Clip or check Kimodo resource import."); yield break;
        }
        // AppearanceAvatar.Awake normally chooses this model, assigns the controller,
        // then adds an SFAgent/ORCA agent. Reproduce only the visual portion here.
        // Reading a component on the source prefab does not execute its Awake.
        GameObject visualPrefab = simpleAppearanceAgent;
        RuntimeAnimatorController appearanceController = null;
        var appearances = simpleAppearanceAgent.GetComponentsInChildren<AppearanceAvatar>(true);
        if (appearances.Length > 1) {
            Fail("Multiple AppearanceAvatar components found. Assign one appearance container."); yield break;
        }
        if (appearances.Length == 1) {
            var appearance = appearances[0];
            if (appearance.avatars == null || appearance.avatars.Length == 0) {
                Fail("AppearanceAvatar.avatars is empty. Assign at least one character in the source prefab."); yield break;
            }
            for (int i = 0; i < appearance.avatars.Length; i++) {
                if (!appearance.avatars[i]) {
                    Fail("AppearanceAvatar.avatars contains a missing entry at index " + i + ". Restore the referenced model."); yield break;
                }
            }
            visualPrefab = appearance.avatars[Random.Range(0, appearance.avatars.Length)];
            appearanceController = appearance.animationController;
            if (visualPrefab.GetComponentInChildren<AppearanceAvatar>(true)) {
                Fail("Selected avatar is another appearance container, not a rendered character prefab."); yield break;
            }
        }
        fixedPosition = spawnPoint.position;
        initialRotation = Quaternion.Euler(0, spawnPoint.eulerAngles.y, 0);
        staging = new GameObject("StationaryPedestrian_InactiveSetup");
        staging.SetActive(false);
        spawnedCharacter = Instantiate(visualPrefab, staging.transform, false);
        spawnedCharacter.name = "StationaryInspector_" + name;
        spawnedCharacter.SetActive(false);
        foreach (var script in spawnedCharacter.GetComponentsInChildren<MonoBehaviour>(true)) {
            if (!script) continue;
            script.enabled = false;
            Destroy(script);
        }
        foreach (var nav in spawnedCharacter.GetComponentsInChildren<NavMeshAgent>(true)) nav.enabled = false;
        foreach (var rb in spawnedCharacter.GetComponentsInChildren<Rigidbody>(true)) {
            rb.useGravity = false;
            rb.isKinematic = true;
        }
        foreach (var legacy in spawnedCharacter.GetComponentsInChildren<Animation>(true)) legacy.enabled = false;
        // Finish deferred removal before activating the clone.
        yield return null;
        if (!string.IsNullOrEmpty(animatorPath)) {
            var node = spawnedCharacter.transform.Find(animatorPath);
            animator = node ? node.GetComponent<Animator>() : null;
        } else {
            // Same picker as AppearanceAvatar: prefer the body's Humanoid Animator.
            animator = IVI.AvatarAnimatorUtility.GetLocomotionAnimator(spawnedCharacter);
        }
        if (!animator || !animator.avatar || !animator.avatar.isValid || !animator.avatar.isHuman) {
            Fail("Selected model " + visualPrefab.name + " has no valid Humanoid Animator. Check its Rig/Avatar or Animator Path."); yield break;
        }
        // Null preserves the model's own controller, matching AppearanceAvatar.Awake.
        if (appearanceController) animator.runtimeAnimatorController = appearanceController;
        if (!idleClip && !animator.runtimeAnimatorController) {
            Fail("Assign Idle Clip: chosen Animator has no base controller."); yield break;
        }
        bool hasVisibleRenderer = false;
        foreach (var renderer in spawnedCharacter.GetComponentsInChildren<Renderer>(true)) {
            if (!renderer.enabled) continue;
            bool active = true;
            for (Transform node = renderer.transform; node && node != spawnedCharacter.transform; node = node.parent)
                if (!node.gameObject.activeSelf) active = false;
            if (active) hasVisibleRenderer = true;
        }
        if (!hasVisibleRenderer) { Fail("Prefab has no enabled renderer in an active child hierarchy."); yield break; }
        // Activate only the ancestor chain required by the selected Animator.
        for (Transform node = animator.transform; node && node != spawnedCharacter.transform; node = node.parent)
            node.gameObject.SetActive(true);
        foreach (var other in spawnedCharacter.GetComponentsInChildren<Animator>(true)) other.enabled = other == animator;
        spawnedCharacter.transform.SetParent(null, true);
        spawnedCharacter.transform.SetPositionAndRotation(fixedPosition, initialRotation);
        animatorLocalPosition = animator.transform.localPosition;
        animatorLocalRotation = animator.transform.localRotation;
        animator.applyRootMotion = false;
        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        animator.speed = 1;
        spawnedCharacter.SetActive(true);
        Destroy(staging); staging = null;
        graph = PlayableGraph.Create("StationaryInspector_" + GetInstanceID());
        graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
        mixer = AnimationMixerPlayable.Create(graph, 2);
        var output = AnimationPlayableOutput.Create(graph, "Character", animator);
        output.SetSourcePlayable(mixer);
        if (idleClip) {
            idlePlayable = AnimationClipPlayable.Create(graph, idleClip);
            idlePlayable.SetSpeed(0);
            graph.Connect(idlePlayable, 0, mixer, 0);
        } else {
            controllerPlayable = AnimatorControllerPlayable.Create(graph, animator.runtimeAnimatorController);
            graph.Connect(controllerPlayable, 0, mixer, 0);
            for (int i = 0; i < controllerPlayable.GetParameterCount(); i++) {
                var parameter = controllerPlayable.GetParameter(i);
                string key = parameter.name.ToLowerInvariant();
                if (parameter.type == AnimatorControllerParameterType.Float &&
                    (key.Contains("speed") || key == "forward" || key == "strafe" || key == "turn"))
                    controllerPlayable.SetFloat(parameter.nameHash, 0);
            }
        }
        if (clipMode) {
            reactionPlayable = AnimationClipPlayable.Create(graph, selectedClip);
            reactionPlayable.SetSpeed(0);
            reactionPlayable.SetApplyFootIK(false);
            graph.Connect(reactionPlayable, 0, mixer, 1);
        }
        mixer.SetInputWeight(0, 1); mixer.SetInputWeight(1, 0);
        graph.Play(); graph.Evaluate(0);
        ready = true; status = "Ready — fixed position";
        Debug.Log("[StationaryInspector] Model=" + visualPrefab.name + "; spawned at " + fixedPosition + ". Select this character and press F in Scene view.", spawnedCharacter);
    }

    static bool ValidClip(AnimationClip clip) { return clip && !clip.legacy && clip.humanMotion && clip.length > 0; }
    void Update()
    {
        if (!ready || !spawnedCharacter) return;
        if (!robotTrunk) {
            if (reacting) Finish();
            status = "Waiting for Robot Trunk"; return;
        }
        Vector3 delta = robotTrunk.position - fixedPosition; delta.y = 0;
        targetDistance = delta.magnitude;
        if (targetDistance > triggerDistance + releaseMargin) latched = false;
        bool noReaction = personality != Personality.Curious && reaction == StationaryReaction.None;
        if (!reacting && !noReaction && targetDistance <= triggerDistance && Time.time >= nextAllowed && (!latched || repeatWhileNear)) {
            reacting = true; latched = true; elapsed = 0;
            blend = Mathf.Max(0, reactionBlendSeconds);
            playSeconds = clipMode ? selectedClip.length / Mathf.Max(0.01f, reactionPlaybackSpeed) : observationSeconds;
            totalSeconds = clipMode ? 2 * blend + playSeconds * (crouching ? 2 : 1) + (crouching ? crouchHoldSeconds : 0) : observationSeconds;
            status = "Reacting in place: " + personality;
        }
        if (!reacting) return;
        if (faceRobot && delta.sqrMagnitude > 0.0001f)
            spawnedCharacter.transform.rotation = Quaternion.RotateTowards(spawnedCharacter.transform.rotation, Quaternion.LookRotation(delta), turnSpeed * Time.deltaTime);
        elapsed += Time.deltaTime;
        if (clipMode) {
            float t = Mathf.Clamp(elapsed - blend, 0, Mathf.Max(0, totalSeconds - 2 * blend));
            float clipTime = Mathf.Min(t, playSeconds) * reactionPlaybackSpeed;
            if (crouching && t > playSeconds + crouchHoldSeconds)
                clipTime = Mathf.Max(0, playSeconds - (t - playSeconds - crouchHoldSeconds)) * reactionPlaybackSpeed;
            reactionPlayable.SetTime(Mathf.Clamp(clipTime, 0, Mathf.Max(0, selectedClip.length - 0.0001f)));
            float weight = blend <= 0 ? 1 : Mathf.Min(Mathf.Clamp01(elapsed / blend), Mathf.Clamp01((totalSeconds - elapsed) / blend));
            mixer.SetInputWeight(0, 1 - weight); mixer.SetInputWeight(1, weight);
        }
        if (elapsed >= totalSeconds) Finish();
    }
    void LateUpdate()
    {
        if (!ready || !spawnedCharacter) return;
        Quaternion facing = spawnedCharacter.transform.rotation;
        if (idleClip) {
            idleTime = Mathf.Repeat(idleTime + Time.deltaTime, idleClip.length);
            idlePlayable.SetTime(idleTime);
        }
        graph.Evaluate(Time.deltaTime);
        spawnedCharacter.transform.rotation = facing;
        // Fix world position even if the source clip contains root translation.
        spawnedCharacter.transform.position = fixedPosition;
        if (animator.transform != spawnedCharacter.transform) {
            animator.transform.localPosition = animatorLocalPosition;
            animator.transform.localRotation = animatorLocalRotation;
        }
        if (!reacting && returnToSpawnRotation)
            spawnedCharacter.transform.rotation = Quaternion.RotateTowards(spawnedCharacter.transform.rotation, initialRotation, turnSpeed * Time.deltaTime);
    }
    void Finish()
    {
        reacting = false;
        mixer.SetInputWeight(0, 1); mixer.SetInputWeight(1, 0);
        nextAllowed = Time.time + cooldownSeconds;
        status = "Idle — fixed position";
    }
    void Fail(string message)
    {
        status = "ERROR: " + message;
        Debug.LogError("[StationaryInspector] " + message, this);
        Cleanup(); enabled = false;
    }
    void Cleanup()
    {
        ready = false;
        if (graph.IsValid()) graph.Destroy();
        if (spawnedCharacter) Destroy(spawnedCharacter);
        if (staging) Destroy(staging);
    }
    void OnDisable() { StopAllCoroutines(); Cleanup(); }
    void OnDestroy() { Cleanup(); }
    void OnDrawGizmosSelected()
    {
        if (!spawnPoint) return;
        Gizmos.color = Color.cyan; Gizmos.DrawWireSphere(Application.isPlaying && ready ? fixedPosition : spawnPoint.position, triggerDistance);
    }
}

#if UNITY_EDITOR
[UnityEditor.CustomEditor(typeof(StationaryInspectorPedestrian))]
public class StationaryInspectorPedestrianEditor : UnityEditor.Editor
{
    void Field(string key) { UnityEditor.EditorGUILayout.PropertyField(serializedObject.FindProperty(key), true); }
    public override void OnInspectorGUI()
    {
        serializedObject.Update();
        UnityEditor.EditorGUILayout.HelpBox("Version 2 — supports AppearanceAvatar/SimpleAppearanceAgent. Fixed-position visual pedestrian. No Original Spawner, navigation, or SEAN/ROS registration. Configure before Play. Custom clips must be non-Legacy Humanoid. Disabling removes the spawned clone; restart Play to spawn again.", UnityEditor.MessageType.Info);
        using (new UnityEditor.EditorGUI.DisabledScope(Application.isPlaying)) {
            foreach (string key in new[] { "simpleAppearanceAgent", "robotTrunk", "spawnPoint", "animatorPath", "idleClip", "personality" }) Field(key);
            var p = (StationaryInspectorPedestrian.Personality)serializedObject.FindProperty("personality").enumValueIndex;
            bool curious = p == StationaryInspectorPedestrian.Personality.Curious;
            Field(curious ? "curiousReaction" : "reaction");
            int choice = serializedObject.FindProperty(curious ? "curiousReaction" : "reaction").enumValueIndex;
            if ((curious && choice != 0) || (!curious && choice == 2)) Field("customReactionClip");
            if (curious && choice == 2) {
                Field("crouchHoldSeconds");
                UnityEditor.EditorGUILayout.HelpBox("Use a standing-to-crouching clip. It plays forward, holds the last pose, then reverses. Do not use a complete down-and-up cycle here.", UnityEditor.MessageType.Info);
            }
            foreach (string key in new[] { "faceRobot", "turnSpeed", "triggerDistance", "releaseMargin", "cooldownSeconds", "repeatWhileNear", "reactionPlaybackSpeed", "reactionBlendSeconds", "observationSeconds", "returnToSpawnRotation" }) Field(key);
        }
        using (new UnityEditor.EditorGUI.DisabledScope(true)) { Field("status"); Field("targetDistance"); Field("spawnedCharacter"); }
        if (Application.isPlaying && GUILayout.Button("Select spawned character")) {
            var go = serializedObject.FindProperty("spawnedCharacter").objectReferenceValue as GameObject;
            if (go) { UnityEditor.Selection.activeGameObject = go; if (UnityEditor.SceneView.lastActiveSceneView) UnityEditor.SceneView.lastActiveSceneView.FrameSelected(); }
        }
        serializedObject.ApplyModifiedProperties();
    }
}
#endif
