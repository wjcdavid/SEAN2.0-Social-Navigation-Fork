
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.AI;

namespace SEAN.Scenario.Agents
{
    /// <summary>
    /// Dynamic O/L F-formation group.
    ///
    /// Main idea:
    /// 1. Starts like StaticSocialGroupSpawner: visible members are placed in O/L formation.
    /// 2. In the Inspector, choose Reformation Mode:
    ///    - AttractArc: movable members form a robot-centered attention arc.
    ///    - SplitCorridor: movable members split left/right from their current formation slots and open a corridor.
    /// 3. SplitCorridor can require that the robot is facing the original group center before triggering.
    /// 4. During active reformation and return, per-person social-force proxies are used.
    ///    Before trigger and after full restoration/cooldown, a group-level proxy is used.
    /// 5. SEAN's existing social-force implementation itself is not modified.
    /// </summary>
    public class DynamicAttentionGroupSpawner : MonoBehaviour
    {
        public enum FormationType
        {
            OShape,
            LShape
        }

        public enum CharacterSource
        {
            SimpleAppearanceAgent,
            RandomRocketbox
        }

        public enum MemberType
        {
            Ordinary,
            Special
        }

        public enum ReformationMode
        {
            AttractArc,
            SplitCorridor
        }

        public enum AttentionFocusMode
        {
            /// <summary>AttractArc is built around the robot position captured at the trigger moment. Members do not chase the robot.</summary>
            LockRobotPositionAtTrigger,

            /// <summary>AttractArc is continuously rebuilt around the current robot position. Members may follow the robot while active.</summary>
            FollowRobotWhileActive
        }

        public enum AttentionArcDirectionMode
        {
            /// <summary>Arc center points from attention focus toward the original group center. Recommended for approach-side arc.</summary>
            FromAttentionFocusToOriginalGroup,

            /// <summary>Arc center points from original group center toward the attention focus.</summary>
            FromOriginalGroupToAttentionFocus,

            /// <summary>Arc center uses robot forward at trigger.</summary>
            RobotForwardAtTrigger,

            /// <summary>Arc center uses robot backward at trigger.</summary>
            RobotBackwardAtTrigger,

            /// <summary>Arc center follows current robot forward while active.</summary>
            CurrentRobotForward,

            /// <summary>Arc center uses Custom Arc World Yaw Degrees.</summary>
            CustomWorldYaw
        }

        public enum SplitCorridorDirectionMode
        {
            /// <summary>Corridor direction points from the robot trigger position toward the original group center. Recommended.</summary>
            FromRobotTriggerPositionToOriginalGroup,

            /// <summary>Corridor direction uses the robot heading captured at trigger.</summary>
            RobotHeadingAtTrigger,

            /// <summary>Corridor direction uses Custom Split Corridor World Yaw Degrees.</summary>
            CustomWorldYaw
        }

        public enum RobotHeadingDirection
        {
            TransformForward,
            TransformBackward
        }

        public enum SlotOrder
        {
            KeepMemberListOrder,
            PreserveInitialLeftRightOrder,
            SortByAngleAroundGroup
        }

        private enum RuntimeState
        {
            InitialGroup,
            AttractActive,
            SplitActive,
            ReturningToInitial,
            Cooldown
        }

        [System.Serializable]
        public class MemberConfig
        {
            [Tooltip("Ordinary = no personality reaction; Special = personality-capable.")]
            public MemberType type = MemberType.Ordinary;

            [Tooltip("When enabled, Ordinary uses RandomRocketbox and Special uses SimpleAppearanceAgent.")]
            public bool automaticSource = true;

            [Tooltip("Used only when Automatic Source is disabled.")]
            public CharacterSource source = CharacterSource.RandomRocketbox;

            [Tooltip("Used only for Special members whose effective source is SimpleAppearanceAgent.")]
            public PedestrianModulator.PersonalityType personality =
                PedestrianModulator.PersonalityType.Indifferent;

            [Tooltip("If true, this member moves during AttractArc or SplitCorridor. If false, the member stays in the original slot but can still look at the robot and play personality animations.")]
            public bool moveDuringReformation = true;

            [Tooltip("Current native reaction options. Configure before Play; original walking animation is retained.")]
            public GroupReactionSettings nativeReaction = new GroupReactionSettings();

            public CharacterSource GetEffectiveSource()
            {
                if (!automaticSource)
                {
                    return source;
                }

                return type == MemberType.Special
                    ? CharacterSource.SimpleAppearanceAgent
                    : CharacterSource.RandomRocketbox;
            }
        }

        private class MemberRuntime
        {
            public bool proxyWanted;
            public Transform root;
            public GroupMemberNativeReaction nativeReaction;
            public Animator animator;
            public PedestrianModulator.PersonalityType personality;
            public bool personalityEnabled;
            public bool moveDuringReformation;
            public Vector3 initialPosition;
            public Vector3 initialForward;
            public Vector3 currentTarget;
            public GameObject proxyContainer;
            public DynamicAttentionSocialProxyFollower proxyFollower;
            public bool reactionTriggered;
        }

        [Header("Generation")]
        public bool generateOnStart = true;
        public bool clearExistingBeforeGenerate = true;

        [Tooltip("The GameObject transform is the requested initial group center.")]
        public FormationType formation = FormationType.OShape;

        [Min(2)]
        public int memberCount = 4;

        [Tooltip("Additional yaw rotation around the initial group center.")]
        public float formationYawDegrees = 0f;

        [Header("O Shape")]
        [Min(0.1f)]
        public float oRadius = 0.8f;

        [Header("L Shape")]
        [Min(0.1f)]
        public float lInterPersonDistance = 0.8f;

        [Range(10f, 170f)]
        public float lAngleDegrees = 90f;

        public bool lIncludeCornerMember = true;

        [Header("Main Reformation Mode")]
        [Tooltip("AttractArc = people form a robot-centered attention arc. SplitCorridor = people split left/right to open a passage.")]
        public ReformationMode reformationMode = ReformationMode.AttractArc;

        [Header("Shared Trigger / Return")]
        public Transform robot;

        [Tooltip("Robot distance to original group center that triggers reformation.")]
        [Min(0.1f)]
        public float triggerDistance = 4f;

        [Tooltip("Robot distance to original group center used for return / invalid distance checks.")]
        [Min(0.1f)]
        public float resetDistance = 5f;

        [Tooltip("When true, members return to the original O/L formation after return conditions are met.")]
        public bool returnToInitialFormation = true;

        [Tooltip("For AttractArc: return immediately when robot leaves reset distance. For SplitCorridor: this distance invalid condition can be required continuously for Invalid Condition Grace Seconds.")]
        public bool returnWhenRobotLeavesResetDistance = true;

        [Tooltip("Return automatically after Max Active Duration Seconds. Usually false for manual robot experiments.")]
        public bool limitActiveDuration = false;

        [Tooltip("Maximum time the group stays active after each trigger, if Limit Active Duration is enabled.")]
        [Min(0.1f)]
        public float maxActiveDurationSeconds = 3f;

        [Tooltip("Members are considered restored when all are within this X/Z distance from their original slots.")]
        [Min(0.01f)]
        public float restorePositionTolerance = 0.15f;

        [Tooltip("When true, after returning, the group cannot trigger again until the robot exits Reset Distance.")]
        public bool requireRobotExitBeforeRetrigger = false;

        [Tooltip("Cooldown after full restoration before another trigger is allowed.")]
        [Min(0f)]
        public float retriggerCooldownSeconds = 2f;

        [Header("Attract Arc Settings")]
        [Tooltip("Only used when Reformation Mode = AttractArc.")]
        public AttentionFocusMode attentionFocusMode = AttentionFocusMode.LockRobotPositionAtTrigger;

        [Tooltip("Arc center direction for AttractArc.")]
        public AttentionArcDirectionMode attentionArcDirectionMode =
            AttentionArcDirectionMode.FromAttentionFocusToOriginalGroup;

        [Tooltip("Used only when Attention Arc Direction Mode = CustomWorldYaw.")]
        public float customArcWorldYawDegrees = 0f;

        [Tooltip("Preferred radius of the attention arc around the attention focus.")]
        [Min(0.1f)]
        public float attentionRadius = 2.0f;

        [Tooltip("Minimum robot-person distance used by AttractArc.")]
        [Min(0.1f)]
        public float minRobotPersonDistance = 1.5f;

        [Tooltip("Direct angular span used for movable members in AttractArc. Only movable members are counted.")]
        [Range(30f, 240f)]
        public float attentionArcSpanDegrees = 140f;

        [Tooltip("Minimum spacing between people along the AttractArc. Radius is expanded automatically when needed.")]
        [Min(0.1f)]
        public float minPersonPersonDistance = 0.75f;

        [Tooltip("When true, AttractArc radius is clamped below Trigger Distance to avoid placing targets behind the original group or outside the NavMesh.")]
        public bool clampAttentionRadiusBelowTriggerDistance = true;

        [Tooltip("Safety margin used when clamping Attention Radius below Trigger Distance.")]
        [Min(0f)]
        public float attentionRadiusTriggerSafetyMargin = 0.2f;

        [Header("Split Corridor Settings")]
        [Tooltip("Only used when Reformation Mode = SplitCorridor. If enabled, robot must face the original group center before the corridor opens.")]
        public bool requireRobotHeadingTowardGroupToTrigger = true;

        [Tooltip("Maximum angle between robot heading and direction to original group center for SplitCorridor trigger.")]
        [Range(1f, 180f)]
        public float triggerHeadingMaxAngleDegrees = 60f;

