using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.ParameterLayout
{
    [CustomPropertyDrawer(typeof(FloatPrecisionSettings), true)]
    internal sealed class FloatPrecisionSettingsDrawer : PropertyDrawer
    {
        private static IEnumerable<SerializedProperty> Fields(SerializedProperty property)
        {
            var mode = property.FindPropertyRelative("Precision");
            yield return mode;
            if (mode.enumValueIndex != (int)FloatPrecisionMode.KeepExisting) yield return property.FindPropertyRelative("Bits");
            yield return property.FindPropertyRelative("Representation");
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            float height = EditorGUIUtility.singleLineHeight;
            if (property.isExpanded)
                foreach (var field in Fields(property))
                    if (field != null) height += EditorGUIUtility.standardVerticalSpacing + EditorGUI.GetPropertyHeight(field, true);
            return height;
        }

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
                    if (field == null) continue;
                    line.y += line.height + EditorGUIUtility.standardVerticalSpacing;
                    line.height = EditorGUI.GetPropertyHeight(field, true);
                    EditorGUI.PropertyField(line, field, true);
                }
                EditorGUI.indentLevel--;
            }
            EditorGUI.EndProperty();
        }
    }
}
