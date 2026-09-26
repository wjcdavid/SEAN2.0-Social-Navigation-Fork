// Version 6 - configuration adapter for the ORIGINAL PedestrianSpawner pipeline.
// Replace Assets/Scripts/Custom/InspectorPedestrianDemo.cs. Do not place in Editor.
// No movement ownership, Animator replacement, or core-source edits in this adapter.
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using SEAN.Scenario.Agents;
using SEAN.AutoTrial;

[DefaultExecutionOrder(-10000)]
[DisallowMultipleComponent]
public class InspectorPedestrianDemo : MonoBehaviour
{
    // Keep v4 enum values so existing Inspector serialization is not reinterpreted.
    public enum WalkAnimation { Original, KimodoRelaxed, KimodoElderly, OldManWalk, DrunkWalk, CarryAndWalk, PacingPhone }
    public enum Reaction { Indifferent, Curious, Scared, Surprised, Assertive }
    [Header("Version 6 - ORIGINAL system adapter")]
    [Tooltip("Assign the existing PedestrianSpawner on ConfigurableSpawnerRoot. This adapter configures its spawn list for one test group at runtime.")]
    public PedestrianSpawner originalSpawner;
    public GameObject simpleAppearanceAgent;
    public Transform spawnPoint;
    public Transform patrolA;
    public Transform patrolB;
    [Min(1)] public int count = 1;
    public WalkAnimation walkAnimation = WalkAnimation.Original;
    public Reaction personality = Reaction.Curious;
    public enum CuriousVariant { ApproachAndFollow, StandAndObserve, CrouchAndObserve, CustomGesture }
    public enum SurprisedVariant { OriginalClip, KimodoSurprised, CustomGesture }
    public enum AssertiveVariant { OriginalBehavior, OriginalGesture, CustomGesture }
    public enum ScaredVariant { MoveAway, GestureThenMoveAway }
    public CuriousVariant curiousReaction;
    public SurprisedVariant surprisedReaction;
    public AssertiveVariant assertiveReaction;
    public ScaredVariant scaredReaction;
    [Tooltip("Optional reaction target. Empty = the robot selected by SEAN. Does not change SFAgent's global robot repulsion target.")]
    public Transform robotTrunk;
    public AnimationClip customReactionClip;
    public AnimationClip crouchClipOverride;
    [Min(0.1f)] public float reactionHoldSeconds = 3f;
    [Min(0.1f)] public float reactionPlaybackSpeed = 1f;
    [Min(0.01f)] public float reactionBlendSeconds = 0.25f;
    [Min(0.1f)] public float crouchTransitionSeconds = 2.8f;
    [Min(0.1f)] public float crouchHoldSeconds = 3f;
    public bool kneelAtClipEnd = true;
    [Min(0.1f)] public float standUpIfCloserThan = 1.2f;
    [Min(0.1f)] public float releaseMargin = 2f;
    [Header("Optional animation override on native movement reactions")]
    [Tooltip("Approach/follow, flee, or indifferent movement: play this full-body clip once at encounter range, then resume native behavior. This is a STOP gesture, not an upper-body overlay while walking.")]
    public bool playGestureBeforeNativeBehavior;

    [Tooltip("Original multiplier, NOT metres per second. 1 keeps the original scale.")]
    [Min(0.01f)] public float walkSpeedMultiplier = 1f;
    [Min(0.1f)] public float triggerDistance = 4f;
    [Min(0.1f)] public float followDistance = 1.8f;
    [Tooltip("Original Curious exit threshold = detectRadius multiplied by this value.")]
    [Min(1.01f)] public float curiousExitMultiplier = 1.3f;
    [Min(0.1f)] public float surpriseFreezeSeconds = 1.5f;
    [Min(0)] public float cooldownSeconds = 4f;
    public bool carryBox;

    [SerializeField] private string status = "Not started";
    [SerializeField] private GameObject activeRobot;
    [SerializeField] private int configuredAgents;
    private readonly HashSet<int> configured = new HashSet<int>();
    private List<SpawnGroupConfig> previousGroups;
    private GameObject previousPrefab;
    private bool ownsConfiguration;
    private float nextReport;