        [Tooltip("How to interpret robot heading. Use TransformBackward if the selected robot transform's blue axis points backward.")]
        public RobotHeadingDirection robotHeadingDirection = RobotHeadingDirection.TransformForward;

        [Tooltip("Direction used as the split corridor centerline.")]
        public SplitCorridorDirectionMode splitCorridorDirectionMode =
            SplitCorridorDirectionMode.FromRobotTriggerPositionToOriginalGroup;

        [Tooltip("Used only when Split Corridor Direction Mode = CustomWorldYaw.")]
        public float customSplitCorridorWorldYawDegrees = 0f;

        [Tooltip("For SplitCorridor, targets are computed from each member's original formation slot. This optional offset shifts all split targets along the corridor direction. Keep 0 for pure in-place splitting.")]
        [Min(0f)]
        public float splitForwardOffset = 0f;

        [Tooltip("Minimum left/right distance from the corridor centerline after splitting. This is half of the intended passage width.")]
        [Min(0.1f)]
        public float splitSideOffset = 1.0f;

        [Tooltip("Optional additional spacing along the corridor direction for people assigned to the same side. Keep 0 to preserve the original along-corridor positions.")]
        [Min(0f)]
        public float splitAlongPathSpacing = 0f;

        [Tooltip("If true and the movable count is odd, the middle mover is assigned based on its original side. If false, the middle mover is assigned to the right side.")]
        public bool splitOddMemberByInitialSide = true;

        [Tooltip("If true, SplitCorridor members turn to face the robot while opening/holding the corridor. If false, they keep their original formation-facing direction.")]
        public bool splitMembersFaceRobot = true;

        [Tooltip("If true, the opened corridor closes when the robot heading no longer points toward the original group center for Invalid Condition Grace Seconds.")]
        public bool returnIfRobotHeadingNotTowardGroup = true;

        [Tooltip("Active-stage maximum angle between robot heading and direction to original group center before the heading is considered invalid.")]
        [Range(1f, 180f)]
        public float activeHeadingMaxAngleDegrees = 75f;

        [Tooltip("For SplitCorridor, heading/distance must remain invalid for this many continuous seconds before returning.")]
        [Min(0f)]
        public float invalidConditionGraceSeconds = 2.0f;

        [Header("Slot Assignment")]
        [Tooltip("PreserveInitialLeftRightOrder avoids crossing paths: left-side movers go to left slots, right-side movers go to right slots.")]
        public SlotOrder slotOrder = SlotOrder.PreserveInitialLeftRightOrder;

        [Header("Movement / Facing")]
        [Tooltip("How fast visible members move toward their current target slot.")]
        [Min(0f)]
        public float memberMoveSpeed = 0.7f;

        [Tooltip("How fast visible members turn toward the current social focus.")]
        [Min(0f)]
        public float memberTurnSpeed = 6f;

        [Tooltip("Small distance at which the member is considered to have reached its target.")]
        [Min(0.001f)]
        public float targetTolerance = 0.05f;

        [Tooltip("If true, AttractArc members face the robot while moving on the arc. SplitCorridor uses Split Members Face Robot.")]
        public bool allMembersLookAtRobot = true;

        [Header("Per-Member Settings")]
        [Tooltip("This list is automatically resized to Member Count.")]
        public List<MemberConfig> members = new List<MemberConfig>();

        [Header("Appearance Sources")]
        [Tooltip("Assign Assets/Resources/Prefabs/SimpleAppearanceAgent.prefab.")]
        public GameObject simpleAppearanceAgentPrefab;

        [Tooltip("Resources path for ordinary Rocketbox avatar prefabs.")]
        public string rocketboxResourcesPath = "Prefabs/Rocketbox";

        [Header("Hybrid Social-Force Proxies")]
        [Tooltip("Assign SimpleAppearanceAgent. The same prefab is used for the initial group proxy and the per-person proxies.")]
        public GameObject socialProxyPrefab;

        [Tooltip("Before trigger and after full restoration/cooldown, one invisible proxy represents the whole social group.")]
        public bool useGroupProxyWhenInactive = true;

        [Tooltip("During active reformation and return, individual invisible proxies follow each visible person.")]
        public bool usePerPersonProxiesWhenActiveOrReturning = true;

        [Tooltip("Approximate normal-person radius used only if the SFAgent radius field cannot be found.")]
        [Min(0.01f)]
        public float proxyBaseRadius = 0.35f;

        [Tooltip("Added to the outermost visible member when computing the initial group proxy radius.")]
        [Min(0f)]
        public float groupProxyPadding = 0.35f;

        [Tooltip("When true, the initial group-level proxy radius is calculated from the generated formation.")]
        public bool calculateGroupProxyRadiusAutomatically = true;

        [Tooltip("Used only when Calculate Group Proxy Radius Automatically is false.")]
        [Min(0.1f)]
        public float manualGroupProxyRadius = 1.5f;

        [SerializeField]
        private float computedGroupProxyRadius;

        [Tooltip("Target social-force radius for each invisible per-person proxy after trigger.")]
        [Min(0.01f)]
        public float perPersonProxyRadius = 0.35f;

        [Tooltip("When true, each per-person proxy calls InitDest every frame at its current followed position.")]
        public bool updateProxyDestEveryFrame = true;

        [Header("NavMesh Validation")]
        [Tooltip("When enabled, initial slots and dynamic target slots keep exact X/Z and only take Y from NavMesh.")]
        public bool requireNavMesh = true;

        [Tooltip("Small local probe radius used while searching vertically at the exact requested X/Z.")]
        [Min(0.01f)]
        public float navMeshSampleDistance = 0.25f;

        [Tooltip("How far above and below Transform Y to search for a NavMesh surface at the exact requested X/Z.")]
        [Min(0.1f)]
        public float navMeshVerticalSearchDistance = 20f;

        [Tooltip("Vertical interval between NavMesh probes.")]
        [Min(0.05f)]
        public float navMeshVerticalStep = 0.25f;

        [Tooltip("Maximum permitted X/Z difference. No horizontal snapping is performed.")]
        [Min(0.001f)]
        public float navMeshHorizontalTolerance = 0.02f;

        [Header("Debug")]
        public bool drawGizmos = true;
        public bool logDetails = true;
        public bool drawDynamicTargets = true;
        public bool logHeadingDiagnostics = false;

        private Transform generatedRoot;
        private Vector3 originalGroupCenter;
        private Vector3 lockedAttentionFocus;
        private bool hasLockedAttentionFocus;
        private Vector3 lockedArcDirection;
        private bool hasLockedArcDirection;
        private Vector3 lockedSplitCorridorDirection;
        private bool hasLockedSplitCorridorDirection;
        private Quaternion formationRotation;
        private readonly List<MemberRuntime> spawnedMembers = new List<MemberRuntime>();
        private readonly List<Vector3> dynamicTargets = new List<Vector3>();
        private GameObject[] rocketboxPool;
        private RuntimeState state = RuntimeState.InitialGroup;
        private float activeStartTime = -1f;
        private float nextAllowedTriggerTime = 0f;
        private bool triggerArmed = true;
        private float invalidConditionStartTime = -1f;
        private GameObject groupProxyContainer;
        private DynamicAttentionGroupProxyLock groupProxyLock;
        private bool hasGenerated;

        private void OnValidate()
        {
            memberCount = Mathf.Max(2, memberCount);
            oRadius = Mathf.Max(0.1f, oRadius);
            lInterPersonDistance = Mathf.Max(0.1f, lInterPersonDistance);
            resetDistance = Mathf.Max(triggerDistance, resetDistance);
            maxActiveDurationSeconds = Mathf.Max(0.1f, maxActiveDurationSeconds);
            restorePositionTolerance = Mathf.Max(0.01f, restorePositionTolerance);
            retriggerCooldownSeconds = Mathf.Max(0f, retriggerCooldownSeconds);

            attentionRadius = Mathf.Max(0.1f, attentionRadius);
            minRobotPersonDistance = Mathf.Max(0.1f, minRobotPersonDistance);
            attentionArcSpanDegrees = Mathf.Clamp(attentionArcSpanDegrees, 30f, 240f);
            minPersonPersonDistance = Mathf.Max(0.1f, minPersonPersonDistance);
            attentionRadiusTriggerSafetyMargin = Mathf.Max(0f, attentionRadiusTriggerSafetyMargin);

            splitForwardOffset = Mathf.Max(0f, splitForwardOffset);
            splitSideOffset = Mathf.Max(0.1f, splitSideOffset);
            splitAlongPathSpacing = Mathf.Max(0f, splitAlongPathSpacing);
            invalidConditionGraceSeconds = Mathf.Max(0f, invalidConditionGraceSeconds);

            memberMoveSpeed = Mathf.Max(0f, memberMoveSpeed);
            memberTurnSpeed = Mathf.Max(0f, memberTurnSpeed);
            targetTolerance = Mathf.Max(0.001f, targetTolerance);

            groupProxyPadding = Mathf.Max(0f, groupProxyPadding);
            manualGroupProxyRadius = Mathf.Max(0.1f, manualGroupProxyRadius);
            perPersonProxyRadius = Mathf.Max(0.01f, perPersonProxyRadius);
            proxyBaseRadius = Mathf.Max(0.01f, proxyBaseRadius);

            navMeshSampleDistance = Mathf.Max(0.01f, navMeshSampleDistance);
            navMeshVerticalSearchDistance = Mathf.Max(0.1f, navMeshVerticalSearchDistance);
            navMeshVerticalStep = Mathf.Max(0.05f, navMeshVerticalStep);
            navMeshHorizontalTolerance = Mathf.Max(0.001f, navMeshHorizontalTolerance);

            EnsureMemberListSize();
        }

