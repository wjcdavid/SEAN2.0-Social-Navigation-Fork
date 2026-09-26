using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Serialization;
// Native integration revision 2: random gait + automatic native L2; old allocation/placement rules, current reaction and special spawners.

namespace SEAN.Scenario.Agents
{
    /// <summary>
    /// Inspector-driven population composer for SEAN's Outdoor scene.
    ///
    /// - Regular and special populations use two independent requested sums.
    /// - The four weights apportion the final regular headcount, not category-pick events.
    /// - Moving groups are 2-3 people and SideBySide/V only.
    /// - Static groups are 3-4 people and L/O only.
    /// - Randomization assigns at most one personality per group; current native L2 choices are separate.
    /// - Curious uses native reaction settings; old serialized S68 presets map to the current crouch runner.
    /// - All personality and group reactions are robot-distance triggered.
    /// - All placement points are sampled on NavMesh inside this component's local box area.
    ///
    /// Existing group spawner scripts are configured and called; shared SEAN files are untouched.
    /// </summary>
    [DefaultExecutionOrder(11000)]
    [AddComponentMenu("SEAN/Population Scenario Generator")]
    [DisallowMultipleComponent]
    public class PopulationScenarioGenerator : MonoBehaviour
    {
        public enum PersonalityChoice
        {
            Normal,
            Scared,
            [InspectorName("Curious")] CuriousLegacy,
            Surprised,
            Assertive,
            S68KneelingDown,
            S68CrouchToStand,
            S68CrouchToStandV2,
            S68IviCrouch
        }

        public enum LegacyPersonalityChoice
        {
            Normal,
            Scared,
            [InspectorName("Curious")] CuriousLegacy,
            Surprised,
            Assertive
        }

        public enum SpecialAppearance
        {
            Normal,
            Cyclist,
            DogWalker,
            FemaleChild,
            MaleChild,
            PhoneUser,
            ScooterUser,
            WheelchairUser,
            WhiteCaneUser
        }

        public enum MovingFormation
        {
            SideBySide,
            VShape
        }

        public enum StaticFormation
        {
            LShape,
            OShape
        }

        public enum GroupReaction
        {
            None,
            Attention,
            Attraction,
            SplitCorridor
        }

        [Serializable]
        public class MovingSingleConfig
        {
            public string label = "Moving Single";
            public PersonalityChoice personality = PersonalityChoice.Normal;
            public SpecialAppearance appearance = SpecialAppearance.Normal;
            public InspectorPedestrianDemo.WalkAnimation walkAnimation;
            public bool carryBox;
            public GroupReactionSettings nativeReaction = new GroupReactionSettings {
                curiousReaction = InspectorPedestrianDemo.CuriousVariant.ApproachAndFollow };
            [Min(0.05f)] public float walkSpeedMultiplier = 1f;
        }

        [Serializable]
        public class StaticSingleConfig
        {
            public string label = "Static Single";
            public PersonalityChoice personality = PersonalityChoice.Normal;
            // Kept only for backwards-compatible scene deserialization. Static singles are always
            // ordinary humanoids; the eight special containers are moving-single-only.
            [HideInInspector] public SpecialAppearance appearance = SpecialAppearance.Normal;
            public float yawDegrees;
            public GroupReactionSettings nativeReaction = GroupReactionSettings.StationaryDefaults();
        }

        [Serializable]
        public class MovingGroupConfig
        {
            public string label = "Moving Group";
            [Range(2, 3)] public int memberCount = 2;
            public MovingFormation formation = MovingFormation.SideBySide;
            public GroupReaction groupReaction = GroupReaction.None;
            public List<LegacyPersonalityChoice> memberPersonalities =
                new List<LegacyPersonalityChoice>();
            public List<GroupReactionSettings> memberReactions = new List<GroupReactionSettings>();
            public bool stopForRobotEncounter = true;
            public bool limitEncounterDuration;
            [Min(0.1f)] public float maxEncounterSeconds = 3f;
            [Min(0f)] public float encounterCooldownSeconds = 2f;
            [Min(0f)] public float moveSpeed = 0.9f;
            [Range(10f, 220f), Tooltip("Used by V Shape only. Total angle between the two arms.")]
            public float openingAngleDegrees = 150f;
        }

        [Serializable]
        public class StaticGroupConfig
        {
            public string label = "Static Group";
            [Range(3, 4)] public int memberCount = 3;
            public StaticFormation formation = StaticFormation.LShape;
            public GroupReaction groupReaction = GroupReaction.None;
            public List<LegacyPersonalityChoice> memberPersonalities =
                new List<LegacyPersonalityChoice>();
            public List<GroupReactionSettings> memberReactions = new List<GroupReactionSettings>();
            public float yawDegrees;
        }

        [Serializable]
        public class S68ClipAssets
        {
            public RuntimeAnimatorController baseController;
            public AnimationClip kneelingDown;
            public AnimationClip crouchToStand;
            public AnimationClip crouchToStandV2;
            public AnimationClip iviCrouchCopy;
        }

        [Serializable]
        public class SpecialPrefabAssets
        {
            public GameObject cyclist;
            public GameObject dogWalker;
            public GameObject femaleChild;
            public GameObject maleChild;
            public GameObject phoneUser;
            public GameObject scooterUser;
            public GameObject wheelchairUser;
            public GameObject whiteCaneUser;
        }

        private struct ReservedCircle
        {
            public Vector3 position;
            public float radius;
            public bool blocksPatrolRoute;
        }

        private struct ReservedPatrolRoute
        {
            public Vector3 a;
            public Vector3 b;
            public float radius;
        }

        private struct AvatarSelection
        {
            public GameObject prefab;
            public RuntimeAnimatorController controller;
            public bool directVelocityDrive;
            public bool supportsLegacyPersonalityAnimation;
        }

        [Header("Lifecycle")]
        public bool generateOnStart;
        public bool clearBeforeGenerate = true;

        [Header("Selected NavMesh Area (local X/Z box)")]
        public Vector3 localAreaCenter;
        public Vector2 areaSize = new Vector2(20f, 20f);
        [Min(0.1f)] public float navMeshSampleRadius = 2f;
        [Tooltip("Reject NavMesh layers far above/below the selected area's world Y. Keep enabled to prevent rooftop/upper-floor groups.")]
        public bool constrainNavMeshToAreaHeight = true;
        [Min(0.1f), Tooltip("Allowed vertical difference from Transform + Local Area Center Y.")]
        public float navMeshHeightTolerance = 1.5f;
        [Min(0.1f), Tooltip("Runtime vertical snap distance for generated group roots and members.")]
        public float groupGroundSnapDistance = 2f;
        [Min(1)] public int placementAttemptsPerItem = 80;
        [Min(0f)] public float minimumClearance = 0.75f;
        public bool drawGenerationArea = true;

        [Header("Robot and Reaction Distances")]
        [Tooltip("Use the manually controlled robot body/base/trunk Transform.")]
        public Transform robot;
        [Min(0.1f)] public float personalityTriggerDistance = 3f;
        [Min(0.1f)] public float personalityResetDistance = 3.5f;
        [Min(0.1f)] public float groupTriggerDistance = 4f;
        [Min(0.1f)] public float groupResetDistance = 5f;

        [Header("Required Shared Assets")]
        public GameObject simpleAppearanceAgentPrefab;
        public string rocketboxResourcesPath = "Prefabs/Rocketbox";
        public SpecialPrefabAssets specialPrefabs = new SpecialPrefabAssets();
        public S68ClipAssets s68Clips = new S68ClipAssets();

        [Header("Legacy crouch preset conversion")]
        [HideInInspector, Min(0f)] public float s68FaceTurnSpeed = 150f;
        [HideInInspector, Min(0.05f)] public float s68TransitionSpeed = 1f;

        [Header("Moving Patrol")]
        [Min(0.5f)] public float patrolMinimumDistance = 4f;
        [Min(0.5f)] public float patrolMaximumDistance = 10f;
        [Tooltip("Reserve every moving-single A/B corridor so later singles and groups cannot cross it.")]
        public bool reserveFullMovingSingleRoutes = true;
        [Min(0f), Tooltip("Extra edge-to-edge gap between all moving patrol corridors.")]
        public float movingRouteClearance = 0.35f;

        [Header("Group Reaction Locked Values")]
        public Vector2 attentionRadiusRange = new Vector2(2.86f, 3f);
        [Min(0.1f)] public float splitCorridorHalfWidth = 1f;
        [Min(0f)] public float movingReactionMemberMoveSpeed = 0.4f;
        [Min(0f)] public float movingReactionMemberTurnSpeed = 150f;
        [Min(0f)] public float staticReactionMemberMoveSpeed = 0.5f;
        [Min(0f)] public float staticReactionMemberTurnSpeed = 1.2f;

        [Header("Randomizer - Two Independent Population Sums")]
        [FormerlySerializedAs("requestedTotalPopulation")]
        [Min(0), Tooltip("Ordinary population only. The four weights divide this final headcount.")]
        public int requestedRegularPopulation = 20;
        [Min(0), Tooltip("Additional moving-single special containers. Not included in the four weights.")]
        public int requestedSpecialPopulation = 0;
        public int randomSeed;
        [Range(0f, 1f)] public float personalityProbability = 0.35f;
        // Kept for old scenes only. New random choices use L1 personality + native L2, not old S68 enum presets.
        [HideInInspector] public float s68ShareOfSinglePersonalities = 0.35f;
        [Range(0f, 1f), Tooltip("Ordinary moving singles only. Chance of a special gait; otherwise use Original walking. Groups and special populations are unchanged.")]
        public float specialWalkProbability = 0.5f;
        [SerializeField, TextArea(3, 10)] private string lastNativeRandomization = "Click Auto-Fill, then Randomize Configuration to check resources and sample gait / L2 choices.";
        [Range(0f, 1f)] public float groupReactionProbability = 0.50f;
        [Tooltip("Final regular-person share after normalization with the other three weights.")]
        [Range(0f, 1f)] public float movingSingleWeight = 0.30f;
        [Tooltip("Final regular-person share after normalization; members are split into 2-3 person groups.")]
        [Range(0f, 1f)] public float movingGroupWeight = 0.25f;
        [Tooltip("Final regular-person share after normalization with the other three weights.")]
        [Range(0f, 1f)] public float staticSingleWeight = 0.25f;
        [Tooltip("Final regular-person share after normalization; members are split into 3-4 person groups.")]
        [Range(0f, 1f)] public float staticGroupWeight = 0.20f;
        [Tooltip("Random V total opening angle range.")]
        public Vector2 vOpeningAngleRange = new Vector2(80f, 150f);

        [Header("Editable Population Configuration")]
        public List<MovingSingleConfig> movingSingles = new List<MovingSingleConfig>();
        public List<MovingGroupConfig> movingGroups = new List<MovingGroupConfig>();
        public List<StaticSingleConfig> staticSingles = new List<StaticSingleConfig>();
        public List<StaticGroupConfig> staticGroups = new List<StaticGroupConfig>();

        [Header("Generated Collision Helpers")]
        public bool addVisibleBodyColliders = true;
        public bool bodyCollidersAreTriggers = true;
        [Min(0.05f)] public float bodyColliderRadius = 0.30f;
        [Min(0.1f)] public float bodyColliderHeight = 1.70f;

        [Header("Debug")]
        public bool logGeneration = true;

        [SerializeField, HideInInspector] private Transform generatedRoot;
        private readonly List<ReservedCircle> reserved = new List<ReservedCircle>();
        private readonly List<ReservedPatrolRoute> movingGroupRoutes =
            new List<ReservedPatrolRoute>();
        private readonly List<ReservedPatrolRoute> movingSingleRoutes =
            new List<ReservedPatrolRoute>();
        // Visible people that scripted moving groups must treat as StopAndWait obstacles.
        // This includes both static people and independently moving singles.
        private readonly List<Transform> personObstacleRoots = new List<Transform>();
        private readonly List<int> areaTriangleStarts = new List<int>();
        private NavMeshTriangulation navMeshTriangulation;
        private GameObject[] rocketboxPool;
        private System.Random random;

