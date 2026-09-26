using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.AI;

namespace SEAN.Scenario.Agents
{
    /// <summary>
    /// Dynamic moving attention group spawner for controllable V/U/side-by-side walking groups.
    ///
    /// Design:
    /// 1. Visible pedestrians are visual-only members locked to formation slots.
    /// 2. The group center moves between Start Point and End Point in ping-pong mode.
    /// 3. Members preserve the selected formation while walking.
    /// 4. One invisible group-level social-force proxy represents the whole group.
    /// 5. Intended for 2-, 3-, and 4-person moving social groups.
    /// 6. Per-member settings follow the DynamicAttentionGroupSpawner style, but appearance source is independent from behavior/personality.
    /// 7. Ordinary members can still use SimpleAppearanceAgent visually; only Special + non-Indifferent enables reaction animation.
    /// 8. Optional small trigger CapsuleColliders can be added to every visible person for RL collision detection.
    /// 9. Optional StopAndWait obstacle avoidance prevents this scripted moving group from walking through static visible people.
    /// 10. V/U geometry uses symmetric angle-based offsets inspired by MovingGroupVU.
    /// 11. Robot encounter behavior follows DynamicAttentionGroupSpawner naming: StopAndFaceRobot, AttractArc, SplitCorridor.
    /// 12. During AttractArc/SplitCorridor, members walk to temporary slots instead of sliding while idle.
    /// 13. Turn behavior is SlotFlip-locked: at endpoints V/U members first swap to the return-direction slots, then every person turns in place; the group root does not rotate as one rigid body.
    /// </summary>
    public class DynamicMovingAttentionGroupSpawner_WJC_SAFE_SlotFlip : MonoBehaviour
    {
        public enum FormationType
        {
            SideBySide,
            VShape,
            UShape
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

        public enum MemberFacingMode
        {
            FaceMovementDirection,
            FaceFormationCenter,
            KeepInitialFormationFacing
        }

        public enum ObstacleAvoidanceMode
        {
            None,
            StopAndWait
        }

        public enum FormationRotationMode
        {
            FixedWorldYaw,
            FollowMovementDirection,
            SmoothFollowMovementDirection,
            RouteRelativeSlots
        }

        public enum EndpointTurnMode
        {
            ImmediateReverse,
            RepositionDuringEndpointWait,
            NaturalTurnAfterEndpoint
        }

        public enum RobotEncounterReformationMode
        {
            StopAndFaceRobot,
            AttractArc,
            SplitCorridor
        }

        public enum AttentionFocusMode
        {
            /// <summary>AttractArc is built around the robot position captured at the trigger moment. Members do not chase the robot.</summary>
            LockRobotPositionAtTrigger,

            /// <summary>AttractArc is continuously rebuilt around the current robot position while the group is stopped.</summary>
            FollowRobotWhileActive
        }

        public enum AttentionArcDirectionMode
        {
            /// <summary>Arc center points from attention focus toward the current moving group center. Matches DynamicAttentionGroupSpawner's default.</summary>
            FromAttentionFocusToOriginalGroup,

            /// <summary>Arc center points from the current moving group center toward the attention focus.</summary>
            FromOriginalGroupToAttentionFocus,

            /// <summary>Arc center uses robot forward captured at trigger.</summary>
            RobotForwardAtTrigger,

            /// <summary>Arc center uses robot backward captured at trigger.</summary>
            RobotBackwardAtTrigger,

            /// <summary>Arc center follows current robot forward while active.</summary>
            CurrentRobotForward,

            /// <summary>Arc center uses Custom Arc World Yaw Degrees.</summary>
            CustomWorldYaw
        }

        public enum SplitCorridorDirectionMode
        {
            /// <summary>Corridor direction points from the robot trigger position toward the current moving group center. Matches DynamicAttentionGroupSpawner's default.</summary>
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

        [Serializable]
        public class MemberConfig
        {
            [Tooltip("Ordinary = no individual personality reaction; Special = reactive member. With Automatic Source, Ordinary uses Random Rocketbox and Special uses SimpleAppearanceAgent.")]
            public MemberType type = MemberType.Ordinary;

            [Tooltip("Automatic mix: Ordinary uses Random Rocketbox; Special uses SimpleAppearanceAgent. Configure each member before Play. Disable to choose its Source manually.")]
            public bool automaticSource = true;

            [Tooltip("Used only when Automatic Source is disabled. Visual appearance source only. Ordinary members may also use SimpleAppearanceAgent.")]
            public CharacterSource source = CharacterSource.SimpleAppearanceAgent;

            [Tooltip("Reaction personality. Only Special members with SimpleAppearanceAgent and non-Indifferent personality can trigger reaction animation. Ordinary members stay Indifferent even if they use SimpleAppearanceAgent visually.")]
            public PedestrianModulator.PersonalityType personality =
                PedestrianModulator.PersonalityType.Indifferent;

            [Tooltip("Allow this member to react near the robot. Whole-body gestures can pause this member briefly within its formation limit; the group route continues unless its existing encounter/obstacle logic stops it.")]
            public bool enableReactionAnimation = true;

            [Tooltip("Use the global Default Reaction Distance from the spawner.")]
            public bool useDefaultReactionDistance = true;

            [Tooltip("Per-member trigger distance, used only when Use Default Reaction Distance is false.")]
            [Min(0.1f)]
            public float reactionDistance = 3f;

            [Tooltip("Matches DynamicAttentionGroupSpawner. If false, this member stays in its walking formation slot during AttractArc/SplitCorridor, but can still face the robot and play personality animation.")]
            public bool moveDuringReformation = true;

            [Tooltip("Current native reaction options. Configure before Play; original walking animation is retained.")]
            public GroupReactionSettings nativeReaction = new GroupReactionSettings();

            public CharacterSource GetEffectiveSource(CharacterSource defaultAppearanceSource)
            {
                if (automaticSource)
                {
                    // Same automatic mix as the original Static/DynamicAttention groups.
                    return type == MemberType.Special
                        ? CharacterSource.SimpleAppearanceAgent
                        : CharacterSource.RandomRocketbox;
                }

                return source;
            }
        }

        private class MemberRuntime
        {
            public Transform root;
            public GroupMemberNativeReaction nativeReaction;
            public Animator animator;
            public Vector3 baseLocalSlot;
            public Vector3 localSlot;
            public Quaternion localFacing;
            public PedestrianModulator.PersonalityType personality;
            public bool reactionEnabled;
            public float reactionDistance;
            public float reactionResetDistance;
            public bool reactionActive;
            public bool moveDuringReformation;
        }

        [Header("Generation")]
        public bool generateOnStart = true;
        public bool clearExistingBeforeGenerate = true;

        [Tooltip("2, 3, or 4. Values outside this range are clamped.")]
        [Range(2, 4)]
        public int memberCount = 3;

        public FormationType formation = FormationType.VShape;

        [Tooltip("Additional yaw applied to formation slots relative to the walking direction.")]
        public float formationYawDegrees = 0f;

        [Header("Path Movement")]
        [Tooltip("Empty transform marking the start of the moving group path. If empty, this GameObject position is used.")]
        public Transform startPoint;

        [Tooltip("Empty transform marking the end of the moving group path. If empty, this GameObject forward direction is used.")]
        public Transform endPoint;

        [Tooltip("Fallback path length used only when End Point is empty.")]
        [Min(0.1f)]
        public float fallbackPathLength = 5f;

        [Tooltip("Group-center walking speed in meters per second.")]
        [Min(0f)]
        public float moveSpeed = 0.9f;

        [Tooltip("Pause at each endpoint before walking back.")]
        [Min(0f)]
        public float waitAtEndSeconds = 0.5f;

        [Tooltip("How close the group center must be to the target endpoint to count as arrived.")]
        [Min(0.01f)]
        public float arrivalTolerance = 0.15f;

        [Tooltip("If true, the group goes Start -> End -> Start repeatedly.")]
        public bool pingPong = true;

        [HideInInspector]
        public bool smoothTurn = true;

        [HideInInspector]
        [Min(1f)]
        public float groupTurnSpeedDegPerSec = 180f;

        [HideInInspector]
        public FormationRotationMode formationRotationMode = FormationRotationMode.RouteRelativeSlots;

        [HideInInspector]
        public EndpointTurnMode endpointTurnMode = EndpointTurnMode.NaturalTurnAfterEndpoint;

        [Header("Natural Endpoint Turn")]
        [Tooltip("SideBySide endpoint pivot duration in seconds. The group center stays near the endpoint while members turn to face the return direction.")]
        [Min(0.05f)]
        public float sideBySideEndpointTurnDuration = 0.8f;

        [Tooltip("V/U endpoint turn duration in seconds. Longer values look smoother.")]
        [Min(0.05f)]
        public float vuEndpointTurnDuration = 1.4f;

        [Tooltip("Legacy compatibility only. SlotFlip keeps the group center fixed at the endpoint; use VU Endpoint Pre Turn Reposition Duration for the rear-person step-forward phase.")]
        [Min(0f)]
        public float vuEndpointTurnAdvanceDistance = 0f;

        [Tooltip("For V/U only: before the in-place 180-degree turn, members move from current walking slots to return-direction slots. This makes the rear member step forward first, then remain the rear member after walking back.")]
        public bool vuRepositionSlotsBeforeEndpointTurn = true;

        [Tooltip("Duration of the V/U slot-flip reposition phase at each endpoint. Increase if the rear member should walk forward more slowly before turning.")]
        [Min(0.05f)]
        public float vuEndpointPreTurnRepositionDuration = 0.65f;

        [HideInInspector]
        public bool endpointTurnClockwise = true;

        [Header("Side-by-Side Shape")]
        [Tooltip("Lateral distance between adjacent members in SideBySide formation.")]
        [Min(0.1f)]
        public float sideBySideSpacing = 0.85f;

        [Header("V / U Symmetric Shape Settings")]
        [Tooltip("Target chord distance between neighboring people in V/U formation. This follows the MovingGroupVU angle-based design.")]
        [Min(0.1f)]
        public float interPersonDistance = 0.8f;

        [Tooltip("V/U only. Bigger = wider V/U. 60 narrow, 120 wide, 180 semicircle. U uses this as the total arc opening.")]
        [Range(10f, 220f)]
        public float openingAngleDegrees = 150f;

        [Tooltip("V only. If true, the V apex points forward and the arms trail behind.")]
        public bool vApexInFront = false;

        [Tooltip("U only. If true, the U opens toward the walking direction. This makes the front wider and the back shorter after centering.")]
        public bool uOpenForward = true;

        [Header("Formation Preservation")]
        [Tooltip("If true, local slots are centered so the group root is the average of all member positions.")]
        public bool centerSlotsAroundGroupCenter = true;

        [Tooltip("Matches DynamicAttentionGroupSpawner. How fast visible members move toward temporary AttractArc/SplitCorridor slots or route-relative slots.")]
        [Min(0f)]
        public float memberMoveSpeed = 0.7f;

        [Tooltip("Matches DynamicAttentionGroupSpawner. Small distance at which a member is considered to have reached its current target slot.")]
        [Min(0.001f)]
        public float targetTolerance = 0.05f;

        [Tooltip("How members face while the group is moving.")]
        public MemberFacingMode memberFacingMode = MemberFacingMode.FaceMovementDirection;

        [Tooltip("How quickly members rotate toward their desired facing direction, in degrees per second.")]
        [Min(1f)]
        public float memberTurnSpeed = 360f;

        [Header("Per-Member Settings")]
        [Tooltip("Automatically resized to Member Count.")]
        public List<MemberConfig> members = new List<MemberConfig>();

        [Header("Appearance Sources")]
        // Retained for serialized/API compatibility with older scenes. Automatic choice is now per type.
        [HideInInspector]
        public CharacterSource defaultAppearanceSource = CharacterSource.SimpleAppearanceAgent;

        [Tooltip("Assign Assets/Resources/Prefabs/SimpleAppearanceAgent.prefab.")]
        public GameObject simpleAppearanceAgentPrefab;

        [Tooltip("Resources path for ordinary Rocketbox avatar prefabs.")]
        public string rocketboxResourcesPath = "Prefabs/Rocketbox";

        [Header("Walking Animation")]
        public bool setWalkingAnimationParameters = true;

        [Tooltip("Animator speed multiplier for visible members.")]
        [Min(0f)]
        public float walkingAnimatorSpeed = 1f;

        [Tooltip("Value used for Animator float parameter named Forward, if present.")]
        public float walkingForwardParameterValue = 1f;

        [Header("Shared Trigger / Return")]
        [Tooltip("Real moving robot transform, e.g. Unitree A1/base/trunk. Same role as DynamicAttentionGroupSpawner.robot.")]
        public Transform robot;

        [Tooltip("If true, members with non-Indifferent personality can play reaction animations when the robot gets close. They keep moving in formation.")]
        public bool enableReactionAnimations = true;

        [Tooltip("Default distance at which reaction animation is triggered.")]
        [Min(0.1f)]
        public float defaultReactionDistance = 3f;

        [Tooltip("Distance above which the member reaction state is reset and can trigger again.")]
        [Min(0.1f)]
        public float defaultReactionResetDistance = 3.5f;

