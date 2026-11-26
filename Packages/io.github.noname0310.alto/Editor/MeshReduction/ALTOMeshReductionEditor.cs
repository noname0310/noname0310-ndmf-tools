using Noname.AvatarTools.Build;
using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.MeshReduction
{
    [CustomEditor(typeof(ALTOMeshReduction))]
    internal sealed class ALTOMeshReductionEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            RendererSelectionInspector.Draw(serializedObject, "ReductionTargets", true);
            DrawPropertiesExcluding(serializedObject, "m_Script", "ScopeRoot", "ApplyOnPlatforms", "TargetSelection", "ReductionTargets");
            serializedObject.ApplyModifiedProperties();
            var component = (ALTOMeshReduction)target;
            if (GUILayout.Button("Add Descriptor Renderers to Preserve"))
            {
                Undo.RecordObject(component, "Preserve descriptor renderers");
                component.AddDescriptorRenderers(); EditorUtility.SetDirty(component);
                PrefabUtility.RecordPrefabInstancePropertyModifications(component);
            }
            EditorGUILayout.HelpBox("The budget includes every surviving mesh in Scope Root, including disabled, unselected and preserved renderers. " +
                "Reduction runs after AAO cleanup. Mixed preserved/unselected inputs remain unchanged when another tool merges them. " +
                "Preserve renderers that use vertex-index or custom vertex-data shader effects.", MessageType.Info);
            if (MeshReductionBackend.Simplify == null)
                EditorGUILayout.HelpBox("Install Meshia Mesh Simplification 3.2 or a compatible 3.x version.", MessageType.Warning);
        }
    }
}