        public int ConfiguredPopulation
        {
            get
            {
                int total = movingSingles != null ? movingSingles.Count : 0;
                total += staticSingles != null ? staticSingles.Count : 0;
                if (movingGroups != null)
                    for (int i = 0; i < movingGroups.Count; i++)
                        if (movingGroups[i] != null) total += Mathf.Clamp(movingGroups[i].memberCount, 2, 3);
                if (staticGroups != null)
                    for (int i = 0; i < staticGroups.Count; i++)
                        if (staticGroups[i] != null) total += Mathf.Clamp(staticGroups[i].memberCount, 3, 4);
                return total;
            }
        }

        public int ConfiguredSpecialPopulation
        {
            get
            {
                int total = 0;
                if (movingSingles == null) return 0;
                for (int i = 0; i < movingSingles.Count; i++)
                {
                    if (movingSingles[i] != null
                        && movingSingles[i].appearance != SpecialAppearance.Normal)
                        total++;
                }
                return total;
            }
        }

        public int ConfiguredRegularPopulation
        {
            get { return Mathf.Max(0, ConfiguredPopulation - ConfiguredSpecialPopulation); }
        }

        public int RequestedCombinedPopulation
        {
            get { return Mathf.Max(0, requestedRegularPopulation) + Mathf.Max(0, requestedSpecialPopulation); }
        }

        private IEnumerator Start()
        {
            if (!generateOnStart) yield break;
            float deadline = Time.realtimeSinceStartup + 10f;
            while (!NativeSceneReady() && Time.realtimeSinceStartup < deadline) yield return null;
            yield return null;
            GeneratePopulation();
        }

        private static bool NativeSceneReady()
        {
            try { return SEAN.instance && SEAN.instance.robot; }
            catch (Exception) { return false; }
        }

        private void OnValidate()
        {
            if (specialPrefabs == null) specialPrefabs = new SpecialPrefabAssets();
            if (s68Clips == null) s68Clips = new S68ClipAssets();
            areaSize.x = Mathf.Max(0.5f, areaSize.x);
            areaSize.y = Mathf.Max(0.5f, areaSize.y);
            navMeshSampleRadius = Mathf.Max(0.1f, navMeshSampleRadius);
            navMeshHeightTolerance = Mathf.Max(0.1f, navMeshHeightTolerance);
            groupGroundSnapDistance = Mathf.Max(0.1f, groupGroundSnapDistance);
            placementAttemptsPerItem = Mathf.Max(1, placementAttemptsPerItem);
            minimumClearance = Mathf.Max(0f, minimumClearance);
            personalityResetDistance = Mathf.Max(personalityTriggerDistance + 0.01f, personalityResetDistance);
            groupResetDistance = Mathf.Max(groupTriggerDistance + 0.01f, groupResetDistance);
            patrolMaximumDistance = Mathf.Max(patrolMinimumDistance, patrolMaximumDistance);
            movingRouteClearance = Mathf.Max(0f, movingRouteClearance);
            attentionRadiusRange.x = Mathf.Max(0.1f, attentionRadiusRange.x);
            attentionRadiusRange.y = Mathf.Max(attentionRadiusRange.x, attentionRadiusRange.y);
            splitCorridorHalfWidth = Mathf.Max(0.1f, splitCorridorHalfWidth);
            requestedRegularPopulation = Mathf.Max(0, requestedRegularPopulation);
            requestedSpecialPopulation = Mathf.Max(0, requestedSpecialPopulation);
            NormalizeAngleRange(ref vOpeningAngleRange, 10f, 220f);
            NormalizeConfigurationLists();
        }

        private static void NormalizeAngleRange(ref Vector2 range, float minimum, float maximum)
        {
            range.x = Mathf.Clamp(range.x, minimum, maximum);
            range.y = Mathf.Clamp(range.y, minimum, maximum);
            if (range.y < range.x)
            {
                float swap = range.x;
                range.x = range.y;
                range.y = swap;
            }
        }

        public void NormalizeConfigurationLists()
        {
            if (movingSingles == null) movingSingles = new List<MovingSingleConfig>();
            if (movingGroups == null) movingGroups = new List<MovingGroupConfig>();
            if (staticSingles == null) staticSingles = new List<StaticSingleConfig>();
            if (staticGroups == null) staticGroups = new List<StaticGroupConfig>();

            for (int i = 0; i < movingSingles.Count; i++)
            {
                if (movingSingles[i] == null) movingSingles[i] = new MovingSingleConfig();
                if (movingSingles[i].appearance != SpecialAppearance.Normal)
                    movingSingles[i].personality = PersonalityChoice.Normal;
            }

            for (int i = 0; i < staticSingles.Count; i++)
            {
                if (staticSingles[i] == null) staticSingles[i] = new StaticSingleConfig();
                // Hard rule: special containers are moving-single-only.
                staticSingles[i].appearance = SpecialAppearance.Normal;
            }

            for (int i = 0; i < movingGroups.Count; i++)
            {
                if (movingGroups[i] == null) movingGroups[i] = new MovingGroupConfig();
                movingGroups[i].memberCount = Mathf.Clamp(movingGroups[i].memberCount, 2, 3);
                if (movingGroups[i].memberPersonalities == null)
                    movingGroups[i].memberPersonalities = new List<LegacyPersonalityChoice>();
                ResizeLegacyList(movingGroups[i].memberPersonalities, movingGroups[i].memberCount);
                ResizeNativeList(ref movingGroups[i].memberReactions, movingGroups[i].memberCount, false);
            }

            for (int i = 0; i < staticGroups.Count; i++)
            {
                if (staticGroups[i] == null) staticGroups[i] = new StaticGroupConfig();
                staticGroups[i].memberCount = Mathf.Clamp(staticGroups[i].memberCount, 3, 4);
                if (staticGroups[i].memberPersonalities == null)
                    staticGroups[i].memberPersonalities = new List<LegacyPersonalityChoice>();
                ResizeLegacyList(staticGroups[i].memberPersonalities, staticGroups[i].memberCount);
                ResizeNativeList(ref staticGroups[i].memberReactions, staticGroups[i].memberCount, true);
            }
        }

        private static void ResizeLegacyList(List<LegacyPersonalityChoice> values, int count)
        {
            if (values == null) return;
            while (values.Count < count) values.Add(LegacyPersonalityChoice.Normal);
            while (values.Count > count) values.RemoveAt(values.Count - 1);
        }

        [ContextMenu("Randomize Population Configuration")]
        public void RandomizeConfiguration()
        {
            random = new System.Random(randomSeed != 0 ? randomSeed : System.Environment.TickCount);
            movingSingles = new List<MovingSingleConfig>();
            movingGroups = new List<MovingGroupConfig>();
            staticSingles = new List<StaticSingleConfig>();
            staticGroups = new List<StaticGroupConfig>();

            int movingSinglePeople;
            int movingGroupPeople;
            int staticSinglePeople;
            int staticGroupPeople;
            AllocateRegularPopulation(
                Mathf.Max(0, requestedRegularPopulation),
                out movingSinglePeople,
                out movingGroupPeople,
                out staticSinglePeople,
                out staticGroupPeople);

            // A one-person moving group and static-group quotas 1/2/5 cannot be partitioned
            // into the legal group sizes. Move only the ungroupable tail into the matching
            // ordinary-single category. No person is dropped and the regular sum stays exact.
            if (movingGroupPeople == 1)
            {
                movingSinglePeople++;
                movingGroupPeople = 0;
            }
            if (staticGroupPeople == 1 || staticGroupPeople == 2)
            {
                staticSinglePeople += staticGroupPeople;
                staticGroupPeople = 0;
            }
            else if (staticGroupPeople == 5)
            {
                staticSinglePeople++;
                staticGroupPeople = 4;
            }

            AddRandomMovingSingles(movingSinglePeople, false);
            AddRandomMovingGroups(movingGroupPeople);
            AddRandomStaticSingles(staticSinglePeople);
            AddRandomStaticGroups(staticGroupPeople);
            AddRandomMovingSingles(Mathf.Max(0, requestedSpecialPopulation), true);

            NormalizeConfigurationLists();
            RandomizeNativeChoices();
            if (logGeneration)
            {
                Debug.Log("[PopulationGenerator] Regular quotas: moving singles="
                    + movingSinglePeople + ", moving-group members=" + movingGroupPeople
                    + ", static singles=" + staticSinglePeople
                    + ", static-group members=" + staticGroupPeople + ".", this);
                Debug.Log("[PopulationGenerator] Randomized " + ConfiguredRegularPopulation
                    + " regular + " + ConfiguredSpecialPopulation + " special = "
                    + ConfiguredPopulation + " people.", this);
            }
        }

        private void AllocateRegularPopulation(
            int total,
            out int movingSinglePeople,
            out int movingGroupPeople,
            out int staticSinglePeople,
            out int staticGroupPeople)
        {
            float[] weights =
            {
                Mathf.Max(0f, movingSingleWeight),
                Mathf.Max(0f, movingGroupWeight),
                Mathf.Max(0f, staticSingleWeight),
                Mathf.Max(0f, staticGroupWeight)
            };
            float sum = weights[0] + weights[1] + weights[2] + weights[3];
            int[] result = new int[4];
            float[] remainders = new float[4];

            if (total > 0 && sum <= 0f)
            {
                // A zero weight sum has no mathematical distribution. Keep the population
                // valid and visible by assigning all ordinary people to moving singles.
                result[0] = total;
            }
            else if (total > 0)
            {
                int assigned = 0;
                for (int i = 0; i < 4; i++)
                {
                    float exact = total * weights[i] / sum;
                    result[i] = Mathf.FloorToInt(exact);
                    remainders[i] = exact - result[i];
                    assigned += result[i];
                }

                // Hamilton/largest-remainder apportionment. Stable category order resolves
                // exact ties, so a non-zero Random Seed remains reproducible.
                while (assigned < total)
                {
                    int best = 0;
                    for (int i = 1; i < 4; i++)
                        if (remainders[i] > remainders[best]) best = i;
                    result[best]++;
                    remainders[best] = -1f;
                    assigned++;
                }
            }

            movingSinglePeople = result[0];
            movingGroupPeople = result[1];
            staticSinglePeople = result[2];
            staticGroupPeople = result[3];
        }

        private void AddRandomMovingSingles(int count, bool specialOnly)
        {
            for (int i = 0; i < count; i++)
            {
                MovingSingleConfig single = new MovingSingleConfig();
                single.label = specialOnly
                    ? "Special Moving Single " + (i + 1)
                    : "Moving Single " + (movingSingles.Count + 1);
                single.personality = specialOnly
                    ? PersonalityChoice.Normal
                    : RandomSinglePersonality();
                single.appearance = specialOnly
                    ? RandomSpecialAppearance()
                    : SpecialAppearance.Normal;
                movingSingles.Add(single);
            }
        }

        private void AddRandomStaticSingles(int count)
        {
            for (int i = 0; i < count; i++)
            {
                StaticSingleConfig single = new StaticSingleConfig();
                single.label = "Static Single " + (staticSingles.Count + 1);
                single.personality = RandomSinglePersonality();
                single.appearance = SpecialAppearance.Normal;
                single.yawDegrees = NextFloat(0f, 360f);
                staticSingles.Add(single);
            }
        }

        private void AddRandomMovingGroups(int people)
        {
            while (people > 0)
            {
                int size = PickMovingGroupSize(people);
                MovingGroupConfig group = new MovingGroupConfig();
                group.label = "Moving Group " + (movingGroups.Count + 1);
                group.memberCount = size;
                group.formation = Chance(0.5f) ? MovingFormation.SideBySide : MovingFormation.VShape;
                group.openingAngleDegrees = NextFloat(vOpeningAngleRange.x, vOpeningAngleRange.y);
                group.groupReaction = RandomGroupReaction();
                group.memberPersonalities = RandomGroupPersonalities(size);
                movingGroups.Add(group);
                people -= size;
            }
        }

