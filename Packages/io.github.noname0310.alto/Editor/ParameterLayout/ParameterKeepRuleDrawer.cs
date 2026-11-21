using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.ParameterLayout
{
    [CustomPropertyDrawer(typeof(ParameterKeepRule))]
    internal sealed class ParameterKeepRuleDrawer : PropertyDrawer
    {
        private static bool ShowPriority(SerializedProperty property) => property.serializedObject.FindProperty("InputAssumption")?.enumValueIndex !=
            (int)ParameterInputAssumption.AvatarDrivenExceptListed;
        public override float GetPropertyHeight(SerializedProperty property, GUIContent label) => EditorGUIUtility.singleLineHeight +
            (property.isExpanded ? EditorGUIUtility.standardVerticalSpacing + EditorGUI.GetPropertyHeight(property.FindPropertyRelative("Parameters"), true) +
                (ShowPriority(property) ? EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing : 0) : 0);
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);
            var line = new Rect(position.x, position.y, position.width, EditorGUIUtility.singleLineHeight);
            property.isExpanded = EditorGUI.Foldout(line, property.isExpanded, label, true);
            if (property.isExpanded)
            {
                EditorGUI.indentLevel++;
                if (ShowPriority(property))
                {
                    line.y += line.height + EditorGUIUtility.standardVerticalSpacing;
                    EditorGUI.PropertyField(line, property.FindPropertyRelative("Priority"));
                }
                line.y += line.height + EditorGUIUtility.standardVerticalSpacing;
                var selection = property.FindPropertyRelative("Parameters");
                line.height = EditorGUI.GetPropertyHeight(selection, true); EditorGUI.PropertyField(line, selection, true);
                EditorGUI.indentLevel--;
            }
            EditorGUI.EndProperty();
        }
    }
}