        [Tooltip("If true, logs which personality animation was triggered.")]
        public bool logReactionAnimationDebug = false;

        [Tooltip("Stop the entire group's route when the robot encounter starts. Enable for the original stop-react-resume behavior.")]
        public bool stopForRobotEncounter = true;

        [Tooltip("Robot distance to moving group center that triggers reformation. Same role as DynamicAttentionGroupSpawner.triggerDistance.")]
        [Min(0.1f)]
        public float triggerDistance = 4.0f;

        [Tooltip("Robot distance to moving group center used for return. Same role as DynamicAttentionGroupSpawner.resetDistance.")]
        [Min(0.1f)]
        public float resetDistance = 5.0f;

        [Tooltip("For AttractArc/StopAndFaceRobot: return immediately when robot leaves Reset Distance. For SplitCorridor: this can be combined with Invalid Condition Grace Seconds.")]
        public bool returnWhenRobotLeavesResetDistance = true;

        [Tooltip("Return automatically after Max Active Duration Seconds. Usually false for manual robot experiments.")]
        public bool limitActiveDuration = false;

        [Tooltip("Maximum time the group stays active after each trigger, if Limit Active Duration is enabled.")]
        [Min(0.1f)]
        public float maxActiveDurationSeconds = 3f;

        [Tooltip("When true, after returning, the group cannot trigger again until the robot exits Reset Distance.")]
        public bool requireRobotExitBeforeRetrigger = false;

        [Tooltip("Cooldown after return before another trigger is allowed.")]
        [Min(0f)]
        public float retriggerCooldownSeconds = 2f;

        [Tooltip("Matches DynamicAttentionGroupSpawner.allMembersLookAtRobot for AttractArc. StopAndFaceRobot also uses this.")]
        public bool allMembersLookAtRobot = true;

        [Tooltip("Enable personal reaction distance checks while the group is stopped for the robot. Personal reaction distance still applies to each member.")]
        public bool triggerReactionsWhenRobotStops = true;

        [Tooltip("Use the existing idle animation parameters while the group is stopped for the robot.")]
        public bool pauseWalkingAnimationWhileStoppedForRobot = true;

        [HideInInspector]
        public bool resetReactionsWhenRobotLeaves = true;

        [Tooltip("If true, logs when the moving group stops/resumes because of the robot encounter.")]
        public bool logRobotEncounterDebug = false;

        [Header("Main Reformation Mode")]
        [Tooltip("StopAndFaceRobot = stop and face robot only. AttractArc and SplitCorridor mirror DynamicAttentionGroupSpawner behavior, adapted from world targets to moving-group local slots.")]
        public RobotEncounterReformationMode reformationMode = RobotEncounterReformationMode.StopAndFaceRobot;

        [Header("Attract Arc Settings")]
        [Tooltip("AttractArc is built around the robot position captured at trigger, or follows the current robot while active.")]
        public AttentionFocusMode attentionFocusMode = AttentionFocusMode.LockRobotPositionAtTrigger;

        [Tooltip("Arc center direction for AttractArc. Names match DynamicAttentionGroupSpawner.")]
        public AttentionArcDirectionMode attentionArcDirectionMode =
            AttentionArcDirectionMode.FromAttentionFocusToOriginalGroup;

        [Tooltip("Used only when Attention Arc Direction Mode = CustomWorldYaw.")]
        public float customArcWorldYawDegrees = 0f;

        [Tooltip("AttractArc: preferred radius of the arc around the robot.")]
        [Min(0.1f)]
        public float attentionRadius = 2.0f;

        [Tooltip("AttractArc: minimum robot-person distance.")]
        [Min(0.1f)]
        public float minRobotPersonDistance = 1.4f;

        [Tooltip("AttractArc: total angular span used for the members.")]
        [Range(30f, 240f)]
        public float attentionArcSpanDegrees = 140f;

        [Tooltip("AttractArc: minimum spacing between neighboring people along the arc. Radius expands automatically when needed.")]
        [Min(0.1f)]
        public float minPersonPersonDistance = 0.75f;

        [Tooltip("When true, AttractArc radius is clamped below Trigger Distance, matching DynamicAttentionGroupSpawner.")]
        public bool clampAttentionRadiusBelowTriggerDistance = true;

        [Tooltip("Safety margin used when clamping Attention Radius below Trigger Distance.")]
        [Min(0f)]
        public float attentionRadiusTriggerSafetyMargin = 0.2f;

        [Header("Split Corridor Settings")]
        [Tooltip("Only used when Reformation Mode = SplitCorridor. If enabled, robot must face the moving group center before the corridor opens.")]
        public bool requireRobotHeadingTowardGroupToTrigger = true;

        [Tooltip("Maximum angle between robot heading and direction to current group center for SplitCorridor trigger.")]
        [Range(1f, 180f)]
        public float triggerHeadingMaxAngleDegrees = 60f;

        [Tooltip("How to interpret robot heading. Use TransformBackward if the selected robot transform's blue axis points backward.")]
        public RobotHeadingDirection robotHeadingDirection = RobotHeadingDirection.TransformForward;

        [Tooltip("Direction used as the split corridor centerline.")]
        public SplitCorridorDirectionMode splitCorridorDirectionMode =
            SplitCorridorDirectionMode.FromRobotTriggerPositionToOriginalGroup;

        [Tooltip("Used only when Split Corridor Direction Mode = CustomWorldYaw.")]
        public float customSplitCorridorWorldYawDegrees = 0f;

        [Tooltip("SplitCorridor: optional forward shift of all split targets along the corridor direction.")]
        [Min(0f)]
        public float splitForwardOffset = 0f;

        [Tooltip("SplitCorridor: half-width of the opened corridor. Members are pushed to at least this left/right distance.")]
        [Min(0.1f)]
        public float splitSideOffset = 1.0f;

        [Tooltip("SplitCorridor: optional spacing along the corridor direction for people assigned to the same side.")]
        [Min(0f)]
        public float splitAlongPathSpacing = 0f;

        [Tooltip("If true and the movable count is odd, the middle mover is assigned based on its original side. Matches DynamicAttentionGroupSpawner.")]
        public bool splitOddMemberByInitialSide = true;

        [Tooltip("Matches DynamicAttentionGroupSpawner. If true, SplitCorridor members face the robot while opening/holding the corridor. If false, they keep walking-formation facing.")]
        public bool splitMembersFaceRobot = true;

        [Tooltip("If true, the opened corridor closes when the robot heading no longer points toward the moving group center for Invalid Condition Grace Seconds.")]
        public bool returnIfRobotHeadingNotTowardGroup = true;

        [Tooltip("Active-stage maximum angle between robot heading and direction to group center before the heading is considered invalid.")]
        [Range(1f, 180f)]
        public float activeHeadingMaxAngleDegrees = 75f;

        [Tooltip("For SplitCorridor, heading/distance must remain invalid for this many continuous seconds before returning.")]
        [Min(0f)]
        public float invalidConditionGraceSeconds = 2.0f;

        [Header("Slot Assignment")]
        [Tooltip("PreserveInitialLeftRightOrder avoids crossing paths: left-side movers go to left slots, right-side movers go to right slots.")]
        public SlotOrder slotOrder = SlotOrder.PreserveInitialLeftRightOrder;

        [SerializeField]
        private bool stoppedByRobotEncounter;

        [Header("Visible Person Body Colliders / RL Collision")]
        [Tooltip("Add one simple body CapsuleCollider to each visible pedestrian after disabling prefab colliders. This is for robot-person collision detection, not social-force behavior.")]
        public bool addVisiblePersonBodyColliders = true;

        [Tooltip("Recommended true for RL: detects collision/overlap without physically pushing the scripted formation around.")]
        public bool visiblePersonCollidersAreTriggers = true;

        [Tooltip("Body collider radius in meters.")]
        [Min(0.01f)]
        public float visiblePersonColliderRadius = 0.3f;

        [Tooltip("Body collider height in meters.")]
        [Min(0.1f)]
        public float visiblePersonColliderHeight = 1.7f;

        [Tooltip("Vertical center of the body collider in meters, relative to the pedestrian root.")]
        [Min(0f)]
        public float visiblePersonColliderCenterY = 0.85f;

        [Tooltip("Only contacts with these layers are counted by the collision reporter. Leave as Everything unless you have a robot layer.")]
        public LayerMask visiblePersonCollisionMask = ~0;

        [Tooltip("Optional Unity layer name to assign to the added body collider GameObject/root. Leave empty to keep the prefab layer.")]
        public string visiblePersonColliderLayerName = string.Empty;

        [SerializeField]
        private int activeVisiblePersonBodyContactCount;

        public int ActiveVisiblePersonBodyContactCount
        {
            get { return activeVisiblePersonBodyContactCount; }
        }

        [Header("Moving Group Obstacle Avoidance")]
        [Tooltip("How this scripted moving group reacts when its next step would overlap a static visible person. StopAndWait prevents obvious pedestrian-pedestrian穿模.")]
        public ObstacleAvoidanceMode obstacleAvoidanceMode = ObstacleAvoidanceMode.StopAndWait;

        [Tooltip("Root used to search for static visible people, e.g. PedestrianControl. If empty, the script tries to find a GameObject named PedestrianControl.")]
        public Transform obstacleSearchRoot;

        [Tooltip("Automatically treat objects named StaticGroupMember_*, DynamicGroupMember_*, MovingGroupMember_* as person obstacles, excluding this moving group itself.")]
        public bool autoDetectNamedPersonObstacles = true;

        [Tooltip("Optional manually assigned static people or group roots to avoid. Useful for static individual pedestrians whose names do not follow the group prefix.")]
        public List<Transform> explicitObstaclePeopleOrRoots = new List<Transform>();

        [Tooltip("Approximate radius of external pedestrian obstacles.")]
        [Min(0.01f)]
        public float obstaclePersonRadius = 0.35f;

        [Tooltip("Extra spacing between this moving group's visible members and external pedestrian obstacles.")]
        [Min(0f)]
        public float obstacleSafetyMargin = 0.15f;

        [Tooltip("Look ahead this many seconds when checking the next center position. 0 means only the immediate next frame.")]
        [Min(0f)]
        public float obstacleLookAheadSeconds = 0.25f;

        [Tooltip("If true, logs when the moving group stops because a visible person obstacle blocks its path.")]
        public bool logObstacleAvoidanceDebug = false;

        [SerializeField]
        private bool stoppedByVisiblePersonObstacle;

        [Header("Group-Level Social Force Proxy")]
        [Tooltip("One invisible group-level proxy represents the entire moving group.")]
        public bool useGroupSocialProxy = true;

        [Tooltip("Assign SimpleAppearanceAgent. It is hidden and becomes the group's only SFAgent.")]
        public GameObject socialProxyPrefab;

        [Tooltip("Approximate normal-person radius used only if the SFAgent radius field cannot be found.")]
        [Min(0.01f)]
        public float proxyBaseRadius = 0.35f;

        [Tooltip("Approximate visible member body radius, used when calculating group social radius.")]
        [Min(0f)]
        public float personRadius = 0.35f;

        [Tooltip("Extra padding added around the whole moving group envelope.")]
        [Min(0f)]
        public float groupProxyPadding = 0.35f;

        public bool calculateGroupSocialRadiusAutomatically = true;

        [Min(0.1f)]
        public float manualGroupSocialRadius = 1.5f;

        [SerializeField]
        private float computedGroupSocialRadius;

        public float ComputedSocialRadius
        {
            get { return computedGroupSocialRadius; }
        }

        [Header("Ground / Height")]
        [Tooltip("If true, each endpoint is vertically projected to ground at generation time using raycast.")]
        public bool projectStartEndToGround = true;

        public LayerMask groundMask = ~0;

        [Min(0.1f)]
        public float groundRaycastHeight = 5f;

        [Min(0.1f)]
        public float groundRaycastDistance = 20f;

        [Header("Debug")]
        public bool drawGizmos = true;
        public bool logDetails = true;

        private Transform generatedRoot;
        private Transform socialProxyTransform;
        private IVI.INavigable socialProxyAgent;
        private readonly List<MemberRuntime> spawnedMembers = new List<MemberRuntime>();
        private readonly List<Vector3> localSlots = new List<Vector3>();
        private readonly HashSet<Collider> activeVisiblePersonBodyContacts = new HashSet<Collider>();
        private readonly List<Transform> obstacleBuffer = new List<Transform>();
        private GameObject[] rocketboxPool;

        private Vector3 startWorld;
        private Vector3 endWorld;
        private Vector3 currentCenter;
        private Vector3 targetCenter;
        private Vector3 lastMoveDirection = Vector3.forward;
        private Quaternion currentGroupRotation = Quaternion.identity;
        private Quaternion fixedFormationWorldRotation = Quaternion.identity;
        private bool movingTowardEnd = true;
        private bool waitingAtEndpoint;
        private float waitTimer;
        private bool turningAtEndpoint;
        private float endpointTurnTimer;
        private float endpointTurnDurationRuntime;
        private float endpointTurnAdvanceDistanceRuntime;
        private Vector3 endpointTurnStartCenter;
        private Vector3 endpointTurnStartDirection = Vector3.forward;
        private Vector3 endpointTurnTargetAfterTurn;
        private bool endpointTurnMovingTowardEndAfterTurn;
        private bool robotEncounterReactionTriggered;

