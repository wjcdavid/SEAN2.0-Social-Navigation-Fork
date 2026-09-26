// Unity 2022.3 / SEAN. Put this ONE file in Assets/Scripts/Custom.
// Revision 3: permit the unanimated Scooter model to travel in its imported pose.
// Retains Revision 2's Animator parameter initialization fix.
// Reads existing appearance containers; only changes its own runtime clone.
// Deliberately uses straight A/B movement, NOT SFAgent, NavMesh, or ROS registration.
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

[DisallowMultipleComponent]
[DefaultExecutionOrder(900)]
[AddComponentMenu("SEAN/Custom/Special Population Spawner")]
public sealed class SpecialPopulationSpawner : MonoBehaviour
{
    public enum Population { Cyclist, DogWalker, FemaleChild, MaleChild, PhoneUser, ScooterUser, WheelchairUser, WhiteCaneUser }
    public enum Motion { Stationary, Moving }
    public enum PositionInput { WorldCoordinates, SceneTransforms }

    public Population population = Population.Cyclist;
    public Motion motion = Motion.Stationary;
    public PositionInput positionInput = PositionInput.WorldCoordinates;
    public Vector3 stationaryPosition;
    public Vector3 startPosition;
    public Vector3 endPosition = new Vector3(0, 0, 5);
    public Transform stationaryPoint;
    public Transform startPoint;
    public Transform endPoint;
    public float stationaryYaw;
    [Min(0.01f)] public float moveSpeed = 0.9f;
    public bool pingPong = true;
    [Min(0f)] public float waitAtEndSeconds = 0.5f;
    [Min(1f)] public float turnSpeed = 180f;
    [Min(0.01f)] public float animationPlaybackSpeed = 1f;
    [Tooltip("Optional controller on the generated clone only. Normally leave empty; container/model controllers are used automatically.")]
    public RuntimeAnimatorController controllerOverride;
    [Tooltip("Normally leave empty. Overrides only this spawner's selected container reference.")]
    public GameObject containerOverride;

    public string Status { get { return status; } }
    public GameObject SpawnedCharacter { get { return character; } }

    private string status = "Not started";
    private GameObject character, staging;
    private Vector3 pointA, pointB, routePosition;
    private Quaternion routeRotation;
    private bool ready, targetIsB, atEndpoint, finished, scooterPoseOnly;
    private float waitRemaining;
    private Motion liveMotion;
    private Population livePopulation;
    private readonly List<AnimatorDriver> animatorDrivers = new List<AnimatorDriver>();
    private readonly List<Animation> legacyPlayers = new List<Animation>();
    private readonly List<LocalPose> animatorRoots = new List<LocalPose>();
    private readonly List<LocalPose> bikePositions = new List<LocalPose>();
    private readonly List<LocalPose> stationaryPose = new List<LocalPose>();

    private static readonly string[] ContainerPaths = {
        "Prefabs/CyclistContainer", "Prefabs/DogWalkerContainer",
        "Prefabs/FemaleChildContainer", "Prefabs/MaleChildContainer",
        "Prefabs/PedetrainAvatars/PhoneUserContainer", "Prefabs/ScooterUserContainer",
        "Prefabs/WheelChairUserContainer", "Prefabs/WhiteCaneUserContainer"
    };

    public static string ResourcePath(Population value) { return ContainerPaths[(int)value]; }

    public GameObject ResolveContainer()
    {
        return containerOverride ? containerOverride : Resources.Load<GameObject>(ResourcePath(population));
    }

    private void Reset()
    {
        stationaryPosition = startPosition = transform.position;
        endPosition = startPosition + transform.forward * 5f;
        stationaryYaw = transform.eulerAngles.y;
    }

    private void OnEnable()
    {
        if (Application.isPlaying) StartCoroutine(Build());
    }

    private void OnDisable()
    {
        StopAllCoroutines();
        ready = false;
        scooterPoseOnly = false;
        // Never reparent during OnDisable: a parent may itself be deactivating.
        if (character) Destroy(character);
        if (staging) Destroy(staging);
        character = staging = null;
        animatorDrivers.Clear(); legacyPlayers.Clear(); animatorRoots.Clear();
        bikePositions.Clear(); stationaryPose.Clear();
        status = "Disabled";
    }

