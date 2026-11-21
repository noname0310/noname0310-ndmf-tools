using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.ObjectOptimization
{
    [CustomEditor(typeof(ALTOTraceAndOptimizeObjects))]
    internal sealed class ALTOTraceAndOptimizeObjectsEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            DrawPropertiesExcluding(serializedObject, "m_Script", "ComponentHoisting");
            if (serializedObject.FindProperty("HoistComponents").boolValue)
                EditorGUILayout.PropertyField(serializedObject.FindProperty("ComponentHoisting"), new GUIContent("Component Types to Hoist"), true);
            EditorGUILayout.HelpBox("Empty Scope Root uses this object's subtree. Analysis covers the entire avatar. " +
                "This component traces runtime consumers and their dependencies, removes unused objects, components and transform outputs, and simplifies redundant curves. " +
                "Expression/network slots, menu controls, external observers and potentially active outputs are retained.", MessageType.Info);
            serializedObject.ApplyModifiedProperties();
        }
    }
}