        private void Start()
        {
            if (generateOnStart)
            {
                GenerateGroup();
            }
        }

        private void Update()
        {
            if (!hasGenerated || robot == null || spawnedMembers.Count == 0)
            {
                return;
            }

            float distanceToOriginalCenter = XZDistance(robot.position, originalGroupCenter);

            if (state == RuntimeState.InitialGroup)
            {
                if (!triggerArmed && requireRobotExitBeforeRetrigger &&
                    distanceToOriginalCenter >= resetDistance)
                {
                    triggerArmed = true;
                }

                bool triggerDistanceOk = distanceToOriginalCenter <= triggerDistance;
                bool triggerCooldownOk = Time.time >= nextAllowedTriggerTime;
                bool triggerHeadingOk = CanTriggerByHeading();

                if (triggerArmed && triggerCooldownOk && triggerDistanceOk && triggerHeadingOk)
                {
                    StartActiveReformation();
                }
            }
            else if (state == RuntimeState.AttractActive || state == RuntimeState.SplitActive)
            {
                UpdateDynamicTargets();

                if (ShouldReturnFromActive(distanceToOriginalCenter))
                {
                    StartReturnToInitialFormation();
                }
            }
            else if (state == RuntimeState.ReturningToInitial)
            {
                if (AllMembersRestored())
                {
                    FinishRestoreToInitialGroup();
                }
            }
            else if (state == RuntimeState.Cooldown)
            {
                if (Time.time >= nextAllowedTriggerTime)
                {
                    state = RuntimeState.InitialGroup;
                    triggerArmed = !requireRobotExitBeforeRetrigger ||
                                   distanceToOriginalCenter >= resetDistance;

                    if (logDetails)
                    {
                        Debug.Log("[DynamicAttentionGroupSpawner] Cooldown finished. Ready for next trigger.", this);
                    }
                }
            }

            UpdateMembers(Time.deltaTime);
        }

        [ContextMenu("Generate Dynamic Attention Group")]
        public void GenerateGroup()
        {
            EnsureMemberListSize();

            if (clearExistingBeforeGenerate)
            {
                ClearGroup();
            }

            if (simpleAppearanceAgentPrefab == null)
            {
                Debug.LogWarning(
                    "[DynamicAttentionGroupSpawner] SimpleAppearanceAgent Prefab is not assigned. " +
                    "Members that request SimpleAppearanceAgent/personality appearance will fall back to Rocketbox if available.",
                    this);
            }

            if (socialProxyPrefab == null)
            {
                Debug.LogError(
                    "[DynamicAttentionGroupSpawner] Assign Social Proxy Prefab, normally SimpleAppearanceAgent.",
                    this);
                return;
            }

            Vector3 requestedCenter = transform.position;

            if (requireNavMesh)
            {
                if (!TryFindNavMeshAtExactXZ(
                        requestedCenter,
                        out originalGroupCenter,
                        "Group center"))
                {
                    return;
                }
            }
            else
            {
                originalGroupCenter = requestedCenter;
            }

            formationRotation =
                Quaternion.AngleAxis(transform.eulerAngles.y + formationYawDegrees, Vector3.up);

            List<Vector3> rawPositions = new List<Vector3>();
            List<Vector3> forwards = new List<Vector3>();

            if (formation == FormationType.OShape)
            {
                BuildOFormation(rawPositions, forwards);
            }
            else
            {
                BuildLFormation(rawPositions, forwards);
            }

            List<Vector3> finalPositions = new List<Vector3>();

            if (requireNavMesh)
            {
                if (!ValidateSlotsAtExactXZ(rawPositions, finalPositions))
                {
                    return;
                }
            }
            else
            {
                finalPositions.AddRange(rawPositions);
            }

            GameObject rootObject = new GameObject("GeneratedDynamicAttentionGroup");
            generatedRoot = rootObject.transform;
            generatedRoot.SetParent(transform, true);
            generatedRoot.position = originalGroupCenter;
            generatedRoot.rotation = Quaternion.identity;

            LoadRocketboxPoolIfNeeded();

            float farthestMemberDistance = 0f;

            for (int i = 0; i < memberCount; i++)
            {
                MemberConfig config = members[i];
                GameObject person = CreateVisibleMember(
                    config,
                    finalPositions[i],
                    forwards[i],
                    i,
                    out Animator animator,
                    out bool personalityEnabled);

                if (person == null)
                {
                    ClearGroup();
                    return;
                }

                person.transform.SetParent(generatedRoot, true);

                MemberRuntime runtime = new MemberRuntime();
                runtime.root = person.transform;
                runtime.animator = animator;
                runtime.personality = personalityEnabled
                    ? config.personality
                    : PedestrianModulator.PersonalityType.Indifferent;
                runtime.personalityEnabled = personalityEnabled;
                runtime.moveDuringReformation = config.moveDuringReformation;
                runtime.initialPosition = finalPositions[i];
                runtime.initialForward = forwards[i].sqrMagnitude > 0.001f
                    ? forwards[i].normalized
                    : formationRotation * Vector3.forward;
                runtime.currentTarget = finalPositions[i];

                farthestMemberDistance = Mathf.Max(
                    farthestMemberDistance,
                    XZDistance(originalGroupCenter, finalPositions[i]));

                if (personalityEnabled)
                {
                    runtime.nativeReaction = GroupMemberNativeReaction.Attach(person, config.nativeReaction,
                        config.personality, this, () => robot, () => true, triggerDistance, resetDistance);
                    if (runtime.nativeReaction) runtime.root = runtime.nativeReaction.transform;
                }
                CreatePerPersonSocialProxy(runtime, i);
                if (runtime.proxyContainer) runtime.proxyContainer.AddComponent<GroupProxyPresence>();

                spawnedMembers.Add(runtime);
            }

            computedGroupProxyRadius = calculateGroupProxyRadiusAutomatically
                ? farthestMemberDistance + groupProxyPadding
                : manualGroupProxyRadius;

            CreateGroupSocialProxy(computedGroupProxyRadius);

            state = RuntimeState.InitialGroup;
            activeStartTime = -1f;
            nextAllowedTriggerTime = 0f;
            triggerArmed = true;
            invalidConditionStartTime = -1f;
            hasLockedAttentionFocus = false;
            hasLockedArcDirection = false;
            hasLockedSplitCorridorDirection = false;
            lockedAttentionFocus = Vector3.zero;
            lockedArcDirection = Vector3.forward;
            lockedSplitCorridorDirection = Vector3.forward;
            SetGroupProxyActive(useGroupProxyWhenInactive);
            SetPerPersonProxiesActive(false);
            hasGenerated = true;

            if (logDetails)
            {
                Debug.Log(
                    "[DynamicAttentionGroupSpawner] Generated " + formation +
                    " with " + memberCount +
                    " members. Movable during reformation = " + GetMovableMemberCount() +
                    ". Mode = " + reformationMode + ".",
                    this);
            }
        }

        [ContextMenu("Clear Dynamic Attention Group")]
        public void ClearGroup()
        {
            spawnedMembers.Clear();
            dynamicTargets.Clear();
            state = RuntimeState.InitialGroup;
            activeStartTime = -1f;
            nextAllowedTriggerTime = 0f;
            triggerArmed = true;
            invalidConditionStartTime = -1f;
            hasLockedAttentionFocus = false;
            hasLockedArcDirection = false;
            hasLockedSplitCorridorDirection = false;
            lockedAttentionFocus = Vector3.zero;
            lockedArcDirection = Vector3.forward;
            lockedSplitCorridorDirection = Vector3.forward;
            groupProxyContainer = null;
            groupProxyLock = null;
            hasGenerated = false;

            Transform oldRoot = transform.Find("GeneratedDynamicAttentionGroup");
            if (oldRoot == null)
            {
                generatedRoot = null;
                return;
            }

            if (Application.isPlaying)
            {
                Destroy(oldRoot.gameObject);
            }
            else
            {
                DestroyImmediate(oldRoot.gameObject);
            }

            generatedRoot = null;
        }

        private void EnsureMemberListSize()
        {
            if (members == null)
            {
                members = new List<MemberConfig>();
            }

            while (members.Count < memberCount)
            {
                members.Add(new MemberConfig());
            }

            while (members.Count > memberCount)
            {
                members.RemoveAt(members.Count - 1);
            }

            for (int i = 0; i < members.Count; i++)
            {
                MemberConfig config = members[i];
                if (config == null)
                {
                    config = new MemberConfig();
                    members[i] = config;
                }

                if (config.automaticSource)
                {
                    config.source = config.type == MemberType.Special
                        ? CharacterSource.SimpleAppearanceAgent
                        : CharacterSource.RandomRocketbox;
                }

                if (config.type == MemberType.Ordinary)
                {
                    config.personality = PedestrianModulator.PersonalityType.Indifferent;
                }
            }
        }

        private void BuildOFormation(List<Vector3> positions, List<Vector3> forwards)
        {
            for (int i = 0; i < memberCount; i++)
            {
                float angle = 360f * i / memberCount;
                Vector3 localOffset =
                    Quaternion.AngleAxis(angle, Vector3.up) * Vector3.forward * oRadius;

                Vector3 position = originalGroupCenter + formationRotation * localOffset;
                Vector3 forward = originalGroupCenter - position;
                forward.y = 0f;

                positions.Add(position);
                forwards.Add(
                    forward.sqrMagnitude > 0.001f
                        ? forward.normalized
                        : formationRotation * Vector3.forward);
            }
        }

