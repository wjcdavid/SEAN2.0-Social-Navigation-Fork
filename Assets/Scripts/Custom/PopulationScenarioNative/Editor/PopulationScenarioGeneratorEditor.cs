#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace SEAN.Scenario.Agents.EditorTools
{
    [CustomEditor(typeof(PopulationScenarioGenerator))]
    public class PopulationScenarioGeneratorEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var composer = (PopulationScenarioGenerator)target;
            composer.NormalizeConfigurationLists();
            serializedObject.Update();
            EditorGUILayout.HelpBox("Native integration v2: random Original/special gait for ordinary moving singles; random native L2 with no manual clips. Moving groups retain their existing walk controllers. Configure before Play.", MessageType.Info);
            using (new EditorGUI.DisabledScope(Application.isPlaying)) {
                DrawPropertiesExcluding(serializedObject, "movingSingles", "staticSingles", "movingGroups", "staticGroups", "lastNativeRandomization");
                DrawSingles("movingSingles", true);
                DrawSingles("staticSingles", false);
                DrawGroups("movingGroups", true);
                DrawGroups("staticGroups", false);
            }
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.PropertyField(serializedObject.FindProperty("lastNativeRandomization"), new GUIContent("Randomization Report"), true);
            serializedObject.ApplyModifiedProperties();

            PopulationScenarioGenerator generator =
                (PopulationScenarioGenerator)target;

            EditorGUILayout.Space(10f);
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("Population Generator Controls", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Configured Regular", generator.ConfiguredRegularPopulation.ToString());
            EditorGUILayout.LabelField("Configured Special", generator.ConfiguredSpecialPopulation.ToString());
            EditorGUILayout.LabelField("Requested Combined", generator.RequestedCombinedPopulation.ToString());
            EditorGUILayout.LabelField("Configured Combined", generator.ConfiguredPopulation.ToString());

            if (generator.ConfiguredRegularPopulation != generator.requestedRegularPopulation
                || generator.ConfiguredSpecialPopulation != generator.requestedSpecialPopulation)
            {
                EditorGUILayout.HelpBox(
                    "Configured regular/special counts differ from the requested two sums. This is expected after manual list, appearance, or group-size edits. Click Randomize to rebuild both exact sums.",
                    MessageType.Info);
            }

            GUI.enabled = !Application.isPlaying;
            if (GUILayout.Button("1. Auto-Fill Known Project Assets"))
                AutoFillKnownAssets(generator);

            if (GUILayout.Button("2. Randomize Configuration"))
            {
                Undo.RecordObject(generator, "Randomize population configuration");
                generator.RandomizeConfiguration();
                EditorUtility.SetDirty(generator);
            }

            GUI.enabled = Application.isPlaying;
            if (GUILayout.Button("3. Generate Population"))
                generator.GeneratePopulation();
            if (GUILayout.Button("Clear Generated Population"))
                generator.ClearGeneratedPopulation();
            GUI.enabled = true;

            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox(
                    "Randomize and edit in Edit Mode. Enter Play Mode before Generate Population.",
                    MessageType.Info);
            }

            EditorGUILayout.EndVertical();
        }

        void Field(SerializedProperty p, string name, string label = null)
        {
            var child = p.FindPropertyRelative(name);
            if (child != null) EditorGUILayout.PropertyField(child, label == null ? new GUIContent(child.displayName) : new GUIContent(label), true);
        }
        int Kind(int legacy) { return legacy == 1 ? 2 : legacy == 2 || legacy >= 5 ? 1 : legacy; }
        void DrawSingles(string property, bool moving)
        {
            var list = serializedObject.FindProperty(property);
            list.isExpanded = EditorGUILayout.Foldout(list.isExpanded, moving ? "Moving Singles / 移动单人" : "Static Singles / 静态单人", true);
            if (!list.isExpanded) return;
            EditorGUI.indentLevel++;
            list.arraySize = Mathf.Max(0, EditorGUILayout.IntField("Count", list.arraySize));
            for (int i=0; i<list.arraySize; i++) {
                var item = list.GetArrayElementAtIndex(i);
                item.isExpanded = EditorGUILayout.Foldout(item.isExpanded, "Person " + i, true);
                if (!item.isExpanded) continue;
                EditorGUILayout.BeginVertical("box");
                Field(item, "label");
                if (moving) Field(item, "appearance", "Population / 人群");
                bool special = moving && item.FindPropertyRelative("appearance").intValue != 0;
                if (special) {
                    EditorGUILayout.HelpBox("Uses SpecialPopulationSpawner and this container's existing animation. Special people keep the old moving-only rule; no ordinary gait/personality override.", MessageType.Info);
                    Field(item, "walkSpeedMultiplier", "Speed multiplier (0.9 m/s base)");
                } else {
                    Field(item, "personality", "Personality / 一级反应");
                    if (moving) {
                        Field(item, "walkAnimation"); Field(item, "walkSpeedMultiplier");
                        if (item.FindPropertyRelative("walkAnimation").intValue == 5) Field(item, "carryBox");
                    } else Field(item, "yawDegrees");
                    int legacy = item.FindPropertyRelative("personality").intValue;
                    if (legacy >= 5) EditorGUILayout.HelpBox("Legacy crouch preset: converted to current Curious > CrouchAndObserve at generation. Choose Curious for the new L2 selector.", MessageType.Info);
                    NativeGUI(item.FindPropertyRelative("nativeReaction"), Kind(legacy), false, !moving);
                }
                EditorGUILayout.EndVertical();
            }
            EditorGUI.indentLevel--;
        }
        void DrawGroups(string property, bool moving)
        {
            var list = serializedObject.FindProperty(property);
            list.isExpanded = EditorGUILayout.Foldout(list.isExpanded, moving ? "Moving Groups / 移动组" : "Static Groups / 静态组", true);
            if (!list.isExpanded) return;
            EditorGUI.indentLevel++;
            list.arraySize = Mathf.Max(0, EditorGUILayout.IntField("Group Count", list.arraySize));
            for (int i=0; i<list.arraySize; i++) {
                var item = list.GetArrayElementAtIndex(i);
                item.isExpanded = EditorGUILayout.Foldout(item.isExpanded, "Group " + i, true);
                if (!item.isExpanded) continue;
                EditorGUILayout.BeginVertical("box");
                Field(item, "label"); Field(item, "memberCount"); Field(item, "formation");
                Field(item, "groupReaction", "Group Reaction / 整组反应");
                if (moving) {
                    Field(item, "moveSpeed"); Field(item, "openingAngleDegrees");
                    if (item.FindPropertyRelative("groupReaction").intValue != 0) {
                        Field(item, "stopForRobotEncounter"); Field(item, "limitEncounterDuration");
                        if (item.FindPropertyRelative("limitEncounterDuration").boolValue) Field(item, "maxEncounterSeconds");
                        Field(item, "encounterCooldownSeconds");
                    }
                } else Field(item, "yawDegrees");
                var people = item.FindPropertyRelative("memberPersonalities");
                var reactions = item.FindPropertyRelative("memberReactions");
                // OnValidate sizes both lists together after Member Count is applied.
                int count = Mathf.Min(people.arraySize, reactions.arraySize);
                for (int j=0; j<count; j++) {
                    EditorGUILayout.BeginVertical("box");
                    var personality = people.GetArrayElementAtIndex(j);
                    EditorGUILayout.PropertyField(personality, new GUIContent("Member " + j + " Personality"));
                    if (personality.intValue == 0)
                        EditorGUILayout.LabelField("Source: Random Rocketbox; no personal reaction");
                    else {
                        EditorGUILayout.LabelField("Source: SimpleAppearanceAgent");
                        NativeGUI(reactions.GetArrayElementAtIndex(j), Kind(personality.intValue), true, !moving);
                    }
                    EditorGUILayout.EndVertical();
                }
                EditorGUILayout.EndVertical();
            }
            EditorGUI.indentLevel--;
        }
        void NativeGUI(SerializedProperty p, int kind, bool group, bool stationary)
        {
            if (p == null) return;
            p.isExpanded = EditorGUILayout.Foldout(p.isExpanded, "Native Reaction / 二级反应与动画", true);
            if (!p.isExpanded) return;
            EditorGUI.indentLevel++;
            Field(p, "enabled");
            if (!p.FindPropertyRelative("enabled").boolValue) { EditorGUI.indentLevel--; return; }
            bool custom=false, movement=false, crouch=false;
            if (kind==1) {
                Field(p,"curiousReaction");
                int c=p.FindPropertyRelative("curiousReaction").intValue;
                movement=c==0; crouch=c==2; custom=c==3;
                if (movement) { Field(p,"followDistance"); Field(p,"curiousExitMultiplier"); }
                if (c==1) Field(p,"reactionHoldSeconds");
            } else if (kind==2) {
                Field(p,"scaredReaction"); movement=true;
                custom=p.FindPropertyRelative("scaredReaction").intValue==1;
            } else if (kind==3) {
                Field(p,"surprisedReaction"); int s=p.FindPropertyRelative("surprisedReaction").intValue;
                custom=s==2; if (s==0) Field(p,"surpriseFreezeSeconds");
            } else if (kind==4) {
                Field(p,"assertiveReaction"); int a=p.FindPropertyRelative("assertiveReaction").intValue;
                movement=a==0; custom=a==2;
            }
            if (movement || kind==0) { Field(p,"playGestureBeforeNativeBehavior"); custom |= p.FindPropertyRelative("playGestureBeforeNativeBehavior").boolValue; }
            if (custom) Field(p,"customReactionClip");
            if (crouch) foreach (string f in new[]{"crouchClipOverride","kneelAtClipEnd","crouchTransitionSeconds","crouchHoldSeconds","standUpIfCloserThan"}) Field(p,f);
            Field(p,"reactionPlaybackSpeed"); Field(p,"reactionBlendSeconds"); Field(p,"cooldownSeconds");
            if (group) {
                if (movement) { Field(p,"allowLeaveFormation"); Field(p,"nativeMovementSeconds"); }
                Field(p,"maxFormationOffset"); Field(p,"returnSpeed");
            } else if (stationary && movement)
                EditorGUILayout.HelpBox("Fixed individual: approach/flee translation is disabled. Choose an observation or a gesture to see a visible reaction.", MessageType.Info);
            EditorGUI.indentLevel--;
        }

        private static void AutoFillKnownAssets(PopulationScenarioGenerator generator)
        {
            Undo.RecordObject(generator, "Auto-fill population generator assets");

            if (generator.specialPrefabs == null)
                generator.specialPrefabs = new PopulationScenarioGenerator.SpecialPrefabAssets();
            if (generator.s68Clips == null)
                generator.s68Clips = new PopulationScenarioGenerator.S68ClipAssets();

            generator.simpleAppearanceAgentPrefab = Load<GameObject>(
                "Assets/Resources/Prefabs/SimpleAppearanceAgent.prefab");

            generator.specialPrefabs.cyclist = Load<GameObject>(
                "Assets/Resources/Prefabs/CyclistContainer.prefab");
            generator.specialPrefabs.dogWalker = Load<GameObject>(
                "Assets/Resources/Prefabs/DogWalkerContainer.prefab");
            generator.specialPrefabs.femaleChild = Load<GameObject>(
                "Assets/Resources/Prefabs/FemaleChildContainer.prefab");
            generator.specialPrefabs.maleChild = Load<GameObject>(
                "Assets/Resources/Prefabs/MaleChildContainer.prefab");
            generator.specialPrefabs.phoneUser = Load<GameObject>(
                "Assets/Resources/Prefabs/PedetrainAvatars/PhoneUserContainer.prefab");
            generator.specialPrefabs.scooterUser = Load<GameObject>(
                "Assets/Resources/Prefabs/ScooterUserContainer.prefab");
            generator.specialPrefabs.wheelchairUser = Load<GameObject>(
                "Assets/Resources/Prefabs/WheelChairUserContainer.prefab");
            generator.specialPrefabs.whiteCaneUser = Load<GameObject>(
                "Assets/Resources/Prefabs/WhiteCaneUserContainer.prefab");

            generator.s68Clips.baseController = Load<RuntimeAnimatorController>(
                "Assets/PedestrianAssets/S68Crouch/Resources/S68_CuriousCrouch.controller");
            generator.s68Clips.kneelingDown = LoadAnimationClip(
                "Assets/PedestrianAssets/S68Crouch/Kneeling Down.fbx");
            generator.s68Clips.crouchToStand = LoadAnimationClip(
                "Assets/PedestrianAssets/S68Crouch/Crouch To Stand.fbx");
            generator.s68Clips.crouchToStandV2 = LoadAnimationClip(
                "Assets/PedestrianAssets/S68Crouch/Crouch To Stand v2.fbx");
            generator.s68Clips.iviCrouchCopy = LoadAnimationClip(
                "Assets/PedestrianAssets/S68Crouch/IviCrouch_copy.fbx");

            EditorUtility.SetDirty(generator);
            serializedRefresh(generator);

            int missing = CountMissing(generator);
            if (missing == 0)
                Debug.Log("[PopulationGenerator] Auto-fill complete: all known prefabs, controller, and S68 clips found.", generator);
            else
                Debug.LogWarning("[PopulationGenerator] Auto-fill completed with " + missing
                    + " missing asset reference(s). Check the Inspector and project paths.", generator);
        }

        private static T Load<T>(string path) where T : Object
        {
            T asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset == null)
                Debug.LogWarning("[PopulationGenerator] Asset not found at " + path);
            return asset;
        }

        private static AnimationClip LoadAnimationClip(string path)
        {
            Object[] assets = AssetDatabase.LoadAllAssetsAtPath(path);
            for (int i = 0; i < assets.Length; i++)
            {
                AnimationClip clip = assets[i] as AnimationClip;
                if (clip != null && !clip.name.StartsWith("__preview__"))
                    return clip;
            }
            Debug.LogWarning("[PopulationGenerator] AnimationClip sub-asset not found at " + path);
            return null;
        }

        private static int CountMissing(PopulationScenarioGenerator g)
        {
            int missing = 0;
            if (g.simpleAppearanceAgentPrefab == null) missing++;
            if (g.specialPrefabs.cyclist == null) missing++;
            if (g.specialPrefabs.dogWalker == null) missing++;
            if (g.specialPrefabs.femaleChild == null) missing++;
            if (g.specialPrefabs.maleChild == null) missing++;
            if (g.specialPrefabs.phoneUser == null) missing++;
            if (g.specialPrefabs.scooterUser == null) missing++;
            if (g.specialPrefabs.wheelchairUser == null) missing++;
            if (g.specialPrefabs.whiteCaneUser == null) missing++;
            if (g.s68Clips.baseController == null) missing++;
            if (g.s68Clips.kneelingDown == null) missing++;
            if (g.s68Clips.crouchToStand == null) missing++;
            if (g.s68Clips.crouchToStandV2 == null) missing++;
            if (g.s68Clips.iviCrouchCopy == null) missing++;
            return missing;
        }

        // Keeping this tiny helper separate makes it explicit that Auto-Fill mutates only the
        // selected component and asks Unity to repaint/re-serialize it immediately.
        private static void serializedRefresh(PopulationScenarioGenerator generator)
        {
            PrefabUtility.RecordPrefabInstancePropertyModifications(generator);
            SceneView.RepaintAll();
        }
    }
}
#endif
