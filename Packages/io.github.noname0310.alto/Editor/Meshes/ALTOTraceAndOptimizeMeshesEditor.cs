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
            EditorGUILayout.PropertyField(serializedObject.FindProperty("AllowCoordinateSpaceChanges"));
            if (serializedObject.FindProperty("AllowCoordinateSpaceChanges").boolValue)
                using (new EditorGUI.IndentLevelScope())
                {
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("CoordinateSpaceChangesOnPlatforms"));
                    if (serializedObject.FindProperty("MergeCompatibleSkinnedMeshes").boolValue)
                        EditorGUILayout.PropertyField(serializedObject.FindProperty("ConvertRigidMeshes"));
                    EditorGUILayout.HelpBox("Coordinate-space changes can alter object-space shader effects. World geometry and separate toggles remain protected. " +
                        "Enable Expand Bounds to combine different coordinate roots; the union contains the input culling volumes at build time.", MessageType.Warning);
                }
            if (!serializedObject.FindProperty("MergeCompatibleSkinnedMeshes").boolValue)
                EditorGUILayout.HelpBox("Bounds and coordinate settings still affect which renderers qualify for material-slot, unused-data and static-bone optimization. " +
                    "Rigid mesh conversion requires renderer merging.", MessageType.None);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("MergeIdenticalMaterialSlots"));
            if (serializedObject.FindProperty("MergeIdenticalMaterialSlots").boolValue)
                using (new EditorGUI.IndentLevelScope())
                {
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("AllowMaterialSlotReordering"));
                    if (serializedObject.FindProperty("AllowMaterialSlotReordering").boolValue)
                    {
                        EditorGUILayout.PropertyField(serializedObject.FindProperty("MaterialSlotReorderingOnPlatforms"));
                        EditorGUILayout.HelpBox("Material slots with the same material can merge across intervening slots. " +
                            "The first material stays first. Overlapping surfaces, stencil and depth effects can change when draw order changes.", MessageType.Warning);
                    }
                }
            EditorGUILayout.PropertyField(serializedObject.FindProperty("RemoveUnusedMeshData"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("RebindStaticBones"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("PreserveRenderers"), true);
            var component = (ALTOTraceAndOptimizeMeshes)target;
            var scope = component.ScopeRoot != null ? component.ScopeRoot : component.transform;
            EditorGUILayout.HelpBox("All skinned renderers in '" + scope.name + "' are considered, including inactive and build-generated renderers. " +
                "Analysis covers the whole avatar. Compatible groups merge after triangle reduction; preserved face geometry can join the final mesh. " +
                "Different visibility, Animator masks, lighting, unsupported bindings or external readers keep groups separate. " +
                "Bounds and coordinate-system differences follow the selected policy. See the build report for reasons.", MessageType.Info);
            serializedObject.ApplyModifiedProperties();
        }
    }
}
