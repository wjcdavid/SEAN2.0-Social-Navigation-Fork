using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.AI;

namespace SEAN.Scenario.Agents
{
    /// <summary>
    /// Inspector-configurable strictly static O/L social group.
    ///
    /// Design:
    /// 1. Visible members are visual-only and remain locked to formation slots.
    /// 2. One invisible SEAN social-force agent represents the entire group.
    /// 3. The proxy is locked at the group center and uses a circular social radius.
    /// 4. SimpleAppearance members can rotate/react, but never translate.
    /// </summary>
    public class StaticSocialGroupSpawner : MonoBehaviour
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

        [System.Serializable]
        public class MemberConfig
        {
            [Tooltip("Ordinary = no personality; Special = personality-capable.")]
            public MemberType type = MemberType.Ordinary;

            [Tooltip("When enabled, Ordinary uses RandomRocketbox and Special uses SimpleAppearanceAgent.")]
            public bool automaticSource = true;

            [Tooltip("Used only when Automatic Source is disabled.")]
            public CharacterSource source = CharacterSource.RandomRocketbox;

            [Tooltip("Used only for Special members whose effective source is SimpleAppearanceAgent.")]
            public PedestrianModulator.PersonalityType personality =
                PedestrianModulator.PersonalityType.Indifferent;

            [Tooltip("Current native reaction options. Configure before Play; original walking animation is retained.")]
            public GroupReactionSettings nativeReaction = GroupReactionSettings.StationaryDefaults();

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
            public Transform root;
            public GroupMemberNativeReaction nativeReaction;
            public Vector3 localOffset;
        }

        [Header("Generation")]
        public bool generateOnStart = true;
        public bool clearExistingBeforeGenerate = true;

        [Tooltip("The GameObject transform is the requested group center.")]
        public FormationType formation = FormationType.OShape;

        [Min(2)]
        public int memberCount = 4;

        [Tooltip("Additional yaw rotation around the group center.")]
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

        [Header("Per-Member Settings")]
        [Tooltip("This list is automatically resized to Member Count.")]
        public List<MemberConfig> members = new List<MemberConfig>();

        [Header("Appearance Sources")]
        [Tooltip("Assign Assets/Resources/Prefabs/SimpleAppearanceAgent.prefab.")]
        public GameObject simpleAppearanceAgentPrefab;

        [Tooltip("Resources path for ordinary Rocketbox avatar prefabs.")]
        public string rocketboxResourcesPath = "Prefabs/Rocketbox";

        [Header("Social-Force Group Proxy")]
        [Tooltip("Assign SimpleAppearanceAgent. It is hidden and becomes the group's only SFAgent.")]
        public GameObject socialProxyPrefab;

        [Tooltip("Approximate normal-person radius used only if the SFAgent radius field cannot be found.")]
        [Min(0.01f)]
        public float proxyBaseRadius = 0.35f;

        [Tooltip("Added to the outermost visible member when computing the circular group boundary.")]
        [Min(0f)]
        public float personRadius = 0.35f;

        public bool calculateSocialRadiusAutomatically = true;

        [Min(0.1f)]
        public float manualSocialRadius = 1.5f;

        [SerializeField]
        private float computedSocialRadius;

        public float ComputedSocialRadius
        {
            get { return computedSocialRadius; }
        }

        [Header("NavMesh Validation")]
        [Tooltip("Default true. When disabled, the group uses the Transform XYZ directly and performs no NavMesh checks.")]
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

        [Header("Static Personality Reactions")]
        public Transform robot;

        [Min(0.1f)]
        public float reactionDistance = 3f;

        [Min(0.1f)]
        public float reactionResetDistance = 3.5f;

        [Min(0f)]
        public float turnSpeed = 6f;

        [Header("Debug")]
        public bool drawGizmos = true;
        public bool logDetails = true;

        private Transform generatedRoot;
        private Vector3 fixedCenter;
        private Quaternion formationRotation;
        private readonly List<MemberRuntime> spawnedMembers = new List<MemberRuntime>();
        private GameObject[] rocketboxPool;