        private void BuildLFormation(List<Vector3> positions, List<Vector3> forwards)
        {
            Vector3 armA = formationRotation * Vector3.forward;
            Vector3 armB = Quaternion.AngleAxis(lAngleDegrees, Vector3.up) * armA;

            armA.y = 0f;
            armB.y = 0f;
            armA.Normalize();
            armB.Normalize();

            Vector3 interior = armA + armB;
            interior.y = 0f;
            if (interior.sqrMagnitude < 0.001f)
            {
                interior = armA;
            }
            interior.Normalize();

            int startIndex = lIncludeCornerMember ? 1 : 0;

            for (int i = 0; i < memberCount; i++)
            {
                Vector3 position;
                Vector3 forward;

                if (lIncludeCornerMember && i == 0)
                {
                    position = originalGroupCenter;
                    forward = interior;
                }
                else
                {
                    int armMemberIndex = i - startIndex;
                    int remaining = memberCount - startIndex;
                    int armACount = Mathf.CeilToInt(remaining / 2f);

                    Vector3 arm;
                    int step;

                    if (armMemberIndex < armACount)
                    {
                        arm = armA;
                        step = armMemberIndex + 1;
                    }
                    else
                    {
                        arm = armB;
                        step = armMemberIndex - armACount + 1;
                    }

                    position = originalGroupCenter + arm * lInterPersonDistance * step;
                    forward = PerpendicularTowardInterior(arm, interior);
                }

                positions.Add(position);
                forwards.Add(forward.normalized);
            }
        }

        private bool CanTriggerByHeading()
        {
            if (reformationMode != ReformationMode.SplitCorridor ||
                !requireRobotHeadingTowardGroupToTrigger)
            {
                return true;
            }

            bool headingOk = IsRobotHeadingTowardOriginalGroup(triggerHeadingMaxAngleDegrees);

            if (logHeadingDiagnostics && robot != null)
            {
                float angle = GetRobotHeadingAngleToOriginalGroup();
                Debug.Log(
                    "[DynamicAttentionGroupSpawner] Trigger heading angle to group center = " +
                    angle.ToString("F1") +
                    " deg. OK = " + headingOk,
                    this);
            }

            return headingOk;
        }

        private void StartActiveReformation()
        {
            state = reformationMode == ReformationMode.SplitCorridor
                ? RuntimeState.SplitActive
                : RuntimeState.AttractActive;

            activeStartTime = Time.time;
            invalidConditionStartTime = -1f;

            if (robot != null)
            {
                lockedAttentionFocus = robot.position;
                lockedAttentionFocus.y = originalGroupCenter.y;
                hasLockedAttentionFocus = true;
            }

            lockedArcDirection = ResolveAttractArcDirection(true);
            hasLockedArcDirection = lockedArcDirection.sqrMagnitude > 0.001f;

            lockedSplitCorridorDirection = ResolveSplitCorridorDirection(true);
            hasLockedSplitCorridorDirection = lockedSplitCorridorDirection.sqrMagnitude > 0.001f;

            if (requireRobotExitBeforeRetrigger)
            {
                triggerArmed = false;
            }

            SnapPerPersonProxiesToMembers();
            SetPerPersonProxiesActive(usePerPersonProxiesWhenActiveOrReturning);
            SetGroupProxyActive(false);
            TriggerAllPersonalityAnimationsOnce();
            UpdateDynamicTargets();

            if (logDetails)
            {
                Debug.Log(
                    "[DynamicAttentionGroupSpawner] Reformation triggered. Mode = " +
                    reformationMode +
                    ". Social force switched from group-level to per-person proxies.",
                    this);
            }
        }

        private bool ShouldReturnFromActive(float distanceToOriginalCenter)
        {
            if (!returnToInitialFormation)
            {
                return false;
            }

            bool byDuration = limitActiveDuration &&
                              activeStartTime >= 0f &&
                              Time.time - activeStartTime >= maxActiveDurationSeconds;

            if (byDuration)
            {
                return true;
            }

            if (state == RuntimeState.AttractActive)
            {
                bool byDistance = returnWhenRobotLeavesResetDistance &&
                                  distanceToOriginalCenter >= resetDistance;
                return byDistance;
            }

            if (state == RuntimeState.SplitActive)
            {
                bool invalid = false;

                if (returnWhenRobotLeavesResetDistance &&
                    distanceToOriginalCenter >= resetDistance)
                {
                    invalid = true;
                }

                if (returnIfRobotHeadingNotTowardGroup &&
                    !IsRobotHeadingTowardOriginalGroup(activeHeadingMaxAngleDegrees))
                {
                    invalid = true;
                }

                if (!invalid)
                {
                    invalidConditionStartTime = -1f;
                    return false;
                }

                if (invalidConditionStartTime < 0f)
                {
                    invalidConditionStartTime = Time.time;
                    return invalidConditionGraceSeconds <= 0f;
                }

                return Time.time - invalidConditionStartTime >= invalidConditionGraceSeconds;
            }

            return false;
        }

        private void StartReturnToInitialFormation()
        {
            state = RuntimeState.ReturningToInitial;
            dynamicTargets.Clear();
            invalidConditionStartTime = -1f;
            SetGroupProxyActive(false);
            SetPerPersonProxiesActive(usePerPersonProxiesWhenActiveOrReturning);
            ResetReactionState();

            if (logDetails)
            {
                Debug.Log(
                    "[DynamicAttentionGroupSpawner] Returning to initial formation. Per-person proxies remain active until members are restored.",
                    this);
            }
        }

        private bool AllMembersRestored()
        {
            for (int i = 0; i < spawnedMembers.Count; i++)
            {
                MemberRuntime member = spawnedMembers[i];
                if (member == null || member.root == null)
                {
                    continue;
                }

                if (XZDistance(member.root.position, member.initialPosition) > restorePositionTolerance)
                {
                    return false;
                }
            }

            return true;
        }

        private void FinishRestoreToInitialGroup()
        {
            for (int i = 0; i < spawnedMembers.Count; i++)
            {
                MemberRuntime member = spawnedMembers[i];
                if (member == null || member.root == null)
                {
                    continue;
                }

                member.root.position = member.initialPosition;
                TurnMember(member.root, member.initialForward, 1f);
                UpdateAnimatorMovement(member, member.initialPosition);
            }

            activeStartTime = -1f;
            hasLockedAttentionFocus = false;
            hasLockedArcDirection = false;
            hasLockedSplitCorridorDirection = false;
            lockedAttentionFocus = Vector3.zero;
            lockedArcDirection = Vector3.forward;
            lockedSplitCorridorDirection = Vector3.forward;
            invalidConditionStartTime = -1f;

            SetPerPersonProxiesActive(false);
            SetGroupProxyActive(useGroupProxyWhenInactive);

            nextAllowedTriggerTime = Time.time + retriggerCooldownSeconds;
            state = RuntimeState.Cooldown;

            if (!requireRobotExitBeforeRetrigger)
            {
                triggerArmed = true;
            }

            if (logDetails)
            {
                Debug.Log(
                    "[DynamicAttentionGroupSpawner] Initial group restored. Social force switched back to group-level proxy. Cooldown started.",
                    this);
            }
        }

        private Vector3 GetAttentionFocusPosition()
        {
            if (attentionFocusMode == AttentionFocusMode.LockRobotPositionAtTrigger &&
                hasLockedAttentionFocus)
            {
                return lockedAttentionFocus;
            }

            if (robot != null)
            {
                Vector3 focus = robot.position;
                focus.y = originalGroupCenter.y;
                return focus;
            }

            return originalGroupCenter;
        }

        private void UpdateDynamicTargets()
        {
            dynamicTargets.Clear();

            for (int i = 0; i < spawnedMembers.Count; i++)
            {
                MemberRuntime member = spawnedMembers[i];
                dynamicTargets.Add(member != null ? member.initialPosition : originalGroupCenter);
            }

            if (robot == null || spawnedMembers.Count == 0)
            {
                return;
            }

            if (state == RuntimeState.AttractActive)
            {
                UpdateAttractArcTargets();
            }
            else if (state == RuntimeState.SplitActive)
            {
                UpdateSplitCorridorTargets();
            }
        }

        private void UpdateAttractArcTargets()
        {
            int movableCount = GetMovableMemberCount();
            if (movableCount == 0)
            {
                return;
            }

            Vector3 focus = GetAttentionFocusPosition();
            Vector3 arcDirection = ResolveAttractArcDirection(false);
            if (arcDirection.sqrMagnitude < 0.001f)
            {
                arcDirection = Vector3.forward;
            }

            arcDirection.y = 0f;
            arcDirection.Normalize();

            float arcSpan = Mathf.Clamp(attentionArcSpanDegrees, 1f, 240f);
            float radius = GetAdjustedAttentionRadius(arcSpan, movableCount);

            if (clampAttentionRadiusBelowTriggerDistance)
            {
                float maxRadius = Mathf.Max(0.1f, triggerDistance - attentionRadiusTriggerSafetyMargin);
                if (radius > maxRadius && logDetails)
                {
                    Debug.LogWarning(
                        "[DynamicAttentionGroupSpawner] Attention radius was clamped from " +
                        radius.ToString("F2") +
                        " to " + maxRadius.ToString("F2") +
                        " because Clamp Attention Radius Below Trigger Distance is enabled.",
                        this);
                }

                radius = Mathf.Min(radius, maxRadius);
            }

            Vector3 arcRight = Vector3.Cross(Vector3.up, arcDirection).normalized;
            List<int> movableIndices = GetMovableIndicesOrdered(arcRight, originalGroupCenter);

            float centerYaw = DirectionToYaw(arcDirection);
            float startYaw = centerYaw - arcSpan * 0.5f;

            for (int slot = 0; slot < movableIndices.Count; slot++)
            {
                int memberIndex = movableIndices[slot];

                float t = (slot + 0.5f) / movableIndices.Count;
                float yaw = startYaw + arcSpan * t;
                Vector3 dir = Quaternion.AngleAxis(yaw, Vector3.up) * Vector3.forward;
                Vector3 requested = focus + dir.normalized * radius;

                dynamicTargets[memberIndex] = ResolveDynamicTargetOnNavMesh(
                    requested,
                    "AttractArc slot " + memberIndex,
                    spawnedMembers[memberIndex]);
            }
        }