        private Vector3 encounterCenter;
        private Vector3 lockedAttentionFocus;
        private bool hasLockedAttentionFocus;
        private Vector3 lockedArcDirection;
        private bool hasLockedArcDirection;
        private Vector3 lockedSplitCorridorDirection;
        private bool hasLockedSplitCorridorDirection;
        private float activeStartTime = -1f;
        private float invalidConditionStartTime = -1f;
        private float nextAllowedTriggerTime = 0f;
        private bool triggerArmed = true;
        private readonly List<Vector3> encounterBaseLocalSlots = new List<Vector3>();
        private readonly List<Vector3> encounterBaseWorldPositions = new List<Vector3>();

        private bool endpointPreTurnRepositioning;
        private bool useEndpointCustomSlots;
        private readonly List<Vector3> endpointTurnStartSlots = new List<Vector3>();
        private readonly List<Vector3> endpointTurnTargetSlots = new List<Vector3>();

        private void OnValidate()
        {
            // WJC-locked turn behavior: no Inspector mode choice.
            // SideBySide pivots between people; V/U advances a little and naturally turns back.
            formationRotationMode = FormationRotationMode.RouteRelativeSlots;
            endpointTurnMode = EndpointTurnMode.NaturalTurnAfterEndpoint;
            smoothTurn = true;
            endpointTurnClockwise = true;
            vApexInFront = false;
            uOpenForward = true;
            vuEndpointTurnAdvanceDistance = 0f;
            vuRepositionSlotsBeforeEndpointTurn = true;

            // SlotFlip logic: V/U are always generated with rear members behind while walking forward.
            // At endpoints, slots flip first, then every member turns in place.
            vApexInFront = false;
            uOpenForward = true;
            vuEndpointTurnAdvanceDistance = 0f;
            vuRepositionSlotsBeforeEndpointTurn = true;
            vuEndpointPreTurnRepositionDuration = Mathf.Max(0.05f, vuEndpointPreTurnRepositionDuration);

            memberCount = Mathf.Clamp(memberCount, 2, 4);
            fallbackPathLength = Mathf.Max(0.1f, fallbackPathLength);
            moveSpeed = Mathf.Max(0f, moveSpeed);
            waitAtEndSeconds = Mathf.Max(0f, waitAtEndSeconds);
            arrivalTolerance = Mathf.Max(0.01f, arrivalTolerance);
            groupTurnSpeedDegPerSec = Mathf.Max(1f, groupTurnSpeedDegPerSec);
            sideBySideEndpointTurnDuration = Mathf.Max(0.05f, sideBySideEndpointTurnDuration);
            vuEndpointTurnDuration = Mathf.Max(0.05f, vuEndpointTurnDuration);
            vuEndpointTurnAdvanceDistance = Mathf.Max(0f, vuEndpointTurnAdvanceDistance);
            sideBySideSpacing = Mathf.Max(0.1f, sideBySideSpacing);
            interPersonDistance = Mathf.Max(0.1f, interPersonDistance);
            openingAngleDegrees = Mathf.Clamp(openingAngleDegrees, 10f, 220f);
            memberMoveSpeed = Mathf.Max(0f, memberMoveSpeed);
            targetTolerance = Mathf.Max(0.001f, targetTolerance);
            memberTurnSpeed = Mathf.Max(1f, memberTurnSpeed);
            walkingAnimatorSpeed = Mathf.Max(0f, walkingAnimatorSpeed);
            defaultReactionDistance = Mathf.Max(0.1f, defaultReactionDistance);
            defaultReactionResetDistance = Mathf.Max(defaultReactionDistance + 0.01f, defaultReactionResetDistance);
            triggerDistance = Mathf.Max(0.1f, triggerDistance);
            resetDistance = Mathf.Max(triggerDistance + 0.01f, resetDistance);
            maxActiveDurationSeconds = Mathf.Max(0.1f, maxActiveDurationSeconds);
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
            visiblePersonColliderRadius = Mathf.Max(0.01f, visiblePersonColliderRadius);
            visiblePersonColliderHeight = Mathf.Max(0.1f, visiblePersonColliderHeight);
            visiblePersonColliderCenterY = Mathf.Max(0f, visiblePersonColliderCenterY);
            obstaclePersonRadius = Mathf.Max(0.01f, obstaclePersonRadius);
            obstacleSafetyMargin = Mathf.Max(0f, obstacleSafetyMargin);
            obstacleLookAheadSeconds = Mathf.Max(0f, obstacleLookAheadSeconds);
            proxyBaseRadius = Mathf.Max(0.01f, proxyBaseRadius);
            personRadius = Mathf.Max(0f, personRadius);
            groupProxyPadding = Mathf.Max(0f, groupProxyPadding);
            manualGroupSocialRadius = Mathf.Max(0.1f, manualGroupSocialRadius);
            groundRaycastHeight = Mathf.Max(0.1f, groundRaycastHeight);
            groundRaycastDistance = Mathf.Max(0.1f, groundRaycastDistance);

            EnsureMemberListSize();
            NormalizeMemberConfigs();
        }

        private void OnEnable()
        {
            EnsureMemberListSize();
        }

        private void Start()
        {
            if (generateOnStart)
            {
                GenerateGroup();
            }
        }

        private void LateUpdate()
        {
            if (generatedRoot == null)
            {
                return;
            }

            UpdateRobotEncounterState();
            UpdateMovement();
            UpdateMemberLocalFacings();

            if (!stoppedByRobotEncounter || !triggerReactionsWhenRobotStops)
            {
                UpdateReactionAnimations();
            }

            UpdateSocialProxy();
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
                if (members[i] == null)
                {
                    members[i] = new MemberConfig();
                }
            }
        }

        private void NormalizeMemberConfigs()
        {
            for (int i = 0; i < members.Count; i++)
            {
                MemberConfig config = members[i];
                if (config == null)
                {
                    continue;
                }

                if (config.automaticSource)
                {
                    // Mirror the effective choice in the Inspector before Play.
                    config.source = config.GetEffectiveSource(defaultAppearanceSource);
                }

                // Ordinary members are visual pedestrians only and do not use personality reactions,
                // even if their visual appearance comes from SimpleAppearanceAgent.
                if (config.type == MemberType.Ordinary)
                {
                    config.personality = PedestrianModulator.PersonalityType.Indifferent;
                }

                config.reactionDistance = Mathf.Max(0.1f, config.reactionDistance);
            }
        }

        [ContextMenu("Generate Moving Social Group")]
        public void GenerateGroup()
        {
            formationRotationMode = FormationRotationMode.RouteRelativeSlots;
            endpointTurnMode = EndpointTurnMode.NaturalTurnAfterEndpoint;
            smoothTurn = true;
            endpointTurnClockwise = true;

            EnsureMemberListSize();
            NormalizeMemberConfigs();

            if (clearExistingBeforeGenerate)
            {
                ClearGroup();
            }

            ResolveStartEndPoints();
            BuildLocalSlots();

            if (localSlots.Count != memberCount)
            {
                Debug.LogError("[DynamicMovingAttentionGroupSpawner_WJC_SAFE_SlotFlip] Failed to build local slots.", this);
                return;
            }

            currentCenter = startWorld;
            targetCenter = endWorld;
            movingTowardEnd = true;
            waitingAtEndpoint = false;
            waitTimer = 0f;
            turningAtEndpoint = false;
            endpointPreTurnRepositioning = false;
            useEndpointCustomSlots = false;
            endpointTurnTimer = 0f;
            endpointTurnStartSlots.Clear();
            endpointTurnTargetSlots.Clear();
            stoppedByRobotEncounter = false;
            robotEncounterReactionTriggered = false;
            activeStartTime = -1f;
            invalidConditionStartTime = -1f;
            nextAllowedTriggerTime = 0f;
            triggerArmed = true;
            hasLockedAttentionFocus = false;
            hasLockedArcDirection = false;
            hasLockedSplitCorridorDirection = false;
            lockedAttentionFocus = Vector3.zero;
            lockedArcDirection = Vector3.forward;
            lockedSplitCorridorDirection = Vector3.forward;
            encounterCenter = currentCenter;
            encounterBaseLocalSlots.Clear();
            encounterBaseWorldPositions.Clear();

            Vector3 initialDirection = endWorld - startWorld;
            initialDirection.y = 0f;
            if (initialDirection.sqrMagnitude < 0.001f)
            {
                initialDirection = transform.forward;
            }

            lastMoveDirection = initialDirection.normalized;
            fixedFormationWorldRotation = Quaternion.LookRotation(lastMoveDirection, Vector3.up) *
                                          Quaternion.AngleAxis(formationYawDegrees, Vector3.up);
            currentGroupRotation = fixedFormationWorldRotation;

            GameObject rootObject = new GameObject("GeneratedMovingSocialGroup");
            generatedRoot = rootObject.transform;
            generatedRoot.SetParent(transform, true);
            generatedRoot.position = currentCenter;
            generatedRoot.rotation = currentGroupRotation;

            LoadRocketboxPoolIfNeeded();

            for (int i = 0; i < memberCount; i++)
            {
                MemberConfig config = members[i];
                Vector3 localSlot = localSlots[i];
                Quaternion localFacing = ComputeLocalFacing(localSlot);

                bool personalityEnabledForMember;
                GameObject person = CreateVisibleMember(
                    config,
                    i,
                    localSlot,
                    localFacing,
                    out personalityEnabledForMember);

                if (person == null)
                {
                    ClearGroup();
                    return;
                }

                float memberReactionDistance = config != null && !config.useDefaultReactionDistance
                    ? Mathf.Max(0.1f, config.reactionDistance)
                    : defaultReactionDistance;

                MemberRuntime runtime = new MemberRuntime
                {
                    root = person.transform,
                    animator = IVI.AvatarAnimatorUtility.GetLocomotionAnimator(person),
                    baseLocalSlot = localSlot,
                    localSlot = localSlot,
                    localFacing = localFacing,
                    personality = personalityEnabledForMember && config != null
                        ? config.personality
                        : PedestrianModulator.PersonalityType.Indifferent,
                    reactionEnabled = personalityEnabledForMember,
                    reactionDistance = memberReactionDistance,
                    reactionResetDistance = Mathf.Max(memberReactionDistance + 0.01f, defaultReactionResetDistance),
                    reactionActive = false,
                    moveDuringReformation = config == null || config.moveDuringReformation
                };
                if (personalityEnabledForMember)
                {
                    runtime.nativeReaction = GroupMemberNativeReaction.Attach(person, config.nativeReaction,
                        config.personality, this, () => robot, () => enableReactionAnimations || (triggerReactionsWhenRobotStops && stoppedByRobotEncounter),
                        runtime.reactionDistance, runtime.reactionResetDistance);
                    if (runtime.nativeReaction) runtime.root = runtime.nativeReaction.transform;
                }
                spawnedMembers.Add(runtime);
            }

            computedGroupSocialRadius = calculateGroupSocialRadiusAutomatically
                ? EstimateGroupSocialRadiusFromSlots()
                : manualGroupSocialRadius;

            if (useGroupSocialProxy)
            {
                CreateGroupSocialProxy(computedGroupSocialRadius);
            }

            if (logDetails)
            {
                Debug.Log(
                    "[DynamicMovingAttentionGroupSpawner_WJC_SAFE_SlotFlip] Generated " + memberCount +
                    "-person " + formation +
                    " moving group. Social radius = " +
                    computedGroupSocialRadius.ToString("F2") + " m.",
                    this);
            }
        }

        [ContextMenu("Clear Moving Social Group")]
        public void ClearGroup()
        {
            spawnedMembers.Clear();
            localSlots.Clear();
            activeVisiblePersonBodyContacts.Clear();
            activeVisiblePersonBodyContactCount = 0;
            stoppedByVisiblePersonObstacle = false;
            socialProxyTransform = null;
            socialProxyAgent = null;
            stoppedByRobotEncounter = false;
            robotEncounterReactionTriggered = false;
            activeStartTime = -1f;
            invalidConditionStartTime = -1f;
            nextAllowedTriggerTime = 0f;
            triggerArmed = true;
            hasLockedAttentionFocus = false;
            hasLockedArcDirection = false;
            hasLockedSplitCorridorDirection = false;
            encounterBaseLocalSlots.Clear();
            encounterBaseWorldPositions.Clear();
            turningAtEndpoint = false;
            endpointTurnTimer = 0f;

            Transform oldRoot = transform.Find("GeneratedMovingSocialGroup");
            if (oldRoot != null)
            {
                if (Application.isPlaying)
                {
                    Destroy(oldRoot.gameObject);
                }
                else
                {
                    DestroyImmediate(oldRoot.gameObject);
                }
            }

            generatedRoot = null;
        }

        private void ResolveStartEndPoints()
        {
            startWorld = startPoint != null ? startPoint.position : transform.position;
            endWorld = endPoint != null
                ? endPoint.position
                : transform.position + transform.forward * fallbackPathLength;

            if (projectStartEndToGround)
            {
                startWorld = ProjectPointToGround(startWorld);
                endWorld = ProjectPointToGround(endWorld);
            }
        }