        private void OnValidate()
        {
            memberCount = Mathf.Max(2, memberCount);
            oRadius = Mathf.Max(0.1f, oRadius);
            lInterPersonDistance = Mathf.Max(0.1f, lInterPersonDistance);
            reactionResetDistance = Mathf.Max(reactionDistance, reactionResetDistance);
            navMeshSampleDistance = Mathf.Max(0.01f, navMeshSampleDistance);
            navMeshVerticalSearchDistance = Mathf.Max(0.1f, navMeshVerticalSearchDistance);
            navMeshVerticalStep = Mathf.Max(0.05f, navMeshVerticalStep);
            navMeshHorizontalTolerance = Mathf.Max(0.001f, navMeshHorizontalTolerance);

            EnsureMemberListSize();

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

                // Ordinary members never have a personality reaction.
                if (config.type == MemberType.Ordinary)
                {
                    config.personality =
                        PedestrianModulator.PersonalityType.Indifferent;
                }
            }
        }

        private void OnEnable()
        {
            EnsureMemberListSize();
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
            // Keeps the Inspector's per-member list synchronized even when Member Count
            // is changed during Play mode.
            EnsureMemberListSize();

            // Strictly lock every visible person to their assigned formation slot.
            for (int i = 0; i < spawnedMembers.Count; i++)
            {
                MemberRuntime member = spawnedMembers[i];
                if (member == null || member.root == null)
                {
                    continue;
                }

                member.root.position = fixedCenter + formationRotation * member.localOffset;

                // Position is the single source of truth for a strictly static member.
                // Do not write velocity on a kinematic Rigidbody; Unity warns about that.
            }
        }

        [ContextMenu("Generate Static Social Group")]
        public void GenerateGroup()
        {
            EnsureMemberListSize();

            if (clearExistingBeforeGenerate)
            {
                ClearGroup();
            }

            if (simpleAppearanceAgentPrefab == null)
            {
                Debug.LogError(
                    "[StaticSocialGroupSpawner] Assign SimpleAppearanceAgent Prefab.",
                    this);
                return;
            }

            if (socialProxyPrefab == null)
            {
                Debug.LogError(
                    "[StaticSocialGroupSpawner] Assign Social Proxy Prefab, normally SimpleAppearanceAgent.",
                    this);
                return;
            }

            Vector3 requestedCenter = transform.position;

            if (requireNavMesh)
            {
                if (!TryFindNavMeshAtExactXZ(
                        requestedCenter,
                        out fixedCenter,
                        "Group center"))
                {
                    return;
                }
            }
            else
            {
                // No NavMesh validation or Y adjustment.
                fixedCenter = requestedCenter;
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

            GameObject rootObject = new GameObject("GeneratedStaticSocialGroup");
            generatedRoot = rootObject.transform;
            generatedRoot.SetParent(transform, true);
            generatedRoot.position = fixedCenter;
            generatedRoot.rotation = Quaternion.identity;

            LoadRocketboxPoolIfNeeded();

            float farthestMemberDistance = 0f;

            for (int i = 0; i < memberCount; i++)
            {
                MemberConfig config = members[i];
                GameObject person = CreateVisibleMember(config, finalPositions[i], forwards[i], i);

                if (person == null)
                {
                    ClearGroup();
                    return;
                }

                person.transform.SetParent(generatedRoot, true);

                MemberRuntime runtime = new MemberRuntime();
                runtime.root = person.transform;

                // Preserve the valid NavMesh-projected slot as a local formation offset.
                Vector3 worldOffset = finalPositions[i] - fixedCenter;
                runtime.localOffset = Quaternion.Inverse(formationRotation) * worldOffset;
                if (config.type == MemberType.Special && config.GetEffectiveSource() == CharacterSource.SimpleAppearanceAgent)
                {
                    runtime.nativeReaction = GroupMemberNativeReaction.Attach(person, config.nativeReaction,
                        config.personality, this, () => robot, () => true, reactionDistance, reactionResetDistance);
                    if (runtime.nativeReaction) runtime.root = runtime.nativeReaction.transform;
                }
                spawnedMembers.Add(runtime);

                farthestMemberDistance =
                    Mathf.Max(farthestMemberDistance, XZDistance(fixedCenter, finalPositions[i]));
            }

            computedSocialRadius = calculateSocialRadiusAutomatically
                ? farthestMemberDistance + personRadius
                : manualSocialRadius;

            CreateSocialProxy(computedSocialRadius);

            if (logDetails)
            {
                Debug.Log(
                    "[StaticSocialGroupSpawner] Generated " + formation +
                    " with " + memberCount +
                    " members. Social radius = " +
                    computedSocialRadius.ToString("F2") + " m.",
                    this);
            }
        }

        [ContextMenu("Clear Static Social Group")]
        public void ClearGroup()
        {
            spawnedMembers.Clear();

            Transform oldRoot = transform.Find("GeneratedStaticSocialGroup");
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

        private void BuildOFormation(List<Vector3> positions, List<Vector3> forwards)
        {
            for (int i = 0; i < memberCount; i++)
            {
                float angle = 360f * i / memberCount;
                Vector3 localOffset =
                    Quaternion.AngleAxis(angle, Vector3.up) * Vector3.forward * oRadius;

                Vector3 position = fixedCenter + formationRotation * localOffset;
                Vector3 forward = fixedCenter - position;
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
                    position = fixedCenter;
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

                    position = fixedCenter + arm * lInterPersonDistance * step;
                    forward = PerpendicularTowardInterior(arm, interior);
                }

                positions.Add(position);
                forwards.Add(forward.normalized);
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
                        "[StaticSocialGroupSpawner] Group was not generated because at least one member is not on the NavMesh.",
                        this);
                    return false;
                }

                resolvedPositions.Add(resolved);
            }

            return true;
        }

        /// <summary>
        /// Keeps the requested X/Z unchanged and searches only for a valid NavMesh Y.
        /// A nearby NavMesh point with a different X/Z is rejected rather than used.
        /// </summary>
        private bool TryFindNavMeshAtExactXZ(
            Vector3 requested,
            out Vector3 resolved,
            string label)
        {
            resolved = requested;

            float step = Mathf.Max(0.05f, navMeshVerticalStep);
            int stepsPerDirection =
                Mathf.CeilToInt(navMeshVerticalSearchDistance / step);
            int totalProbeCount = stepsPerDirection * 2 + 1;

            for (int probeIndex = 0;
                 probeIndex < totalProbeCount;
                 probeIndex++)
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
                    // This is a nearby NavMesh point, not the requested X/Z.
                    continue;
                }

                // Preserve X/Z exactly; only take Y from the NavMesh.
                resolved = new Vector3(
                    requested.x,
                    hit.position.y,
                    requested.z);

                return true;
            }

            Debug.LogError(
                "[StaticSocialGroupSpawner] " + label +
                " is not on the NavMesh at X=" +
                requested.x.ToString("F3") +
                ", Z=" + requested.z.ToString("F3") +
                ". X/Z were not moved.",
                this);

            return false;
        }

        private GameObject CreateVisibleMember(
            MemberConfig config,
            Vector3 position,
            Vector3 forward,
            int index)
        {
            GameObject avatarPrefab = null;
            RuntimeAnimatorController personalityController = null;
            bool supportsPersonality = false;

            CharacterSource effectiveSource = config.GetEffectiveSource();

            if (effectiveSource == CharacterSource.SimpleAppearanceAgent)
            {
                AppearanceAvatar appearance =
                    simpleAppearanceAgentPrefab.GetComponentInChildren<AppearanceAvatar>(true);

                if (appearance == null ||
                    appearance.avatars == null ||
                    appearance.avatars.Length == 0)
                {
                    Debug.LogError(
                        "[StaticSocialGroupSpawner] SimpleAppearanceAgent has no AppearanceAvatar avatars.",
                        this);
                    return null;
                }

                avatarPrefab =
                    appearance.avatars[UnityEngine.Random.Range(0, appearance.avatars.Length)];
                personalityController = appearance.animationController;
                supportsPersonality = true;
            }
            else
            {
                if (rocketboxPool == null || rocketboxPool.Length == 0)
                {
                    Debug.LogError(
                        "[StaticSocialGroupSpawner] No Rocketbox prefabs found at Resources/" +
                        rocketboxResourcesPath + ".",
                        this);
                    return null;
                }

                avatarPrefab =
                    rocketboxPool[UnityEngine.Random.Range(0, rocketboxPool.Length)];
            }

            if (avatarPrefab == null)
            {
                return null;
            }

            Quaternion rotation =
                forward.sqrMagnitude > 0.001f
                    ? Quaternion.LookRotation(forward, Vector3.up)
                    : Quaternion.identity;

            GameObject person =
                Instantiate(avatarPrefab, position, rotation);

            person.name =
                "StaticGroupMember_" + index + "_" + avatarPrefab.name;

            PrepareVisualOnlyPerson(person);

            Animator animator = IVI.AvatarAnimatorUtility.GetLocomotionAnimator(person);
            if (animator != null)
            {
                if (supportsPersonality && personalityController != null)
                {
                    animator.runtimeAnimatorController = personalityController;
                }

                // Base.cs explicitly keeps pedestrian Animators active even when off-screen.
                // Static members keep animation, but root translation is discarded.
                animator.applyRootMotion = false;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                animator.speed = 1f;
                animator.Rebind();
                animator.Update(0f);

                SetAnimatorFloatIfPresent(animator, "Forward", 0f);
                SetAnimatorFloatIfPresent(animator, "Strafe", 0f);
                SetAnimatorBoolIfPresent(animator, "Idling", true);
            }

            // Reactions are now connected after the logical formation slot is created.

            return person;
        }

        private static void SetAnimatorFloatIfPresent(
            Animator animator,
            string parameterName,
            float value)
        {
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

        private void PrepareVisualOnlyPerson(GameObject person)
        {
            NavMeshAgent[] navAgents =
                person.GetComponentsInChildren<NavMeshAgent>(true);

            for (int i = 0; i < navAgents.Length; i++)
            {
                navAgents[i].enabled = false;
            }

            IVI.INavigable navigable =
                person.GetComponentInChildren<IVI.INavigable>();

            if (navigable != null)
            {
                Behaviour movementBehaviour = navigable as Behaviour;
                if (movementBehaviour != null)
                {
                    movementBehaviour.enabled = false;
                }
            }

            Rigidbody[] rigidbodies =
                person.GetComponentsInChildren<Rigidbody>(true);

            for (int i = 0; i < rigidbodies.Length; i++)
            {
                rigidbodies[i].isKinematic = true;
                rigidbodies[i].useGravity = false;
            }

            // Members are visual-only. The circular group proxy owns collision/social force.
            Collider[] colliders =
                person.GetComponentsInChildren<Collider>(true);

            for (int i = 0; i < colliders.Length; i++)
            {
                colliders[i].enabled = false;
            }

            Animator[] animators =
                person.GetComponentsInChildren<Animator>(true);

            for (int i = 0; i < animators.Length; i++)
            {
                animators[i].enabled = true;
                animators[i].applyRootMotion = false;
            }
        }

        private void CreateSocialProxy(float socialRadius)
        {
            GameObject proxyContainer =
                Instantiate(socialProxyPrefab, fixedCenter, Quaternion.identity, generatedRoot);

            proxyContainer.name = "StaticGroupSocialForceProxy";

            IVI.INavigable proxyAgent =
                proxyContainer.GetComponentInChildren<IVI.INavigable>();

            if (proxyAgent == null)
            {
                Debug.LogError(
                    "[StaticSocialGroupSpawner] Social Proxy Prefab has no IVI.INavigable/SFAgent.",
                    this);
                Destroy(proxyContainer);
                return;
            }

            // A static proxy has no trajectory destination history. Disable trajectory
            // tracking components before their first Update to avoid null-vector errors.
            DisableProxyTrackingComponents(proxyContainer);

            // Hide proxy visuals but keep its social-force component active.
            Renderer[] renderers =
                proxyContainer.GetComponentsInChildren<Renderer>(true);

            for (int i = 0; i < renderers.Length; i++)
            {
                renderers[i].enabled = false;
            }

            Animator[] animators =
                proxyContainer.GetComponentsInChildren<Animator>(true);

            for (int i = 0; i < animators.Length; i++)
            {
                animators[i].enabled = false;
                animators[i].applyRootMotion = false;
            }

            Collider[] oldColliders =
                proxyContainer.GetComponentsInChildren<Collider>(true);

            for (int i = 0; i < oldColliders.Length; i++)
            {
                oldColliders[i].enabled = false;
            }

            GameObject proxyAgentObject = proxyAgent.gameObject;
            bool exactRadiusSet =
                TrySetSocialForceRadius(proxyAgentObject, socialRadius);

            float colliderRadius = socialRadius;

            if (!exactRadiusSet)
            {
                float scale =
                    socialRadius / Mathf.Max(0.01f, proxyBaseRadius);

                proxyAgent.transform.localScale =
                    Vector3.Scale(proxyAgent.transform.localScale,
                        new Vector3(scale, scale, scale));

                colliderRadius = proxyBaseRadius;

                Debug.LogWarning(
                    "[StaticSocialGroupSpawner] Could not find an explicit SFAgent radius field. " +
                    "Using transform scale as the fallback. Inspect SFAgent.cs later for the exact radius API.",
                    this);
            }

            CapsuleCollider groupCollider =
                proxyAgentObject.AddComponent<CapsuleCollider>();

            groupCollider.direction = 1;
            groupCollider.radius = colliderRadius;
            groupCollider.height = colliderRadius * 2f;
            groupCollider.center = new Vector3(0f, colliderRadius, 0f);
            groupCollider.isTrigger = true;

            proxyAgent.transform.position = fixedCenter;
            proxyAgent.InitDest(fixedCenter);

            StaticSocialProxyLock proxyLock =
                proxyAgentObject.AddComponent<StaticSocialProxyLock>();

            proxyLock.Initialize(proxyAgent, fixedCenter);
        }

        private void DisableProxyTrackingComponents(GameObject proxyContainer)
        {
            Behaviour[] behaviours =
                proxyContainer.GetComponentsInChildren<Behaviour>(true);

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

        private bool TrySetSocialForceRadius(GameObject agentObject, float radius)
        {
            Component[] components =
                agentObject.GetComponents<Component>();

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

                // Limit reflection to likely social-force/navigation components.
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
                        // Ignore runtime/Unity-managed fields that cannot be modified.
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
                        // Ignore properties whose setters reject runtime changes.
                    }
                }

                type = type.BaseType;
            }

            return false;
        }

        private void LoadRocketboxPoolIfNeeded()
        {
            if (rocketboxPool == null || rocketboxPool.Length == 0)
            {
                rocketboxPool =
                    Resources.LoadAll<GameObject>(rocketboxResourcesPath);
            }
        }

        private Vector3 PerpendicularTowardInterior(
            Vector3 arm,
            Vector3 interior)
        {
            Vector3 p1 = Vector3.Cross(Vector3.up, arm).normalized;
            Vector3 p2 = -p1;

            return Vector3.Dot(p1, interior) >=
                   Vector3.Dot(p2, interior)
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

            float radius = Application.isPlaying && computedSocialRadius > 0f
                ? computedSocialRadius
                : (calculateSocialRadiusAutomatically
                    ? EstimateEditorRadius()
                    : manualSocialRadius);

            Gizmos.DrawWireSphere(transform.position, radius);
        }

        private float EstimateEditorRadius()
        {
            if (formation == FormationType.OShape)
            {
                return oRadius + personRadius;
            }

            int nonCornerCount =
                memberCount - (lIncludeCornerMember ? 1 : 0);

            int longestArmSteps =
                Mathf.CeilToInt(nonCornerCount / 2f);

            return longestArmSteps * lInterPersonDistance + personRadius;
        }
    }

    /// <summary>
    /// Strictly locks the invisible group proxy at one world position.
    /// The SFAgent remains enabled and can still contribute social force.
    /// </summary>
    public class StaticSocialProxyLock : MonoBehaviour
    {
        private IVI.INavigable agent;
        private Vector3 fixedPosition;
        private Quaternion fixedRotation;

        public void Initialize(
            IVI.INavigable navigable,
            Vector3 position)
        {
            agent = navigable;
            fixedPosition = position;
            fixedRotation = transform.rotation;

            transform.position = fixedPosition;
            agent.InitDest(fixedPosition);
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
        }
    }

    /// <summary>
    /// Visual-only reaction controller. It may rotate and trigger Animator states,
    /// but it never changes the member's position.
    /// </summary>
    public class StaticGroupMemberReaction : MonoBehaviour
    {
        public Transform robot;
        public PedestrianModulator.PersonalityType personality;
        public bool enablePersonalityReaction = true;

        public float reactionDistance = 3f;
        public float reactionResetDistance = 3.5f;
        public float turnSpeed = 6f;
        public bool logReactionDebug = true;

        [HideInInspector]
        public Vector3 baseForward = Vector3.forward;

        private Animator animator;
        private bool reactionActive;

        private void Awake()
        {
            animator = IVI.AvatarAnimatorUtility.GetLocomotionAnimator(gameObject);
        }

        private void Update()
        {
            if (!enablePersonalityReaction ||
                robot == null ||
                personality == PedestrianModulator.PersonalityType.Indifferent)
            {
                TurnToward(baseForward);
                return;
            }

            Vector3 toRobot = robot.position - transform.position;
            toRobot.y = 0f;
            float distance = toRobot.magnitude;

            if (!reactionActive && distance <= reactionDistance)
            {
                reactionActive = true;
                TriggerPersonalityAnimation();
            }
            else if (reactionActive && distance > reactionResetDistance)
            {
                reactionActive = false;
                ResetReactionBools();
            }

            Vector3 targetForward = baseForward;

            if (reactionActive && toRobot.sqrMagnitude > 0.001f)
            {
                if (personality == PedestrianModulator.PersonalityType.Scared)
                {
                    targetForward = -toRobot.normalized;
                }
                else
                {
                    targetForward = toRobot.normalized;
                }
            }

            TurnToward(targetForward);
        }

        private void TurnToward(Vector3 direction)
        {
            direction.y = 0f;
            if (direction.sqrMagnitude < 0.001f)
            {
                return;
            }

            Quaternion target =
                Quaternion.LookRotation(direction.normalized, Vector3.up);

            transform.rotation =
                Quaternion.Slerp(
                    transform.rotation,
                    target,
                    turnSpeed * Time.deltaTime);
        }

        private void TriggerPersonalityAnimation()
        {
            if (animator == null)
            {
                Debug.LogError(
                    "[StaticGroupMemberReaction] No locomotion Animator found on " + name + ".",
                    this);
                return;
            }

            if (personality == PedestrianModulator.PersonalityType.Surprised)
            {
                // This exactly matches Base.TriggerAnimation("Surprised"):
                // the original PedestrianModulator triggers the Animator parameter named Surprised.
                if (!HasTriggerParameter("Surprised"))
                {
                    Debug.LogError(
                        "[StaticGroupMemberReaction] Animator controller '" +
                        (animator.runtimeAnimatorController != null
                            ? animator.runtimeAnimatorController.name
                            : "null") +
                        "' has no Trigger parameter named 'Surprised'.",
                        this);
                    return;
                }

                animator.ResetTrigger("Surprised");
                animator.SetTrigger("Surprised");

                if (logReactionDebug)
                {
                    Debug.Log(
                        "[StaticGroupMemberReaction] Triggered Surprised on " +
                        name + " using controller " +
                        animator.runtimeAnimatorController.name + ".",
                        this);
                }

                return;
            }

            if (personality == PedestrianModulator.PersonalityType.Assertive)
            {
                if (HasTriggerParameter("AssertiveGesture"))
                {
                    animator.ResetTrigger("AssertiveGesture");
                    animator.SetTrigger("AssertiveGesture");
                }
                else
                {
                    TryCrossFadeState("AssertiveGesture");
                }
            }
        }

        private bool HasTriggerParameter(string parameterName)
        {
            AnimatorControllerParameter[] parameters = animator.parameters;

            for (int i = 0; i < parameters.Length; i++)
            {
                if (parameters[i].name == parameterName &&
                    parameters[i].type == AnimatorControllerParameterType.Trigger)
                {
                    return true;
                }
            }

            return false;
        }

        private void ResetReactionBools()
        {
            if (animator == null)
            {
                return;
            }

            TrySetAnimatorSignal("AssertiveGesture", false);
            TrySetAnimatorSignal("Surprised", false);
        }

        private bool TrySetAnimatorSignal(string parameterName, bool active)
        {
            AnimatorControllerParameter[] parameters = animator.parameters;

            for (int i = 0; i < parameters.Length; i++)
            {
                AnimatorControllerParameter parameter = parameters[i];

                if (parameter.name != parameterName)
                {
                    continue;
                }

                if (parameter.type == AnimatorControllerParameterType.Trigger)
                {
                    if (active)
                    {
                        animator.SetTrigger(parameterName);
                    }
                    else
                    {
                        animator.ResetTrigger(parameterName);
                    }
                    return true;
                }

                if (parameter.type == AnimatorControllerParameterType.Bool)
                {
                    animator.SetBool(parameterName, active);
                    return true;
                }
            }

            return false;
        }

        private bool TryCrossFadeState(string stateName)
        {
            int stateHash = Animator.StringToHash(stateName);

            for (int layer = 0; layer < animator.layerCount; layer++)
            {
                if (animator.HasState(layer, stateHash))
                {
                    animator.CrossFade(stateHash, 0.1f, layer);
                    return true;
                }
            }

            return false;
        }
    }
}