        private void UpdateSplitCorridorTargets()
        {
            int movableCount = GetMovableMemberCount();
            if (movableCount == 0)
            {
                return;
            }

            Vector3 corridorDir = ResolveSplitCorridorDirection(false);
            if (corridorDir.sqrMagnitude < 0.001f)
            {
                corridorDir = originalGroupCenter - (hasLockedAttentionFocus ? lockedAttentionFocus : robot.position);
            }

            corridorDir.y = 0f;
            if (corridorDir.sqrMagnitude < 0.001f)
            {
                corridorDir = Vector3.forward;
            }
            corridorDir.Normalize();

            // The split corridor is defined through the original group center, not through the robot.
            // Members therefore split from their existing formation slots instead of walking toward the robot.
            Vector3 corridorRight = Vector3.Cross(Vector3.up, corridorDir).normalized;
            List<int> movableIndices = GetMovableIndicesOrdered(corridorRight, originalGroupCenter);

            List<int> left = new List<int>();
            List<int> right = new List<int>();

            for (int order = 0; order < movableIndices.Count; order++)
            {
                int memberIndex = movableIndices[order];
                bool assignLeft;

                if (order < movableIndices.Count / 2)
                {
                    assignLeft = true;
                }
                else if (order > movableIndices.Count / 2)
                {
                    assignLeft = false;
                }
                else
                {
                    // Middle element for odd count.
                    if (movableIndices.Count % 2 == 1)
                    {
                        if (splitOddMemberByInitialSide)
                        {
                            float lateral = Vector3.Dot(
                                spawnedMembers[memberIndex].initialPosition - originalGroupCenter,
                                corridorRight);
                            assignLeft = lateral < 0f;
                        }
                        else
                        {
                            assignLeft = false;
                        }
                    }
                    else
                    {
                        assignLeft = false;
                    }
                }

                if (assignLeft)
                {
                    left.Add(memberIndex);
                }
                else
                {
                    right.Add(memberIndex);
                }
            }

            // Avoid putting all movable members on one side when there are at least two movers.
            if (movableIndices.Count > 1)
            {
                if (left.Count == 0 && right.Count > 1)
                {
                    left.Add(right[0]);
                    right.RemoveAt(0);
                }
                else if (right.Count == 0 && left.Count > 1)
                {
                    right.Add(left[left.Count - 1]);
                    left.RemoveAt(left.Count - 1);
                }
            }

            AssignSplitSideTargets(
                left,
                -1f,
                corridorDir,
                corridorRight);

            AssignSplitSideTargets(
                right,
                1f,
                corridorDir,
                corridorRight);
        }

        private void AssignSplitSideTargets(
            List<int> sideIndices,
            float sideSign,
            Vector3 corridorDir,
            Vector3 corridorRight)
        {
            for (int j = 0; j < sideIndices.Count; j++)
            {
                int memberIndex = sideIndices[j];
                MemberRuntime member = spawnedMembers[memberIndex];
                if (member == null)
                {
                    continue;
                }

                Vector3 relative = member.initialPosition - originalGroupCenter;
                float originalAlong = Vector3.Dot(relative, corridorDir);
                float originalLateral = Vector3.Dot(relative, corridorRight);

                float extraAlongOffset = 0f;
                if (sideIndices.Count > 1 && splitAlongPathSpacing > 0f)
                {
                    extraAlongOffset = (j - (sideIndices.Count - 1) * 0.5f) * splitAlongPathSpacing;
                }

                // In-place split rule:
                // preserve the member's original along-corridor coordinate, but push it outside
                // the corridor half-width on its assigned side. This opens a passage inside
                // the original formation instead of moving people to the robot.
                float targetAlong = originalAlong + splitForwardOffset + extraAlongOffset;
                float targetLateralMagnitude = Mathf.Max(Mathf.Abs(originalLateral), splitSideOffset);
                float targetLateral = sideSign * targetLateralMagnitude;

                Vector3 requested =
                    originalGroupCenter +
                    corridorDir * targetAlong +
                    corridorRight * targetLateral;

                dynamicTargets[memberIndex] = ResolveDynamicTargetOnNavMesh(
                    requested,
                    "SplitCorridor slot " + memberIndex,
                    member);
            }
        }

        private Vector3 ResolveDynamicTargetOnNavMesh(
            Vector3 requested,
            string label,
            MemberRuntime member)
        {
            Vector3 resolved;

            if (requireNavMesh)
            {
                if (!TryFindNavMeshAtExactXZ(requested, out resolved, label))
                {
                    // If this dynamic slot falls outside the NavMesh, keep the member where it is.
                    resolved = member != null && member.root != null
                        ? member.root.position
                        : requested;
                }
            }
            else
            {
                resolved = requested;
            }

            return resolved;
        }

        private int GetMovableMemberCount()
        {
            int count = 0;
            for (int i = 0; i < spawnedMembers.Count; i++)
            {
                MemberRuntime member = spawnedMembers[i];
                if (member != null && member.moveDuringReformation)
                {
                    count++;
                }
            }

            return count;
        }

        private List<int> GetMovableIndicesOrdered(Vector3 rightReference, Vector3 centerReference)
        {
            List<int> indices = new List<int>();

            for (int i = 0; i < spawnedMembers.Count; i++)
            {
                MemberRuntime member = spawnedMembers[i];
                if (member != null && member.moveDuringReformation)
                {
                    indices.Add(i);
                }
            }

            if (slotOrder == SlotOrder.KeepMemberListOrder || indices.Count <= 1)
            {
                return indices;
            }

            if (slotOrder == SlotOrder.SortByAngleAroundGroup)
            {
                indices.Sort((a, b) =>
                {
                    Vector3 pa = spawnedMembers[a].initialPosition - originalGroupCenter;
                    Vector3 pb = spawnedMembers[b].initialPosition - originalGroupCenter;

                    float aa = Mathf.Atan2(pa.x, pa.z);
                    float ab = Mathf.Atan2(pb.x, pb.z);

                    return aa.CompareTo(ab);
                });

                return indices;
            }

            // PreserveInitialLeftRightOrder:
            // smaller lateral value = left, larger lateral value = right.
            rightReference.y = 0f;
            if (rightReference.sqrMagnitude < 0.001f)
            {
                rightReference = Vector3.right;
            }
            rightReference.Normalize();

            indices.Sort((a, b) =>
            {
                float la = Vector3.Dot(
                    spawnedMembers[a].initialPosition - centerReference,
                    rightReference);

                float lb = Vector3.Dot(
                    spawnedMembers[b].initialPosition - centerReference,
                    rightReference);

                return la.CompareTo(lb);
            });

            return indices;
        }

        private float GetAdjustedAttentionRadius(float arcSpanDegrees, int movingMemberCount)
        {
            float radius = Mathf.Max(attentionRadius, minRobotPersonDistance);

            float arcSpanRadians = Mathf.Max(0.01f, arcSpanDegrees * Mathf.Deg2Rad);
            float neededForSpacing = movingMemberCount * minPersonPersonDistance / arcSpanRadians;

            return Mathf.Max(radius, neededForSpacing);
        }

        private Vector3 ResolveAttractArcDirection(bool atTrigger)
        {
            if (!atTrigger)
            {
                if ((attentionArcDirectionMode == AttentionArcDirectionMode.RobotForwardAtTrigger ||
                     attentionArcDirectionMode == AttentionArcDirectionMode.RobotBackwardAtTrigger) &&
                    hasLockedArcDirection)
                {
                    return lockedArcDirection;
                }

                if (attentionArcDirectionMode != AttentionArcDirectionMode.CurrentRobotForward &&
                    hasLockedArcDirection &&
                    state == RuntimeState.AttractActive)
                {
                    return lockedArcDirection;
                }
            }

            Vector3 focus = GetAttentionFocusPosition();
            Vector3 direction = Vector3.forward;

            if (attentionArcDirectionMode == AttentionArcDirectionMode.FromAttentionFocusToOriginalGroup)
            {
                direction = originalGroupCenter - focus;
            }
            else if (attentionArcDirectionMode == AttentionArcDirectionMode.FromOriginalGroupToAttentionFocus)
            {
                direction = focus - originalGroupCenter;
            }
            else if (attentionArcDirectionMode == AttentionArcDirectionMode.RobotForwardAtTrigger ||
                     attentionArcDirectionMode == AttentionArcDirectionMode.CurrentRobotForward)
            {
                direction = GetRobotHeadingVector();
            }
            else if (attentionArcDirectionMode == AttentionArcDirectionMode.RobotBackwardAtTrigger)
            {
                direction = -GetRobotHeadingVector();
            }
            else if (attentionArcDirectionMode == AttentionArcDirectionMode.CustomWorldYaw)
            {
                direction = YawToDirection(customArcWorldYawDegrees);
            }

            direction.y = 0f;
            if (direction.sqrMagnitude < 0.001f)
            {
                direction = Vector3.forward;
            }

            return direction.normalized;
        }

