using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.Build
{
    [CustomEditor(typeof(ALTOOptimizationPlatformFilter))]
    internal sealed class ALTOOptimizationPlatformFilterEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("ApplyOnPlatforms"));
            EditorGUILayout.HelpBox("Applies to ALTO settings on this object and its descendants. " +
                "Scope Root can still target the whole avatar. Nested platform selections intersect. " +
                "Other components and avatar objects are retained.", MessageType.Info);
            serializedObject.ApplyModifiedProperties();
        }
    }
}
