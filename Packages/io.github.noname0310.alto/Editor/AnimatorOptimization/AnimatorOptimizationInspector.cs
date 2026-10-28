using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.AnimatorOptimization
{
    [CustomEditor(typeof(ALTOTraceAndOptimizeAnimator))]
    internal sealed class AnimatorOptimizationInspector : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("RemoveUnreachableStates"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("RemoveUnusedParameterCurves"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("RemoveUnusedParameterDeclarations"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("SimplifyUnusedStateLogic"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("SimplifyConstantBlendTrees"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("MergeSameWeightClips"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("BakeConstantMaterialProperties"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("RemoveUnusedMaterialProperties"));
            serializedObject.ApplyModifiedProperties();
            if (((ALTOTraceAndOptimizeAnimator)target).FindDescriptor() == null)
                EditorGUILayout.HelpBox("Place this component inside an avatar with a VRC Avatar Descriptor.", MessageType.Error);
            EditorGUILayout.HelpBox("Optimizes the entire avatar's generated Animator controllers during NDMF builds. " +
                "Expression parameters, platform inputs, Contacts, PhysBones and Parameter Drivers participate in analysis. " +
                "Original controllers, animation clips and materials are preserved.", MessageType.Info);
            EditorGUILayout.HelpBox("Unsupported behaviours and synchronized layers disable graph analysis. " +
                "Structural Blend Tree rewrites require static clips, Write Defaults On and compatible weights. " +
                "Material baking supports Float/Range properties; Color and Vector channels are preserved. " +
                "Unused material tracks are removed only as complete properties after checking shader sources, fallbacks and material swaps. " +
                "Dead state logic can simplify dynamic trees when their implicit defaults are preserved. " +
                "Layer indices and public parameter definitions are retained. Optimization counts are printed in the Console.", MessageType.None);
        }
    }
}
