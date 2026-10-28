using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.MaterialAtlas
{
    [CustomPropertyDrawer(typeof(MaterialAtlasGroup))]
    internal sealed class MaterialAtlasGroupDrawer : PropertyDrawer
    {
        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            float height = EditorGUIUtility.singleLineHeight;
            if (!property.isExpanded) return height;
            var field = property.Copy(); var end = property.GetEndProperty();
            if (field.NextVisible(true))
                do
                {
                    if (SerializedProperty.EqualContents(field, end)) break;
                    if (!Visible(property, field)) continue;
                    height += EditorGUIUtility.standardVerticalSpacing + EditorGUI.GetPropertyHeight(field, true);
                } while (field.NextVisible(false));
            return height;
        }

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            using (new EditorGUI.PropertyScope(position, label, property))
            {
                position.height = EditorGUIUtility.singleLineHeight;
                property.isExpanded = EditorGUI.Foldout(position, property.isExpanded, label, true);
                if (!property.isExpanded) return;
                using (new EditorGUI.IndentLevelScope())
                {
                    var field = property.Copy(); var end = property.GetEndProperty();
                    if (field.NextVisible(true))
                        do
                        {
                            if (SerializedProperty.EqualContents(field, end)) break;
                            if (!Visible(property, field)) continue;
                            position.y += position.height + EditorGUIUtility.standardVerticalSpacing;
                            position.height = EditorGUI.GetPropertyHeight(field, true);
                            EditorGUI.PropertyField(position, field, true);
                        } while (field.NextVisible(false));
                }
            }
        }

        private static bool Visible(SerializedProperty property, SerializedProperty field)
        {
            var selection = property.FindPropertyRelative("MaterialSelection");
            return field.name != "Materials" || selection.hasMultipleDifferentValues ||
                selection.intValue == (int)AtlasMaterialSelectionMode.SelectedMaterials;
        }
    }
}
