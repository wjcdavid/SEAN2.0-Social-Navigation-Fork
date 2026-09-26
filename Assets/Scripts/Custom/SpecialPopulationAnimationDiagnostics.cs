// Unity 2022.3. Put in Assets/Scripts/Custom. No Add Component is needed.
// Read-only runtime inspection: does not change assets, parameters, playback or poses.
#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

public static class SpecialPopulationAnimationDiagnostics
{
    private const string Menu = "Tools/SEAN/Diagnose Special Population Animation";
    private static readonly HumanBodyBones[] LegBones = {
        HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot,
        HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot
    };
    private static readonly List<Observation> Observations = new List<Observation>();
    private static StringBuilder report;
    private static double startEditorTime;
    private static float startGameTime;
    private static bool running;

    private sealed class BoneObservation
    {
        public Transform bone;
        public Quaternion initial;
        public float maxAngle;
    }

    private sealed class Observation
    {
        public SpecialPopulationSpawner spawner;
        public GameObject character;
        public Animator animator;
        public Vector3 startPosition;
        public float travelled;
        public Vector3 previousPosition;
        public int sampleCount;
        public readonly Dictionary<HumanBodyBones, BoneObservation> bones =
            new Dictionary<HumanBodyBones, BoneObservation>();
    }

    [MenuItem(Menu)]
    private static void Capture()
    {
        if (running) { Debug.Log("[SpecialPopulationDiagnostic] A capture is already running."); return; }
        if (!EditorApplication.isPlaying || EditorApplication.isPaused)
        {
            Debug.LogWarning("[SpecialPopulationDiagnostic] Enter Play, unpause, and run this menu while the character is moving.");
            return;
        }
        report = new StringBuilder();
        report.AppendLine("Special population animation diagnostic v1 - read only");
        report.AppendLine("Unity=" + Application.unityVersion + "; timeScale=" + F(Time.timeScale));
        Observations.Clear();
        var spawners = UnityEngine.Object.FindObjectsOfType<SpecialPopulationSpawner>(true);
        report.AppendLine("Scene spawner count=" + spawners.Length);
        foreach (var spawner in spawners)
        {
            report.AppendLine("\nSPAWNER " + HierarchyPath(spawner.transform));
            report.AppendLine("Population=" + spawner.population + "; Motion=" + spawner.motion
                + "; enabled=" + spawner.isActiveAndEnabled + "; Status=" + spawner.Status);
            report.AppendLine("ControllerOverride=" + Asset(spawner.controllerOverride));
            var container = spawner.ResolveContainer();
            report.AppendLine("Container=" + Asset(container));
            if (container)
            {
                foreach (var component in container.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (!component || component.GetType().Name != "AppearanceAvatar") continue;
                    var so = new SerializedObject(component);
                    var controller = so.FindProperty("animationController");
                    var avatars = so.FindProperty("avatars");
                    if (controller != null) report.AppendLine("ContainerController=" + Asset(controller.objectReferenceValue));
                    if (avatars != null && avatars.isArray && avatars.arraySize > 0)
                        report.AppendLine("ContainerAvatar0=" + Asset(avatars.GetArrayElementAtIndex(0).objectReferenceValue));
                }
            }
            var character = spawner.SpawnedCharacter;
            if (!character) { report.AppendLine("NO GENERATED CHARACTER (see spawner Status above)"); continue; }
            foreach (var renderer in character.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                report.AppendLine("SKIN " + HierarchyPath(renderer.transform) + "; enabled=" + renderer.enabled
                    + "; active=" + renderer.gameObject.activeInHierarchy + "; mesh=" + Asset(renderer.sharedMesh)
                    + "; bones=" + renderer.bones.Length + "; rootBone=" + HierarchyPath(renderer.rootBone));
            var animators = character.GetComponentsInChildren<Animator>(true);
            report.AppendLine("AnimatorCount=" + animators.Length);
            foreach (var animator in animators)
            {
                var observation = new Observation {
                    spawner = spawner, character = character, animator = animator,
                    startPosition = character.transform.position, previousPosition = character.transform.position
                };
                if (animator.isInitialized && animator.avatar && animator.avatar.isValid && animator.avatar.isHuman)
                {
                    foreach (var id in LegBones)
                    {
                        var bone = animator.GetBoneTransform(id);
                        if (bone) observation.bones[id] = new BoneObservation { bone = bone, initial = bone.localRotation };
                    }
                }
                Observations.Add(observation);
                DescribeAnimator(animator, spawner, "START");
            }
        }
        startEditorTime = EditorApplication.timeSinceStartup;
        startGameTime = Time.time;
        running = true;
        EditorApplication.update += Tick;
        Debug.Log("[SpecialPopulationDiagnostic] Reading playback and leg movement for 2 seconds. Leave Play running.");
    }

    private static void Tick()
    {
        if (!running) return;
        if (!EditorApplication.isPlaying || EditorApplication.isPaused)
        { report.AppendLine("Capture interrupted: Play stopped or paused."); Finish(); return; }
        foreach (var o in Observations)
        {
            if (!o.character || !o.animator) continue;
            Vector3 position = o.character.transform.position;
            o.travelled += Vector3.Distance(position, o.previousPosition);
            o.previousPosition = position;
            ++o.sampleCount;
            foreach (var b in o.bones.Values)
                if (b.bone) b.maxAngle = Mathf.Max(b.maxAngle, Quaternion.Angle(b.initial, b.bone.localRotation));
        }
        if (EditorApplication.timeSinceStartup - startEditorTime >= 2.0) Finish();
    }

    private static void Finish()
    {
        EditorApplication.update -= Tick;
        running = false;
        report.AppendLine("\nGAME SECONDS OBSERVED=" + F(Time.time - startGameTime));
        foreach (var o in Observations)
        {
            report.AppendLine("\nRESULT " + (o.spawner ? o.spawner.population.ToString() : "destroyed spawner"));
            report.AppendLine("Samples=" + o.sampleCount + "; rootTravelMetres=" + F(o.travelled));
            if (o.spawner) report.AppendLine("FinalStatus=" + o.spawner.Status);
            foreach (var id in LegBones)
            {
                BoneObservation b;
                if (o.bones.TryGetValue(id, out b))
                    report.AppendLine(id + "=" + HierarchyPath(b.bone) + "; maxLocalAngleChange=" + F(b.maxAngle));
                else report.AppendLine(id + "=NOT_SAMPLED (no valid initialized Humanoid mapping, or bone absent)");
            }
            if (o.animator) DescribeAnimator(o.animator, o.spawner, "END");
            else report.AppendLine("Animator destroyed during capture.");
        }
        report.AppendLine("\nThis report observes only. Zero bone movement during a turn/endpoint pause is expected.");
        string folder = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Temp");
        string path = Path.Combine(folder, "SpecialPopulationAnimationReport.txt");
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(path, report.ToString(), new UTF8Encoding(false));
            Debug.Log("[SpecialPopulationDiagnostic] Report saved: " + path + "\nUpload this text file for diagnosis.");
            EditorUtility.RevealInFinder(path);
        }
        catch (Exception ex)
        { Debug.LogError("[SpecialPopulationDiagnostic] Could not save report: " + ex.Message); Debug.Log(report.ToString()); }
        Observations.Clear();
    }

