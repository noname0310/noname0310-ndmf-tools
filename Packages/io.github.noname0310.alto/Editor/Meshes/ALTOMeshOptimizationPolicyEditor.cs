using System.Linq;
using Noname.AvatarTools.Build;
using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.Meshes
{
    [CustomEditor(typeof(ALTOMeshOptimizationPolicy))]
    internal sealed class ALTOMeshOptimizationPolicyEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var component = (ALTOMeshOptimizationPolicy)target;
            EditorGUILayout.PropertyField(serializedObject.FindProperty("ScopeRoot"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("ApplyOnPlatforms"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("AllowCoordinateSpaceChanges"));
            if (serializedObject.FindProperty("AllowCoordinateSpaceChanges").boolValue)
                using (new EditorGUI.IndentLevelScope())
                {
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("ConvertRigidMeshes"));
                    EditorGUILayout.HelpBox("Object-space shader effects can change. Independent toggles, world geometry and other compatibility checks remain protected. " +
                        "Merging different coordinate roots also requires Expand Bounds on Trace And Optimize Meshes.", MessageType.Warning);
                }
            EditorGUILayout.PropertyField(serializedObject.FindProperty("AllowTransparentSortingChanges"));
            if (serializedObject.FindProperty("AllowTransparentSortingChanges").boolValue)
                EditorGUILayout.HelpBox("Merged transparent meshes lose independent camera-distance sorting, including relative to other renderers. " +
                    "Transparency and overlapping surfaces can change. Sorting layer/order and independent toggles remain protected. " +
                    "Material Slot Reordering is a separate permission.", MessageType.Warning);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("AllowShaderIndexChanges"));
            if (serializedObject.FindProperty("AllowShaderIndexChanges").boolValue)
                EditorGUILayout.HelpBox("Mesh IDs and skinning inputs are no longer required to keep their original shader meaning. " +
                    "Index-based masks, deformation and other effects can change, including on shaders the analyzer cannot verify. " +
                    "Animation, material defaults, sorting and coordinate-space checks keep their separate requirements.", MessageType.Warning);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("AllowMaterialSlotReordering"));
            if (serializedObject.FindProperty("AllowMaterialSlotReordering").boolValue)
                EditorGUILayout.HelpBox("Equal materials can merge across intervening slots. The first material stays first, but overlapping surfaces, stencil and depth effects can change.", MessageType.Warning);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("AllowAnimatedMaterialDefaultChanges"));
            if (serializedObject.FindProperty("AllowAnimatedMaterialDefaultChanges").boolValue)
                EditorGUILayout.HelpBox("Renderers with matching material animation use the retained renderer's starting values. " +
                    "Appearance can change at zero or partial animation weight and when defaults are restored. " +
                    "Independent animation and different unanimated channels still prevent merging. Changed values are listed in the build report.", MessageType.Warning);
            var descriptor = component.FindDescriptor();
            if (descriptor == null)
                EditorGUILayout.HelpBox("Place this component inside an avatar with a VRC Avatar Descriptor.", MessageType.Error);
            else if (!descriptor.GetComponentsInChildren<ALTOTraceAndOptimizeMeshes>(true).Any(c => c.enabled))
                EditorGUILayout.HelpBox("This policy requires an enabled ALTO Trace And Optimize Meshes component. It does not run mesh optimization by itself.", MessageType.Info);
            if ((component.ApplyOnPlatforms & OptimizationBuildScope.Platform(EditorUserBuildSettings.activeBuildTarget)) == 0)
                EditorGUILayout.HelpBox("This policy is inactive for the current build target: " + EditorUserBuildSettings.activeBuildTarget + ".", MessageType.Info);
            EditorGUILayout.HelpBox("Applies only within the optimizer's scope. A more-specific policy replaces broader policies, including when its options are off. " +
                "Policies with the same scope must agree on the active platform. Merged outputs retain only permissions shared by every input. " +
                "Preserve Renderers, animation and external-reference checks still apply. Shader-index proof is required unless explicitly allowed above.", MessageType.None);
            serializedObject.ApplyModifiedProperties();
        }
    }
}