        private Vector3 ProjectPointToGround(Vector3 point)
        {
            Vector3 origin = point + Vector3.up * groundRaycastHeight;
            RaycastHit[] hits = Physics.RaycastAll(
                origin,
                Vector3.down,
                groundRaycastDistance,
                groundMask,
                QueryTriggerInteraction.Ignore);

            if (hits == null || hits.Length == 0)
            {
                return point;
            }

            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

            for (int i = 0; i < hits.Length; i++)
            {
                RaycastHit hit = hits[i];
                if (hit.collider == null || hit.collider.isTrigger)
                {
                    continue;
                }

                if (generatedRoot != null && hit.collider.transform.IsChildOf(generatedRoot))
                {
                    continue;
                }

                point.y = hit.point.y;
                return point;
            }

            return point;
        }

        private void BuildLocalSlots()
        {
            localSlots.Clear();

            switch (formation)
            {
                case FormationType.SideBySide:
                    BuildSideBySideSlots(localSlots);
                    break;
                case FormationType.VShape:
                    BuildVSlots(localSlots);
                    break;
                case FormationType.UShape:
                    BuildUSlots(localSlots);
                    break;
                default:
                    BuildSideBySideSlots(localSlots);
                    break;
            }

            if (centerSlotsAroundGroupCenter && localSlots.Count > 0)
            {
                Vector3 centroid = Vector3.zero;
                for (int i = 0; i < localSlots.Count; i++)
                {
                    centroid += localSlots[i];
                }
                centroid /= localSlots.Count;

                for (int i = 0; i < localSlots.Count; i++)
                {
                    localSlots[i] -= centroid;
                }
            }
        }

        private void BuildSideBySideSlots(List<Vector3> slots)
        {
            float center = (memberCount - 1) * 0.5f;
            for (int i = 0; i < memberCount; i++)
            {
                float x = (i - center) * sideBySideSpacing;
                slots.Add(new Vector3(x, 0f, 0f));
            }
        }

        private float Rank(int index)
        {
            // Odd n=3:  -1, 0, 1
            // Even n=4: -1.5, -0.5, 0.5, 1.5
            return index - (memberCount - 1) * 0.5f;
        }

        private void BuildVSlots(List<Vector3> slots)
        {
            if (memberCount <= 1)
            {
                slots.Add(Vector3.zero);
                return;
            }

            float halfAngleRadians =
                Mathf.Clamp(openingAngleDegrees, 10f, 220f) * 0.5f * Mathf.Deg2Rad;

            Vector3 backDirection = vApexInFront ? Vector3.back : Vector3.forward;
            Vector3 rightDirection = Vector3.right;

            for (int i = 0; i < memberCount; i++)
            {
                float rank = Rank(i);

                if (Mathf.Abs(rank) < 0.001f)
                {
                    slots.Add(Vector3.zero);
                    continue;
                }

                float side = Mathf.Sign(rank);
                float absRank = Mathf.Abs(rank);
                float armDistance = absRank * interPersonDistance;

                Vector3 offset =
                    backDirection * Mathf.Cos(halfAngleRadians) * armDistance +
                    rightDirection * side * Mathf.Sin(halfAngleRadians) * armDistance;

                slots.Add(offset);
            }
        }

        private void BuildUSlots(List<Vector3> slots)
        {
            if (memberCount <= 1)
            {
                slots.Add(Vector3.zero);
                return;
            }

            float opening = Mathf.Clamp(openingAngleDegrees, 10f, 220f);
            float stepDegrees = opening / Mathf.Max(memberCount - 1, 1);
            float stepRadians = stepDegrees * Mathf.Deg2Rad;

            // Chord distance between adjacent members is approximately interPersonDistance.
            float denominator = 2f * Mathf.Sin(stepRadians * 0.5f);
            float radius = interPersonDistance / Mathf.Max(0.001f, denominator);
            radius = Mathf.Clamp(radius, 0.2f, 20f);

            Vector3 rightDirection = Vector3.right;
            Vector3 forwardDirection = uOpenForward ? Vector3.forward : Vector3.back;

            for (int i = 0; i < memberCount; i++)
            {
                float angleDegrees = Rank(i) * stepDegrees;
                float angleRadians = angleDegrees * Mathf.Deg2Rad;

                float lateral = radius * Mathf.Sin(angleRadians);
                float forwardDepth = radius * (1f - Mathf.Cos(angleRadians));

                Vector3 offset =
                    rightDirection * lateral +
                    forwardDirection * forwardDepth;

                slots.Add(offset);
            }
        }

        private Quaternion ComputeLocalFacing(Vector3 localSlot)
        {
            if (memberFacingMode == MemberFacingMode.FaceMovementDirection)
            {
                Vector3 direction = lastMoveDirection;
                direction.y = 0f;
                if (direction.sqrMagnitude < 0.001f)
                {
                    direction = Vector3.forward;
                }

                Quaternion rootRotation = generatedRoot != null
                    ? generatedRoot.rotation
                    : currentGroupRotation;

                Quaternion worldFacing = Quaternion.LookRotation(direction.normalized, Vector3.up);
                return Quaternion.Inverse(rootRotation) * worldFacing;
            }

            if (memberFacingMode == MemberFacingMode.KeepInitialFormationFacing)
            {
                return Quaternion.identity;
            }

            Vector3 toCenter = -localSlot;
            toCenter.y = 0f;
            if (toCenter.sqrMagnitude < 0.001f)
            {
                return Quaternion.identity;
            }

            return Quaternion.LookRotation(toCenter.normalized, Vector3.up);
        }

        private GameObject CreateVisibleMember(
            MemberConfig config,
            int index,
            Vector3 localSlot,
            Quaternion localFacing,
            out bool personalityEnabled)
        {
            personalityEnabled = false;

            GameObject avatarPrefab = ResolveAvatarPrefab(
                config,
                out RuntimeAnimatorController controller,
                out bool supportsPersonality);

            if (avatarPrefab == null)
            {
                return null;
            }

            GameObject person = Instantiate(avatarPrefab, generatedRoot);
            person.name = "MovingGroupMember_" + index + "_" + avatarPrefab.name;
            person.transform.localPosition = localSlot;
            person.transform.localRotation = localFacing;

            PrepareVisualOnlyPerson(person);
            AddVisiblePersonBodyCollider(person, index);
            ConfigureAnimator(person, controller);

            personalityEnabled = config != null &&
                                 config.type == MemberType.Special &&
                                 config.GetEffectiveSource(defaultAppearanceSource) == CharacterSource.SimpleAppearanceAgent &&
                                 supportsPersonality &&
                                 config.enableReactionAnimation;

            return person;
        }

        private GameObject ResolveAvatarPrefab(
            MemberConfig config,
            out RuntimeAnimatorController controller,
            out bool supportsPersonality)
        {
            controller = null;
            supportsPersonality = false;

            CharacterSource source = config != null
                ? config.GetEffectiveSource(defaultAppearanceSource)
                : CharacterSource.RandomRocketbox;

            if (source == CharacterSource.SimpleAppearanceAgent)
            {
                if (simpleAppearanceAgentPrefab == null)
                {
                    Debug.LogWarning(
                        "[DynamicMovingAttentionGroupSpawner_WJC_SAFE_SlotFlip] SimpleAppearanceAgent prefab is missing. Falling back to Rocketbox.",
                        this);
                    return ResolveRandomRocketboxPrefab();
                }

                AppearanceAvatar appearance =
                    simpleAppearanceAgentPrefab.GetComponentInChildren<AppearanceAvatar>(true);

                if (appearance == null || appearance.avatars == null || appearance.avatars.Length == 0)
                {
                    Debug.LogWarning(
                        "[DynamicMovingAttentionGroupSpawner_WJC_SAFE_SlotFlip] SimpleAppearanceAgent has no AppearanceAvatar avatars. Falling back to Rocketbox.",
                        this);
                    return ResolveRandomRocketboxPrefab();
                }

                controller = appearance.animationController;
                supportsPersonality = controller != null;
                return appearance.avatars[UnityEngine.Random.Range(0, appearance.avatars.Length)];
            }

            return ResolveRandomRocketboxPrefab();
        }

        private GameObject ResolveRandomRocketboxPrefab()
        {
            LoadRocketboxPoolIfNeeded();
            if (rocketboxPool == null || rocketboxPool.Length == 0)
            {
                Debug.LogError(
                    "[DynamicMovingAttentionGroupSpawner_WJC_SAFE_SlotFlip] No Rocketbox prefabs found at Resources/" +
                    rocketboxResourcesPath + ".",
                    this);
                return null;
            }

            return rocketboxPool[UnityEngine.Random.Range(0, rocketboxPool.Length)];
        }

        private void LoadRocketboxPoolIfNeeded()
        {
            if (rocketboxPool != null && rocketboxPool.Length > 0)
            {
                return;
            }

            rocketboxPool = Resources.LoadAll<GameObject>(rocketboxResourcesPath);
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
                animators[i].cullingMode = AnimatorCullingMode.AlwaysAnimate;
            }
        }

        private void ConfigureAnimator(
            GameObject person,
            RuntimeAnimatorController controller)
        {
            Animator animator = IVI.AvatarAnimatorUtility.GetLocomotionAnimator(person);
            if (animator == null)
            {
                return;
            }

            if (controller != null)
            {
                animator.runtimeAnimatorController = controller;
            }

            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            animator.speed = walkingAnimatorSpeed;
            animator.Rebind();
            animator.Update(0f);

            if (setWalkingAnimationParameters)
            {
                SetAnimatorFloatIfPresent(animator, "Forward", walkingForwardParameterValue);
                SetAnimatorFloatIfPresent(animator, "Strafe", 0f);
                SetAnimatorBoolIfPresent(animator, "Idling", false);
            }
        }

        private void UpdateRobotEncounterState()
        {
            if (!stopForRobotEncounter || robot == null || generatedRoot == null)
            {
                stoppedByRobotEncounter = false;
                robotEncounterReactionTriggered = false;
                activeStartTime = -1f;
                invalidConditionStartTime = -1f;
                return;
            }

            float distanceToGroup = GroundDistance(robot.position, currentCenter);

            if (!stoppedByRobotEncounter)
            {
                if (!triggerArmed && requireRobotExitBeforeRetrigger &&
                    distanceToGroup >= resetDistance)
                {
                    triggerArmed = true;
                }

                bool triggerDistanceOk = distanceToGroup <= triggerDistance;
                bool triggerCooldownOk = Time.time >= nextAllowedTriggerTime;
                bool triggerHeadingOk = CanTriggerRobotEncounterByHeading();

                if (triggerArmed && triggerCooldownOk && triggerDistanceOk && triggerHeadingOk)
                {
                    StartRobotEncounter(distanceToGroup);
                }
            }
            else
            {
                if (ShouldReturnFromRobotEncounter(distanceToGroup))
                {
                    FinishRobotEncounter(distanceToGroup);
                }
            }
        }

        private void StartRobotEncounter(float distanceToGroup)
        {
            stoppedByRobotEncounter = true;
            robotEncounterReactionTriggered = false;
            activeStartTime = Time.time;
            invalidConditionStartTime = -1f;

            CaptureRobotEncounterContext();

            if (requireRobotExitBeforeRetrigger)
            {
                triggerArmed = false;
            }

            SetStoppedWalkingAnimationParameters(true);

            if (triggerReactionsWhenRobotStops)
            {
                TriggerAllRobotEncounterReactionsOnce();
            }

            if (logRobotEncounterDebug)
            {
                Debug.Log(
                    "[DynamicMovingAttentionGroupSpawner_WJC_SAFE_SlotFlip] Robot encounter triggered at distance " +
                    distanceToGroup.ToString("F2") + " m. Mode = " + reformationMode + ".",
                    this);
            }
        }

