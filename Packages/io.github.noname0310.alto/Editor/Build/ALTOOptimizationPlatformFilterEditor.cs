using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.Build
{
    [CustomEditor(typeof(ALTOOptimizationPlatformFilter))]
    [CanEditMultipleObjects]
    internal sealed class ALTOOptimizationPlatformFilterEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            // Unity can omit the header checkbox for a MonoBehaviour with no lifecycle
            // callbacks. The installer still uses enabled, so always expose it here.
            var enabled = serializedObject.FindProperty("m_Enabled");
            EditorGUILayout.PropertyField(enabled, new GUIContent("Enable Platform Filter",
                "Restrict ALTO settings on this object and its descendants to the selected build platforms."));
            bool disabled = !enabled.hasMultipleDifferentValues && !enabled.boolValue;
            using (new EditorGUI.DisabledScope(disabled))
                EditorGUILayout.PropertyField(serializedObject.FindProperty("ApplyOnPlatforms"));
            if (disabled)
                EditorGUILayout.HelpBox("Disabled: this filter imposes no platform restriction. Other platform settings still apply.", MessageType.Info);
            EditorGUILayout.HelpBox("Applies to ALTO settings on this object and its descendants. " +
                "Scope Root can still target the whole avatar. Nested platform selections intersect. " +
                "Other components and avatar objects are retained.", MessageType.Info);
            serializedObject.ApplyModifiedProperties();
        }
    }
}