        private void AddRandomStaticGroups(int people)
        {
            while (people > 0)
            {
                int size = PickStaticGroupSize(people);
                StaticGroupConfig group = new StaticGroupConfig();
                group.label = "Static Group " + (staticGroups.Count + 1);
                group.memberCount = size;
                group.formation = Chance(0.5f) ? StaticFormation.LShape : StaticFormation.OShape;
                group.groupReaction = RandomGroupReaction();
                group.memberPersonalities = RandomGroupPersonalities(size);
                group.yawDegrees = NextFloat(0f, 360f);
                staticGroups.Add(group);
                people -= size;
            }
        }

        private int PickMovingGroupSize(int remaining)
        {
            if (remaining == 2 || remaining == 3) return remaining;
            bool twoValid = IsMovingGroupQuotaRepresentable(remaining - 2);
            bool threeValid = IsMovingGroupQuotaRepresentable(remaining - 3);
            if (twoValid && threeValid) return Chance(0.5f) ? 2 : 3;
            return twoValid ? 2 : 3;
        }

        private int PickStaticGroupSize(int remaining)
        {
            if (remaining == 3 || remaining == 4) return remaining;
            bool threeValid = IsStaticGroupQuotaRepresentable(remaining - 3);
            bool fourValid = IsStaticGroupQuotaRepresentable(remaining - 4);
            if (threeValid && fourValid) return Chance(0.5f) ? 3 : 4;
            return threeValid ? 3 : 4;
        }

        private static bool IsMovingGroupQuotaRepresentable(int people)
        {
            return people == 0 || people >= 2;
        }

        private static bool IsStaticGroupQuotaRepresentable(int people)
        {
            return people == 0 || (people >= 3 && people != 5);
        }

        private PersonalityChoice RandomSinglePersonality()
        {
            if (!Chance(personalityProbability)) return PersonalityChoice.Normal;
            return (PersonalityChoice)NextIntInclusive((int)PersonalityChoice.Scared, (int)PersonalityChoice.Assertive);
        }

        // Configuration only: no Animator, movement, heading, cooldown, or formation solver is modified.
        private void RandomizeNativeChoices()
        {
            var available = PopulationNativeRandomCatalog.Read(simpleAppearanceAgentPrefab);
            int ordinaryMoving = 0, specialGaits = 0;
            foreach (var person in movingSingles) {
                if (person.appearance != SpecialAppearance.Normal) continue;
                ordinaryMoving++;
                person.walkAnimation = InspectorPedestrianDemo.WalkAnimation.Original;
                if (Chance(specialWalkProbability) && available.walks.Count > 0) {
                    person.walkAnimation = available.walks[NextIntInclusive(0, available.walks.Count - 1)];
                    specialGaits++;
                }
                person.nativeReaction = RandomNativeReaction(ToRuntimePersonality(person.personality), false, available);
            }
            foreach (var person in staticSingles)
                person.nativeReaction = RandomNativeReaction(ToRuntimePersonality(person.personality), true, available);
            foreach (var group in movingGroups)
                for (int i=0; i<group.memberCount; i++)
                    group.memberReactions[i] = RandomNativeReaction(ToRuntimePersonality(SafeLegacy(group.memberPersonalities,i)), false, available);
            foreach (var group in staticGroups)
                for (int i=0; i<group.memberCount; i++)
                    group.memberReactions[i] = RandomNativeReaction(ToRuntimePersonality(SafeLegacy(group.memberPersonalities,i)), true, available);
            var walkNames = new List<string>();
            foreach (var walk in available.walks) walkNames.Add(walk.ToString());
            lastNativeRandomization = "Ordinary moving singles: " + ordinaryMoving + "; special gait: " + specialGaits +
                "; Original: " + (ordinaryMoving-specialGaits) + "\nAvailable special gaits: " + string.Join(", ", walkNames) +
                "\nUnavailable gait/base-controller resources: " + string.Join(", ", available.unavailableWalks) +
                "\nAuto L2 resources: Crouch=" + available.crouch + ", KimodoSurprised=" + available.kimodoSurprised +
                ", OriginalGesture=" + available.assertiveGesture + ". Custom clips are never randomly selected.";
            if (logGeneration) Debug.Log("[Population native v2] " + lastNativeRandomization, this);
        }
        private GroupReactionSettings RandomNativeReaction(PedestrianModulator.PersonalityType personality,
            bool fixedPosition, PopulationNativeRandomCatalog.Snapshot available)
        {
            var result = fixedPosition ? GroupReactionSettings.StationaryDefaults() : new GroupReactionSettings();
            // Leave clip fields EMPTY: selected native variants resolve their own built-in assets.
            result.customReactionClip = null;
            result.crouchClipOverride = null;
            result.playGestureBeforeNativeBehavior = false;
            if (personality == PedestrianModulator.PersonalityType.Curious) {
                var choices = new List<InspectorPedestrianDemo.CuriousVariant> { InspectorPedestrianDemo.CuriousVariant.StandAndObserve };
                if (!fixedPosition) choices.Add(InspectorPedestrianDemo.CuriousVariant.ApproachAndFollow);
                if (available.crouch) choices.Add(InspectorPedestrianDemo.CuriousVariant.CrouchAndObserve);
                result.curiousReaction = choices[NextIntInclusive(0, choices.Count-1)];
            } else if (personality == PedestrianModulator.PersonalityType.Scared) {
                result.scaredReaction = InspectorPedestrianDemo.ScaredVariant.MoveAway;
            } else if (personality == PedestrianModulator.PersonalityType.Surprised) {
                result.surprisedReaction = available.kimodoSurprised && Chance(0.5f)
                    ? InspectorPedestrianDemo.SurprisedVariant.KimodoSurprised : InspectorPedestrianDemo.SurprisedVariant.OriginalClip;
            } else if (personality == PedestrianModulator.PersonalityType.Assertive) {
                result.assertiveReaction = available.assertiveGesture && Chance(0.5f)
                    ? InspectorPedestrianDemo.AssertiveVariant.OriginalGesture : InspectorPedestrianDemo.AssertiveVariant.OriginalBehavior;
            }
            return result;
        }

        private SpecialAppearance RandomSpecialAppearance()
        {
            return (SpecialAppearance)NextIntInclusive(
                (int)SpecialAppearance.Cyclist,
                (int)SpecialAppearance.WhiteCaneUser);
        }

        private List<LegacyPersonalityChoice> RandomGroupPersonalities(int count)
        {
            List<LegacyPersonalityChoice> result = new List<LegacyPersonalityChoice>();
            for (int i = 0; i < count; i++) result.Add(LegacyPersonalityChoice.Normal);
            // Hard randomization rule: zero or one personality-bearing member per group.
            if (Chance(personalityProbability))
            {
                int member = NextIntInclusive(0, count - 1);
                result[member] = (LegacyPersonalityChoice)NextIntInclusive(
                    (int)LegacyPersonalityChoice.Scared,
                    (int)LegacyPersonalityChoice.Assertive);
            }
            return result;
        }

        private GroupReaction RandomGroupReaction()
        {
            if (!Chance(groupReactionProbability)) return GroupReaction.None;
            return (GroupReaction)NextIntInclusive(
                (int)GroupReaction.Attention,
                (int)GroupReaction.SplitCorridor);
        }

        [ContextMenu("Generate Population")]
        public void GeneratePopulation()
        {
            if (!Application.isPlaying)
            {
                Debug.LogError("[PopulationGenerator] Enter Play Mode before Generate Population.", this);
                return;
            }

            NormalizeConfigurationLists();
            if (!ValidateAssets()) return;

            if (clearBeforeGenerate) ClearGeneratedPopulation();
            random = new System.Random(randomSeed != 0 ? randomSeed : System.Environment.TickCount);
            if (!BuildAreaNavMeshTriangleCache()) return;
            reserved.Clear();
            movingGroupRoutes.Clear();
            movingSingleRoutes.Clear();
            personObstacleRoots.Clear();
            rocketboxPool = Resources.LoadAll<GameObject>(rocketboxResourcesPath);

            GameObject rootObject = new GameObject("GeneratedPopulation");
            rootObject.transform.SetParent(transform, false);
            generatedRoot = rootObject.transform;

            int generatedPeople = 0;

            // Singles/static items are created first so moving groups receive every visible person
            // already present as an explicit StopAndWait obstacle.
            for (int i = 0; i < staticSingles.Count; i++)
                if (GenerateStaticSingle(staticSingles[i], i)) generatedPeople++;

            for (int i = 0; i < staticGroups.Count; i++)
                if (GenerateStaticGroup(staticGroups[i], i))
                    generatedPeople += staticGroups[i].memberCount;

            // Place the wider special-avatar patrol corridors before ordinary moving singles.
            // The configured list and generated object indices remain unchanged; only route
            // reservation order changes. This avoids painting a wide cyclist/dog/scooter route
            // into a corner after many narrow routes have already consumed the NavMesh.
            for (int i = 0; i < movingSingles.Count; i++)
                if (movingSingles[i] != null
                    && movingSingles[i].appearance != SpecialAppearance.Normal
                    && GenerateMovingSingle(movingSingles[i], i))
                    generatedPeople++;

            for (int i = 0; i < movingSingles.Count; i++)
                if (movingSingles[i] != null
                    && movingSingles[i].appearance == SpecialAppearance.Normal
                    && GenerateMovingSingle(movingSingles[i], i))
                    generatedPeople++;

            for (int i = 0; i < movingGroups.Count; i++)
                if (GenerateMovingGroup(movingGroups[i], i))
                    generatedPeople += movingGroups[i].memberCount;

            if (addVisibleBodyColliders && generatedRoot != null)
            {
                VisiblePersonBodyColliderInstaller installer =
                    generatedRoot.gameObject.AddComponent<VisiblePersonBodyColliderInstaller>();
                installer.installOnStart = false;
                installer.keepInstallingForNewGeneratedPeople = true;
                installer.logInstallDetails = false;
                installer.includeMovingGroupMembers = false;
                installer.colliderIsTrigger = bodyCollidersAreTriggers;
                installer.radius = bodyColliderRadius;
                installer.height = bodyColliderHeight;
                installer.centerY = bodyColliderHeight * 0.5f;
                installer.InstallNow();
            }

            if (logGeneration)
                Debug.Log("[Population native] Spawn requests accepted " + generatedPeople + "/"
                    + ConfiguredPopulation + " configured people.", this);
        }

        [ContextMenu("Clear Generated Population")]
        public void ClearGeneratedPopulation()
        {
            Transform root = generatedRoot != null ? generatedRoot : transform.Find("GeneratedPopulation");
            if (root != null)
            {
                root.gameObject.SetActive(false);
                if (Application.isPlaying) Destroy(root.gameObject);
                else DestroyImmediate(root.gameObject);
            }
            generatedRoot = null;
            reserved.Clear();
            movingGroupRoutes.Clear();
            movingSingleRoutes.Clear();
            personObstacleRoots.Clear();
        }

        private bool ValidateAssets()
        {
            if (specialPrefabs == null) specialPrefabs = new SpecialPrefabAssets();
            if (s68Clips == null) s68Clips = new S68ClipAssets();
            if (simpleAppearanceAgentPrefab == null)
            {
                Debug.LogError("[PopulationGenerator] Assign SimpleAppearanceAgent Prefab or click Auto-Fill Known Assets.", this);
                return false;
            }
            if (robot == null)
            {
                Debug.LogError("[PopulationGenerator] Assign the manually controlled Robot Transform.", this);
                return false;
            }
            if (NavMesh.CalculateTriangulation().vertices.Length == 0)
            {
                Debug.LogError("[PopulationGenerator] No baked NavMesh is available.", this);
                return false;
            }
            if (!NativeSceneReady())
            {
                Debug.LogError("[PopulationGenerator] Native SEAN robot is not initialized. Select/enable the robot, wait one frame, then Generate again.", this);
                return false;
            }
            return ValidateNativeChoices();
        }