    void Awake()
    {
        if (!originalSpawner) {
            // Restrict automatic lookup to the known sibling object, including inactive roots.
            Transform parent = transform.parent;
            Transform root = parent ? parent.Find("ConfigurableSpawnerRoot") : null;
            if (root) originalSpawner = root.GetComponent<PedestrianSpawner>();
        }
        if (!originalSpawner || !simpleAppearanceAgent || !simpleAppearanceAgent.GetComponent<AppearanceAvatar>()) {
            Error("Assign the existing PedestrianSpawner and an AppearanceAvatar container prefab."); return;
        }
        if (!patrolA || !patrolB) { Error("Assign both patrol points; this test adapter uses the original patrol implementation."); return; }
        Transform spawn = spawnPoint ? spawnPoint : transform;
        if (walkAnimation != WalkAnimation.Original && !Resources.Load<RuntimeAnimatorController>(ClipName())) {
            Error("Missing Resources controller: " + ClipName()); return;
        }
        previousGroups = originalSpawner.spawnGroups;
        previousPrefab = originalSpawner.agentPrefab;
        originalSpawner.agentPrefab = simpleAppearanceAgent;
        originalSpawner.spawnGroups = new List<SpawnGroupConfig> {
            new SpawnGroupConfig {
                label = "OriginalInspector", count = count, personality = OriginalPersonality(),
                spawnPoints = new List<Transform> { spawn }, patrol = true,
                patrolPointA = patrolA, patrolPointB = patrolB,
                walkSpeedMultiplier = walkSpeedMultiplier
            }
        };
        ownsConfiguration = true;
        status = "Waiting for the original ConfigurableSpawner scenario to spawn agents";

    }

    void Update()
    {
        if (!ownsConfiguration) return;
        if (originalSpawner && originalSpawner.agents != null) {
            foreach (var agent in originalSpawner.agents) {
                if (!agent || !agent.name.StartsWith("OriginalInspector_")) continue;
                int id = agent.GetInstanceID();
                if (!configured.Add(id)) continue;
                // Let Base, TrackedAgent and TrackedTrajectory execute their normal lifecycle.
                // Never disable these components or add/enable a NavMeshAgent here.
                var mod = agent.GetComponent<PedestrianModulator>();
                if (mod) {
                    mod.detectRadius = triggerDistance;
                    mod.scaredRadius = triggerDistance;
                    mod.surpriseRadius = triggerDistance;
                    mod.followDist = followDistance;
                    mod.detectExitMargin = curiousExitMultiplier;
                    mod.freezeDuration = surpriseFreezeSeconds;
                    mod.cooldownDuration = cooldownSeconds;
                }
                var extension = agent.gameObject.AddComponent<OriginalInspectorReactionAgent>();
                extension.Configure(this, agent, mod);
                configuredAgents++;
                Debug.Log("[OriginalInspector v6] Native agent configured: " + agent.name +
                    "; personality=" + personality + "; gait=" + walkAnimation, agent);
            }
        }
        if (Time.unscaledTime < nextReport) return;
        nextReport = Time.unscaledTime + 1f;
        try {
            var sean = SEAN.SEAN.instance;
            if (!sean || !(sean.pedestrianBehavior is SEAN.Scenario.PedestrianBehavior.ConfigurableSpawner)) {
                status = "Select ConfigurableSpawner in the SEAN pedestrian behavior selector"; return;
            }
            var robot = sean.robot;
            activeRobot = robot ? robot.gameObject : null;
            status = !activeRobot ? "Original system has no active robot" :
                configuredAgents == 0 ? "Waiting for original spawner; confirm ConfigurableSpawnerRoot is active" :
                "Original pipeline active; robot=" + activeRobot.name + "; agents=" + configuredAgents;
        } catch (System.Exception ex) {
            status = "Original scenario/robot lookup: " + ex.Message;
        }
    }

