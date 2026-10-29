using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.Meshes
{
    [CustomEditor(typeof(ALTOTraceAndOptimizeMeshes))]
    internal sealed class ALTOTraceAndOptimizeMeshesEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("ScopeRoot"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("MergeCompatibleSkinnedMeshes"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("ExpandBounds"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("MergeIdenticalMaterialSlots"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("RemoveUnusedMeshData"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("RebindStaticBones"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("PreserveRenderers"), true);
            var component = (ALTOTraceAndOptimizeMeshes)target;
            var scope = component.ScopeRoot != null ? component.ScopeRoot : component.transform;
            EditorGUILayout.HelpBox("All skinned renderers in '" + scope.name + "' are considered, including inactive and build-generated renderers. " +
                "Analysis covers the whole avatar. Compatible groups merge after triangle reduction; preserved face geometry can join the final mesh. " +
                "Different visibility, Animator masks, lighting, unsupported bindings or external readers keep groups separate. " +
                "Use ALTO Mesh Optimization Policy to allow coordinate-space changes, rigid conversion or material-slot reordering on selected platforms and scopes. " +
                "Without a matching policy, only conservative optimizations run. See the build report for reasons.", MessageType.Info);
            serializedObject.ApplyModifiedProperties();
        }
    }
}