        private bool ValidateNativeChoices()
        {
            bool valid = true;
            foreach (var item in movingSingles)
                if (item != null && item.appearance == SpecialAppearance.Normal)
                    valid &= ValidateChoice(item.personality, item.nativeReaction, "Moving single: " + item.label);
            foreach (var item in staticSingles)
                if (item != null) valid &= ValidateChoice(item.personality, item.nativeReaction, "Static single: " + item.label);
            foreach (var group in movingGroups)
                for (int i=0; i<group.memberCount; i++)
                    if (SafeLegacy(group.memberPersonalities,i) != LegacyPersonalityChoice.Normal)
                        valid &= ValidateChoice((PersonalityChoice)(int)SafeLegacy(group.memberPersonalities,i), NativeMemberAt(group.memberReactions,i), group.label + " member " + i);
            foreach (var group in staticGroups)
                for (int i=0; i<group.memberCount; i++)
                    if (SafeLegacy(group.memberPersonalities,i) != LegacyPersonalityChoice.Normal)
                        valid &= ValidateChoice((PersonalityChoice)(int)SafeLegacy(group.memberPersonalities,i), NativeMemberAt(group.memberReactions,i), group.label + " member " + i);
            return valid;
        }
        private bool ValidateChoice(PersonalityChoice personality, GroupReactionSettings settings, string label)
        {
            var s = settings ?? new GroupReactionSettings();
            if (!s.enabled) return true;
            var kind = ToRuntimePersonality(personality);
            AnimationClip clip = null;
            bool mustHaveClip = false;
            bool native = kind == PedestrianModulator.PersonalityType.Indifferent || kind == PedestrianModulator.PersonalityType.Scared ||
                (kind == PedestrianModulator.PersonalityType.Curious && s.curiousReaction == InspectorPedestrianDemo.CuriousVariant.ApproachAndFollow) ||
                (kind == PedestrianModulator.PersonalityType.Assertive && s.assertiveReaction == InspectorPedestrianDemo.AssertiveVariant.OriginalBehavior);
            bool custom = (kind == PedestrianModulator.PersonalityType.Curious && s.curiousReaction == InspectorPedestrianDemo.CuriousVariant.CustomGesture) ||
                (kind == PedestrianModulator.PersonalityType.Scared && s.scaredReaction == InspectorPedestrianDemo.ScaredVariant.GestureThenMoveAway) ||
                (kind == PedestrianModulator.PersonalityType.Surprised && s.surprisedReaction == InspectorPedestrianDemo.SurprisedVariant.CustomGesture) ||
                (kind == PedestrianModulator.PersonalityType.Assertive && s.assertiveReaction == InspectorPedestrianDemo.AssertiveVariant.CustomGesture) ||
                (native && s.playGestureBeforeNativeBehavior);
            if (custom) { clip = s.customReactionClip; mustHaveClip = true; }
            if (kind == PedestrianModulator.PersonalityType.Curious && s.curiousReaction == InspectorPedestrianDemo.CuriousVariant.CrouchAndObserve && s.crouchClipOverride) {
                clip = s.crouchClipOverride; mustHaveClip = true;
            }
            if (IsS68(personality)) {
                bool atEnd; ResolveS68Clip(personality, out clip, out atEnd);
                if (s.crouchClipOverride) clip = s.crouchClipOverride;
                mustHaveClip = true;
            }
            if (mustHaveClip && (!clip || clip.legacy || !clip.humanMotion || clip.length <= 0)) {
                Debug.LogError("[Population native] " + label + ": selected reaction requires a non-Legacy Humanoid AnimationClip. Fill Custom Reaction Clip / Crouch Clip Override before Generate.", this);
                return false;
            }
            return true;
        }

        private bool ConfigurationUsesS68()
        {
            if (movingSingles != null)
                for (int i = 0; i < movingSingles.Count; i++)
                    if (movingSingles[i] != null && IsS68(movingSingles[i].personality)) return true;
            if (staticSingles != null)
                for (int i = 0; i < staticSingles.Count; i++)
                    if (staticSingles[i] != null && IsS68(staticSingles[i].personality)) return true;
            return false;
        }

        private bool GenerateMovingSingle(MovingSingleConfig config, int index)
        {
            if (config == null) return false;
            SpecialAppearance appearance = IsS68(config.personality) ? SpecialAppearance.Normal : config.appearance;
            float radius = appearance == SpecialAppearance.Normal ? 0.35f : 0.75f;
            int before = reserved.Count;
            Vector3 a, b;
            if (!TryReserveMovingSingleRoute(radius, out a, out b)) return false;
            if (appearance != SpecialAppearance.Normal) {
                GameObject container = SpecialContainer(appearance);
                if (!container) { RollbackReservations(before); Debug.LogError("[Population native] Missing special container: " + appearance, this); return false; }
                var host = new GameObject("PopulationSpecialSingle_" + index + "_" + appearance);
                host.SetActive(false); // SpecialPopulationSpawner.OnEnable starts Build immediately.
                host.transform.SetParent(generatedRoot, false);
                host.transform.position = a;
                var special = host.AddComponent<SpecialPopulationSpawner>();
                special.population = (SpecialPopulationSpawner.Population)((int)appearance - 1);
                special.motion = SpecialPopulationSpawner.Motion.Moving;
                special.positionInput = SpecialPopulationSpawner.PositionInput.WorldCoordinates;
                special.startPosition = a; special.endPosition = b;
                special.moveSpeed = Mathf.Max(0.05f, config.walkSpeedMultiplier) * 0.9f;
                special.containerOverride = container;
                var follower = host.AddComponent<GeneratedSpecialPopulationRoute>();
                follower.Configure(special, addVisibleBodyColliders, bodyCollidersAreTriggers, bodyColliderRadius, bodyColliderHeight);
                // The host follows the asynchronously built body; group obstacle lists keep this stable reference.
                personObstacleRoots.Add(host.transform);
                host.SetActive(true);
            } else {
                var settings = ResolveNativeSettings(config.personality, config.nativeReaction, false);
                bool animatedChoice = config.personality != PersonalityChoice.Normal ||
                    config.walkAnimation != InspectorPedestrianDemo.WalkAnimation.Original ||
                    (settings.enabled && settings.playGestureBeforeNativeBehavior);
                AvatarSelection selection;
                if (!TrySelectAvatar(SpecialAppearance.Normal, animatedChoice, out selection)) { RollbackReservations(before); return false; }
                GameObject person = SpawnAvatar(selection, a, Quaternion.LookRotation(FlatDirection(a, b)), true);
                if (!person) { RollbackReservations(before); return false; }
                person.name = "PopulationMovingSingle_" + index + "_" + SafeLabel(config.label);
                person.transform.SetParent(generatedRoot, true);
                AddBodyCollider(person);
                Base agent = person.GetComponentInChildren<Base>(true);
                var mod = agent.GetComponent<PedestrianModulator>();
                if (!mod) mod = agent.gameObject.AddComponent<PedestrianModulator>();
                var kind = settings.enabled ? ToRuntimePersonality(config.personality) : PedestrianModulator.PersonalityType.Indifferent;
                mod.personality = kind;
                mod.walkSpeedMultiplier = Mathf.Max(0.05f, config.walkSpeedMultiplier);
                ConfigureLegacyPersonalityDistances(mod);
                mod.inspectorReactionTarget = robot;
                mod.followDist = settings.followDistance;
                mod.detectExitMargin = mod.followExitMargin = Mathf.Max(1.01f, settings.curiousExitMultiplier);
                mod.freezeDuration = settings.surpriseFreezeSeconds;
                mod.cooldownDuration = settings.cooldownSeconds;
                var patrol = agent.gameObject.AddComponent<GeneratedNativeSinglePatrol>();
                patrol.Configure(agent, mod, robot, a, b, personalityTriggerDistance, personalityResetDistance);
                if (animatedChoice) {
                    var data = new GameObject("Native reaction settings (inactive)");
                    data.SetActive(false);
                    data.transform.SetParent(person.transform, false);
                    var choice = data.AddComponent<InspectorPedestrianDemo>();
                    settings.CopyTo(choice, kind, robot, personalityTriggerDistance, personalityResetDistance);
                    if (!settings.enabled) choice.playGestureBeforeNativeBehavior = false;
                    choice.walkAnimation = config.walkAnimation;
                    choice.carryBox = config.carryBox;
                    var runner = agent.gameObject.AddComponent<OriginalInspectorReactionAgent>();
                    runner.Configure(choice, agent, mod);
                }
                personObstacleRoots.Add(person.transform);
            }
            if (reserveFullMovingSingleRoutes) movingSingleRoutes.Add(new ReservedPatrolRoute { a=a, b=b, radius=radius });
            return true;
        }

        private bool GenerateStaticSingle(StaticSingleConfig config, int index)
        {
            Vector3 position;
            int before = reserved.Count;
            if (!TryReserveLocation(0.35f, out position, "static single", true)) return false;
            var settings = ResolveNativeSettings(config.personality, config.nativeReaction, true);
            bool reactive = settings.enabled && (config.personality != PersonalityChoice.Normal || settings.playGestureBeforeNativeBehavior);
            AvatarSelection selection;
            if (!TrySelectAvatar(SpecialAppearance.Normal, reactive, out selection)) { RollbackReservations(before); return false; }
            GameObject person = SpawnAvatar(selection, position, Quaternion.Euler(0, config.yawDegrees, 0), false);
            if (!person) { RollbackReservations(before); return false; }
            person.name = "PopulationStaticSingle_" + index + "_" + SafeLabel(config.label);
            person.transform.SetParent(generatedRoot, true);
            PrepareStaticAnimator(person);
            AddBodyCollider(person);
            AddStaticObstacleBox(person);
            personObstacleRoots.Add(person.transform);
            if (reactive) {
                // Reuse the fixed-slot native bridge. No old proximity/legacy reaction scripts.
                settings.allowLeaveFormation = false;
                GroupMemberNativeReaction.Attach(person, settings, ToRuntimePersonality(config.personality),
                    this, () => robot, () => true, personalityTriggerDistance, personalityResetDistance);
            }
            return true;
        }

