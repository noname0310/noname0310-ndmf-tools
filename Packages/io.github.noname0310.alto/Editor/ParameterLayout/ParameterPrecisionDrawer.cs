using System;
using System.Collections.Generic;
using System.Linq;
using Noname.AvatarTools.ParameterPolicy;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.ParameterLayout
{
    [InitializeOnLoad]
    internal static class ParameterPrecisionPreview
    {
        private static readonly Dictionary<(int Avatar, int Source, string Name), AnimatorControllerParameterType?> Types = new();
        static ParameterPrecisionPreview()
        {
            EditorApplication.projectChanged += Types.Clear;
            EditorApplication.hierarchyChanged += Types.Clear;
            Undo.undoRedoPerformed += Types.Clear;
        }

        internal static AnimatorControllerParameterType? Find(GameObject avatar, Object source, string name)
        {
            if (avatar == null || string.IsNullOrWhiteSpace(name)) return null;
            var key = (avatar.GetInstanceID(), source == null ? 0 : source.GetInstanceID(), name);
            if (Types.TryGetValue(key, out var cached)) return cached;
            var found = new HashSet<AnimatorControllerParameterType>();
            var resolved = ParameterSourceResolver.Resolve(new AnimatorParameterSource { Source = source != null ? source : avatar }, avatar);
            foreach (var asset in resolved.Parameters.Where(p => p.Name == name).Select(p => p.Asset).Distinct())
            {
                if (asset is VRCExpressionParameters expressions)
                    foreach (var p in expressions.parameters ?? Array.Empty<VRCExpressionParameters.Parameter>())
                        if (p?.name == name) found.Add(p.valueType == VRCExpressionParameters.ValueType.Float ? AnimatorControllerParameterType.Float :
                            p.valueType == VRCExpressionParameters.ValueType.Int ? AnimatorControllerParameterType.Int : AnimatorControllerParameterType.Bool);
                var runtime = asset as RuntimeAnimatorController;
                var seen = new HashSet<RuntimeAnimatorController>();
                while (runtime is AnimatorOverrideController over && seen.Add(runtime)) runtime = over.runtimeAnimatorController;
                if (runtime is AnimatorController controller)
                    foreach (var p in controller.parameters) if (p.name == name) found.Add(p.type);
            }
            var owner = source as GameObject ?? (source as Component)?.gameObject ?? avatar;
            foreach (var p in nadena.dev.ndmf.ParameterInfo.ForUI.GetParametersForObject(owner))
                if ((p.OriginalName == name || source == null && p.EffectiveName == name) && p.ParameterType.HasValue &&
                    (source is not Component component || p.Source == component)) found.Add(p.ParameterType.Value);
            return Types[key] = found.Count == 1 ? found.Single() : null;
        }
    }

    [CustomPropertyDrawer(typeof(ParameterPrecisionSettings), true)]
    internal sealed class ParameterPrecisionDrawer : PropertyDrawer
    {
        private static AnimatorControllerParameterType? Type(SerializedProperty property)
        {
            var kind = (ParameterPrecisionType)property.FindPropertyRelative("Type").enumValueIndex;
            if (kind == ParameterPrecisionType.Float) return AnimatorControllerParameterType.Float;
            if (kind == ParameterPrecisionType.Int) return AnimatorControllerParameterType.Int;
            var avatar = (property.serializedObject.targetObject as Component)?.GetComponentInParent<VRCAvatarDescriptor>(true);
            return ParameterPrecisionPreview.Find(avatar?.gameObject, property.FindPropertyRelative("Source").objectReferenceValue,
                property.FindPropertyRelative("Name").stringValue);
        }

        private static IEnumerable<SerializedProperty> Fields(SerializedProperty property)
        {
            var selection = property.FindPropertyRelative("Parameters");
            if (selection != null)
            {
                yield return selection;
                yield return property.FindPropertyRelative("Integers");
                yield return property.FindPropertyRelative("Floats");
            }
            else
            {
                yield return property.FindPropertyRelative("Source");
                yield return property.FindPropertyRelative("Name");
                yield return property.FindPropertyRelative("Type");
                var type = Type(property);
                if (type == AnimatorControllerParameterType.Int) yield return property.FindPropertyRelative("Integers");
                if (type == AnimatorControllerParameterType.Float) yield return property.FindPropertyRelative("Floats");
            }
        }

        private static string Notice(SerializedProperty property)
        {
            if (property.FindPropertyRelative("Parameters") != null) return null;
            var type = Type(property);
            return !type.HasValue ? "Choose an input to detect its type. For build-generated inputs, select Float or Int above." :
                type is AnimatorControllerParameterType.Bool or AnimatorControllerParameterType.Trigger ? "Only Int and Float inputs support precision overrides." : null;
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label) => EditorGUIUtility.singleLineHeight +
            (property.isExpanded ? Fields(property).Sum(p => EditorGUI.GetPropertyHeight(p, true) + EditorGUIUtility.standardVerticalSpacing) +
                (Notice(property) != null ? 42 : 0) : 0);

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);
            var line = new Rect(position.x, position.y, position.width, EditorGUIUtility.singleLineHeight);
            property.isExpanded = EditorGUI.Foldout(line, property.isExpanded, label, true);
            if (property.isExpanded)
            {
                EditorGUI.indentLevel++;
                foreach (var field in Fields(property))
                {
                    line.y += line.height + EditorGUIUtility.standardVerticalSpacing;
                    line.height = EditorGUI.GetPropertyHeight(field, true);
                    EditorGUI.PropertyField(line, field, true);
                }
                if (Notice(property) is string notice)
                {
                    line.y += line.height + EditorGUIUtility.standardVerticalSpacing; line.height = 40;
                    EditorGUI.HelpBox(line, notice, MessageType.Info);
                }
                EditorGUI.indentLevel--;
            }
            EditorGUI.EndProperty();
        }
    }

    [CustomPropertyDrawer(typeof(IntegerRangeSettings))]
    internal sealed class IntegerRangeSettingsDrawer : PropertyDrawer
    {
        public override float GetPropertyHeight(SerializedProperty property, GUIContent label) =>
            EditorGUIUtility.singleLineHeight * (property.isExpanded ? property.FindPropertyRelative("Range").enumValueIndex == 0 ? 2 : 4 : 1) +
            EditorGUIUtility.standardVerticalSpacing * (property.isExpanded ? property.FindPropertyRelative("Range").enumValueIndex == 0 ? 1 : 3 : 0);
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);
            position.height = EditorGUIUtility.singleLineHeight;
            property.isExpanded = EditorGUI.Foldout(position, property.isExpanded, label, true);
            if (property.isExpanded)
            {
                EditorGUI.indentLevel++;
                foreach (var field in property.FindPropertyRelative("Range").enumValueIndex == 0 ? new[] { "Range" } : new[] { "Range", "Minimum", "Maximum" })
                { position.y += position.height + EditorGUIUtility.standardVerticalSpacing; EditorGUI.PropertyField(position, property.FindPropertyRelative(field)); }
                EditorGUI.indentLevel--;
            }
            EditorGUI.EndProperty();
        }
    }
}