        private Vector3 ResolveSplitCorridorDirection(bool atTrigger)
        {
            if (!atTrigger && hasLockedSplitCorridorDirection)
            {
                return lockedSplitCorridorDirection;
            }

            Vector3 focus = hasLockedAttentionFocus ? lockedAttentionFocus : robot != null ? robot.position : transform.position;
            focus.y = originalGroupCenter.y;

            Vector3 direction = Vector3.forward;

            if (splitCorridorDirectionMode == SplitCorridorDirectionMode.FromRobotTriggerPositionToOriginalGroup)
            {
                direction = originalGroupCenter - focus;
            }
            else if (splitCorridorDirectionMode == SplitCorridorDirectionMode.RobotHeadingAtTrigger)
            {
                direction = GetRobotHeadingVector();
            }
            else if (splitCorridorDirectionMode == SplitCorridorDirectionMode.CustomWorldYaw)
            {
                direction = YawToDirection(customSplitCorridorWorldYawDegrees);
            }

            direction.y = 0f;
            if (direction.sqrMagnitude < 0.001f)
            {
                direction = Vector3.forward;
            }

            return direction.normalized;
        }

        private bool IsRobotHeadingTowardOriginalGroup(float maxAngleDegrees)
        {
            if (robot == null)
            {
                return false;
            }

            Vector3 toGroup = originalGroupCenter - robot.position;
            toGroup.y = 0f;

            if (toGroup.sqrMagnitude < 0.001f)
            {
                return true;
            }

            Vector3 heading = GetRobotHeadingVector();
            if (heading.sqrMagnitude < 0.001f)
            {
                return false;
            }

            float angle = Vector3.Angle(heading.normalized, toGroup.normalized);
            return angle <= maxAngleDegrees;
        }

        private float GetRobotHeadingAngleToOriginalGroup()
        {
            if (robot == null)
            {
                return 180f;
            }

            Vector3 toGroup = originalGroupCenter - robot.position;
            toGroup.y = 0f;

            Vector3 heading = GetRobotHeadingVector();

            if (toGroup.sqrMagnitude < 0.001f || heading.sqrMagnitude < 0.001f)
            {
                return 0f;
            }

            return Vector3.Angle(heading.normalized, toGroup.normalized);
        }

        private Vector3 GetRobotHeadingVector()
        {
            if (robot == null)
            {
                return Vector3.forward;
            }

            Vector3 heading = robotHeadingDirection == RobotHeadingDirection.TransformBackward
                ? -robot.forward
                : robot.forward;

            heading.y = 0f;

            if (heading.sqrMagnitude < 0.001f)
            {
                return Vector3.forward;
            }

            return heading.normalized;
        }

        private static Vector3 YawToDirection(float yawDegrees)
        {
            return Quaternion.AngleAxis(yawDegrees, Vector3.up) * Vector3.forward;
        }

        private static float DirectionToYaw(Vector3 direction)
        {
            direction.y = 0f;
            if (direction.sqrMagnitude < 0.001f)
            {
                return 0f;
            }

            direction.Normalize();
            return Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
        }

        private void UpdateMembers(float dt)
        {
            for (int i = 0; i < spawnedMembers.Count; i++)
            {
                MemberRuntime member = spawnedMembers[i];
                if (member == null || member.root == null)
                {
                    continue;
                }

                Vector3 targetPosition = member.initialPosition;
                Vector3 targetFacing = member.initialForward;

                bool active =
                    state == RuntimeState.AttractActive ||
                    state == RuntimeState.SplitActive;

                if (active && robot != null)
                {
                    if (i < dynamicTargets.Count)
                    {
                        targetPosition = dynamicTargets[i];
                    }

                    bool faceRobot = state == RuntimeState.SplitActive
                        ? splitMembersFaceRobot
                        : allMembersLookAtRobot;

                    if (faceRobot)
                    {
                        targetFacing = robot.position - member.root.position;
                        targetFacing.y = 0f;
                    }
                    else
                    {
                        targetFacing = member.initialForward;
                    }
                }
                else if (state == RuntimeState.ReturningToInitial)
                {
                    targetPosition = member.initialPosition;
                    targetFacing = member.initialForward;
                }

                MoveMember(member, targetPosition, dt);
                TurnMember(member.root, targetFacing, dt);
                if (!(member.nativeReaction && member.nativeReaction.OwnsBody))
                    UpdateAnimatorMovement(member, targetPosition);
                if (member.proxyContainer)
                    member.proxyContainer.SetActive(member.proxyWanted && !(member.nativeReaction && member.nativeReaction.OwnsBody));
            }
        }

        private void MoveMember(MemberRuntime member, Vector3 targetPosition, float dt)
        {
            if (memberMoveSpeed <= 0f)
            {
                return;
            }

            Vector3 current = member.root.position;
            Vector3 next = Vector3.MoveTowards(
                current,
                targetPosition,
                memberMoveSpeed * dt);

            member.root.position = next;
            member.currentTarget = targetPosition;
        }

        private void TurnMember(Transform member, Vector3 direction, float dt)
        {
            direction.y = 0f;
            if (direction.sqrMagnitude < 0.001f)
            {
                return;
            }

            Quaternion targetRotation = Quaternion.LookRotation(direction.normalized, Vector3.up);
            member.rotation = Quaternion.Slerp(
                member.rotation,
                targetRotation,
                memberTurnSpeed * dt);
        }

        private void UpdateAnimatorMovement(MemberRuntime member, Vector3 targetPosition)
        {
            if (member.animator == null)
            {
                return;
            }

            bool moving = XZDistance(member.root.position, targetPosition) > targetTolerance;

            SetAnimatorBoolIfPresent(member.animator, "Idling", !moving);
            SetAnimatorFloatIfPresent(member.animator, "Forward", moving ? 0.5f : 0f);
            SetAnimatorFloatIfPresent(member.animator, "Strafe", 0f);
        }

        private void TriggerAllPersonalityAnimationsOnce()
        {
            for (int i = 0; i < spawnedMembers.Count; i++)
            {
                MemberRuntime member = spawnedMembers[i];
                if (member == null || member.reactionTriggered || !member.personalityEnabled)
                {
                    continue;
                }

                TriggerPersonalityAnimation(member);
                member.reactionTriggered = true;
            }
        }

        private void ResetReactionState()
        {
            for (int i = 0; i < spawnedMembers.Count; i++)
            {
                MemberRuntime member = spawnedMembers[i];
                if (member == null)
                {
                    continue;
                }

                member.reactionTriggered = false;
                ResetPersonalityBools(member.animator);
            }
        }

        private void TriggerPersonalityAnimation(MemberRuntime member)
        {
            // GroupMemberNativeReaction calls the current native reaction interfaces.
        }

        private void ResetPersonalityBools(Animator animator)
        {
            // Native runner owns its own latch, cooldown and animation reset.
        }

        private GameObject CreateVisibleMember(
            MemberConfig config,
            Vector3 position,
            Vector3 forward,
            int index,
            out Animator animator,
            out bool personalityEnabled)
        {
            animator = null;
            personalityEnabled = false;

            GameObject avatarPrefab = null;
            RuntimeAnimatorController personalityController = null;
            bool supportsPersonality = false;

            CharacterSource effectiveSource = config.GetEffectiveSource();

            // Robustness:
            // Personality should not be allowed to make the whole group disappear.
            // A Special member normally requests SimpleAppearanceAgent, but if that
            // prefab / AppearanceAvatar / avatar list is not set up correctly, we
            // fall back to a Rocketbox avatar instead of aborting group generation.
            if (effectiveSource == CharacterSource.SimpleAppearanceAgent)
            {
                AppearanceAvatar appearance = null;

                if (simpleAppearanceAgentPrefab != null)
                {
                    appearance = simpleAppearanceAgentPrefab.GetComponentInChildren<AppearanceAvatar>(true);
                }

                if (appearance != null)
                {
                    personalityController = appearance.animationController;

                    if (appearance.avatars != null && appearance.avatars.Length > 0)
                    {
                        avatarPrefab =
                            appearance.avatars[UnityEngine.Random.Range(0, appearance.avatars.Length)];
                        supportsPersonality = personalityController != null;
                    }
                }

                if (avatarPrefab == null)
                {
                    Debug.LogWarning(
                        "[DynamicAttentionGroupSpawner] Member " + index +
                        " requested SimpleAppearanceAgent/personality appearance, but the prefab is missing " +
                        "or has no AppearanceAvatar avatars. Falling back to RandomRocketbox so the group remains visible.",
                        this);

                    avatarPrefab = PickRandomRocketboxPrefab();

                    supportsPersonality = personalityController != null;
                }
            }
            else
            {
                avatarPrefab = PickRandomRocketboxPrefab();
            }

            if (avatarPrefab == null)
            {
                Debug.LogError(
                    "[DynamicAttentionGroupSpawner] Could not create member " + index +
                    ". No valid SimpleAppearanceAgent avatar or Rocketbox fallback was available.",
                    this);
                return null;
            }

            Quaternion rotation =
                forward.sqrMagnitude > 0.001f
                    ? Quaternion.LookRotation(forward, Vector3.up)
                    : Quaternion.identity;

            GameObject person = Instantiate(avatarPrefab, position, rotation);
            person.name = "DynamicAttentionMember_" + index + "_" + avatarPrefab.name;

            PrepareVisualOnlyPerson(person);

            animator = IVI.AvatarAnimatorUtility.GetLocomotionAnimator(person);
            if (animator != null)
            {
                if (supportsPersonality && personalityController != null)
                {
                    animator.runtimeAnimatorController = personalityController;
                }

                animator.applyRootMotion = false;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                animator.speed = 1f;
                animator.Rebind();
                animator.Update(0f);

                SetAnimatorFloatIfPresent(animator, "Forward", 0f);
                SetAnimatorFloatIfPresent(animator, "Strafe", 0f);
                SetAnimatorBoolIfPresent(animator, "Idling", true);
            }

            personalityEnabled =
                config.type == MemberType.Special &&
                supportsPersonality &&
                animator != null &&
                config.personality != PedestrianModulator.PersonalityType.Indifferent;

            return person;
        }

