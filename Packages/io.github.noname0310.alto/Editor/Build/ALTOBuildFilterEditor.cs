using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.Build
{
    [CustomEditor(typeof(ALTOBuildFilter))]
    internal sealed class ALTOBuildFilterEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var component = (ALTOBuildFilter)target;
            EditorGUILayout.HelpBox("Legacy combined filter. Existing settings remain supported. New setups use ALTO Build Exclusions before generators and ALTO Rendering Filter after generators.", MessageType.Info);
            FilterEditorGUI.Header(serializedObject, component);
            FilterEditorGUI.Exclusions(serializedObject);
            FilterEditorGUI.Rendering(serializedObject);
            serializedObject.ApplyModifiedProperties();
            EditorGUILayout.Space();
            if (GUILayout.Button("Build Failure Diagnostics")) BuildFailureDiagnosticsWindow.Open(component);
        }
    }
}