        private bool GenerateMovingGroup(MovingGroupConfig config, int index)
        {
            int count = Mathf.Clamp(config.memberCount, 2, 3);
            float radius = 0.35f + (count - 1) * 0.45f;
            int reservationCountBefore = reserved.Count;
            Vector3 a;
            Vector3 b;
            if (!TryReserveMovingGroupRoute(radius, out a, out b))
            {
                RollbackReservations(reservationCountBefore);
                Debug.LogError("[PopulationGenerator] No non-overlapping A/B route for "
                    + config.label + ". Enlarge the area or reduce moving-group count.", this);
                return false;
            }

            GameObject host = new GameObject("PopulationMovingGroup_" + index + "_" + SafeLabel(config.label));
            host.transform.SetParent(generatedRoot, true);
            host.transform.position = a;
            Transform start = CreatePoint(host.transform, "PointA", a);
            Transform end = CreatePoint(host.transform, "PointB", b);

            if (config.groupReaction == GroupReaction.None)
            {
                MovingSocialGroupSpawner_WJC_SlotFlip spawner =
                    host.AddComponent<MovingSocialGroupSpawner_WJC_SlotFlip>();
                spawner.generateOnStart = false;
                spawner.memberCount = count;
                spawner.formation = config.formation == MovingFormation.SideBySide
                    ? MovingSocialGroupSpawner_WJC_SlotFlip.FormationType.SideBySide
                    : MovingSocialGroupSpawner_WJC_SlotFlip.FormationType.VShape;
                spawner.openingAngleDegrees = config.openingAngleDegrees;
                spawner.startPoint = start;
                spawner.endPoint = end;
                spawner.moveSpeed = Mathf.Max(0f, config.moveSpeed);
                spawner.simpleAppearanceAgentPrefab = simpleAppearanceAgentPrefab;
                spawner.useGroupSocialProxy = true;
                spawner.socialProxyPrefab = simpleAppearanceAgentPrefab;
                spawner.robotTransform = robot;
                spawner.enableReactionAnimations = HasLegacyPersonality(config.memberPersonalities);
                spawner.defaultReactionDistance = personalityTriggerDistance;
                spawner.defaultReactionResetDistance = personalityResetDistance;
                spawner.stopForRobotEncounter = false;
                spawner.obstacleAvoidanceMode =
                    MovingSocialGroupSpawner_WJC_SlotFlip.ObstacleAvoidanceMode.StopAndWait;
                spawner.obstacleSearchRoot = generatedRoot;
                spawner.explicitObstaclePeopleOrRoots = new List<Transform>(personObstacleRoots);
                ConfigureMovingGroupObstacleClearance(spawner);
                spawner.members = BuildMovingMembers(config.memberPersonalities, config.memberReactions, count);
                spawner.GenerateGroup();
            }
            else
            {
                DynamicMovingAttentionGroupSpawner_WJC_SAFE_SlotFlip spawner =
                    host.AddComponent<DynamicMovingAttentionGroupSpawner_WJC_SAFE_SlotFlip>();
                spawner.generateOnStart = false;
                spawner.memberCount = count;
                spawner.formation = config.formation == MovingFormation.SideBySide
                    ? DynamicMovingAttentionGroupSpawner_WJC_SAFE_SlotFlip.FormationType.SideBySide
                    : DynamicMovingAttentionGroupSpawner_WJC_SAFE_SlotFlip.FormationType.VShape;
                spawner.openingAngleDegrees = config.openingAngleDegrees;
                spawner.startPoint = start;
                spawner.endPoint = end;
                spawner.moveSpeed = Mathf.Max(0f, config.moveSpeed);
                spawner.simpleAppearanceAgentPrefab = simpleAppearanceAgentPrefab;
                spawner.useGroupSocialProxy = true;
                spawner.socialProxyPrefab = simpleAppearanceAgentPrefab;
                spawner.robot = robot;
                spawner.triggerDistance = groupTriggerDistance;
                spawner.resetDistance = groupResetDistance;
                spawner.enableReactionAnimations = HasLegacyPersonality(config.memberPersonalities);
                spawner.defaultReactionDistance = personalityTriggerDistance;
                spawner.defaultReactionResetDistance = personalityResetDistance;
                spawner.memberMoveSpeed = movingReactionMemberMoveSpeed;
                spawner.memberTurnSpeed = movingReactionMemberTurnSpeed;
                spawner.attentionRadius = NextFloat(attentionRadiusRange.x, attentionRadiusRange.y);
                spawner.attentionArcSpanDegrees = ArcSpanForCount(count);
                spawner.reformationMode = ToMovingReformationMode(config.groupReaction);
                spawner.stopForRobotEncounter = config.stopForRobotEncounter;
                spawner.returnWhenRobotLeavesResetDistance = true;
                spawner.limitActiveDuration = config.limitEncounterDuration;
                spawner.maxActiveDurationSeconds = Mathf.Max(0.1f, config.maxEncounterSeconds);
                spawner.retriggerCooldownSeconds = Mathf.Max(0f, config.encounterCooldownSeconds);
                spawner.obstacleAvoidanceMode =
                    DynamicMovingAttentionGroupSpawner_WJC_SAFE_SlotFlip.ObstacleAvoidanceMode.StopAndWait;
                spawner.obstacleSearchRoot = generatedRoot;
                spawner.explicitObstaclePeopleOrRoots = new List<Transform>(personObstacleRoots);
                ConfigureMovingGroupObstacleClearance(spawner);
                spawner.members = BuildDynamicMovingMembers(
                    config.memberPersonalities,
                    config.memberReactions,
                    count,
                    config.groupReaction == GroupReaction.Attraction
                        || config.groupReaction == GroupReaction.SplitCorridor);
                spawner.GenerateGroup();
            }
            AttachAndRunGroupGrounder(host);
            return true;
        }

        private bool GenerateStaticGroup(StaticGroupConfig config, int index)
        {
            int count = Mathf.Clamp(config.memberCount, 3, 4);
            float radius = 0.45f + (count - 1) * 0.45f;
            Vector3 center;
            if (!TryReserveLocation(radius, out center, "static group", true)) return false;

            GameObject host = new GameObject("PopulationStaticGroup_" + index + "_" + SafeLabel(config.label));
            host.transform.SetParent(generatedRoot, true);
            host.transform.position = center;
            host.transform.rotation = Quaternion.Euler(0f, config.yawDegrees, 0f);
            personObstacleRoots.Add(host.transform);

            if (config.groupReaction == GroupReaction.None)
                BuildStaticGroup(host, config, count);
            else
                BuildReactiveStaticGroup(host, config, count);

            AttachAndRunGroupGrounder(host);

            // The center reservation is only an envelope. Register every visible member at its
            // real post-formation position too, so a later A/B route cannot pass through a gap
            // that looks clear around the center but is occupied by an L/O member at runtime.
            RegisterStaticGroupMembers(host.transform);
            return true;
        }

        private void AttachAndRunGroupGrounder(GameObject host)
        {
            if (host == null) return;
            GeneratedGroupNavMeshGrounder grounder =
                host.GetComponent<GeneratedGroupNavMeshGrounder>();
            if (grounder == null)
                grounder = host.AddComponent<GeneratedGroupNavMeshGrounder>();
            grounder.Configure(
                AreaReferenceWorldY(),
                constrainNavMeshToAreaHeight ? navMeshHeightTolerance : 10000f,
                groupGroundSnapDistance,
                logGeneration);
            grounder.GroundNow();
        }

        private void BuildStaticGroup(GameObject host, StaticGroupConfig config, int count)
        {
            StaticSocialGroupSpawner spawner = host.AddComponent<StaticSocialGroupSpawner>();
            spawner.generateOnStart = false;
            spawner.memberCount = count;
            spawner.formation = config.formation == StaticFormation.LShape
                ? StaticSocialGroupSpawner.FormationType.LShape
                : StaticSocialGroupSpawner.FormationType.OShape;
            spawner.simpleAppearanceAgentPrefab = simpleAppearanceAgentPrefab;
            spawner.socialProxyPrefab = simpleAppearanceAgentPrefab;
            spawner.robot = robot;
            spawner.reactionDistance = personalityTriggerDistance;
            spawner.reactionResetDistance = personalityResetDistance;
            spawner.turnSpeed = staticReactionMemberTurnSpeed;
            spawner.members = BuildStaticMembers(config.memberPersonalities, config.memberReactions, count);
            spawner.GenerateGroup();
        }

        private void BuildReactiveStaticGroup(GameObject host, StaticGroupConfig config, int count)
        {
            DynamicAttentionGroupSpawner spawner = host.AddComponent<DynamicAttentionGroupSpawner>();
            spawner.generateOnStart = false;
            spawner.memberCount = count;
            spawner.formation = config.formation == StaticFormation.LShape
                ? DynamicAttentionGroupSpawner.FormationType.LShape
                : DynamicAttentionGroupSpawner.FormationType.OShape;
            spawner.simpleAppearanceAgentPrefab = simpleAppearanceAgentPrefab;
            spawner.socialProxyPrefab = simpleAppearanceAgentPrefab;
            spawner.robot = robot;
            spawner.triggerDistance = groupTriggerDistance;
            spawner.resetDistance = groupResetDistance;
            spawner.reformationMode = config.groupReaction == GroupReaction.SplitCorridor
                ? DynamicAttentionGroupSpawner.ReformationMode.SplitCorridor
                : DynamicAttentionGroupSpawner.ReformationMode.AttractArc;
            spawner.attentionRadius = NextFloat(attentionRadiusRange.x, attentionRadiusRange.y);
            spawner.attentionArcSpanDegrees = ArcSpanForCount(count);
            spawner.memberMoveSpeed = staticReactionMemberMoveSpeed;
            spawner.memberTurnSpeed = staticReactionMemberTurnSpeed;
            spawner.allMembersLookAtRobot = true;
            spawner.members = BuildDynamicStaticMembers(
                config.memberPersonalities,
                config.memberReactions,
                count,
                config.groupReaction == GroupReaction.Attraction
                    || config.groupReaction == GroupReaction.SplitCorridor);
            spawner.GenerateGroup();
        }

        private List<MovingSocialGroupSpawner_WJC_SlotFlip.MemberConfig> BuildMovingMembers(
            List<LegacyPersonalityChoice> values, List<GroupReactionSettings> reactions, int count)
        {
            List<MovingSocialGroupSpawner_WJC_SlotFlip.MemberConfig> result =
                new List<MovingSocialGroupSpawner_WJC_SlotFlip.MemberConfig>();
            for (int i = 0; i < count; i++)
            {
                LegacyPersonalityChoice p = SafeLegacy(values, i);
                result.Add(new MovingSocialGroupSpawner_WJC_SlotFlip.MemberConfig
                {
                    type = p == LegacyPersonalityChoice.Normal
                        ? MovingSocialGroupSpawner_WJC_SlotFlip.MemberType.Ordinary
                        : MovingSocialGroupSpawner_WJC_SlotFlip.MemberType.Special,
                    automaticSource = false,
                    source = p == LegacyPersonalityChoice.Normal
                        ? MovingSocialGroupSpawner_WJC_SlotFlip.CharacterSource.RandomRocketbox
                        : MovingSocialGroupSpawner_WJC_SlotFlip.CharacterSource.SimpleAppearanceAgent,
                    nativeReaction = NativeMemberAt(reactions, i),
                    personality = ToRuntimePersonality(p),
                    enableReactionAnimation = p != LegacyPersonalityChoice.Normal,
                    useDefaultReactionDistance = true
                });
            }
            return result;
        }

        private List<DynamicMovingAttentionGroupSpawner_WJC_SAFE_SlotFlip.MemberConfig> BuildDynamicMovingMembers(
            List<LegacyPersonalityChoice> values, List<GroupReactionSettings> reactions, int count, bool moveDuringReaction)
        {
            List<DynamicMovingAttentionGroupSpawner_WJC_SAFE_SlotFlip.MemberConfig> result =
                new List<DynamicMovingAttentionGroupSpawner_WJC_SAFE_SlotFlip.MemberConfig>();
            for (int i = 0; i < count; i++)
            {
                LegacyPersonalityChoice p = SafeLegacy(values, i);
                result.Add(new DynamicMovingAttentionGroupSpawner_WJC_SAFE_SlotFlip.MemberConfig
                {
                    type = p == LegacyPersonalityChoice.Normal
                        ? DynamicMovingAttentionGroupSpawner_WJC_SAFE_SlotFlip.MemberType.Ordinary
                        : DynamicMovingAttentionGroupSpawner_WJC_SAFE_SlotFlip.MemberType.Special,
                    automaticSource = false,
                    source = p == LegacyPersonalityChoice.Normal
                        ? DynamicMovingAttentionGroupSpawner_WJC_SAFE_SlotFlip.CharacterSource.RandomRocketbox
                        : DynamicMovingAttentionGroupSpawner_WJC_SAFE_SlotFlip.CharacterSource.SimpleAppearanceAgent,
                    nativeReaction = NativeMemberAt(reactions, i),
                    personality = ToRuntimePersonality(p),
                    enableReactionAnimation = p != LegacyPersonalityChoice.Normal,
                    useDefaultReactionDistance = true,
                    moveDuringReformation = moveDuringReaction
                });
            }
            return result;
        }

        private List<StaticSocialGroupSpawner.MemberConfig> BuildStaticMembers(
            List<LegacyPersonalityChoice> values, List<GroupReactionSettings> reactions, int count)
        {
            List<StaticSocialGroupSpawner.MemberConfig> result =
                new List<StaticSocialGroupSpawner.MemberConfig>();
            for (int i = 0; i < count; i++)
            {
                LegacyPersonalityChoice p = SafeLegacy(values, i);
                result.Add(new StaticSocialGroupSpawner.MemberConfig
                {
                    type = p == LegacyPersonalityChoice.Normal
                        ? StaticSocialGroupSpawner.MemberType.Ordinary
                        : StaticSocialGroupSpawner.MemberType.Special,
                    automaticSource = true,
                    nativeReaction = NativeMemberAt(reactions, i),
                    personality = ToRuntimePersonality(p)
                });
            }
            return result;
        }

