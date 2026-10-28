using Noname.AvatarTools.Build;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace Noname.AvatarTools.Expressions
{
    [CustomEditor(typeof(ALTOTraceAndOptimizeExpressions))]
    internal sealed class ALTOTraceAndOptimizeExpressionsEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update(); DrawPropertiesExcluding(serializedObject, "m_Script"); serializedObject.ApplyModifiedProperties();
            var component = (ALTOTraceAndOptimizeExpressions)target;
            EditorGUILayout.HelpBox("Analyzes the avatar's complete menu and controller graphs, wherever this component is attached. " +
                "Parameter Policy preservation is shared. Synchronized declarations remain unchanged. " +
                "Preserved controls match display names and types after generation; ambiguous matches are all retained. " +
                "Preserving a menu does not undo a parameter freeze. Unverified shader outputs remain consumers.", MessageType.Info);
            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || component.FindDescriptor() == null))
                if (GUILayout.Button("Build and Analyze Avatar"))
                {
                    var avatar = component.FindDescriptor().gameObject;
                    EditorApplication.delayCall += () => { if (avatar != null) AvatarBuildAnalysis.Analyze(avatar); };
                }
        }
    }

    [CustomPropertyDrawer(typeof(ExpressionControlPreservation))]
    internal sealed class ExpressionControlPreservationDrawer : PropertyDrawer
    {
        private static bool HasSubMenus(SerializedProperty property) => property.FindPropertyRelative("Selected").boolValue &&
            property.FindPropertyRelative("ControlType").intValue == (int)VRCExpressionsMenu.Control.ControlType.SubMenu;

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            int rows = HasSubMenus(property) ? 4 : 3;
            return EditorGUIUtility.singleLineHeight * rows + EditorGUIUtility.standardVerticalSpacing * (rows - 1);
        }
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);
            var line = position; line.height = EditorGUIUtility.singleLineHeight;
            EditorGUI.LabelField(line, label); line.y += line.height + EditorGUIUtility.standardVerticalSpacing;
            var menuProperty = property.FindPropertyRelative("Menu");
            EditorGUI.BeginChangeCheck(); EditorGUI.PropertyField(line, menuProperty);
            if (EditorGUI.EndChangeCheck()) property.FindPropertyRelative("Selected").boolValue = false;
            line.y += line.height + EditorGUIUtility.standardVerticalSpacing;
            var asset = menuProperty.objectReferenceValue as VRCExpressionsMenu;
            bool selected = property.FindPropertyRelative("Selected").boolValue;
            string text = selected ? property.FindPropertyRelative("ControlName").stringValue + " (" +
                (VRCExpressionsMenu.Control.ControlType)property.FindPropertyRelative("ControlType").intValue + ")" : "Choose Control...";
            using (new EditorGUI.DisabledScope(asset == null))
                if (GUI.Button(line, text, EditorStyles.popup))
                {
                    var popup = new GenericMenu();
                    var serialized = property.serializedObject; string propertyPath = property.propertyPath;
                    if (asset.controls != null)
                        for (int i = 0; i < asset.controls.Count; i++)
                        {
                            var control = asset.controls[i]; if (control == null) continue;
                            string name = control.name ?? ""; var kind = control.type;
                            popup.AddItem(new GUIContent(i + ": " + name.Replace('/', '\u2215') + " (" + kind + ")"), false, () =>
                            {
                                serialized.Update(); var row = serialized.FindProperty(propertyPath); if (row == null) return;
                                row.FindPropertyRelative("ControlName").stringValue = name;
                                row.FindPropertyRelative("ControlType").intValue = (int)kind;
                                row.FindPropertyRelative("Selected").boolValue = true; serialized.ApplyModifiedProperties();
                            });
                        }
                    popup.ShowAsContext();
                }
            if (HasSubMenus(property))
            {
                line.y += line.height + EditorGUIUtility.standardVerticalSpacing;
                EditorGUI.PropertyField(line, property.FindPropertyRelative("IncludeSubMenus"));
            }
            EditorGUI.EndProperty();
        }
    }
}