        private bool ShouldReturnFromRobotEncounter(float distanceToGroup)
        {
            bool byDuration = limitActiveDuration &&
                              activeStartTime >= 0f &&
                              Time.time - activeStartTime >= maxActiveDurationSeconds;

            if (byDuration)
            {
                return true;
            }

            if (reformationMode != RobotEncounterReformationMode.SplitCorridor)
            {
                return returnWhenRobotLeavesResetDistance &&
                       distanceToGroup >= resetDistance;
            }

            bool invalid = false;

            if (returnWhenRobotLeavesResetDistance &&
                distanceToGroup >= resetDistance)
            {
                invalid = true;
            }

            if (returnIfRobotHeadingNotTowardGroup &&
                !IsRobotHeadingTowardEncounterCenter(activeHeadingMaxAngleDegrees))
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

        private void FinishRobotEncounter(float distanceToGroup)
        {
            stoppedByRobotEncounter = false;
            robotEncounterReactionTriggered = false;
            activeStartTime = -1f;
            invalidConditionStartTime = -1f;
            nextAllowedTriggerTime = Time.time + retriggerCooldownSeconds;

            if (resetReactionsWhenRobotLeaves)
            {
                ResetAllReactionAnimationStates();
            }

            SetStoppedWalkingAnimationParameters(false);

            if (logRobotEncounterDebug)
            {
                Debug.Log(
                    "[DynamicMovingAttentionGroupSpawner_WJC_SAFE_SlotFlip] Robot encounter cleared at distance " +
                    distanceToGroup.ToString("F2") + " m. Group resumes walking.",
                    this);
            }
        }

        private void TriggerAllRobotEncounterReactionsOnce()
        {
            if (robotEncounterReactionTriggered)
            {
                return;
            }

            for (int i = 0; i < spawnedMembers.Count; i++)
            {
                MemberRuntime member = spawnedMembers[i];
                if (member == null || member.animator == null)
                {
                    continue;
                }

                if (!member.reactionEnabled ||
                    member.personality == PedestrianModulator.PersonalityType.Indifferent)
                {
                    continue;
                }

                member.reactionActive = true;
                TriggerPersonalityAnimation(member);
            }

            robotEncounterReactionTriggered = true;
        }

        private void ResetAllReactionAnimationStates()
        {
            for (int i = 0; i < spawnedMembers.Count; i++)
            {
                MemberRuntime member = spawnedMembers[i];
                if (member == null)
                {
                    continue;
                }

                member.reactionActive = false;
                ResetPersonalityAnimationState(member.animator);
            }
        }

        private void SetStoppedWalkingAnimationParameters(bool stopped)
        {
            if (!setWalkingAnimationParameters)
            {
                return;
            }

            for (int i = 0; i < spawnedMembers.Count; i++)
            {
                MemberRuntime member = spawnedMembers[i];
                if (member == null || member.animator == null)
                {
                    continue;
                }

                if (stopped && pauseWalkingAnimationWhileStoppedForRobot)
                {
                    SetAnimatorFloatIfPresent(member.animator, "Forward", 0f);
                    SetAnimatorFloatIfPresent(member.animator, "Strafe", 0f);
                    SetAnimatorBoolIfPresent(member.animator, "Idling", true);
                }
                else
                {
                    SetAnimatorFloatIfPresent(member.animator, "Forward", walkingForwardParameterValue);
                    SetAnimatorFloatIfPresent(member.animator, "Strafe", 0f);
                    SetAnimatorBoolIfPresent(member.animator, "Idling", false);
                }
            }
        }

        private void UpdateMovement()
        {
            if (stoppedByRobotEncounter)
            {
                ApplyGroupPose(lastMoveDirection);
                return;
            }

            if (turningAtEndpoint)
            {
                UpdateEndpointTurn();
                return;
            }

            if (waitingAtEndpoint)
            {
                waitTimer -= Time.deltaTime;
                if (waitTimer <= 0f)
                {
                    waitingAtEndpoint = false;

                    if (!pingPong)
                    {
                        currentCenter = startWorld;
                        targetCenter = endWorld;
                        movingTowardEnd = true;
                        UpdateLastMoveDirectionTowardTarget();
                    }
                }

                ApplyGroupPose(lastMoveDirection);
                return;
            }

            Vector3 toTarget = targetCenter - currentCenter;
            toTarget.y = 0f;
            float distance = toTarget.magnitude;

            if (distance <= arrivalTolerance)
            {
                currentCenter = targetCenter;
                generatedRoot.position = currentCenter;

                if (pingPong)
                {
                    bool nextMovingTowardEnd = !movingTowardEnd;
                    Vector3 nextTarget = nextMovingTowardEnd ? endWorld : startWorld;

                    if (endpointTurnMode == EndpointTurnMode.NaturalTurnAfterEndpoint)
                    {
                        StartEndpointTurn(nextMovingTowardEnd, nextTarget);
                        return;
                    }

                    movingTowardEnd = nextMovingTowardEnd;
                    targetCenter = nextTarget;
                    UpdateLastMoveDirectionTowardTarget();
                }
                else
                {
                    currentCenter = startWorld;
                    targetCenter = endWorld;
                    movingTowardEnd = true;
                    UpdateLastMoveDirectionTowardTarget();
                }

                waitingAtEndpoint = true;
                waitTimer = waitAtEndSeconds;
                ApplyGroupPose(lastMoveDirection);
                return;
            }

            Vector3 moveDirection = toTarget.normalized;
            lastMoveDirection = moveDirection;

            float step = moveSpeed * Time.deltaTime;
            float lookAheadStep = step + moveSpeed * obstacleLookAheadSeconds;
            Vector3 candidateCenter = Vector3.MoveTowards(currentCenter, targetCenter, Mathf.Max(step, lookAheadStep));

            Transform blockingObstacle;
            if (ShouldStopForVisiblePersonObstacle(candidateCenter, moveDirection, out blockingObstacle))
            {
                stoppedByVisiblePersonObstacle = true;
                if (logObstacleAvoidanceDebug && blockingObstacle != null)
                {
                    Debug.Log(
                        "[DynamicMovingAttentionGroupSpawner_WJC_SAFE_SlotFlip] StopAndWait: moving group paused before overlapping obstacle " +
                        blockingObstacle.name + ".",
                        this);
                }

                ApplyGroupPose(moveDirection);
                return;
            }

            stoppedByVisiblePersonObstacle = false;
            currentCenter = Vector3.MoveTowards(currentCenter, targetCenter, step);

            ApplyGroupPose(moveDirection);
        }

        private void StartEndpointTurn(bool nextMovingTowardEnd, Vector3 nextTarget)
        {
            endpointTurnMovingTowardEndAfterTurn = nextMovingTowardEnd;
            endpointTurnTargetAfterTurn = nextTarget;
            endpointTurnStartCenter = currentCenter;

            endpointTurnStartDirection = lastMoveDirection;
            endpointTurnStartDirection.y = 0f;
            if (endpointTurnStartDirection.sqrMagnitude < 0.001f)
            {
                endpointTurnStartDirection = (targetCenter - currentCenter);
                endpointTurnStartDirection.y = 0f;
            }
            if (endpointTurnStartDirection.sqrMagnitude < 0.001f)
            {
                endpointTurnStartDirection = Vector3.forward;
            }
            endpointTurnStartDirection.Normalize();

            endpointTurnDurationRuntime = formation == FormationType.SideBySide
                ? sideBySideEndpointTurnDuration
                : vuEndpointTurnDuration;
            endpointTurnDurationRuntime = Mathf.Max(0.05f, endpointTurnDurationRuntime);

            endpointTurnTimer = 0f;
            turningAtEndpoint = true;
            endpointPreTurnRepositioning = false;
            useEndpointCustomSlots = false;
            stoppedByVisiblePersonObstacle = false;
            endpointTurnStartSlots.Clear();
            endpointTurnTargetSlots.Clear();

            bool isVuShape = formation == FormationType.VShape || formation == FormationType.UShape;
            if (isVuShape && vuRepositionSlotsBeforeEndpointTurn)
            {
                PrepareEndpointSlotFlip(nextTarget);
                endpointPreTurnRepositioning = true;
                useEndpointCustomSlots = true;
                endpointTurnTimer = 0f;
                ApplyGroupPosePreservingEndpointSlots(endpointTurnStartDirection);
                return;
            }

            // Side-by-side: no slot flip is needed. Keep positions fixed and turn members in place.
            PrepareEndpointInPlaceTurnSlots();
            useEndpointCustomSlots = true;
            ApplyGroupPosePreservingEndpointSlots(endpointTurnStartDirection);
        }

        private void PrepareEndpointInPlaceTurnSlots()
        {
            endpointTurnStartSlots.Clear();
            endpointTurnTargetSlots.Clear();

            for (int i = 0; i < spawnedMembers.Count; i++)
            {
                MemberRuntime member = spawnedMembers[i];
                Vector3 slot = member != null ? member.localSlot : Vector3.zero;
                endpointTurnStartSlots.Add(slot);
                endpointTurnTargetSlots.Add(slot);
            }
        }

        private void PrepareEndpointSlotFlip(Vector3 nextTarget)
        {
            endpointTurnStartSlots.Clear();
            endpointTurnTargetSlots.Clear();

            Vector3 nextDirection = nextTarget - currentCenter;
            nextDirection.y = 0f;
            if (nextDirection.sqrMagnitude < 0.001f)
            {
                nextDirection = -endpointTurnStartDirection;
            }
            if (nextDirection.sqrMagnitude < 0.001f)
            {
                nextDirection = Vector3.forward;
            }
            nextDirection.Normalize();

            for (int i = 0; i < spawnedMembers.Count; i++)
            {
                MemberRuntime member = spawnedMembers[i];
                if (member == null)
                {
                    endpointTurnStartSlots.Add(Vector3.zero);
                    endpointTurnTargetSlots.Add(Vector3.zero);
                    continue;
                }

                endpointTurnStartSlots.Add(member.localSlot);
                endpointTurnTargetSlots.Add(GetRouteRelativeSlot(member.baseLocalSlot, nextDirection));
            }
        }

        private Vector3 GetRouteRelativeSlot(Vector3 baseSlot, Vector3 movementDirection)
        {
            movementDirection.y = 0f;
            if (movementDirection.sqrMagnitude < 0.001f)
            {
                movementDirection = lastMoveDirection.sqrMagnitude > 0.001f
                    ? lastMoveDirection
                    : Vector3.forward;
            }

            Quaternion rootRotation = generatedRoot != null
                ? generatedRoot.rotation
                : fixedFormationWorldRotation;

            Vector3 localDirection = Quaternion.Inverse(rootRotation) * movementDirection.normalized;
            localDirection.y = 0f;
            if (localDirection.sqrMagnitude < 0.001f)
            {
                localDirection = Vector3.forward;
            }

            Quaternion localDelta = Quaternion.FromToRotation(Vector3.forward, localDirection.normalized);
            return localDelta * baseSlot;
        }

        private void UpdateEndpointTurn()
        {
            if (endpointPreTurnRepositioning)
            {
                endpointTurnTimer += Time.deltaTime;
                float t = Mathf.Clamp01(endpointTurnTimer / Mathf.Max(0.05f, vuEndpointPreTurnRepositionDuration));
                float smoothT = Smooth01(t);

                for (int i = 0; i < spawnedMembers.Count; i++)
                {
                    MemberRuntime member = spawnedMembers[i];
                    if (member == null)
                    {
                        continue;
                    }

                    Vector3 startSlot = i < endpointTurnStartSlots.Count ? endpointTurnStartSlots[i] : member.localSlot;
                    Vector3 targetSlot = i < endpointTurnTargetSlots.Count ? endpointTurnTargetSlots[i] : member.localSlot;
                    member.localSlot = Vector3.Lerp(startSlot, targetSlot, smoothT);
                    if (member.root != null)
                    {
                        member.root.localPosition = member.localSlot;
                    }
                }

                lastMoveDirection = endpointTurnStartDirection;
                ApplyGroupPosePreservingEndpointSlots(endpointTurnStartDirection);

                if (t >= 1f)
                {
                    endpointPreTurnRepositioning = false;
                    endpointTurnTimer = 0f;
                    for (int i = 0; i < spawnedMembers.Count; i++)
                    {
                        MemberRuntime member = spawnedMembers[i];
                        if (member != null && i < endpointTurnTargetSlots.Count)
                        {
                            member.localSlot = endpointTurnTargetSlots[i];
                            if (member.root != null)
                            {
                                member.root.localPosition = member.localSlot;
                            }
                        }
                    }
                }

                return;
            }

            endpointTurnTimer += Time.deltaTime;
            float turnT = Mathf.Clamp01(endpointTurnTimer / Mathf.Max(0.05f, endpointTurnDurationRuntime));
            float smoothTurnT = Smooth01(turnT);

            Vector3 targetDirection = endpointTurnTargetAfterTurn - currentCenter;
            targetDirection.y = 0f;
            if (targetDirection.sqrMagnitude < 0.001f)
            {
                targetDirection = -endpointTurnStartDirection;
            }
            if (targetDirection.sqrMagnitude < 0.001f)
            {
                targetDirection = Vector3.forward;
            }
            targetDirection.Normalize();

            Vector3 turnDirection = Vector3.Slerp(endpointTurnStartDirection, targetDirection, smoothTurnT);
            turnDirection.y = 0f;
            if (turnDirection.sqrMagnitude < 0.001f)
            {
                turnDirection = targetDirection;
            }
            turnDirection.Normalize();

            currentCenter = endpointTurnStartCenter;
            lastMoveDirection = turnDirection;
            ApplyGroupPosePreservingEndpointSlots(turnDirection);

            if (turnT >= 1f)
            {
                turningAtEndpoint = false;
                endpointPreTurnRepositioning = false;
                useEndpointCustomSlots = false;
                movingTowardEnd = endpointTurnMovingTowardEndAfterTurn;
                targetCenter = endpointTurnTargetAfterTurn;
                UpdateLastMoveDirectionTowardTarget();
                ApplyGroupPose(lastMoveDirection);
            }
        }

        private void ApplyGroupPosePreservingEndpointSlots(Vector3 movementDirection)
        {
            useEndpointCustomSlots = true;
            ApplyGroupPose(movementDirection);
        }

        private static float Smooth01(float t)
        {
            t = Mathf.Clamp01(t);
            return t * t * (3f - 2f * t);
        }

        private void UpdateLastMoveDirectionTowardTarget()
        {
            Vector3 direction = targetCenter - currentCenter;
            direction.y = 0f;

            if (direction.sqrMagnitude > 0.001f)
            {
                lastMoveDirection = direction.normalized;
            }
        }

        private void RestoreBaseMemberSlots()
        {
            for (int i = 0; i < spawnedMembers.Count; i++)
            {
                MemberRuntime member = spawnedMembers[i];
                if (member == null)
                {
                    continue;
                }

                member.localSlot = member.baseLocalSlot;
            }
        }

        private void UpdateRouteRelativeMemberSlots(Vector3 movementDirection)
        {
            if (spawnedMembers.Count == 0)
            {
                return;
            }

            movementDirection.y = 0f;
            if (movementDirection.sqrMagnitude < 0.001f)
            {
                movementDirection = lastMoveDirection.sqrMagnitude > 0.001f
                    ? lastMoveDirection
                    : Vector3.forward;
            }

            Vector3 localDirection = Quaternion.Inverse(fixedFormationWorldRotation) * movementDirection.normalized;
            localDirection.y = 0f;
            if (localDirection.sqrMagnitude < 0.001f)
            {
                localDirection = Vector3.forward;
            }

            Quaternion localDelta = Quaternion.FromToRotation(Vector3.forward, localDirection.normalized);

            for (int i = 0; i < spawnedMembers.Count; i++)
            {
                MemberRuntime member = spawnedMembers[i];
                if (member == null)
                {
                    continue;
                }

                member.localSlot = localDelta * member.baseLocalSlot;
            }
        }

        private void UpdateRobotEncounterReformationSlots(Vector3 movementDirection)
        {
            if (reformationMode == RobotEncounterReformationMode.StopAndFaceRobot ||
                robot == null ||
                generatedRoot == null ||
                spawnedMembers.Count == 0)
            {
                return;
            }

            if (reformationMode == RobotEncounterReformationMode.AttractArc)
            {
                UpdateAttractArcLocalSlots();
            }
            else if (reformationMode == RobotEncounterReformationMode.SplitCorridor)
            {
                UpdateSplitCorridorLocalSlots(movementDirection);
            }
        }

        private void UpdateAttractArcLocalSlots()
        {
            int movableCount = GetMovableMemberCount();
            if (movableCount <= 0)
            {
                RestoreEncounterBaseMemberSlots();
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
                radius = Mathf.Min(radius, maxRadius);
            }

            Vector3 arcRight = Vector3.Cross(Vector3.up, arcDirection).normalized;
            List<int> movableIndices = GetMovableIndicesOrdered(arcRight, encounterCenter);

            float centerYaw = DirectionToYaw(arcDirection);
            float startYaw = centerYaw - arcSpan * 0.5f;

            RestoreNonMovableEncounterBaseSlots();

            for (int slot = 0; slot < movableIndices.Count; slot++)
            {
                int memberIndex = movableIndices[slot];
                MemberRuntime member = spawnedMembers[memberIndex];

                float t = (slot + 0.5f) / movableIndices.Count;
                float yaw = startYaw + arcSpan * t;
                Vector3 dir = Quaternion.AngleAxis(yaw, Vector3.up) * Vector3.forward;
                Vector3 targetWorld = focus + dir.normalized * radius;
                targetWorld.y = currentCenter.y;

                member.localSlot = generatedRoot.InverseTransformPoint(targetWorld);
            }
        }

        private void UpdateSplitCorridorLocalSlots(Vector3 movementDirection)
        {
            int movableCount = GetMovableMemberCount();
            if (movableCount <= 0)
            {
                RestoreEncounterBaseMemberSlots();
                return;
            }

            Vector3 corridorDir = ResolveSplitCorridorDirection(false);
            if (corridorDir.sqrMagnitude < 0.001f)
            {
                corridorDir = encounterCenter - (hasLockedAttentionFocus ? lockedAttentionFocus : robot.position);
            }

            corridorDir.y = 0f;
            if (corridorDir.sqrMagnitude < 0.001f)
            {
                corridorDir = Vector3.forward;
            }
            corridorDir.Normalize();

            Vector3 corridorRight = Vector3.Cross(Vector3.up, corridorDir).normalized;
            List<int> movableIndices = GetMovableIndicesOrdered(corridorRight, encounterCenter);

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
                    if (movableIndices.Count % 2 == 1)
                    {
                        if (splitOddMemberByInitialSide)
                        {
                            float lateral = Vector3.Dot(
                                GetEncounterBaseWorldPosition(memberIndex) - encounterCenter,
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

            RestoreNonMovableEncounterBaseSlots();

            AssignSplitSideLocalSlots(left, -1f, corridorDir, corridorRight);
            AssignSplitSideLocalSlots(right, 1f, corridorDir, corridorRight);
        }

        private void AssignSplitSideLocalSlots(
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

                Vector3 relative = GetEncounterBaseWorldPosition(memberIndex) - encounterCenter;
                float originalAlong = Vector3.Dot(relative, corridorDir);
                float originalLateral = Vector3.Dot(relative, corridorRight);

                float extraAlongOffset = 0f;
                if (sideIndices.Count > 1 && splitAlongPathSpacing > 0f)
                {
                    extraAlongOffset = (j - (sideIndices.Count - 1) * 0.5f) * splitAlongPathSpacing;
                }

                float targetAlong = originalAlong + splitForwardOffset + extraAlongOffset;
                float targetLateralMagnitude = Mathf.Max(Mathf.Abs(originalLateral), splitSideOffset);
                float targetLateral = sideSign * targetLateralMagnitude;

                Vector3 targetWorld =
                    encounterCenter +
                    corridorDir * targetAlong +
                    corridorRight * targetLateral;
                targetWorld.y = currentCenter.y;

                member.localSlot = generatedRoot.InverseTransformPoint(targetWorld);
            }
        }

        private void CaptureRobotEncounterContext()
        {
            encounterCenter = currentCenter;
            encounterBaseLocalSlots.Clear();
            encounterBaseWorldPositions.Clear();

            for (int i = 0; i < spawnedMembers.Count; i++)
            {
                MemberRuntime member = spawnedMembers[i];
                Vector3 localSlot = member != null ? member.localSlot : Vector3.zero;
                encounterBaseLocalSlots.Add(localSlot);

                Vector3 worldPosition = generatedRoot != null
                    ? generatedRoot.TransformPoint(localSlot)
                    : currentCenter + localSlot;
                worldPosition.y = currentCenter.y;
                encounterBaseWorldPositions.Add(worldPosition);
            }

            if (robot != null)
            {
                lockedAttentionFocus = robot.position;
                lockedAttentionFocus.y = currentCenter.y;
                hasLockedAttentionFocus = true;
            }
            else
            {
                hasLockedAttentionFocus = false;
                lockedAttentionFocus = currentCenter;
            }

            lockedArcDirection = ResolveAttractArcDirection(true);
            hasLockedArcDirection = lockedArcDirection.sqrMagnitude > 0.001f;

            lockedSplitCorridorDirection = ResolveSplitCorridorDirection(true);
            hasLockedSplitCorridorDirection = lockedSplitCorridorDirection.sqrMagnitude > 0.001f;
        }

        private void RestoreEncounterBaseMemberSlots()
        {
            for (int i = 0; i < spawnedMembers.Count; i++)
            {
                MemberRuntime member = spawnedMembers[i];
                if (member == null)
                {
                    continue;
                }

                member.localSlot = GetEncounterBaseLocalSlot(i);
            }
        }

        private void RestoreNonMovableEncounterBaseSlots()
        {
            for (int i = 0; i < spawnedMembers.Count; i++)
            {
                MemberRuntime member = spawnedMembers[i];
                if (member == null)
                {
                    continue;
                }

                if (!member.moveDuringReformation)
                {
                    member.localSlot = GetEncounterBaseLocalSlot(i);
                }
            }
        }

        private Vector3 GetEncounterBaseLocalSlot(int index)
        {
            if (index >= 0 && index < encounterBaseLocalSlots.Count)
            {
                return encounterBaseLocalSlots[index];
            }

            if (index >= 0 && index < spawnedMembers.Count && spawnedMembers[index] != null)
            {
                return spawnedMembers[index].baseLocalSlot;
            }

            return Vector3.zero;
        }

        private Vector3 GetEncounterBaseWorldPosition(int index)
        {
            if (index >= 0 && index < encounterBaseWorldPositions.Count)
            {
                return encounterBaseWorldPositions[index];
            }

            if (index >= 0 && index < spawnedMembers.Count && spawnedMembers[index] != null)
            {
                return generatedRoot != null
                    ? generatedRoot.TransformPoint(spawnedMembers[index].baseLocalSlot)
                    : currentCenter + spawnedMembers[index].baseLocalSlot;
            }

            return currentCenter;
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
                    Vector3 pa = GetEncounterBaseWorldPosition(a) - centerReference;
                    Vector3 pb = GetEncounterBaseWorldPosition(b) - centerReference;

                    float aa = Mathf.Atan2(pa.x, pa.z);
                    float ab = Mathf.Atan2(pb.x, pb.z);

                    return aa.CompareTo(ab);
                });

                return indices;
            }

            rightReference.y = 0f;
            if (rightReference.sqrMagnitude < 0.001f)
            {
                rightReference = Vector3.right;
            }
            rightReference.Normalize();

            indices.Sort((a, b) =>
            {
                float la = Vector3.Dot(
                    GetEncounterBaseWorldPosition(a) - centerReference,
                    rightReference);

                float lb = Vector3.Dot(
                    GetEncounterBaseWorldPosition(b) - centerReference,
                    rightReference);

                return la.CompareTo(lb);
            });

            return indices;
        }

