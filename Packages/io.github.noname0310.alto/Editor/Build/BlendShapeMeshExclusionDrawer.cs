using Noname.AvatarTools.Meshes;
using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.Build
{
    [CustomPropertyDrawer(typeof(BlendShapeMeshExclusion))]
    internal sealed class BlendShapeMeshExclusionDrawer : PropertyDrawer
    {
        public override float GetPropertyHeight(SerializedProperty property, GUIContent label) =>
            EditorGUIUtility.singleLineHeight * 3 + EditorGUIUtility.standardVerticalSpacing * 2;

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);
            var row = new Rect(position.x, position.y, position.width, EditorGUIUtility.singleLineHeight);
            var renderer = property.FindPropertyRelative("Renderer");
            EditorGUI.PropertyField(row, renderer, label);
            row.y += EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;
            var shapes = property.FindPropertyRelative("BlendShapes");
            using (new EditorGUI.DisabledScope((renderer.objectReferenceValue as SkinnedMeshRenderer)?.sharedMesh == null))
                if (GUI.Button(row, "Select BlendShape Regions (" + shapes.arraySize + ")"))
                    PopupWindow.Show(row, new BlendShapeRegionSelector(property.serializedObject.targetObject, property.propertyPath));
            row.y += EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;
            EditorGUI.PropertyField(row, property.FindPropertyRelative("DisplacementThreshold"));
            EditorGUI.EndProperty();
        }
    }
}