        private GameObject PickRandomRocketboxPrefab()
        {
            if (rocketboxPool == null || rocketboxPool.Length == 0)
            {
                LoadRocketboxPoolIfNeeded();
            }

            if (rocketboxPool == null || rocketboxPool.Length == 0)
            {
                Debug.LogError(
                    "[DynamicAttentionGroupSpawner] No Rocketbox prefabs found at Resources/" +
                    rocketboxResourcesPath + ".",
                    this);
                return null;
            }

            return rocketboxPool[UnityEngine.Random.Range(0, rocketboxPool.Length)];
        }

        private void PrepareVisualOnlyPerson(GameObject person)
        {
            NavMeshAgent[] navAgents = person.GetComponentsInChildren<NavMeshAgent>(true);
            for (int i = 0; i < navAgents.Length; i++)
            {
                navAgents[i].enabled = false;
            }

            IVI.INavigable navigable = person.GetComponentInChildren<IVI.INavigable>();
            if (navigable != null)
            {
                Behaviour movementBehaviour = navigable as Behaviour;
                if (movementBehaviour != null)
                {
                    movementBehaviour.enabled = false;
                }
            }

            Rigidbody[] rigidbodies = person.GetComponentsInChildren<Rigidbody>(true);
            for (int i = 0; i < rigidbodies.Length; i++)
            {
                rigidbodies[i].isKinematic = true;
                rigidbodies[i].useGravity = false;
            }

            Collider[] colliders = person.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++)
            {
                colliders[i].enabled = false;
            }

            Animator[] animators = person.GetComponentsInChildren<Animator>(true);
            for (int i = 0; i < animators.Length; i++)
            {
                animators[i].enabled = true;
                animators[i].applyRootMotion = false;
            }
        }

        private void CreateGroupSocialProxy(float socialRadius)
        {
            if (socialProxyPrefab == null)
            {
                return;
            }

            GameObject proxyContainer =
                Instantiate(socialProxyPrefab, originalGroupCenter, Quaternion.identity, generatedRoot);

            proxyContainer.name = "DynamicAttentionGroupSocialForceProxy";
            groupProxyContainer = proxyContainer;

            IVI.INavigable proxyAgent =
                proxyContainer.GetComponentInChildren<IVI.INavigable>();

            if (proxyAgent == null)
            {
                Debug.LogError(
                    "[DynamicAttentionGroupSpawner] Social Proxy Prefab has no IVI.INavigable/SFAgent for group proxy.",
                    this);
                Destroy(proxyContainer);
                groupProxyContainer = null;
                return;
            }

            DisableProxyTrackingComponents(proxyContainer);
            HideProxyVisualsAndColliders(proxyContainer);

            GameObject proxyAgentObject = proxyAgent.gameObject;
            bool exactRadiusSet = TrySetSocialForceRadius(proxyAgentObject, socialRadius);
            float colliderRadius = socialRadius;

            if (!exactRadiusSet)
            {
                float scale = socialRadius / Mathf.Max(0.01f, proxyBaseRadius);
                proxyAgent.transform.localScale = Vector3.Scale(
                    proxyAgent.transform.localScale,
                    new Vector3(scale, scale, scale));

                colliderRadius = proxyBaseRadius;

                Debug.LogWarning(
                    "[DynamicAttentionGroupSpawner] Could not find an explicit SFAgent radius field. " +
                    "Using transform scale as fallback for group proxy.",
                    this);
            }

            CapsuleCollider proxyCollider = proxyAgentObject.AddComponent<CapsuleCollider>();
            proxyCollider.direction = 1;
            proxyCollider.radius = colliderRadius;
            proxyCollider.height = colliderRadius * 2f;
            proxyCollider.center = new Vector3(0f, colliderRadius, 0f);
            proxyCollider.isTrigger = true;

            proxyAgent.transform.position = originalGroupCenter;
            proxyAgent.InitDest(originalGroupCenter);

            groupProxyLock = proxyAgentObject.AddComponent<DynamicAttentionGroupProxyLock>();
            groupProxyLock.Initialize(proxyAgent, originalGroupCenter);
        }

        private void CreatePerPersonSocialProxy(MemberRuntime member, int index)
        {
            if (member.root == null || socialProxyPrefab == null)
            {
                return;
            }

            GameObject proxyContainer =
                Instantiate(socialProxyPrefab, member.root.position, Quaternion.identity, generatedRoot);

            proxyContainer.name = "DynamicAttentionSocialForceProxy_" + index;
            member.proxyContainer = proxyContainer;

            IVI.INavigable proxyAgent =
                proxyContainer.GetComponentInChildren<IVI.INavigable>();

            if (proxyAgent == null)
            {
                Debug.LogError(
                    "[DynamicAttentionGroupSpawner] Social Proxy Prefab has no IVI.INavigable/SFAgent.",
                    this);
                Destroy(proxyContainer);
                return;
            }

            DisableProxyTrackingComponents(proxyContainer);
            HideProxyVisualsAndColliders(proxyContainer);

            GameObject proxyAgentObject = proxyAgent.gameObject;
            bool exactRadiusSet = TrySetSocialForceRadius(proxyAgentObject, perPersonProxyRadius);
            float colliderRadius = perPersonProxyRadius;

            if (!exactRadiusSet)
            {
                float scale = perPersonProxyRadius / Mathf.Max(0.01f, proxyBaseRadius);
                proxyAgent.transform.localScale = Vector3.Scale(
                    proxyAgent.transform.localScale,
                    new Vector3(scale, scale, scale));

                colliderRadius = proxyBaseRadius;

                Debug.LogWarning(
                    "[DynamicAttentionGroupSpawner] Could not find an explicit SFAgent radius field. " +
                    "Using transform scale as fallback for per-person proxy.",
                    this);
            }

            CapsuleCollider proxyCollider = proxyAgentObject.AddComponent<CapsuleCollider>();
            proxyCollider.direction = 1;
            proxyCollider.radius = colliderRadius;
            proxyCollider.height = colliderRadius * 2f;
            proxyCollider.center = new Vector3(0f, colliderRadius, 0f);
            proxyCollider.isTrigger = true;

            proxyAgent.transform.position = member.root.position;
            proxyAgent.InitDest(member.root.position);

            DynamicAttentionSocialProxyFollower follower =
                proxyAgentObject.AddComponent<DynamicAttentionSocialProxyFollower>();

            follower.Initialize(proxyAgent, member.root, updateProxyDestEveryFrame);
            member.proxyFollower = follower;
        }

        private void HideProxyVisualsAndColliders(GameObject proxyContainer)
        {
            Renderer[] renderers = proxyContainer.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                renderers[i].enabled = false;
            }

            Animator[] animators = proxyContainer.GetComponentsInChildren<Animator>(true);
            for (int i = 0; i < animators.Length; i++)
            {
                animators[i].enabled = false;
                animators[i].applyRootMotion = false;
            }