    private static void DescribeAnimator(Animator a, SpecialPopulationSpawner spawner, string phase)
    {
        report.AppendLine(phase + " ANIMATOR " + HierarchyPath(a.transform));
        report.AppendLine("enabled=" + a.enabled + "; active=" + a.gameObject.activeInHierarchy
            + "; initialized=" + a.isInitialized + "; speed=" + F(a.speed)
            + "; applyRootMotion=" + a.applyRootMotion + "; culling=" + a.cullingMode);
        report.AppendLine("Controller=" + Asset(a.runtimeAnimatorController));
        report.AppendLine("Avatar=" + Asset(a.avatar) + "; valid=" + (a.avatar && a.avatar.isValid)
            + "; human=" + (a.avatar && a.avatar.isHuman));
        if (a.avatar)
        {
            var importer = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(a.avatar)) as ModelImporter;
            if (importer) report.AppendLine("AvatarModelRig=" + importer.animationType + "; avatarSetup=" + importer.avatarSetup);
        }
        if (!a.runtimeAnimatorController || !a.isInitialized)
        { report.AppendLine("Runtime state/parameter reads skipped: no controller or Animator not initialized."); return; }
        var parameters = a.parameters;
        report.AppendLine("LiveParameterCount=" + parameters.Length + "; SpawnerCachedParameterCount=" + CachedCount(spawner, a));
        foreach (var p in parameters)
        {
            string value = p.type == AnimatorControllerParameterType.Bool ? a.GetBool(p.nameHash).ToString()
                : p.type == AnimatorControllerParameterType.Float ? F(a.GetFloat(p.nameHash))
                : p.type == AnimatorControllerParameterType.Int ? a.GetInteger(p.nameHash).ToString() : "trigger (not read)";
            report.AppendLine("PARAM " + p.name + " [" + p.type + "]=" + value);
        }
        for (int layer = 0; layer < a.layerCount; ++layer)
        {
            var state = a.GetCurrentAnimatorStateInfo(layer);
            report.AppendLine("LAYER " + layer + " " + a.GetLayerName(layer) + "; weight=" + F(a.GetLayerWeight(layer))
                + "; stateHash=" + state.fullPathHash + "; normalizedTime=" + F(state.normalizedTime)
                + "; stateSpeed=" + F(state.speed) + "; speedMultiplier=" + F(state.speedMultiplier));
            DescribeClips("CURRENT", a.GetCurrentAnimatorClipInfo(layer));
            if (a.IsInTransition(layer)) DescribeClips("NEXT", a.GetNextAnimatorClipInfo(layer));
        }
        if (phase == "START")
        {
            RuntimeAnimatorController current = a.runtimeAnimatorController;
            var visited = new HashSet<RuntimeAnimatorController>();
            while (current is AnimatorOverrideController && visited.Add(current))
                current = ((AnimatorOverrideController)current).runtimeAnimatorController;
            var controller = current as AnimatorController;
            if (controller)
                foreach (var layer in controller.layers) DescribeStateMachine(layer.stateMachine, layer.name, new HashSet<Motion>());
        }
    }

    private static void DescribeClips(string label, AnimatorClipInfo[] clips)
    {
        report.AppendLine(label + " clipCount=" + clips.Length);
        foreach (var entry in clips)
        {
            var clip = entry.clip;
            report.AppendLine("  " + Asset(clip) + "; weight=" + F(entry.weight)
                + (clip ? "; length=" + F(clip.length) + "; humanMotion=" + clip.humanMotion
                    + "; legacy=" + clip.legacy + "; looping=" + clip.isLooping : ""));
        }
    }

    private static void DescribeStateMachine(AnimatorStateMachine machine, string prefix, HashSet<Motion> visited)
    {
        if (!machine) return;
        foreach (var entry in machine.states)
            DescribeMotion(entry.state.motion, prefix + "/" + entry.state.name, visited);
        foreach (var child in machine.stateMachines)
            DescribeStateMachine(child.stateMachine, prefix + "/" + child.stateMachine.name, visited);
    }

    private static void DescribeMotion(Motion motion, string path, HashSet<Motion> visited)
    {
        if (!motion) { report.AppendLine("MOTION_NONE_OR_MISSING " + path + " (may be an intentional empty state)"); return; }
        var tree = motion as BlendTree;
        if (!tree || !visited.Add(motion)) return;
        foreach (var child in tree.children)
        {
            string next = path + "/" + tree.name + " " + child.position;
            report.AppendLine("BLEND_CHILD " + next + " = " + Asset(child.motion));
            DescribeMotion(child.motion, next, visited);
        }
    }

    private static string CachedCount(SpecialPopulationSpawner spawner, Animator animator)
    {
        if (!spawner) return "unavailable";
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var field = typeof(SpecialPopulationSpawner).GetField("animatorDrivers", flags);
        var drivers = field == null ? null : field.GetValue(spawner) as IEnumerable;
        if (drivers == null) return "unavailable";
        foreach (var driver in drivers)
        {
            if (driver == null) continue;
            var af = driver.GetType().GetField("animator", flags);
            if (af == null || af.GetValue(driver) as Animator != animator) continue;
            var pf = driver.GetType().GetField("parameters", flags);
            var values = pf == null ? null : pf.GetValue(driver) as Array;
            return values == null ? "unavailable" : values.Length.ToString();
        }
        return "not registered with spawner";
    }

    private static string Asset(UnityEngine.Object obj)
    { return obj ? obj.name + " [" + AssetDatabase.GetAssetPath(obj) + "]" : "NONE/MISSING"; }
    private static string F(float value) { return value.ToString("0.###", CultureInfo.InvariantCulture); }
    private static string HierarchyPath(Transform t)
    {
        if (!t) return "NONE/MISSING";
        string path = t.name;
        while (t.parent) { t = t.parent; path = t.name + "/" + path; }
        return path;
    }
}
#endif