        private List<DynamicAttentionGroupSpawner.MemberConfig> BuildDynamicStaticMembers(
            List<LegacyPersonalityChoice> values, List<GroupReactionSettings> reactions, int count, bool moveDuringReaction)
        {
            List<DynamicAttentionGroupSpawner.MemberConfig> result =
                new List<DynamicAttentionGroupSpawner.MemberConfig>();
            for (int i = 0; i < count; i++)
            {
                LegacyPersonalityChoice p = SafeLegacy(values, i);
                result.Add(new DynamicAttentionGroupSpawner.MemberConfig
                {
                    type = p == LegacyPersonalityChoice.Normal
                        ? DynamicAttentionGroupSpawner.MemberType.Ordinary
                        : DynamicAttentionGroupSpawner.MemberType.Special,
                    automaticSource = true,
                    nativeReaction = NativeMemberAt(reactions, i),
                    personality = ToRuntimePersonality(p),
                    moveDuringReformation = moveDuringReaction
                });
            }
            return result;
        }

        private bool TrySelectAvatar(
            SpecialAppearance appearance,
            bool personalityRequested,
            out AvatarSelection selection)
        {
            selection = new AvatarSelection();
            GameObject container = appearance == SpecialAppearance.Normal
                ? null : SpecialContainer(appearance);

            if (appearance != SpecialAppearance.Normal && container == null)
            {
                Debug.LogError("[PopulationGenerator] Special container reference is missing: "
                    + appearance + ". Click Auto-Fill Known Project Assets.", this);
                return false;
            }

            if (container != null)
            {
                AppearanceAvatar source = container.GetComponentInChildren<AppearanceAvatar>(true);
                if (source == null || source.avatars == null || source.avatars.Length == 0)
                {
                    Debug.LogError("[PopulationGenerator] Special container has no AppearanceAvatar: "
                        + appearance, this);
                    return false;
                }
                selection.prefab = source.avatars[NextIntInclusive(0, source.avatars.Length - 1)];
                selection.controller = source.animationController;
                selection.directVelocityDrive = source.directVelocityDrive;
                selection.supportsLegacyPersonalityAnimation = source.animationController != null;
                return selection.prefab != null;
            }

            if (personalityRequested)
            {
                AppearanceAvatar source =
                    simpleAppearanceAgentPrefab.GetComponentInChildren<AppearanceAvatar>(true);
                if (source == null || source.avatars == null || source.avatars.Length == 0)
                {
                    Debug.LogError("[PopulationGenerator] SimpleAppearanceAgent has no avatars.", this);
                    return false;
                }
                selection.prefab = source.avatars[NextIntInclusive(0, source.avatars.Length - 1)];
                selection.controller = source.animationController;
                selection.directVelocityDrive = source.directVelocityDrive;
                selection.supportsLegacyPersonalityAnimation = source.animationController != null;
                return selection.prefab != null;
            }

            if (rocketboxPool == null || rocketboxPool.Length == 0)
                rocketboxPool = Resources.LoadAll<GameObject>(rocketboxResourcesPath);
            if (rocketboxPool == null || rocketboxPool.Length == 0)
            {
                // Safe fallback so a missing Rocketbox folder does not prevent testing the generator.
                return TrySelectAvatar(SpecialAppearance.Normal, true, out selection);
            }
            selection.prefab = rocketboxPool[NextIntInclusive(0, rocketboxPool.Length - 1)];
            selection.supportsLegacyPersonalityAnimation = false;
            return selection.prefab != null;
        }

        private GameObject SpawnAvatar(
            AvatarSelection selection,
            Vector3 position,
            Quaternion rotation,
            bool moving)
        {
            if (selection.prefab == null) return null;
            GameObject avatar = Instantiate(selection.prefab, position, rotation);
            Animator animator = IVI.AvatarAnimatorUtility.GetLocomotionAnimator(avatar);
            if (animator != null && selection.controller != null)
                animator.runtimeAnimatorController = selection.controller;

            if (moving && (animator == null || avatar.GetComponentInChildren<SkinnedMeshRenderer>() == null)) {
                Debug.LogError("[Population native] Ordinary moving visual needs an active body mesh and Animator: " + selection.prefab.name, this);
                avatar.SetActive(false); Destroy(avatar); return null;
            }
            if (moving)
            {
                // Some special avatars already carry their Base/SFAgent on a nested object.
                // Reuse it instead of adding a second navigation driver to the visible root.
                Base existing = avatar.GetComponentInChildren<Base>(true);
                if (existing == null) existing = avatar.AddComponent<IVI.SFAgent>();
                existing.DirectVelocityDrive = selection.directVelocityDrive;
            }
            else
            {
                DisableNavigation(avatar);
            }
            return avatar;
        }



        private void ConfigureMovingGroupObstacleClearance(
            MovingSocialGroupSpawner_WJC_SlotFlip spawner)
        {
            if (spawner == null) return;
            spawner.autoDetectNamedPersonObstacles = true;
            spawner.visiblePersonColliderRadius = Mathf.Max(0.05f, bodyColliderRadius);
            spawner.obstaclePersonRadius = Mathf.Max(0.35f, bodyColliderRadius);
            spawner.obstacleSafetyMargin = Mathf.Max(0f, minimumClearance);
        }

        private void ConfigureMovingGroupObstacleClearance(
            DynamicMovingAttentionGroupSpawner_WJC_SAFE_SlotFlip spawner)
        {
            if (spawner == null) return;
            spawner.autoDetectNamedPersonObstacles = true;
            spawner.visiblePersonColliderRadius = Mathf.Max(0.05f, bodyColliderRadius);
            spawner.obstaclePersonRadius = Mathf.Max(0.35f, bodyColliderRadius);
            spawner.obstacleSafetyMargin = Mathf.Max(0f, minimumClearance);
        }

        private static DynamicMovingAttentionGroupSpawner_WJC_SAFE_SlotFlip.RobotEncounterReformationMode
            ToMovingReformationMode(GroupReaction reaction)
        {
            if (reaction == GroupReaction.SplitCorridor)
                return DynamicMovingAttentionGroupSpawner_WJC_SAFE_SlotFlip
                    .RobotEncounterReformationMode.SplitCorridor;
            if (reaction == GroupReaction.Attraction)
                return DynamicMovingAttentionGroupSpawner_WJC_SAFE_SlotFlip
                    .RobotEncounterReformationMode.AttractArc;
            return DynamicMovingAttentionGroupSpawner_WJC_SAFE_SlotFlip
                .RobotEncounterReformationMode.StopAndFaceRobot;
        }

        private void RegisterStaticGroupMembers(Transform host)
        {
            if (host == null) return;
            Transform[] all = host.GetComponentsInChildren<Transform>(true);
            float radius = Mathf.Max(0.35f, bodyColliderRadius);
            for (int i = 0; i < all.Length; i++)
            {
                Transform member = all[i];
                if (!IsGeneratedGroupMemberRoot(member)) continue;
                if (!personObstacleRoots.Contains(member))
                    personObstacleRoots.Add(member);
                reserved.Add(new ReservedCircle
                {
                    position = member.position,
                    radius = radius,
                    blocksPatrolRoute = true
                });
            }
        }

        private static bool IsGeneratedGroupMemberRoot(Transform candidate)
        {
            if (candidate == null) return false;
            string n = candidate.name;
            return n.StartsWith("StaticGroupMember_", StringComparison.Ordinal)
                || n.StartsWith("DynamicGroupMember_", StringComparison.Ordinal)
                || n.StartsWith("DynamicAttentionMember_", StringComparison.Ordinal)
                || n.StartsWith("MovingGroupMember_", StringComparison.Ordinal);
        }

        private void ConfigureLegacyPersonalityDistances(PedestrianModulator modulator)
        {
            if (modulator == null) return;
            float trigger = Mathf.Max(0.1f, personalityTriggerDistance);
            float reset = Mathf.Max(trigger + 0.01f, personalityResetDistance);
            modulator.scaredRadius = trigger;
            modulator.detectRadius = trigger;
            modulator.detectExitMargin = reset / trigger;
            modulator.followExitMargin = reset / trigger;
            modulator.surpriseRadius = trigger;
        }

        private void ResolveS68Clip(PersonalityChoice personality, out AnimationClip clip, out bool kneelAtEnd)
        {
            kneelAtEnd = false;
            if (personality == PersonalityChoice.S68KneelingDown)
            {
                clip = s68Clips.kneelingDown;
                kneelAtEnd = true;
            }
            else if (personality == PersonalityChoice.S68CrouchToStand)
                clip = s68Clips.crouchToStand;
            else if (personality == PersonalityChoice.S68CrouchToStandV2)
                clip = s68Clips.crouchToStandV2;
            else
                clip = s68Clips.iviCrouchCopy;
        }

        private static GroupReactionSettings CopySettings(GroupReactionSettings source)
        {
            if (source == null) source = new GroupReactionSettings();
            return new GroupReactionSettings {
                enabled = source.enabled,
                curiousReaction = source.curiousReaction,
                scaredReaction = source.scaredReaction,
                surprisedReaction = source.surprisedReaction,
                assertiveReaction = source.assertiveReaction,
                playGestureBeforeNativeBehavior = source.playGestureBeforeNativeBehavior,
                customReactionClip = source.customReactionClip,
                crouchClipOverride = source.crouchClipOverride,
                reactionHoldSeconds = source.reactionHoldSeconds,
                reactionPlaybackSpeed = source.reactionPlaybackSpeed,
                reactionBlendSeconds = source.reactionBlendSeconds,
                crouchTransitionSeconds = source.crouchTransitionSeconds,
                crouchHoldSeconds = source.crouchHoldSeconds,
                kneelAtClipEnd = source.kneelAtClipEnd,
                standUpIfCloserThan = source.standUpIfCloserThan,
                followDistance = source.followDistance,
                curiousExitMultiplier = source.curiousExitMultiplier,
                surpriseFreezeSeconds = source.surpriseFreezeSeconds,
                cooldownSeconds = source.cooldownSeconds,
                allowLeaveFormation = source.allowLeaveFormation,
                maxFormationOffset = source.maxFormationOffset,
                nativeMovementSeconds = source.nativeMovementSeconds,
                returnSpeed = source.returnSpeed
            };
        }
        private static GroupReactionSettings NativeMemberAt(List<GroupReactionSettings> list, int index)
        {
            return CopySettings(list != null && index < list.Count ? list[index] : null);
        }
        private static void ResizeNativeList(ref List<GroupReactionSettings> list, int count, bool stationary)
        {
            if (list == null) list = new List<GroupReactionSettings>();
            while (list.Count < count) list.Add(stationary ? GroupReactionSettings.StationaryDefaults() : new GroupReactionSettings());
            while (list.Count > count) list.RemoveAt(list.Count - 1);
            for (int i=0; i<list.Count; i++) if (list[i] == null) list[i] = new GroupReactionSettings();
        }
        private GroupReactionSettings ResolveNativeSettings(PersonalityChoice choice, GroupReactionSettings source, bool stationary)
        {
            var result = CopySettings(source);
            if (stationary) result.allowLeaveFormation = false;
            if (IsS68(choice)) {
                AnimationClip clip; bool atEnd;
                ResolveS68Clip(choice, out clip, out atEnd);
                result.curiousReaction = InspectorPedestrianDemo.CuriousVariant.CrouchAndObserve;
                if (!result.crouchClipOverride) result.crouchClipOverride = clip;
                result.kneelAtClipEnd = atEnd;
                if (choice == PersonalityChoice.S68IviCrouch)
                    Debug.LogWarning("[Population native] Legacy IviCrouch preset is retained, but its supplied clip is not a verified crouch. Select Curious > Crouch And Observe with a checked clip.", this);
            }
            return result;
        }

