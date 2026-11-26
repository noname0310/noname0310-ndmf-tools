using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.Build
{
    [CustomEditor(typeof(ALTOOptimizationPolicy))]
    internal sealed class ALTOOptimizationPolicyEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("ScopeRoot"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("ApplyOnPlatforms"));
            var numerical = serializedObject.FindProperty("AllowedNumericalDifferences");
            numerical.intValue = (int)NumericalPolicyGUI.Draw((NumericalOptimization)numerical.intValue);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("AllowInitialValueChanges"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("AllowShaderConstantFolding"));
            serializedObject.ApplyModifiedProperties();
            EditorGUILayout.HelpBox("Controls shared numerical equivalence and initialization assumptions. " +
                "Each feature allows small differences at its continuous-output boundary by default. Clear an item to request stricter proofs for that feature. " +
                "Parameter, control-flow, index and invalid-value analysis remain exact. " +
                "Without an applicable policy, new baking operations must preserve authored starting values. " +
                "The corresponding Trace And Optimize option must also be enabled.", MessageType.Info);
        }
    }
}