        private float GetAdjustedAttentionRadius(float arcSpanDegrees, int movingMemberCount)
        {
            float radius = Mathf.Max(attentionRadius, minRobotPersonDistance);

            float arcSpanRadians = Mathf.Max(0.01f, arcSpanDegrees * Mathf.Deg2Rad);
            float neededForSpacing = movingMemberCount * Mathf.Max(0.1f, minPersonPersonDistance) / arcSpanRadians;

            return Mathf.Max(radius, neededForSpacing);
        }

        private Vector3 GetAttentionFocusPosition()
        {
            Vector3 focus;

            if (attentionFocusMode == AttentionFocusMode.LockRobotPositionAtTrigger &&
                hasLockedAttentionFocus)
            {
                focus = lockedAttentionFocus;
            }
            else
            {
                focus = robot != null ? robot.position : currentCenter;
            }

            focus.y = currentCenter.y;
            return focus;
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
                    stoppedByRobotEncounter)
                {
                    return lockedArcDirection;
                }
            }

            Vector3 focus = GetAttentionFocusPosition();
            Vector3 direction = Vector3.forward;

            if (attentionArcDirectionMode == AttentionArcDirectionMode.FromAttentionFocusToOriginalGroup)
            {
                direction = encounterCenter - focus;
            }
            else if (attentionArcDirectionMode == AttentionArcDirectionMode.FromOriginalGroupToAttentionFocus)
            {
                direction = focus - encounterCenter;
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

            Vector3 focus = hasLockedAttentionFocus ? lockedAttentionFocus : robot != null ? robot.position : currentCenter;
            focus.y = currentCenter.y;

            Vector3 direction = Vector3.forward;

            if (splitCorridorDirectionMode == SplitCorridorDirectionMode.FromRobotTriggerPositionToOriginalGroup)
            {
                direction = encounterCenter - focus;
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

        private bool CanTriggerRobotEncounterByHeading()
        {
            if (reformationMode != RobotEncounterReformationMode.SplitCorridor ||
                !requireRobotHeadingTowardGroupToTrigger)
            {
                return true;
            }

            return IsRobotHeadingTowardEncounterCenter(triggerHeadingMaxAngleDegrees);
        }

        private bool IsRobotHeadingTowardEncounterCenter(float maxAngleDegrees)
        {
            if (robot == null)
            {
                return false;
            }

            Vector3 center = stoppedByRobotEncounter ? encounterCenter : currentCenter;
            Vector3 toGroup = center - robot.position;
            toGroup.y = 0f;
            if (toGroup.sqrMagnitude < 0.001f)
            {
                return true;
            }

            Vector3 heading = GetRobotHeadingVector();
            heading.y = 0f;
            if (heading.sqrMagnitude < 0.001f)
            {
                return false;
            }

            float angle = Vector3.Angle(heading.normalized, toGroup.normalized);
            return angle <= maxAngleDegrees;
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
                heading = Vector3.forward;
            }

            return heading.normalized;
        }

        private static Vector3 YawToDirection(float yawDegrees)
        {
            return Quaternion.AngleAxis(yawDegrees, Vector3.up) * Vector3.forward;
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

        private void ApplyGroupPose(Vector3 movementDirection)
        {
            if (movementDirection.sqrMagnitude < 0.001f)
            {
                movementDirection = lastMoveDirection.sqrMagnitude > 0.001f
                    ? lastMoveDirection
                    : Vector3.forward;
            }

            movementDirection.y = 0f;
            if (movementDirection.sqrMagnitude < 0.001f)
            {
                movementDirection = Vector3.forward;
            }

            Quaternion desiredRotation;
            if (formationRotationMode == FormationRotationMode.RouteRelativeSlots)
            {
                desiredRotation = fixedFormationWorldRotation;
                if (!useEndpointCustomSlots)
                {
                    UpdateRouteRelativeMemberSlots(movementDirection);
                }
            }
            else if (formationRotationMode == FormationRotationMode.FixedWorldYaw)
            {
                desiredRotation = fixedFormationWorldRotation;
                RestoreBaseMemberSlots();
            }
            else
            {
                desiredRotation = Quaternion.LookRotation(movementDirection.normalized, Vector3.up) *
                                  Quaternion.AngleAxis(formationYawDegrees, Vector3.up);
                RestoreBaseMemberSlots();
            }

            if (stoppedByRobotEncounter && !useEndpointCustomSlots)
            {
                UpdateRobotEncounterReformationSlots(movementDirection);
            }

            bool smoothFormationTurn =
                formationRotationMode == FormationRotationMode.SmoothFollowMovementDirection ||
                (formationRotationMode == FormationRotationMode.FollowMovementDirection && smoothTurn);

            if (smoothFormationTurn)
            {
                currentGroupRotation = Quaternion.RotateTowards(
                    currentGroupRotation,
                    desiredRotation,
                    groupTurnSpeedDegPerSec * Time.deltaTime);
            }
            else
            {
                currentGroupRotation = desiredRotation;
            }

            generatedRoot.position = currentCenter;
            generatedRoot.rotation = currentGroupRotation;
        }

        private Quaternion ComputeDesiredLocalFacing(MemberRuntime member)
        {
            bool shouldLookAtRobot =
                reformationMode == RobotEncounterReformationMode.SplitCorridor
                    ? splitMembersFaceRobot
                    : allMembersLookAtRobot;

            if (stoppedByRobotEncounter &&
                shouldLookAtRobot &&
                robot != null &&
                member != null &&
                member.root != null &&
                generatedRoot != null)
            {
                Vector3 toRobot = robot.position - member.root.position;
                toRobot.y = 0f;

                if (toRobot.sqrMagnitude > 0.001f)
                {
                    Quaternion worldFacing = Quaternion.LookRotation(toRobot.normalized, Vector3.up);
                    return Quaternion.Inverse(generatedRoot.rotation) * worldFacing;
                }
            }

            return ComputeLocalFacing(member.localSlot);
        }

        private void UpdateMemberLocalFacings()
        {
            for (int i = 0; i < spawnedMembers.Count; i++)
            {
                MemberRuntime member = spawnedMembers[i];
                if (member == null || member.root == null)
                {
                    continue;
                }

                Quaternion targetLocalFacing = ComputeDesiredLocalFacing(member);
                member.localFacing = Quaternion.RotateTowards(
                    member.root.localRotation,
                    targetLocalFacing,
                    memberTurnSpeed * Time.deltaTime);

                if (formationRotationMode == FormationRotationMode.RouteRelativeSlots || stoppedByRobotEncounter)
                {
                    member.root.localPosition = Vector3.MoveTowards(
                        member.root.localPosition,
                        member.localSlot,
                        memberMoveSpeed * Time.deltaTime);
                }
                else
                {
                    member.root.localPosition = member.localSlot;
                }

                member.root.localRotation = member.localFacing;

                if (member.animator != null && setWalkingAnimationParameters &&
                    !(member.nativeReaction && member.nativeReaction.OwnsBody))
                {
                    Vector3 remaining = member.localSlot - member.root.localPosition;
                    remaining.y = 0f;
                    bool movingToTemporaryOrReturnSlot = remaining.magnitude > targetTolerance;

                    bool stopAndFaceOnly =
                        stoppedByRobotEncounter &&
                        reformationMode == RobotEncounterReformationMode.StopAndFaceRobot;

                    bool idling = stopAndFaceOnly || (stoppedByRobotEncounter && !movingToTemporaryOrReturnSlot);
                    float forward = movingToTemporaryOrReturnSlot || !stoppedByRobotEncounter
                        ? walkingForwardParameterValue
                        : 0f;

                    SetAnimatorFloatIfPresent(member.animator, "Forward", forward);
                    SetAnimatorFloatIfPresent(member.animator, "Strafe", 0f);
                    SetAnimatorBoolIfPresent(member.animator, "Idling", idling);
                }
            }
        }

        private void UpdateReactionAnimations()
        {
            // Distance triggering is owned by GroupMemberNativeReaction.
        }

        private void TriggerPersonalityAnimation(MemberRuntime member)
        {
            // Native runner replaces the old guessed Curious/Scared Animator parameters.
        }

        private void ResetPersonalityAnimationState(Animator animator)
        {
            // Native runner owns reset; do not reset its parameters from the formation loop.
        }

        private static float GroundDistance(Vector3 a, Vector3 b)
        {
            a.y = 0f;
            b.y = 0f;
            return Vector3.Distance(a, b);
        }

        private void AddVisiblePersonBodyCollider(GameObject person, int index)
        {
            if (!addVisiblePersonBodyColliders || person == null)
            {
                return;
            }

            int requestedLayer = -1;
            if (!string.IsNullOrEmpty(visiblePersonColliderLayerName))
            {
                requestedLayer = LayerMask.NameToLayer(visiblePersonColliderLayerName);
                if (requestedLayer < 0)
                {
                    Debug.LogWarning(
                        "[DynamicMovingAttentionGroupSpawner_WJC_SAFE_SlotFlip] Visible Person Collider Layer Name '" +
                        visiblePersonColliderLayerName + "' does not exist. Keeping prefab layer.",
                        this);
                }
            }

            if (requestedLayer >= 0)
            {
                person.layer = requestedLayer;
            }

            CapsuleCollider capsule = person.AddComponent<CapsuleCollider>();
            capsule.direction = 1;
            capsule.radius = visiblePersonColliderRadius;
            capsule.height = Mathf.Max(visiblePersonColliderHeight, visiblePersonColliderRadius * 2f);
            capsule.center = new Vector3(0f, visiblePersonColliderCenterY, 0f);
            capsule.isTrigger = visiblePersonCollidersAreTriggers;

            Rigidbody rb = person.GetComponent<Rigidbody>();
            if (rb == null)
            {
                rb = person.AddComponent<Rigidbody>();
            }
            rb.isKinematic = true;
            rb.useGravity = false;

            DynamicMovingAttentionGroupPersonBodyReporter_WJC_SAFE reporter =
                person.GetComponent<DynamicMovingAttentionGroupPersonBodyReporter_WJC_SAFE>();
            if (reporter == null)
            {
                reporter = person.AddComponent<DynamicMovingAttentionGroupPersonBodyReporter_WJC_SAFE>();
            }
            reporter.Initialize(this, index);
        }

        internal void RegisterVisiblePersonBodyEnter(int memberIndex, Collider other)
        {
            if (ShouldIgnoreVisiblePersonBodyContact(other))
            {
                return;
            }

            activeVisiblePersonBodyContacts.Add(other);
            activeVisiblePersonBodyContactCount = activeVisiblePersonBodyContacts.Count;
        }

        internal void RegisterVisiblePersonBodyExit(int memberIndex, Collider other)
        {
            if (other == null)
            {
                return;
            }

            activeVisiblePersonBodyContacts.Remove(other);
            activeVisiblePersonBodyContactCount = activeVisiblePersonBodyContacts.Count;
        }

        private bool ShouldIgnoreVisiblePersonBodyContact(Collider other)
        {
            if (other == null)
            {
                return true;
            }

            // The native root's perception/social colliders are not visible body contacts.
            if (other.GetComponent<GroupReactionSFAgent>()) return true;
            var reactionBody = other.GetComponentInParent<GroupReactionSFAgent>();
            if (reactionBody && reactionBody.bridge && reactionBody.bridge.Owner == this) return true;

            if ((visiblePersonCollisionMask.value & (1 << other.gameObject.layer)) == 0)
            {
                return true;
            }

            if (generatedRoot != null && other.transform.IsChildOf(generatedRoot))
            {
                return true;
            }

            if (socialProxyTransform != null && other.transform.IsChildOf(socialProxyTransform))
            {
                return true;
            }

            return false;
        }

        private bool ShouldStopForVisiblePersonObstacle(
            Vector3 candidateCenter,
            Vector3 moveDirection,
            out Transform blockingObstacle)
        {
            blockingObstacle = null;

            if (obstacleAvoidanceMode == ObstacleAvoidanceMode.None || localSlots.Count == 0)
            {
                return false;
            }

            Quaternion candidateRotation;
            if (moveDirection.sqrMagnitude > 0.001f)
            {
                candidateRotation = Quaternion.LookRotation(moveDirection.normalized, Vector3.up) *
                                    Quaternion.AngleAxis(formationYawDegrees, Vector3.up);
            }
            else
            {
                candidateRotation = currentGroupRotation;
            }

            GatherVisiblePersonObstacles(obstacleBuffer);
            if (obstacleBuffer.Count == 0)
            {
                return false;
            }

            float combinedRadius = visiblePersonColliderRadius + obstaclePersonRadius + obstacleSafetyMargin;
            float combinedRadiusSq = combinedRadius * combinedRadius;

            for (int i = 0; i < localSlots.Count; i++)
            {
                Vector3 candidateMemberPosition = candidateCenter + candidateRotation * localSlots[i];
                candidateMemberPosition.y = 0f;

                for (int j = 0; j < obstacleBuffer.Count; j++)
                {
                    Transform obstacle = obstacleBuffer[j];
                    if (obstacle == null)
                    {
                        continue;
                    }

                    Vector3 obstaclePosition = obstacle.position;
                    obstaclePosition.y = 0f;
                    if ((candidateMemberPosition - obstaclePosition).sqrMagnitude <= combinedRadiusSq)
                    {
                        blockingObstacle = obstacle;
                        return true;
                    }
                }
            }

            return false;
        }

        private void GatherVisiblePersonObstacles(List<Transform> output)
        {
            output.Clear();

            if (autoDetectNamedPersonObstacles)
            {
                Transform root = obstacleSearchRoot;
                if (root == null)
                {
                    GameObject pedestrianControl = GameObject.Find("PedestrianControl");
                    if (pedestrianControl != null)
                    {
                        root = pedestrianControl.transform;
                    }
                }

                if (root != null)
                {
                    Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
                    for (int i = 0; i < transforms.Length; i++)
                    {
                        Transform t = transforms[i];
                        if (IsVisiblePersonObstacleTransform(t))
                        {
                            AddObstacleTransformIfUnique(output, t);
                        }
                    }
                }
            }

            if (explicitObstaclePeopleOrRoots != null)
            {
                for (int i = 0; i < explicitObstaclePeopleOrRoots.Count; i++)
                {
                    Transform root = explicitObstaclePeopleOrRoots[i];
                    if (root == null)
                    {
                        continue;
                    }

                    if (IsObstacleExcluded(root))
                    {
                        continue;
                    }

                    if (HasPersonLikeRendererOrAnimator(root) || IsVisiblePersonObstacleTransform(root))
                    {
                        AddObstacleTransformIfUnique(output, root);
                    }

                    Transform[] children = root.GetComponentsInChildren<Transform>(true);
                    for (int c = 0; c < children.Length; c++)
                    {
                        Transform child = children[c];
                        if (IsVisiblePersonObstacleTransform(child))
                        {
                            AddObstacleTransformIfUnique(output, child);
                        }
                    }
                }
            }
        }

        private bool IsVisiblePersonObstacleTransform(Transform t)
        {
            if (t == null || IsObstacleExcluded(t))
            {
                return false;
            }

            string n = t.name;
            return n.StartsWith("StaticGroupMember_", StringComparison.Ordinal) ||
                   n.StartsWith("DynamicGroupMember_", StringComparison.Ordinal) ||
                   n.StartsWith("DynamicAttentionMember_", StringComparison.Ordinal) ||
                   n.StartsWith("MovingGroupMember_", StringComparison.Ordinal) ||
                   n.Contains("StaticGroupMember") ||
                   n.Contains("DynamicGroupMember") ||
                   n.Contains("DynamicAttentionMember") ||
                   n.Contains("MovingGroupMember");
        }

        private bool IsObstacleExcluded(Transform t)
        {
            if (t == null)
            {
                return true;
            }

            var nativeMember = t.GetComponentInParent<GroupReactionSFAgent>();
            if (nativeMember && nativeMember.bridge && nativeMember.bridge.Owner == this) return true;

            if (generatedRoot != null && t.IsChildOf(generatedRoot))
            {
                return true;
            }

            if (socialProxyTransform != null && t.IsChildOf(socialProxyTransform))
            {
                return true;
            }

            return false;
        }

        private bool HasPersonLikeRendererOrAnimator(Transform root)
        {
            if (root == null)
            {
                return false;
            }

            if (root.GetComponentInChildren<Animator>(true) != null)
            {
                return true;
            }

            Renderer renderer = root.GetComponentInChildren<Renderer>(true);
            return renderer != null;
        }

        private static void AddObstacleTransformIfUnique(List<Transform> output, Transform t)
        {
            if (t == null)
            {
                return;
            }

            for (int i = 0; i < output.Count; i++)
            {
                if (output[i] == t)
                {
                    return;
                }
            }

            output.Add(t);
        }

        private float EstimateGroupSocialRadiusFromSlots()
        {
            float farthest = 0f;
            for (int i = 0; i < localSlots.Count; i++)
            {
                Vector3 slot = localSlots[i];
                slot.y = 0f;
                farthest = Mathf.Max(farthest, slot.magnitude);
            }

            return Mathf.Max(0.1f, farthest + personRadius + groupProxyPadding);
        }

        private void CreateGroupSocialProxy(float radius)
        {
            if (socialProxyPrefab == null)
            {
                Debug.LogWarning(
                    "[DynamicMovingAttentionGroupSpawner_WJC_SAFE_SlotFlip] Social Proxy Prefab is missing. Group-level social force will not be created.",
                    this);
                return;
            }

            GameObject proxyContainer = Instantiate(
                socialProxyPrefab,
                currentCenter,
                currentGroupRotation,
                generatedRoot);

            proxyContainer.name = "MovingGroupSocialForceProxy";
            proxyContainer.transform.localPosition = Vector3.zero;
            proxyContainer.transform.localRotation = Quaternion.identity;

            IVI.INavigable proxyAgent = proxyContainer.GetComponentInChildren<IVI.INavigable>();
            if (proxyAgent == null)
            {
                Debug.LogError(
                    "[DynamicMovingAttentionGroupSpawner_WJC_SAFE_SlotFlip] Social Proxy Prefab has no IVI.INavigable/SFAgent.",
                    this);
                Destroy(proxyContainer);
                return;
            }

            DisableProxyTrackingComponents(proxyContainer);
            HideProxyVisuals(proxyContainer);

            GameObject proxyAgentObject = proxyAgent.gameObject;
            bool exactRadiusSet = TrySetSocialForceRadius(proxyAgentObject, radius);
            float colliderRadius = radius;

            if (!exactRadiusSet)
            {
                float scale = radius / Mathf.Max(0.01f, proxyBaseRadius);
                proxyAgent.transform.localScale = Vector3.Scale(
                    proxyAgent.transform.localScale,
                    new Vector3(scale, scale, scale));

                colliderRadius = proxyBaseRadius;

                Debug.LogWarning(
                    "[DynamicMovingAttentionGroupSpawner_WJC_SAFE_SlotFlip] Could not find an explicit SFAgent radius field. " +
                    "Using transform scale as fallback.",
                    this);
            }

            CapsuleCollider groupCollider = proxyAgentObject.AddComponent<CapsuleCollider>();
            groupCollider.direction = 1;
            groupCollider.radius = colliderRadius;
            groupCollider.height = colliderRadius * 2f;
            groupCollider.center = new Vector3(0f, colliderRadius, 0f);
            groupCollider.isTrigger = true;

            proxyAgent.transform.position = currentCenter;
            proxyAgent.InitDest(currentCenter);

            socialProxyTransform = proxyAgent.transform;
            socialProxyAgent = proxyAgent;

            DynamicMovingAttentionGroupSocialProxyFollower_WJC_SAFE follower =
                proxyAgentObject.AddComponent<DynamicMovingAttentionGroupSocialProxyFollower_WJC_SAFE>();
            follower.Initialize(proxyAgent, generatedRoot);
        }

        private void UpdateSocialProxy()
        {
            if (!useGroupSocialProxy || socialProxyTransform == null || socialProxyAgent == null)
            {
                return;
            }

            socialProxyTransform.position = currentCenter;
            socialProxyAgent.InitDest(currentCenter);
        }

        private void HideProxyVisuals(GameObject proxyContainer)
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

            Collider[] colliders = proxyContainer.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++)
            {
                colliders[i].enabled = false;
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
                    fullName.EndsWith(".TrackedTrajectory", StringComparison.Ordinal) ||
                    typeName == "PositionPublisher")
                {
                    behaviour.enabled = false;
                }
            }
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