        private GameObject SpecialContainer(SpecialAppearance appearance)
        {
            switch (appearance)
            {
                case SpecialAppearance.Cyclist: return specialPrefabs.cyclist;
                case SpecialAppearance.DogWalker: return specialPrefabs.dogWalker;
                case SpecialAppearance.FemaleChild: return specialPrefabs.femaleChild;
                case SpecialAppearance.MaleChild: return specialPrefabs.maleChild;
                case SpecialAppearance.PhoneUser: return specialPrefabs.phoneUser;
                case SpecialAppearance.ScooterUser: return specialPrefabs.scooterUser;
                case SpecialAppearance.WheelchairUser: return specialPrefabs.wheelchairUser;
                case SpecialAppearance.WhiteCaneUser: return specialPrefabs.whiteCaneUser;
                default: return null;
            }
        }

        private bool TryReserveLocation(
            float radius,
            out Vector3 position,
            string label,
            bool blocksPatrolRoute = false)
        {
            for (int attempt = 0; attempt < placementAttemptsPerItem; attempt++)
            {
                Vector3 navMeshPoint;
                if (!TrySampleAreaNavMeshPoint(out navMeshPoint)) break;
                if (!IsReservationClear(navMeshPoint, radius)) continue;

                position = navMeshPoint;
                reserved.Add(new ReservedCircle
                {
                    position = position,
                    radius = radius,
                    blocksPatrolRoute = blocksPatrolRoute
                });
                return true;
            }
            position = Vector3.zero;
            Debug.LogError("[PopulationGenerator] Could not place " + label
                + " on NavMesh without overlap. Enlarge the area or reduce population.", this);
            return false;
        }

        /// <summary>
        /// Selects a moving-single route transactionally. A failed B search does not condemn the
        /// person to one unlucky A point: the candidate A is discarded and a new A/B pair is
        /// attempted. Reservations are committed only after the complete corridor passes every
        /// NavMesh, static-obstacle and moving-route check.
        /// </summary>
        private bool TryReserveMovingSingleRoute(float radius, out Vector3 a, out Vector3 b)
        {
            int aAttempts = Mathf.Max(placementAttemptsPerItem, 40);
            int bAttempts = Mathf.Max(placementAttemptsPerItem * 4, 120);

            for (int outer = 0; outer < aAttempts; outer++)
            {
                Vector3 candidateA;
                if (!TrySampleAreaNavMeshPoint(out candidateA)) break;
                if (!IsReservationClear(candidateA, radius)) continue;

                for (int inner = 0; inner < bAttempts; inner++)
                {
                    Vector3 candidateB;
                    if (!TrySampleAreaNavMeshPoint(out candidateB)) break;
                    if (!IsReservationClear(candidateB, radius)) continue;

                    Vector3 delta = candidateB - candidateA;
                    delta.y = 0f;
                    float distance = delta.magnitude;
                    if (distance < patrolMinimumDistance * 0.9f
                        || distance > patrolMaximumDistance * 1.05f)
                        continue;
                    if (!IsPatrolSegmentClear(candidateA, candidateB, radius)) continue;
                    if (reserveFullMovingSingleRoutes
                        && !IsRouteClearOfMovingRoutes(candidateA, candidateB, radius))
                        continue;

                    a = candidateA;
                    b = candidateB;
                    reserved.Add(new ReservedCircle
                    {
                        position = a,
                        radius = radius,
                        blocksPatrolRoute = false
                    });
                    reserved.Add(new ReservedCircle
                    {
                        position = b,
                        radius = radius,
                        blocksPatrolRoute = false
                    });
                    return true;
                }
            }

            a = Vector3.zero;
            b = Vector3.zero;
            return false;
        }

        /// <summary>
        /// Legacy fixed-A helper retained for compatibility with any future call sites. New
        /// moving singles use TryReserveMovingSingleRoute so they can retry the full A/B pair.
        /// </summary>
        private bool TryReservePatrolEndpoint(Vector3 a, float radius, out Vector3 b)
        {
            int attempts = Mathf.Max(placementAttemptsPerItem * 4, 120);
            for (int attempt = 0; attempt < attempts; attempt++)
            {
                Vector3 candidate;
                if (!TrySampleAreaNavMeshPoint(out candidate)) break;
                Vector3 d = candidate - a;
                d.y = 0f;
                float distance = d.magnitude;
                if (distance >= patrolMinimumDistance * 0.9f
                    && distance <= patrolMaximumDistance * 1.05f
                    && IsReservationClear(candidate, radius)
                    && IsPatrolSegmentClear(a, candidate, radius)
                    && (!reserveFullMovingSingleRoutes
                        || IsRouteClearOfMovingRoutes(a, candidate, radius)))
                {
                    b = candidate;
                    reserved.Add(new ReservedCircle
                    {
                        position = b,
                        radius = radius,
                        blocksPatrolRoute = false
                    });
                    return true;
                }
            }
            b = Vector3.zero;
            return false;
        }

        /// <summary>
        /// Selects a complete moving-group route transactionally. Its swept formation corridor
        /// must stay on NavMesh, avoid static footprints, and remain separated from every earlier
        /// moving-group corridor. Failed candidates reserve nothing, so a different A can be tried.
        /// </summary>
        private bool TryReserveMovingGroupRoute(float radius, out Vector3 a, out Vector3 b)
        {
            int aAttempts = Mathf.Max(placementAttemptsPerItem, 40);
            int bAttempts = Mathf.Max(placementAttemptsPerItem * 4, 120);

            for (int outer = 0; outer < aAttempts; outer++)
            {
                Vector3 candidateA;
                if (!TrySampleAreaNavMeshPoint(out candidateA)) break;
                if (!IsReservationClear(candidateA, radius)) continue;

                for (int inner = 0; inner < bAttempts; inner++)
                {
                    Vector3 candidateB;
                    if (!TrySampleAreaNavMeshPoint(out candidateB)) break;
                    if (!IsReservationClear(candidateB, radius)) continue;

                    Vector3 delta = candidateB - candidateA;
                    delta.y = 0f;
                    float distance = delta.magnitude;
                    if (distance < patrolMinimumDistance * 0.9f
                        || distance > patrolMaximumDistance * 1.05f)
                        continue;
                    if (!IsPatrolSegmentClear(candidateA, candidateB, radius)) continue;
                    if (!IsMovingGroupRouteClear(candidateA, candidateB, radius)) continue;

                    a = candidateA;
                    b = candidateB;
                    reserved.Add(new ReservedCircle
                    {
                        position = a,
                        radius = radius,
                        blocksPatrolRoute = false
                    });
                    reserved.Add(new ReservedCircle
                    {
                        position = b,
                        radius = radius,
                        blocksPatrolRoute = false
                    });
                    movingGroupRoutes.Add(new ReservedPatrolRoute
                    {
                        a = a,
                        b = b,
                        radius = radius
                    });
                    return true;
                }
            }

            a = Vector3.zero;
            b = Vector3.zero;
            return false;
        }

        private bool IsReservationClear(Vector3 candidate, float radius)
        {
            for (int i = 0; i < reserved.Count; i++)
            {
                Vector3 delta = candidate - reserved[i].position;
                delta.y = 0f;
                if (delta.magnitude < radius + reserved[i].radius + minimumClearance)
                    return false;
            }
            return true;
        }

        /// <summary>
        /// The group spawners follow a straight A/B route. Reject a B point when that segment
        /// would cut through a static person's/group's reserved footprint or leave the baked
        /// NavMesh between the endpoints. This avoids creating permanently blocked routes.
        /// </summary>
        private bool IsPatrolSegmentClear(Vector3 a, Vector3 b, float movingRadius)
        {
            for (int i = 0; i < reserved.Count; i++)
            {
                ReservedCircle obstacle = reserved[i];
                if (!obstacle.blocksPatrolRoute) continue;
                float required = movingRadius + obstacle.radius + minimumClearance;
                if (PointSegmentDistanceSquaredXZ(obstacle.position, a, b) < required * required)
                    return false;
            }

            Vector3 flat = b - a;
            flat.y = 0f;
            float length = flat.magnitude;
            int samples = Mathf.Max(2, Mathf.CeilToInt(length / 0.5f));
            for (int i = 1; i < samples; i++)
            {
                Vector3 sample = Vector3.Lerp(a, b, i / (float)samples);
                NavMeshHit hit;
                if (!NavMesh.SamplePosition(sample, out hit, 0.25f, NavMesh.AllAreas))
                    return false;
                Vector3 offset = hit.position - sample;
                offset.y = 0f;
                if (offset.sqrMagnitude > 0.25f * 0.25f || !InsideArea(hit.position))
                    return false;
            }
            return true;
        }

        private bool IsMovingGroupRouteClear(Vector3 a, Vector3 b, float radius)
        {
            return IsRouteClearFromList(a, b, radius, movingGroupRoutes)
                && IsRouteClearFromList(a, b, radius, movingSingleRoutes);
        }

        private bool IsRouteClearOfMovingRoutes(Vector3 a, Vector3 b, float radius)
        {
            return IsRouteClearFromList(a, b, radius, movingSingleRoutes)
                && IsRouteClearFromList(a, b, radius, movingGroupRoutes);
        }

        private bool IsRouteClearFromList(
            Vector3 a,
            Vector3 b,
            float radius,
            List<ReservedPatrolRoute> routes)
        {
            for (int i = 0; i < routes.Count; i++)
            {
                ReservedPatrolRoute route = routes[i];
                float required = radius + route.radius + movingRouteClearance;
                if (SegmentDistanceSquaredXZ(a, b, route.a, route.b) < required * required)
                    return false;
            }
            return true;
        }

        private static float SegmentDistanceSquaredXZ(
            Vector3 a0,
            Vector3 a1,
            Vector3 b0,
            Vector3 b1)
        {
            if (SegmentsIntersectXZ(a0, a1, b0, b1)) return 0f;
            return Mathf.Min(
                Mathf.Min(
                    PointSegmentDistanceSquaredXZ(a0, b0, b1),
                    PointSegmentDistanceSquaredXZ(a1, b0, b1)),
                Mathf.Min(
                    PointSegmentDistanceSquaredXZ(b0, a0, a1),
                    PointSegmentDistanceSquaredXZ(b1, a0, a1)));
        }

        private static bool SegmentsIntersectXZ(Vector3 a0, Vector3 a1, Vector3 b0, Vector3 b1)
        {
            Vector2 p = new Vector2(a0.x, a0.z);
            Vector2 r = new Vector2(a1.x - a0.x, a1.z - a0.z);
            Vector2 q = new Vector2(b0.x, b0.z);
            Vector2 s = new Vector2(b1.x - b0.x, b1.z - b0.z);
            float denominator = Cross2D(r, s);
            Vector2 qMinusP = q - p;

            if (Mathf.Abs(denominator) < 0.000001f)
            {
                if (Mathf.Abs(Cross2D(qMinusP, r)) >= 0.000001f) return false;
                float rr = Vector2.Dot(r, r);
                if (rr < 0.000001f) return (p - q).sqrMagnitude < 0.000001f;
                float t0 = Vector2.Dot(qMinusP, r) / rr;
                float t1 = t0 + Vector2.Dot(s, r) / rr;
                float min = Mathf.Min(t0, t1);
                float max = Mathf.Max(t0, t1);
                return max >= 0f && min <= 1f;
            }

            float t = Cross2D(qMinusP, s) / denominator;
            float u = Cross2D(qMinusP, r) / denominator;
            return t >= 0f && t <= 1f && u >= 0f && u <= 1f;
        }

        private static float Cross2D(Vector2 a, Vector2 b)
        {
            return a.x * b.y - a.y * b.x;
        }

        private static float PointSegmentDistanceSquaredXZ(Vector3 point, Vector3 a, Vector3 b)
        {
            Vector2 p = new Vector2(point.x, point.z);
            Vector2 start = new Vector2(a.x, a.z);
            Vector2 end = new Vector2(b.x, b.z);
            Vector2 segment = end - start;
            float denominator = segment.sqrMagnitude;
            if (denominator < 0.000001f) return (p - start).sqrMagnitude;
            float t = Mathf.Clamp01(Vector2.Dot(p - start, segment) / denominator);
            return (p - (start + segment * t)).sqrMagnitude;
        }