    private bool ResolvePositions(out Vector3 a, out Vector3 b, out Quaternion rotation, out string error)
    {
        error = null;
        a = motion == Motion.Stationary ? stationaryPosition : startPosition;
        b = endPosition;
        rotation = Quaternion.Euler(0, stationaryYaw, 0);
        if (positionInput == PositionInput.SceneTransforms)
        {
            if (motion == Motion.Stationary)
            {
                if (!stationaryPoint) { error = "Assign Stationary Point."; return false; }
                a = stationaryPoint.position;
                rotation = Quaternion.Euler(0, stationaryPoint.eulerAngles.y, 0);
            }
            else
            {
                if (!startPoint || !endPoint) { error = "Assign Start Point and End Point."; return false; }
                a = startPoint.position; b = endPoint.position;
            }
        }
        if (!Finite(a) || (motion == Motion.Moving && !Finite(b)))
        { error = "Positions must contain finite numbers."; return false; }
        if (motion == Motion.Moving)
        {
            Vector3 direction = b - a; direction.y = 0;
            if (direction.sqrMagnitude < 0.01f)
            { error = "Start and End need at least 0.1 m of horizontal separation."; return false; }
            rotation = Quaternion.LookRotation(direction, Vector3.up);
        }
        return true;
    }

    private static bool Finite(Vector3 p)
    {
        return !(float.IsNaN(p.x) || float.IsNaN(p.y) || float.IsNaN(p.z)
            || float.IsInfinity(p.x) || float.IsInfinity(p.y) || float.IsInfinity(p.z));
    }