                Type type = component.GetType();
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
                    changed |= TrySetFloatMember(component, type, candidateNames[n], radius);
                }
            }

            return changed;
        }

        private bool TrySetFloatMember(
            object target,
            Type startingType,
            string memberName,
            float value)
        {
            Type type = startingType;
            while (type != null)
            {
                FieldInfo field = type.GetField(
                    memberName,
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly);

                if (field != null &&
                    field.FieldType == typeof(float))
                {
                    // Do not touch const/static/readonly fields. Some SEAN/Unity components expose
                    // constant radius-like fields; RuntimeFieldInfo.SetValue throws
                    // FieldAccessException if we try to modify them.
                    if (field.IsLiteral || field.IsInitOnly || field.IsStatic)
                    {
                        type = type.BaseType;
                        continue;
                    }

                    try
                    {
                        field.SetValue(target, value);
                        return true;
                    }
                    catch (FieldAccessException)
                    {
                        return false;
                    }
                    catch
                    {
                        return false;
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
                    property.CanWrite &&
                    property.GetIndexParameters().Length == 0)
                {
                    try
                    {
                        property.SetValue(target, value, null);
                        return true;
                    }
                    catch
                    {
                        return false;
                    }
                }

                type = type.BaseType;
            }

            return false;
        }

        private static bool SetAnimatorTriggerIfPresent(Animator animator, string parameterName)
        {
            if (animator == null || string.IsNullOrEmpty(parameterName))
            {
                return false;
            }

            AnimatorControllerParameter[] parameters = animator.parameters;
            for (int i = 0; i < parameters.Length; i++)
            {
                if (parameters[i].name == parameterName &&
                    parameters[i].type == AnimatorControllerParameterType.Trigger)
                {
                    animator.ResetTrigger(parameterName);
                    animator.SetTrigger(parameterName);
                    return true;
                }
            }

            return false;
        }

        private static bool ResetAnimatorTriggerIfPresent(Animator animator, string parameterName)
        {
            if (animator == null || string.IsNullOrEmpty(parameterName))
            {
                return false;
            }

            AnimatorControllerParameter[] parameters = animator.parameters;
            for (int i = 0; i < parameters.Length; i++)
            {
                if (parameters[i].name == parameterName &&
                    parameters[i].type == AnimatorControllerParameterType.Trigger)
                {
                    animator.ResetTrigger(parameterName);
                    return true;
                }
            }

            return false;
        }

        private static bool SetAnimatorBoolIfPresentReturningBool(
            Animator animator,
            string parameterName,
            bool value)
        {
            if (animator == null)
            {
                return false;
            }

            AnimatorControllerParameter[] parameters = animator.parameters;
            for (int i = 0; i < parameters.Length; i++)
            {
                if (parameters[i].name == parameterName &&
                    parameters[i].type == AnimatorControllerParameterType.Bool)
                {
                    animator.SetBool(parameterName, value);
                    return true;
                }
            }

            return false;
        }

        private static bool TryCrossFadeState(Animator animator, string stateName, float transitionDuration)
        {
            if (animator == null || string.IsNullOrEmpty(stateName))
            {
                return false;
            }

            int stateHash = Animator.StringToHash(stateName);
            for (int layer = 0; layer < animator.layerCount; layer++)
            {
                if (animator.HasState(layer, stateHash))
                {
                    animator.CrossFadeInFixedTime(stateHash, transitionDuration, layer);
                    return true;
                }
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

        private static bool SetAnimatorBoolIfPresent(
            Animator animator,
            string parameterName,
            bool value)
        {
            if (animator == null)
            {
                return false;
            }

            AnimatorControllerParameter[] parameters = animator.parameters;
            for (int i = 0; i < parameters.Length; i++)
            {
                if (parameters[i].name == parameterName &&
                    parameters[i].type == AnimatorControllerParameterType.Bool)
                {
                    animator.SetBool(parameterName, value);
                    return true;
                }
            }

            return false;
        }

        private void OnDrawGizmosSelected()
        {
            if (!drawGizmos)
            {
                return;
            }

            Vector3 s = startPoint != null ? startPoint.position : transform.position;
            Vector3 e = endPoint != null ? endPoint.position : transform.position + transform.forward * fallbackPathLength;

            Gizmos.DrawLine(s, e);
            Gizmos.DrawWireSphere(s, 0.2f);
            Gizmos.DrawWireSphere(e, 0.2f);

            Vector3 dir = e - s;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.001f)
            {
                dir = transform.forward;
            }
            Quaternion rot = Quaternion.LookRotation(dir.normalized, Vector3.up) *
                             Quaternion.AngleAxis(formationYawDegrees, Vector3.up);

            List<Vector3> previewSlots = new List<Vector3>();
            int oldCount = memberCount;
            memberCount = Mathf.Clamp(memberCount, 2, 4);
            BuildLocalSlotsForPreview(previewSlots);
            memberCount = oldCount;

            for (int i = 0; i < previewSlots.Count; i++)
            {
                Vector3 p = s + rot * previewSlots[i];
                Gizmos.DrawWireSphere(p, 0.12f);
            }

            float radius = Application.isPlaying && computedGroupSocialRadius > 0f
                ? computedGroupSocialRadius
                : EstimatePreviewRadius(previewSlots);
            Gizmos.DrawWireSphere(s, radius);

            if (Application.isPlaying && stoppedByVisiblePersonObstacle)
            {
                Gizmos.DrawWireSphere(currentCenter, Mathf.Max(0.1f, visiblePersonColliderRadius + obstacleSafetyMargin));
            }

            if (Application.isPlaying && stoppedByRobotEncounter)
            {
                Gizmos.DrawWireSphere(currentCenter, Mathf.Max(0.1f, triggerDistance));
            }
        }

        private void BuildLocalSlotsForPreview(List<Vector3> slots)
        {
            slots.Clear();
            switch (formation)
            {
                case FormationType.SideBySide:
                    BuildSideBySideSlots(slots);
                    break;
                case FormationType.VShape:
                    BuildVSlots(slots);
                    break;
                case FormationType.UShape:
                    BuildUSlots(slots);
                    break;
            }

            if (centerSlotsAroundGroupCenter && slots.Count > 0)
            {
                Vector3 centroid = Vector3.zero;
                for (int i = 0; i < slots.Count; i++)
                {
                    centroid += slots[i];
                }
                centroid /= slots.Count;

                for (int i = 0; i < slots.Count; i++)
                {
                    slots[i] -= centroid;
                }
            }
        }

        private float EstimatePreviewRadius(List<Vector3> slots)
        {
            if (!calculateGroupSocialRadiusAutomatically)
            {
                return manualGroupSocialRadius;
            }

            float farthest = 0f;
            for (int i = 0; i < slots.Count; i++)
            {
                Vector3 slot = slots[i];
                slot.y = 0f;
                farthest = Mathf.Max(farthest, slot.magnitude);
            }

            return Mathf.Max(0.1f, farthest + personRadius + groupProxyPadding);
        }
    }

    /// <summary>
    /// Reports trigger/collision contacts from a visible pedestrian body collider back to the moving group spawner.
    /// The collider is for RL collision detection; it does not drive the pedestrian's scripted movement.
    /// </summary>
    public class DynamicMovingAttentionGroupPersonBodyReporter_WJC_SAFE : MonoBehaviour
    {
        private DynamicMovingAttentionGroupSpawner_WJC_SAFE_SlotFlip owner;
        private int memberIndex;

        public void Initialize(DynamicMovingAttentionGroupSpawner_WJC_SAFE_SlotFlip spawner, int index)
        {
            owner = spawner;
            memberIndex = index;
        }

        private void OnTriggerEnter(Collider other)
        {
            if (owner != null)
            {
                owner.RegisterVisiblePersonBodyEnter(memberIndex, other);
            }
        }

        private void OnTriggerExit(Collider other)
        {
            if (owner != null)
            {
                owner.RegisterVisiblePersonBodyExit(memberIndex, other);
            }
        }

        private void OnCollisionEnter(Collision collision)
        {
            if (owner != null && collision != null)
            {
                owner.RegisterVisiblePersonBodyEnter(memberIndex, collision.collider);
            }
        }

        private void OnCollisionExit(Collision collision)
        {
            if (owner != null && collision != null)
            {
                owner.RegisterVisiblePersonBodyExit(memberIndex, collision.collider);
            }
        }
    }

    /// <summary>
    /// Keeps one invisible SFAgent proxy aligned with the moving group center.
    /// </summary>
    public class DynamicMovingAttentionGroupSocialProxyFollower_WJC_SAFE : MonoBehaviour
    {
        private IVI.INavigable agent;
        private Transform target;

        public void Initialize(IVI.INavigable navigable, Transform targetTransform)
        {
            agent = navigable;
            target = targetTransform;
        }

        private void LateUpdate()
        {
            if (target == null)
            {
                return;
            }

            transform.position = target.position;
            transform.rotation = target.rotation;

            if (agent != null)
            {
                agent.InitDest(target.position);
            }

            Rigidbody rb = GetComponent<Rigidbody>();
            if (rb != null && !rb.isKinematic)
            {
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }
        }
    }
}