        private void RollbackReservations(int countBefore)
        {
            int removeCount = reserved.Count - countBefore;
            if (removeCount > 0)
                reserved.RemoveRange(countBefore, removeCount);
        }

        /// <summary>
        /// Builds a cache of baked NavMesh triangles whose local X/Z bounds overlap the selected
        /// generator rectangle. Sampling those triangles directly makes placement independent of
        /// this GameObject's Y position; every returned point starts on the baked NavMesh itself.
        /// </summary>
        private bool BuildAreaNavMeshTriangleCache()
        {
            areaTriangleStarts.Clear();
            navMeshTriangulation = NavMesh.CalculateTriangulation();
            Vector3[] vertices = navMeshTriangulation.vertices;
            int[] indices = navMeshTriangulation.indices;
            if (vertices == null || vertices.Length == 0 || indices == null || indices.Length < 3)
            {
                Debug.LogError("[PopulationGenerator] No baked NavMesh triangles are available.", this);
                return false;
            }

            float minX = localAreaCenter.x - areaSize.x * 0.5f;
            float maxX = localAreaCenter.x + areaSize.x * 0.5f;
            float minZ = localAreaCenter.z - areaSize.y * 0.5f;
            float maxZ = localAreaCenter.z + areaSize.y * 0.5f;
            float referenceY = AreaReferenceWorldY();
            float minAllowedY = referenceY - navMeshHeightTolerance;
            float maxAllowedY = referenceY + navMeshHeightTolerance;

            for (int start = 0; start + 2 < indices.Length; start += 3)
            {
                int ia = indices[start];
                int ib = indices[start + 1];
                int ic = indices[start + 2];
                if (ia < 0 || ib < 0 || ic < 0
                    || ia >= vertices.Length || ib >= vertices.Length || ic >= vertices.Length)
                    continue;

                Vector3 a = transform.InverseTransformPoint(vertices[ia]);
                Vector3 b = transform.InverseTransformPoint(vertices[ib]);
                Vector3 c = transform.InverseTransformPoint(vertices[ic]);
                float triangleMinX = Mathf.Min(a.x, Mathf.Min(b.x, c.x));
                float triangleMaxX = Mathf.Max(a.x, Mathf.Max(b.x, c.x));
                float triangleMinZ = Mathf.Min(a.z, Mathf.Min(b.z, c.z));
                float triangleMaxZ = Mathf.Max(a.z, Mathf.Max(b.z, c.z));

                if (triangleMaxX < minX || triangleMinX > maxX
                    || triangleMaxZ < minZ || triangleMinZ > maxZ)
                    continue;

                if (constrainNavMeshToAreaHeight)
                {
                    float triangleMinY = Mathf.Min(vertices[ia].y,
                        Mathf.Min(vertices[ib].y, vertices[ic].y));
                    float triangleMaxY = Mathf.Max(vertices[ia].y,
                        Mathf.Max(vertices[ib].y, vertices[ic].y));
                    if (triangleMaxY < minAllowedY || triangleMinY > maxAllowedY)
                        continue;
                }

                areaTriangleStarts.Add(start);
            }

            if (areaTriangleStarts.Count == 0)
            {
                Debug.LogError("[PopulationGenerator] The selected area does not overlap a baked NavMesh triangle at the allowed height. Move the generator/local center onto the intended sidewalk, enlarge the area, or increase NavMesh Height Tolerance.", this);
                return false;
            }
            return true;
        }

        private bool TrySampleAreaNavMeshPoint(out Vector3 position)
        {
            int attempts = Mathf.Max(placementAttemptsPerItem * 4, 120);
            Vector3[] vertices = navMeshTriangulation.vertices;
            int[] indices = navMeshTriangulation.indices;

            for (int attempt = 0; attempt < attempts; attempt++)
            {
                int start = areaTriangleStarts[NextIntInclusive(0, areaTriangleStarts.Count - 1)];
                Vector3 a = vertices[indices[start]];
                Vector3 b = vertices[indices[start + 1]];
                Vector3 c = vertices[indices[start + 2]];

                // Uniform random point over the selected triangle.
                float root = Mathf.Sqrt(NextFloat(0f, 1f));
                float v = NextFloat(0f, 1f);
                Vector3 candidate = (1f - root) * a
                    + root * (1f - v) * b
                    + root * v * c;
                if (!InsideArea(candidate)) continue;
                if (!InsideAllowedNavMeshHeight(candidate.y)) continue;

                NavMeshHit hit;
                float snap = Mathf.Clamp(navMeshSampleRadius, 0.05f, 0.5f);
                if (NavMesh.SamplePosition(candidate, out hit, snap, NavMesh.AllAreas)
                    && InsideArea(hit.position)
                    && InsideAllowedNavMeshHeight(hit.position.y))
                {
                    position = hit.position;
                    return true;
                }
            }

            position = Vector3.zero;
            return false;
        }

        private float AreaReferenceWorldY()
        {
            return transform.TransformPoint(localAreaCenter).y;
        }

        private bool InsideAllowedNavMeshHeight(float worldY)
        {
            return !constrainNavMeshToAreaHeight
                || Mathf.Abs(worldY - AreaReferenceWorldY()) <= navMeshHeightTolerance;
        }

        private bool InsideArea(Vector3 world)
        {
            Vector3 local = transform.InverseTransformPoint(world) - localAreaCenter;
            return Mathf.Abs(local.x) <= areaSize.x * 0.5f + 0.05f
                && Mathf.Abs(local.z) <= areaSize.y * 0.5f + 0.05f;
        }

        private Transform CreatePoint(Transform parent, string name, Vector3 worldPosition)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, true);
            go.transform.position = worldPosition;
            return go.transform;
        }

        private static Vector3 FlatDirection(Vector3 from, Vector3 to)
        {
            Vector3 direction = to - from;
            direction.y = 0f;
            return direction.sqrMagnitude > 0.0001f ? direction.normalized : Vector3.forward;
        }

        private static void DisableNavigation(GameObject root)
        {
            NavMeshAgent[] navAgents = root.GetComponentsInChildren<NavMeshAgent>(true);
            for (int i = 0; i < navAgents.Length; i++)
                if (navAgents[i] != null) navAgents[i].enabled = false;

            MonoBehaviour[] behaviours = root.GetComponentsInChildren<MonoBehaviour>(true);
            for (int i = 0; i < behaviours.Length; i++)
            {
                MonoBehaviour behaviour = behaviours[i];
                if (behaviour != null && behaviour is IVI.INavigable)
                    behaviour.enabled = false;
            }
        }

        private static void PrepareStaticAnimator(GameObject person)
        {
            Animator animator = IVI.AvatarAnimatorUtility.GetLocomotionAnimator(person);
            if (animator == null) return;
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            animator.speed = 1f;
            SetAnimatorFloat(animator, "Forward", 0f);
            SetAnimatorFloat(animator, "Strafe", 0f);
            SetAnimatorBool(animator, "Idling", true);
        }

        private void AddBodyCollider(GameObject person)
        {
            if (!addVisibleBodyColliders || person == null) return;
            GameObject holder = new GameObject("PopulationBodyCollider");
            holder.transform.SetParent(person.transform, false);
            CapsuleCollider capsule = holder.AddComponent<CapsuleCollider>();
            capsule.radius = bodyColliderRadius;
            capsule.height = Mathf.Max(bodyColliderHeight, bodyColliderRadius * 2f);
            capsule.center = new Vector3(0f, bodyColliderHeight * 0.5f, 0f);
            capsule.isTrigger = bodyCollidersAreTriggers;
        }

        private void AddStaticObstacleBox(GameObject person)
        {
            BoxCollider box = person.GetComponent<BoxCollider>();
            if (box == null) box = person.AddComponent<BoxCollider>();
            box.center = new Vector3(0f, bodyColliderHeight * 0.5f, 0f);
            box.size = new Vector3(bodyColliderRadius * 2f, bodyColliderHeight, bodyColliderRadius * 2f);
            box.isTrigger = false;
        }

        private static void SetAnimatorFloat(Animator animator, string name, float value)
        {
            AnimatorControllerParameter[] ps = animator.parameters;
            for (int i = 0; i < ps.Length; i++)
                if (ps[i].name == name && ps[i].type == AnimatorControllerParameterType.Float)
                {
                    animator.SetFloat(name, value);
                    return;
                }
        }

        private static void SetAnimatorBool(Animator animator, string name, bool value)
        {
            AnimatorControllerParameter[] ps = animator.parameters;
            for (int i = 0; i < ps.Length; i++)
                if (ps[i].name == name && ps[i].type == AnimatorControllerParameterType.Bool)
                {
                    animator.SetBool(name, value);
                    return;
                }
        }

        private static bool IsS68(PersonalityChoice personality)
        {
            return (int)personality >= (int)PersonalityChoice.S68KneelingDown;
        }

        private static PedestrianModulator.PersonalityType ToRuntimePersonality(PersonalityChoice personality)
        {
            switch (personality)
            {
                case PersonalityChoice.Scared: return PedestrianModulator.PersonalityType.Scared;
                case PersonalityChoice.CuriousLegacy: return PedestrianModulator.PersonalityType.Curious;
                case PersonalityChoice.Surprised: return PedestrianModulator.PersonalityType.Surprised;
                case PersonalityChoice.Assertive: return PedestrianModulator.PersonalityType.Assertive;
                case PersonalityChoice.S68KneelingDown:
                case PersonalityChoice.S68CrouchToStand:
                case PersonalityChoice.S68CrouchToStandV2:
                case PersonalityChoice.S68IviCrouch:
                    return PedestrianModulator.PersonalityType.Curious;
                default: return PedestrianModulator.PersonalityType.Indifferent;
            }
        }

        private static PedestrianModulator.PersonalityType ToRuntimePersonality(LegacyPersonalityChoice personality)
        {
            switch (personality)
            {
                case LegacyPersonalityChoice.Scared: return PedestrianModulator.PersonalityType.Scared;
                case LegacyPersonalityChoice.CuriousLegacy: return PedestrianModulator.PersonalityType.Curious;
                case LegacyPersonalityChoice.Surprised: return PedestrianModulator.PersonalityType.Surprised;
                case LegacyPersonalityChoice.Assertive: return PedestrianModulator.PersonalityType.Assertive;
                default: return PedestrianModulator.PersonalityType.Indifferent;
            }
        }

        private static LegacyPersonalityChoice SafeLegacy(List<LegacyPersonalityChoice> values, int index)
        {
            return values != null && index >= 0 && index < values.Count
                ? values[index] : LegacyPersonalityChoice.Normal;
        }

        private static bool HasLegacyPersonality(List<LegacyPersonalityChoice> values)
        {
            if (values == null) return false;
            for (int i = 0; i < values.Count; i++)
                if (values[i] != LegacyPersonalityChoice.Normal) return true;
            return false;
        }

        private static float ArcSpanForCount(int count)
        {
            if (count <= 2) return 30f;
            if (count == 3) return 45f;
            return 60f;
        }

        private bool Chance(float probability)
        {
            return random.NextDouble() < Mathf.Clamp01(probability);
        }

        private int NextIntInclusive(int min, int max)
        {
            if (max <= min) return min;
            return random.Next(min, max + 1);
        }

        private float NextFloat(float min, float max)
        {
            return min + (float)random.NextDouble() * (max - min);
        }

        private static string SafeLabel(string value)
        {
            if (string.IsNullOrEmpty(value)) return "Unnamed";
            return value.Replace('/', '_').Replace('\\', '_');
        }

        private void OnDrawGizmosSelected()
        {
            if (!drawGenerationArea) return;
            Matrix4x4 old = Gizmos.matrix;
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.color = new Color(0.1f, 0.9f, 1f, 0.75f);
            Gizmos.DrawWireCube(localAreaCenter, new Vector3(areaSize.x, 0.05f, areaSize.y));
            Gizmos.matrix = old;
        }
    }
}