    private IEnumerator Build()
    {
        status = "Loading existing appearance";
        ready = false; finished = false; atEndpoint = false; targetIsB = true;
        scooterPoseOnly = false;
        liveMotion = motion; livePopulation = population;
        string error;
        if (!ResolvePositions(out pointA, out pointB, out routeRotation, out error))
        { Fail(error); yield break; }
        if (moveSpeed <= 0 || float.IsNaN(moveSpeed) || float.IsInfinity(moveSpeed)
            || turnSpeed <= 0 || float.IsNaN(turnSpeed) || float.IsInfinity(turnSpeed)
            || animationPlaybackSpeed <= 0 || float.IsNaN(animationPlaybackSpeed) || float.IsInfinity(animationPlaybackSpeed)
            || waitAtEndSeconds < 0 || float.IsNaN(waitAtEndSeconds) || float.IsInfinity(waitAtEndSeconds))
        { Fail("Speed, turn speed and playback speed must be positive finite numbers; wait time must be finite and nonnegative."); yield break; }

        GameObject container = ResolveContainer();
        if (!container) { Fail("Container missing: Assets/Resources/" + ResourcePath(population) + ".prefab"); yield break; }
        var appearances = container.GetComponentsInChildren<SEAN.Scenario.Agents.AppearanceAvatar>(true);
        if (appearances.Length != 1)
        { Fail("Expected exactly one AppearanceAvatar on the selected container."); yield break; }
        var appearance = appearances[0];
        if (appearance.avatars == null || appearance.avatars.Length == 0)
        { Fail("AppearanceAvatar.Avatars is empty on " + container.name); yield break; }
        // These eight canonical containers currently each have one avatar. Use the first
        // explicitly instead of randomly hiding a missing first reference with another entry.
        GameObject prefab = appearance.avatars[0];
        if (!prefab) { Fail("Avatars[0] is Missing/None on " + container.name + ". Restore the model and original .meta."); yield break; }
        if (prefab.GetComponentInChildren<SEAN.Scenario.Agents.AppearanceAvatar>(true))
        { Fail("Avatars[0] points to another container, not a complete character."); yield break; }

        staging = new GameObject("SpecialPopulation_InactiveSetup");
        staging.SetActive(false);
        character = Instantiate(prefab, staging.transform, false);
        character.name = "SpecialPopulation_" + population + "_" + name;
        character.SetActive(false);

        // Take ownership before any Awake/Start from the clone can add a second mover,
        // Publisher or trajectory tracker. Source prefab assets remain untouched.
        bool hasBikeLock = false;
        foreach (var behaviour in character.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (!behaviour) continue;
            bool keepBike = livePopulation == Population.Cyclist && liveMotion == Motion.Moving
                && behaviour.GetType().FullName == "IVI.BikeAnimateInPlace";
            if (keepBike) { behaviour.enabled = true; hasBikeLock = true; continue; }
            behaviour.enabled = false;
            Destroy(behaviour);
        }
        foreach (var nav in character.GetComponentsInChildren<NavMeshAgent>(true)) nav.enabled = false;
        foreach (var rb in character.GetComponentsInChildren<Rigidbody>(true))
        {
            if (!rb.isKinematic) { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
            rb.useGravity = false; rb.isKinematic = true;
        }
        foreach (var source in character.GetComponentsInChildren<AudioSource>(true))
        { source.playOnAwake = false; source.enabled = false; }
        foreach (var camera in character.GetComponentsInChildren<Camera>(true)) camera.enabled = false;
        foreach (var listener in character.GetComponentsInChildren<AudioListener>(true)) listener.enabled = false;
        foreach (var navObstacle in character.GetComponentsInChildren<NavMeshObstacle>(true)) navObstacle.enabled = false;
        // Destroy is deferred. Keep the clone inactive until all competing scripts are gone.
        yield return null;
        if (!character) yield break;

        if (!HasVisibleMesh(character.transform))
        { Fail("No active renderer with a valid mesh. Open the avatar prefab and check Missing Prefab/FBX references."); yield break; }
        Animator[] candidates = character.GetComponentsInChildren<Animator>(true);
        Animator body = FindBodyAnimator(candidates, character.transform);
        RuntimeAnimatorController selected = controllerOverride ? controllerOverride : appearance.animationController;
        if (selected && !body)
        { Fail("The selected controller has no body Animator to drive. Check the character's nested model references."); yield break; }
        if (body && selected) body.runtimeAnimatorController = selected;
        // selected == null deliberately retains each model's own controller (chair/scooter).
        foreach (var animator in candidates)
        {
            if (!WouldBeActive(animator.transform, character.transform)) continue;
            if (!HasAnimation(animator.runtimeAnimatorController))
            { animator.enabled = false; continue; }
            if (animator.avatar && animator.avatar.isHuman && !animator.avatar.isValid)
            { Fail("Invalid Humanoid Avatar on " + animator.name); yield break; }
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            animator.updateMode = AnimatorUpdateMode.Normal;
            animator.fireEvents = false;
            animator.enabled = true;
            if (animator.transform != character.transform) animatorRoots.Add(new LocalPose(animator.transform));
            animatorDrivers.Add(new AnimatorDriver(animator));
        }
        foreach (var player in character.GetComponentsInChildren<Animation>(true))
        {
            if (!WouldBeActive(player.transform, character.transform) || !player.clip) continue;
            player.playAutomatically = false;
            player.enabled = true;
            legacyPlayers.Add(player);
        }
        if (liveMotion == Motion.Moving && animatorDrivers.Count == 0 && legacyPlayers.Count == 0)
        {
            // The supplied Scooter package contains meshes and a posed skeleton,
            // but no animation takes or controller. Translation does not require a
            // walk cycle: retain the imported pose while moving the complete model.
            // An explicitly supplied but unplayable override still reports an error.
            if (livePopulation != Population.ScooterUser || controllerOverride)
            { Fail("This model has no playable Animator Controller or default legacy clip. Restore its animation assets or assign Controller Override; no substitute animation was invented."); yield break; }
            scooterPoseOnly = true;
            Debug.Log("[SpecialPopulation] Scooter has no playable animation. Moving the complete model in its imported pose; no kick or wheel animation is being played.", this);
        }
        if (!hasBikeLock && livePopulation == Population.Cyclist)
        {
            foreach (var node in character.GetComponentsInChildren<Transform>(true))
                if (node.name == "Armature_sepeda")
                {
                    foreach (var bone in node.GetComponentsInChildren<Transform>(true)) bikePositions.Add(new LocalPose(bone));
                    break;
                }
        }

        character.transform.SetParent(null, false);
        routePosition = pointA;
        character.transform.SetPositionAndRotation(routePosition, routeRotation);
        character.SetActive(true);
        Destroy(staging); staging = null;

        bool moving = liveMotion == Motion.Moving;
        foreach (var driver in animatorDrivers)
        {
            driver.Feed(moving, moveSpeed, 1f);
            // Resolve one authored pose, so stationary means a frozen character, not an
            // uninitialised T-pose. This is setup sampling, not an idle animation loop.
            driver.animator.Update(0.02f);
        }
        foreach (var player in legacyPlayers)
        {
            player.Play(player.clip.name);
            foreach (AnimationState state in player) { state.time = 0f; state.speed = 0f; }
            player.Sample();
        }
        RestoreAnimatorRoots();
        foreach (var pose in bikePositions) pose.RestorePosition();
        character.transform.SetPositionAndRotation(routePosition, routeRotation);
        if (!moving || scooterPoseOnly)
            foreach (var t in character.GetComponentsInChildren<Transform>(true))
                if (t != character.transform) stationaryPose.Add(new LocalPose(t));
        SetPlayback(moving);
        if (!moving)
        {
            foreach (var driver in animatorDrivers) driver.animator.enabled = false;
            foreach (var player in legacyPlayers) player.enabled = false;
            foreach (var pose in stationaryPose) pose.Restore();
        }
        ready = true;
        status = moving ? "Moving A -> B (straight line)" : "Stationary - pose frozen";
        if (scooterPoseOnly) status += " - Scooter imported pose, no animation";
        Debug.Log("[SpecialPopulation] " + population + "; " + status + "; model=" + prefab.name
            + "; controller=" + (body && body.runtimeAnimatorController ? body.runtimeAnimatorController.name : "model defaults")
            + "; position=" + routePosition, character);
    }

    private void Update()
    {
        if (!ready || !character || liveMotion == Motion.Stationary || finished) return;
        if (atEndpoint)
        {
            waitRemaining -= Time.deltaTime;
            if (waitRemaining > 0f) return;
            atEndpoint = false;
            targetIsB = !targetIsB;
        }
        Vector3 targetPosition = targetIsB ? pointB : pointA;
        Vector3 delta = targetPosition - routePosition;
        Vector3 flat = delta; flat.y = 0;
        if (flat.sqrMagnitude > 0.000001f)
        {
            Quaternion desired = Quaternion.LookRotation(flat, Vector3.up);
            routeRotation = Quaternion.RotateTowards(routeRotation, desired, turnSpeed * Time.deltaTime);
            character.transform.rotation = routeRotation;
            // Turn before translating; do not slide backwards through an endpoint turn.
            if (Quaternion.Angle(routeRotation, desired) > 5f)
            { SetPlayback(false); status = "Turning at endpoint"; return; }
        }
        Vector3 next = Vector3.MoveTowards(routePosition, targetPosition, moveSpeed * Time.deltaTime);
        SetPlayback((next - routePosition).sqrMagnitude > 0.000000001f);
        routePosition = next;
        character.transform.SetPositionAndRotation(routePosition, routeRotation);
        status = targetIsB ? "Moving A -> B (straight line)" : "Moving B -> A (straight line)";
        if (scooterPoseOnly) status += " - Scooter imported pose, no animation";
        if ((targetPosition - routePosition).sqrMagnitude <= 0.00000001f)
        {
            SetPlayback(false);
            if (!pingPong) { finished = true; status = "Arrived - pose frozen"; }
            else { atEndpoint = true; waitRemaining = waitAtEndSeconds; status = "Waiting at endpoint - pose frozen"; }
        }
    }

    private void LateUpdate()
    {
        if (!ready || !character) return;
        if (liveMotion == Motion.Stationary || scooterPoseOnly)
            foreach (var pose in stationaryPose) pose.Restore();
        else
        {
            RestoreAnimatorRoots();
            foreach (var pose in bikePositions) pose.RestorePosition();
        }
        // An imported root curve cannot move the character away from its requested route.
        character.transform.SetPositionAndRotation(routePosition, routeRotation);
    }

    private void SetPlayback(bool moving)
    {
        foreach (var driver in animatorDrivers)
            driver.Feed(moving, moveSpeed, moving ? animationPlaybackSpeed : 0f);
        foreach (var player in legacyPlayers)
            if (player) foreach (AnimationState state in player) state.speed = moving ? animationPlaybackSpeed : 0f;
    }

    private void RestoreAnimatorRoots()
    {
        foreach (var pose in animatorRoots) pose.Restore();
    }

    private void Fail(string message)
    {
        ready = false; status = "ERROR: " + message;
        Debug.LogError("[SpecialPopulation] " + message, this);
        if (character) Destroy(character);
        if (staging) Destroy(staging);
        character = staging = null;
    }

    private static bool WouldBeActive(Transform t, Transform root)
    {
        for (var n = t; n && n != root; n = n.parent) if (!n.gameObject.activeSelf) return false;
        return true;
    }

    private static bool HasVisibleMesh(Transform root)
    {
        foreach (var r in root.GetComponentsInChildren<Renderer>(true))
        {
            if (!r.enabled || !WouldBeActive(r.transform, root)) continue;
            var skin = r as SkinnedMeshRenderer;
            if (skin && skin.sharedMesh) return true;
            var filter = r.GetComponent<MeshFilter>();
            if (filter && filter.sharedMesh) return true;
        }
        return false;
    }

    private static Animator FindBodyAnimator(Animator[] all, Transform root)
    {
        foreach (var a in all) if (WouldBeActive(a.transform, root) && a.avatar && a.avatar.isHuman) return a;
        foreach (var a in all) if (WouldBeActive(a.transform, root) && a.runtimeAnimatorController) return a;
        foreach (var a in all) if (WouldBeActive(a.transform, root)) return a;
        return null;
    }

    private static bool HasAnimation(RuntimeAnimatorController controller)
    {
        if (!controller) return false;
        foreach (var clip in controller.animationClips) if (clip && !clip.legacy && clip.length > 0f) return true;
        return false;
    }

    private sealed class AnimatorDriver
    {
        public readonly Animator animator;
        private AnimatorControllerParameter[] parameters = new AnimatorControllerParameter[0];
        private RuntimeAnimatorController parameterController;
        public AnimatorDriver(Animator value)
        {
            animator = value;
            // Build creates drivers while the clone is inactive. Unity can return an
            // empty parameter list at that point; never cache it as the final list.
        }
        public void Feed(bool moving, float speed, float playback)
        {
            if (!animator) return;
            animator.speed = playback;
            if (!animator.isActiveAndEnabled || !animator.isInitialized)
            {
                // Retry after activation/initialization, including after a re-enable.
                parameterController = null;
                return;
            }
            RuntimeAnimatorController currentController = animator.runtimeAnimatorController;
            if (!currentController) { parameterController = null; return; }
            if (parameterController != currentController || parameters.Length != animator.parameterCount)
            {
                parameters = animator.parameters;
                parameterController = currentController;
            }
            foreach (var p in parameters)
            {
                string key = p.name.ToLowerInvariant();
                if (p.type == AnimatorControllerParameterType.Bool && (key == "idling" || key == "isidling"))
                    animator.SetBool(p.nameHash, !moving);
                if (p.type != AnimatorControllerParameterType.Float) continue;
                if (key == "forward") animator.SetFloat(p.nameHash, moving ? speed / 0.6f : 0f);
                else if (key == "strafe" || key == "turn") animator.SetFloat(p.nameHash, 0f);
                else if (key == "speed" || key == "movespeed") animator.SetFloat(p.nameHash, moving ? speed : 0f);
            }
        }
    }

    private sealed class LocalPose
    {
        private readonly Transform node;
        private readonly Vector3 position, scale;
        private readonly Quaternion rotation;
        public LocalPose(Transform t) { node = t; position = t.localPosition; rotation = t.localRotation; scale = t.localScale; }
        public void RestorePosition() { if (node) node.localPosition = position; }
        public void Restore() { if (!node) return; node.localPosition = position; node.localRotation = rotation; node.localScale = scale; }
    }

    private void OnDrawGizmosSelected()
    {
        Vector3 a, b; Quaternion q; string error;
        if (!ResolvePositions(out a, out b, out q, out error)) return;
        Gizmos.color = Color.cyan; Gizmos.DrawWireSphere(a, 0.2f);
        if (motion == Motion.Moving)
        { Gizmos.color = Color.yellow; Gizmos.DrawWireSphere(b, 0.2f); Gizmos.DrawLine(a, b); }
    }
}

#if UNITY_EDITOR
// Kept inside UNITY_EDITOR so the complete deliverable is one .cs file in Custom.
[UnityEditor.CustomEditor(typeof(SpecialPopulationSpawner))]
public sealed class SpecialPopulationSpawnerEditor : UnityEditor.Editor
{
    private bool advanced;
    public override void OnInspectorGUI()
    {
        serializedObject.Update();
        var component = (SpecialPopulationSpawner)target;
        UnityEditor.EditorGUILayout.HelpBox("Version 3. Choose population and Stationary / Moving before Play. "
            + "Uses existing character controllers. Moving follows a straight A/B route; stationary freezes its pose.", UnityEditor.MessageType.Info);
        using (new UnityEditor.EditorGUI.DisabledScope(UnityEngine.Application.isPlaying))
        {
            Field("population", "Population / 特殊人群");
            Field("motion", "Motion / 移动或静止");
            Field("positionInput", "Position Input / 位置输入");
            bool stationary = serializedObject.FindProperty("motion").enumValueIndex == 0;
            bool scene = serializedObject.FindProperty("positionInput").enumValueIndex == 1;
            if (stationary)
            {
                Field(scene ? "stationaryPoint" : "stationaryPosition", scene ? "Stationary Point / 静止点" : "Stationary Position / 静止坐标");
                if (!scene) Field("stationaryYaw", "Yaw / 朝向角度");
            }
            else
            {
                Field(scene ? "startPoint" : "startPosition", scene ? "Start Point / 起点" : "Start Position / 起点坐标");
                Field(scene ? "endPoint" : "endPosition", scene ? "End Point / 终点" : "End Position / 终点坐标");
                Field("moveSpeed", "Move Speed / 米每秒");
                Field("pingPong", "Ping Pong / 往返");
            }
            advanced = UnityEditor.EditorGUILayout.Foldout(advanced, "Advanced / 一般不需要修改", true);
            if (advanced)
            {
                if (!stationary)
                {
                    Field("waitAtEndSeconds", "Wait At End / 端点等待秒数");
                    Field("turnSpeed", "Turn Speed / 转向速度");
                    Field("animationPlaybackSpeed", "Animation Playback Speed");
                }
                Field("containerOverride", "Container Override / 可选");
                Field("controllerOverride", "Controller Override / 可选");
            }
        }
        serializedObject.ApplyModifiedProperties();
        GameObject container = component.ResolveContainer();
        using (new UnityEditor.EditorGUI.DisabledScope(true))
        {
            UnityEditor.EditorGUILayout.ObjectField("Auto Container / 自动读取", container, typeof(GameObject), false);
            UnityEditor.EditorGUILayout.ObjectField("Spawned Character", component.SpawnedCharacter, typeof(GameObject), true);
        }
        if (!container)
            UnityEditor.EditorGUILayout.HelpBox("Missing: Assets/Resources/" + SpecialPopulationSpawner.ResourcePath(component.population) + ".prefab", UnityEditor.MessageType.Error);
        else
        {
            var source = container.GetComponentInChildren<SEAN.Scenario.Agents.AppearanceAvatar>(true);
            if (!source || source.avatars == null || source.avatars.Length == 0 || !source.avatars[0])
                UnityEditor.EditorGUILayout.HelpBox("Container has no valid AppearanceAvatar.Avatars[0]. Restore the referenced prefab and .meta.", UnityEditor.MessageType.Error);
            else using (new UnityEditor.EditorGUI.DisabledScope(true))
            {
                UnityEditor.EditorGUILayout.ObjectField("Auto Avatar / 实际人物", source.avatars[0], typeof(GameObject), false);
                UnityEditor.EditorGUILayout.ObjectField("Container Controller", source.animationController, typeof(RuntimeAnimatorController), false);
            }
        }
        UnityEditor.EditorGUILayout.LabelField("Status", component.Status, UnityEditor.EditorStyles.wordWrappedLabel);
        if (component.SpawnedCharacter && GUILayout.Button("Select spawned character / 选中生成的人物"))
        {
            UnityEditor.Selection.activeGameObject = component.SpawnedCharacter;
            if (UnityEditor.SceneView.lastActiveSceneView) UnityEditor.SceneView.lastActiveSceneView.FrameSelected();
        }
        if (UnityEngine.Application.isPlaying) Repaint();
    }
    private void Field(string key, string label)
    {
        UnityEditor.EditorGUILayout.PropertyField(serializedObject.FindProperty(key), new GUIContent(label), true);
    }
}
#endif
