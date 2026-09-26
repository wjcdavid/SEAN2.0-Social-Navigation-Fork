using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using SEAN.Scenario.Agents;

// Configure mixed members before Play. No regeneration or prefab writes in the drawer.
public abstract class GroupMemberConfigDrawerBase : PropertyDrawer
{
    bool IsSpecial(SerializedProperty p) { return p.FindPropertyRelative("type").enumValueIndex == 1; }
    bool Automatic(SerializedProperty p) { return p.FindPropertyRelative("automaticSource").boolValue; }
    bool CanReact(SerializedProperty p)
    {
        return IsSpecial(p) && (Automatic(p) || p.FindPropertyRelative("source").enumValueIndex == 0);
    }
    List<string> Fields(SerializedProperty p)
    {
        var fields = new List<string> { "type", "automaticSource", "source" };
        if (CanReact(p)) {
            fields.Add("personality");
            if (p.FindPropertyRelative("enableReactionAnimation") != null) fields.Add("enableReactionAnimation");
            if (p.FindPropertyRelative("useDefaultReactionDistance") != null) {
                fields.Add("useDefaultReactionDistance");
                if (!p.FindPropertyRelative("useDefaultReactionDistance").boolValue) fields.Add("reactionDistance");
            }
            fields.Add("nativeReaction");
        }
        if (p.FindPropertyRelative("moveDuringReformation") != null) fields.Add("moveDuringReformation");
        return fields;
    }
    public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
    {
        float height = EditorGUIUtility.singleLineHeight;
        if (!property.isExpanded) return height;
        foreach (string name in Fields(property))
            height += EditorGUI.GetPropertyHeight(property.FindPropertyRelative(name), true) + EditorGUIUtility.standardVerticalSpacing;
        if (IsSpecial(property) && !CanReact(property)) height += 3 * EditorGUIUtility.singleLineHeight;
        return height;
    }
    public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
    {
        EditorGUI.BeginProperty(position, label, property);
        var line = new Rect(position.x, position.y, position.width, EditorGUIUtility.singleLineHeight);
        property.isExpanded = EditorGUI.Foldout(line, property.isExpanded, label, true);
        if (property.isExpanded) {
            EditorGUI.indentLevel++;
            foreach (string name in Fields(property)) {
                var field = property.FindPropertyRelative(name);
                line.y += line.height + EditorGUIUtility.standardVerticalSpacing;
                line.height = EditorGUI.GetPropertyHeight(field, true);
                if (name == "source" && Automatic(property)) {
                    EditorGUI.LabelField(line, "Effective Source", IsSpecial(property) ? "Simple Appearance Agent" : "Random Rocketbox");
                } else {
                    using (new EditorGUI.DisabledScope(Application.isPlaying)) EditorGUI.PropertyField(line, field, true);
                }
            }
            if (IsSpecial(property) && !CanReact(property)) {
                line.y += line.height + EditorGUIUtility.standardVerticalSpacing;
                line.height = 2.5f * EditorGUIUtility.singleLineHeight;
                EditorGUI.HelpBox(line, "This manual Rocketbox source keeps its original visuals. Enable Automatic Source or choose Simple Appearance Agent for native individual reactions.", MessageType.Info);
            }
            EditorGUI.indentLevel--;
        }
        EditorGUI.EndProperty();
    }
}

[CustomPropertyDrawer(typeof(MovingSocialGroupSpawner_WJC_SlotFlip.MemberConfig))]
public sealed class MovingGroupMemberConfigDrawer : GroupMemberConfigDrawerBase { }

[CustomPropertyDrawer(typeof(DynamicMovingAttentionGroupSpawner_WJC_SAFE_SlotFlip.MemberConfig))]
public sealed class DynamicMovingGroupMemberConfigDrawer : GroupMemberConfigDrawerBase { }

[CustomPropertyDrawer(typeof(DynamicAttentionGroupSpawner.MemberConfig))]
public sealed class DynamicAttentionGroupMemberConfigDrawer : GroupMemberConfigDrawerBase { }

[CustomPropertyDrawer(typeof(StaticSocialGroupSpawner.MemberConfig))]
public sealed class StaticGroupMemberConfigDrawer : GroupMemberConfigDrawerBase { }