            Collider[] oldColliders = proxyContainer.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < oldColliders.Length; i++)
            {
                oldColliders[i].enabled = false;
            }
        }

        private void DisableProxyTrackingComponents(GameObject proxyContainer)
        {
            Behaviour[] behaviours = proxyContainer.GetComponentsInChildren<Behaviour>(true);

            for (int i = 0; i < behaviours.Length; i++)
            {
                Behaviour behaviour = behaviours[i];
                if (behaviour == null)
                {
                    continue;
                }

                string typeName = behaviour.GetType().Name;
                string fullName = behaviour.GetType().FullName ?? string.Empty;

                if (typeName == "TrackedTrajectory" ||
                    fullName.EndsWith(".TrackedTrajectory") ||
                    typeName == "PositionPublisher")
                {
                    behaviour.enabled = false;
                }
            }
        }

        private void SetGroupProxyActive(bool active)
        {
            if (groupProxyContainer != null)
            {
                groupProxyContainer.SetActive(active);
            }
        }

        private void SetPerPersonProxiesActive(bool active)
        {
            for (int i = 0; i < spawnedMembers.Count; i++)
            {
                MemberRuntime member = spawnedMembers[i];
                if (member != null && member.proxyContainer != null)
                {
                    member.proxyWanted = active;
                    member.proxyContainer.SetActive(active && !(member.nativeReaction && member.nativeReaction.OwnsBody));
                }
            }
        }

        private void SnapPerPersonProxiesToMembers()
        {
            for (int i = 0; i < spawnedMembers.Count; i++)
            {
                MemberRuntime member = spawnedMembers[i];
                if (member == null || member.root == null || member.proxyContainer == null)
                {
                    continue;
                }

                member.proxyContainer.transform.position = member.root.position;

                if (member.proxyFollower != null)
                {
                    member.proxyFollower.SnapToTargetNow();
                }
            }
        }

        private bool ValidateSlotsAtExactXZ(
            List<Vector3> requestedPositions,
            List<Vector3> resolvedPositions)
        {
            for (int i = 0; i < requestedPositions.Count; i++)
            {
                Vector3 resolved;

                if (!TryFindNavMeshAtExactXZ(
                        requestedPositions[i],
                        out resolved,
                        "Member slot " + i))
                {
                    Debug.LogError(
                        "[DynamicAttentionGroupSpawner] Group was not generated because at least one member is not on the NavMesh.",
                        this);
                    return false;
                }

                resolvedPositions.Add(resolved);
            }

            return true;
        }

        private bool TryFindNavMeshAtExactXZ(
            Vector3 requested,
            out Vector3 resolved,
            string label)
        {
            resolved = requested;

            float step = Mathf.Max(0.05f, navMeshVerticalStep);
            int stepsPerDirection = Mathf.CeilToInt(navMeshVerticalSearchDistance / step);
            int totalProbeCount = stepsPerDirection * 2 + 1;

            for (int probeIndex = 0; probeIndex < totalProbeCount; probeIndex++)
            {
                float verticalOffset;

                if (probeIndex == 0)
                {
                    verticalOffset = 0f;
                }
                else
                {
                    int level = (probeIndex + 1) / 2;
                    float sign = probeIndex % 2 == 1 ? 1f : -1f;
                    verticalOffset = level * step * sign;
                }

                Vector3 probePoint = new Vector3(
                    requested.x,
                    requested.y + verticalOffset,
                    requested.z);

                NavMeshHit hit;
                if (!NavMesh.SamplePosition(
                        probePoint,
                        out hit,
                        navMeshSampleDistance,
                        NavMesh.AllAreas))
                {
                    continue;
                }

                float horizontalError = Vector2.Distance(
                    new Vector2(requested.x, requested.z),
                    new Vector2(hit.position.x, hit.position.z));

                if (horizontalError > navMeshHorizontalTolerance)
                {
                    continue;
                }

                resolved = new Vector3(requested.x, hit.position.y, requested.z);
                return true;
            }

            if (logDetails)
            {
                Debug.LogWarning(
                    "[DynamicAttentionGroupSpawner] " + label +
                    " is not on the NavMesh at X=" + requested.x.ToString("F3") +
                    ", Z=" + requested.z.ToString("F3") +
                    ". X/Z were not moved.",
                    this);
            }

            return false;
        }

        private bool TrySetSocialForceRadius(GameObject agentObject, float radius)
        {
            Component[] components = agentObject.GetComponents<Component>();

            string[] candidateNames =
            {
                "radius",
                "Radius",
                "agentRadius",
                "AgentRadius",
                "personalRadius",
                "PersonalRadius",
                "personalSpaceRadius",
                "PersonalSpaceRadius"
            };

            bool changed = false;

            for (int i = 0; i < components.Length; i++)
            {
                Component component = components[i];
                if (component == null)
                {
                    continue;
                }

                System.Type type = component.GetType();
                string typeName = type.Name;

                if (!typeName.Contains("SFAgent") &&
                    !typeName.Contains("Social") &&
                    !typeName.Contains("Agent") &&
                    !typeName.Contains("Base"))
                {
                    continue;
                }

                for (int n = 0; n < candidateNames.Length; n++)
                {
                    changed |= TrySetFloatMember(
                        component,
                        type,
                        candidateNames[n],
                        radius);
                }
            }

            return changed;
        }

        private bool TrySetFloatMember(
            object target,
            System.Type startingType,
            string memberName,
            float value)
        {
            System.Type type = startingType;

            while (type != null)
            {
                FieldInfo field = type.GetField(
                    memberName,
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly);

                if (field != null &&
                    field.FieldType == typeof(float) &&
                    !field.IsLiteral &&
                    !field.IsInitOnly)
                {
                    try
                    {
                        field.SetValue(target, value);
                        return true;
                    }
                    catch (System.Exception)
                    {
                    }
                }

                PropertyInfo property = type.GetProperty(
                    memberName,
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly);

                if (property != null &&
                    property.PropertyType == typeof(float) &&
                    property.CanWrite)
                {
                    try
                    {
                        property.SetValue(target, value, null);
                        return true;
                    }
                    catch (System.Exception)
                    {
                    }
                }

                type = type.BaseType;
            }

            return false;
        }

        private static void SetAnimatorFloatIfPresent(
            Animator animator,
            string parameterName,
            float value)
        {
            if (animator == null)
            {
                return;
            }

            AnimatorControllerParameter[] parameters = animator.parameters;
            for (int i = 0; i < parameters.Length; i++)
            {
                if (parameters[i].name == parameterName &&
                    parameters[i].type == AnimatorControllerParameterType.Float)
                {
                    animator.SetFloat(parameterName, value);
                    return;
                }
            }
        }

        private static void SetAnimatorBoolIfPresent(
            Animator animator,
            string parameterName,
            bool value)
        {
            if (animator == null)
            {
                return;
            }

            AnimatorControllerParameter[] parameters = animator.parameters;
            for (int i = 0; i < parameters.Length; i++)
            {
                if (parameters[i].name == parameterName &&
                    parameters[i].type == AnimatorControllerParameterType.Bool)
                {
                    animator.SetBool(parameterName, value);
                    return;
                }
            }
        }

        private static void SetAnimatorTriggerIfPresent(
            Animator animator,
            string parameterName)
        {
            if (animator == null)
            {
                return;
            }

            AnimatorControllerParameter[] parameters = animator.parameters;
            for (int i = 0; i < parameters.Length; i++)
            {
                if (parameters[i].name == parameterName &&
                    parameters[i].type == AnimatorControllerParameterType.Trigger)
                {
                    animator.SetTrigger(parameterName);
                    return;
                }
            }
        }

        private void LoadRocketboxPoolIfNeeded()
        {
            if (rocketboxPool == null || rocketboxPool.Length == 0)
            {
                rocketboxPool = Resources.LoadAll<GameObject>(rocketboxResourcesPath);
            }
        }

        private Vector3 PerpendicularTowardInterior(Vector3 arm, Vector3 interior)
        {
            Vector3 p1 = Vector3.Cross(Vector3.up, arm).normalized;
            Vector3 p2 = -p1;

            return Vector3.Dot(p1, interior) >= Vector3.Dot(p2, interior)
                ? p1
                : p2;
        }

        private float XZDistance(Vector3 a, Vector3 b)
        {
            a.y = 0f;
            b.y = 0f;
            return Vector3.Distance(a, b);
        }

        private void OnDrawGizmosSelected()
        {
            if (!drawGizmos)
            {
                return;
            }

            Gizmos.DrawWireSphere(transform.position, triggerDistance);

            if (returnToInitialFormation && returnWhenRobotLeavesResetDistance)
            {
                Gizmos.DrawWireSphere(transform.position, resetDistance);
            }

            if (drawDynamicTargets && dynamicTargets != null)
            {
                for (int i = 0; i < dynamicTargets.Count; i++)
                {
                    Gizmos.DrawWireSphere(dynamicTargets[i], 0.12f);
                }
            }

            if (Application.isPlaying && hasLockedAttentionFocus)
            {
                Gizmos.DrawWireSphere(lockedAttentionFocus, 0.2f);
            }
        }
    }

    /// <summary>
    /// Keeps the initial group-level social-force proxy fixed at the original group center.
    /// </summary>
    public class DynamicAttentionGroupProxyLock : MonoBehaviour
    {
        private IVI.INavigable agent;
        private Vector3 fixedPosition;
        private Quaternion fixedRotation;

        public void Initialize(IVI.INavigable navigable, Vector3 position)
        {
            agent = navigable;
            fixedPosition = position;
            fixedRotation = transform.rotation;

            transform.position = fixedPosition;
            if (agent != null)
            {
                agent.InitDest(fixedPosition);
            }
        }

        private void LateUpdate()
        {
            transform.position = fixedPosition;
            transform.rotation = fixedRotation;

            Rigidbody rb = GetComponent<Rigidbody>();
            if (rb != null && !rb.isKinematic)
            {
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }

            if (agent != null)
            {
                agent.InitDest(fixedPosition);
            }
        }
    }

    /// <summary>
    /// Keeps an invisible SEAN social-force proxy aligned with one visible group member.
    /// The proxy moves, but SEAN's social-force implementation itself is not modified.
    /// </summary>
    public class DynamicAttentionSocialProxyFollower : MonoBehaviour
    {
        private IVI.INavigable agent;
        private Transform followTarget;
        private bool updateDestEveryFrame;

        public void Initialize(
            IVI.INavigable navigable,
            Transform target,
            bool updateDest)
        {
            agent = navigable;
            followTarget = target;
            updateDestEveryFrame = updateDest;

            if (followTarget != null)
            {
                transform.position = followTarget.position;

                if (agent != null)
                {
                    agent.InitDest(followTarget.position);
                }
            }
        }

        public void SnapToTargetNow()
        {
            if (followTarget == null)
            {
                return;
            }

            transform.position = followTarget.position;
            transform.rotation = followTarget.rotation;

            if (agent != null)
            {
                agent.InitDest(followTarget.position);
            }
        }

        private void LateUpdate()
        {
            if (followTarget == null)
            {
                return;
            }

            transform.position = followTarget.position;
            transform.rotation = followTarget.rotation;

            Rigidbody rb = GetComponent<Rigidbody>();
            if (rb != null && !rb.isKinematic)
            {
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }

            if (agent != null && updateDestEveryFrame)
            {
                agent.InitDest(followTarget.position);
            }
        }
    }
}
