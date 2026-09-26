using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using SEAN.Scenario.Agents;

[CustomPropertyDrawer(typeof(GroupReactionSettings))]
public sealed class GroupReactionSettingsDrawer : PropertyDrawer
{
    List<string> Fields(SerializedProperty p)
    {
        var names = new List<string> { "enabled" };
        if (!p.FindPropertyRelative("enabled").boolValue) return names;
        string parent = p.propertyPath.Substring(0, p.propertyPath.LastIndexOf('.'));
        var personality = p.serializedObject.FindProperty(parent + ".personality");
        var kind = personality == null ? PedestrianModulator.PersonalityType.Indifferent :
            (PedestrianModulator.PersonalityType)personality.intValue;
        bool custom = false, native = false, crouch = false;
        switch (kind) {
            case PedestrianModulator.PersonalityType.Curious:
                names.Add("curiousReaction");
                int c = p.FindPropertyRelative("curiousReaction").enumValueIndex;
                native = c == 0; crouch = c == 2; custom = c == 3;
                if (native) { names.Add("followDistance"); names.Add("curiousExitMultiplier"); }
                if (c == 1) names.Add("reactionHoldSeconds");
                break;
            case PedestrianModulator.PersonalityType.Scared:
                names.Add("scaredReaction"); native = true;
                custom = p.FindPropertyRelative("scaredReaction").enumValueIndex == 1;
                break;
            case PedestrianModulator.PersonalityType.Surprised:
                names.Add("surprisedReaction");
                int s = p.FindPropertyRelative("surprisedReaction").enumValueIndex;
                custom = s == 2;
                if (s == 0) names.Add("surpriseFreezeSeconds");
                break;
            case PedestrianModulator.PersonalityType.Assertive:
                names.Add("assertiveReaction");
                int a = p.FindPropertyRelative("assertiveReaction").enumValueIndex;
                native = a == 0; custom = a == 2;
                break;
        }
        if (native || kind == PedestrianModulator.PersonalityType.Indifferent) {
            names.Add("playGestureBeforeNativeBehavior");
            custom |= p.FindPropertyRelative("playGestureBeforeNativeBehavior").boolValue;
        }
        if (custom) names.Add("customReactionClip");
        if (crouch) names.AddRange(new[] { "crouchClipOverride", "kneelAtClipEnd", "crouchTransitionSeconds", "crouchHoldSeconds", "standUpIfCloserThan" });
        names.AddRange(new[] { "reactionPlaybackSpeed", "reactionBlendSeconds", "cooldownSeconds" });
        if (native) names.AddRange(new[] { "allowLeaveFormation", "nativeMovementSeconds" });
        names.Add("maxFormationOffset");
        names.Add("returnSpeed");
        return names;
    }

    public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
    {
        float h = EditorGUIUtility.singleLineHeight;
        if (property.isExpanded) foreach (string name in Fields(property))
            h += EditorGUI.GetPropertyHeight(property.FindPropertyRelative(name), true) + EditorGUIUtility.standardVerticalSpacing;
        return h;
    }

    public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
    {
        EditorGUI.BeginProperty(position, label, property);
        var line = new Rect(position.x, position.y, position.width, EditorGUIUtility.singleLineHeight);
        property.isExpanded = EditorGUI.Foldout(line, property.isExpanded, new GUIContent("Native Reaction", "Current native reaction choices; original walking animation stays unchanged."), true);
        if (property.isExpanded) {
            EditorGUI.indentLevel++;
            foreach (string name in Fields(property)) {
                var field = property.FindPropertyRelative(name);
                line.y += line.height + EditorGUIUtility.standardVerticalSpacing;
                line.height = EditorGUI.GetPropertyHeight(field, true);
                using (new EditorGUI.DisabledScope(Application.isPlaying)) EditorGUI.PropertyField(line, field, true);
            }
            EditorGUI.indentLevel--;
        }
        EditorGUI.EndProperty();
    }
}