    PedestrianModulator.PersonalityType OriginalPersonality() {
        switch (personality) {
            case Reaction.Curious: return PedestrianModulator.PersonalityType.Curious;
            case Reaction.Scared: return PedestrianModulator.PersonalityType.Scared;
            case Reaction.Surprised: return PedestrianModulator.PersonalityType.Surprised;
            case Reaction.Assertive: return PedestrianModulator.PersonalityType.Assertive;
            default: return PedestrianModulator.PersonalityType.Indifferent;
        }
    }
    public string ClipName() {
        switch (walkAnimation) {
            case WalkAnimation.KimodoRelaxed: return "kimodo_relaxed_walk";
            case WalkAnimation.KimodoElderly: return "kimodo_elderly_shuffle";
            case WalkAnimation.OldManWalk: return "Old_Man_Walk";
            case WalkAnimation.DrunkWalk: return "Drunk_Walk";
            case WalkAnimation.CarryAndWalk: return "carry_and_walk";
            case WalkAnimation.PacingPhone: return "Pacing_And_Talking_On_A_Phone";
            default: return "";
        }
    }
    void Error(string message) {
        status = "ERROR: " + message;
        Debug.LogError("[OriginalInspector v6] " + message, this);
        enabled = false;
    }
    void OnDestroy() {
        // Do not destroy native agents or interfere with the manager lifecycle.
        if (ownsConfiguration && originalSpawner) {
            originalSpawner.spawnGroups = previousGroups;
            originalSpawner.agentPrefab = previousPrefab;
        }
    }
}

#if UNITY_EDITOR
[UnityEditor.CustomEditor(typeof(InspectorPedestrianDemo))]
public class InspectorPedestrianDemoEditor : UnityEditor.Editor
{
    void Field(string n) { UnityEditor.EditorGUILayout.PropertyField(serializedObject.FindProperty(n), true); }
    public override void OnInspectorGUI()
    {
        serializedObject.Update();
        using (new UnityEditor.EditorGUI.DisabledScope(true)) Field("m_Script");
        UnityEditor.EditorGUILayout.HelpBox("Version 6: native PedestrianSpawner / SFAgent movement. No added NavMesh boundary constraint. Configure before Play. Clip reactions are stop-and-play; they do not include AutoTrial contact IK or full S68 timing.", UnityEditor.MessageType.Info);
        using (new UnityEditor.EditorGUI.DisabledScope(Application.isPlaying)) {
            Field("originalSpawner"); Field("simpleAppearanceAgent"); Field("robotTrunk");
            Field("spawnPoint"); Field("patrolA"); Field("patrolB"); Field("count");
            Field("walkAnimation"); Field("personality");
            var kind = (InspectorPedestrianDemo.Reaction)serializedObject.FindProperty("personality").enumValueIndex;
            bool custom = false, crouch = false, nativeMovement = false;
            switch (kind) {
                case InspectorPedestrianDemo.Reaction.Curious:
                    Field("curiousReaction"); int c = serializedObject.FindProperty("curiousReaction").enumValueIndex;
                    nativeMovement = c == 0; crouch = c == 2; custom = c == 3;
                    if (c == 0) { Field("followDistance"); Field("curiousExitMultiplier"); }
                    if (c == 1) Field("reactionHoldSeconds");
                    break;
                case InspectorPedestrianDemo.Reaction.Scared:
                    Field("scaredReaction"); nativeMovement = serializedObject.FindProperty("scaredReaction").enumValueIndex == 0; custom = !nativeMovement; break;
                case InspectorPedestrianDemo.Reaction.Surprised:
                    Field("surprisedReaction"); custom = serializedObject.FindProperty("surprisedReaction").enumValueIndex == 2;
                    if (serializedObject.FindProperty("surprisedReaction").enumValueIndex == 0) Field("surpriseFreezeSeconds");
                    break;
                case InspectorPedestrianDemo.Reaction.Assertive:
                    Field("assertiveReaction"); int a = serializedObject.FindProperty("assertiveReaction").enumValueIndex;
                    nativeMovement = a == 0; custom = a == 2; break;
                default: nativeMovement = true; break;
            }
            if (nativeMovement) {
                Field("playGestureBeforeNativeBehavior");
                custom |= serializedObject.FindProperty("playGestureBeforeNativeBehavior").boolValue;
            }
            if (custom) Field("customReactionClip");
            if (crouch) { Field("crouchClipOverride"); Field("kneelAtClipEnd"); Field("crouchTransitionSeconds"); Field("crouchHoldSeconds"); Field("standUpIfCloserThan"); }
            Field("reactionPlaybackSpeed"); Field("reactionBlendSeconds");
            Field("walkSpeedMultiplier"); Field("triggerDistance"); Field("releaseMargin"); Field("cooldownSeconds");
            if (serializedObject.FindProperty("walkAnimation").enumValueIndex == 5) Field("carryBox");
        }
        using (new UnityEditor.EditorGUI.DisabledScope(true)) { Field("status"); Field("activeRobot"); Field("configuredAgents"); }
        serializedObject.ApplyModifiedProperties();
    }
}
#endif
