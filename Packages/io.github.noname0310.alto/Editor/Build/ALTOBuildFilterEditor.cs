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
            EditorGUILayout.PropertyField(serializedObject.FindProperty("ScopeRoot"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("ApplyOnPlatforms"));
            if (component.FindDescriptor() == null)
                EditorGUILayout.HelpBox("Place this component inside an avatar with a VRC Avatar Descriptor.", MessageType.Error);
            if ((component.ApplyOnPlatforms & OptimizationBuildScope.Platform(EditorUserBuildSettings.activeBuildTarget)) == 0)
                EditorGUILayout.HelpBox("This filter is inactive for the current build target: " + EditorUserBuildSettings.activeBuildTarget + ".", MessageType.Info);
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Before Generators", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("ExcludedObjects"), true);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("ExcludedComponents"), true);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("RemoveAudioSources"));
            if (serializedObject.FindProperty("RemoveAudioSources").boolValue)
                using (new EditorGUI.IndentLevelScope())
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("PreserveAudioSources"), true);
            EditorGUILayout.HelpBox("Objects include their children. Remaining skinning, constraint and component references are checked before removal. " +
                "Preserved parameter sources cannot be excluded.", MessageType.None);
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("After Generators", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("BlendShapeExclusions"), true);
            EditorGUILayout.HelpBox("BlendShape Exclusions removes triangles touched by any selected morph frame, regardless of current weights or visibility. " +
                "An empty selection removes nothing. Missing morphs, removed targets, Cloth and animated mesh references stop the build. " +
                "ALTO Trace And Optimize Meshes can compact the unused vertex data afterwards.", MessageType.Info);
            EditorGUILayout.LabelField("Automatic Rendering Rules", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("RemoveAdditionalMaterialPasses"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("ExcludedRendererTypes"));
            if (serializedObject.FindProperty("RemoveAdditionalMaterialPasses").boolValue ||
                serializedObject.FindProperty("ExcludedRendererTypes").intValue != (int)RendererFilterTypes.None)
                using (new EditorGUI.IndentLevelScope())
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("PreserveAutomaticRenderers"), true);
            EditorGUILayout.HelpBox("These are explicit visual simplifications. Particle, line and trail rendering are not categorically unsupported on Android. " +
                "Referenced components are retained. Preserved renderers are exempt from automatic rules, but not explicit exclusions below.", MessageType.None);
            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("MaterialExclusions"), true);
            EditorGUILayout.HelpBox("Material Pass removes extra slots that draw the last submesh again. SubMesh Geometry removes the geometry and every pass drawing it. " +
                "Use Slot -1 to match all selected slots. An empty rule does nothing.", MessageType.Info);
            EditorGUILayout.HelpBox("Animated material swaps require an explicit slot when materials differ. Animated mesh references and conflicting mappings of shared clips are reported during the build. " +
                "This component changes build copies; it has no continuous preview.", MessageType.None);
            serializedObject.ApplyModifiedProperties();
            EditorGUILayout.Space();
            if (GUILayout.Button("Build Failure Diagnostics")) BuildFailureDiagnosticsWindow.Open(component);
        }
    }
}
