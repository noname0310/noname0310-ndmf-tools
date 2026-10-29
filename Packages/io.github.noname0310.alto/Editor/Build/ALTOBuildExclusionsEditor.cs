using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.Build
{
    [CustomEditor(typeof(ALTOBuildExclusions), true)]
    internal sealed class ALTOBuildExclusionsEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var component = (ALTOBuildExclusions)target;
            FilterEditorGUI.Header(serializedObject, component);
            FilterEditorGUI.Exclusions(serializedObject);
            serializedObject.ApplyModifiedProperties();
            EditorGUILayout.Space();
            if (GUILayout.Button("Build Failure Diagnostics")) BuildFailureDiagnosticsWindow.Open(component);
        }
    }
}
