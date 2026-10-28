using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.Meshes
{
    [CustomEditor(typeof(ALTOMeshRendererSettingsOverride))]
    internal sealed class ALTOMeshRendererSettingsOverrideEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("ScopeRoot"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("ApplyOnPlatforms"));
            DrawSetting("OverrideProbeAnchor", "AnchorSelection");
            if (serializedObject.FindProperty("OverrideProbeAnchor").boolValue &&
                serializedObject.FindProperty("AnchorSelection").enumValueIndex == (int)MeshAnchorSelection.Explicit)
                using (new EditorGUI.IndentLevelScope())
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("AnchorOverride"));
            DrawSetting("OverrideLightProbeUsage", "LightProbes");
            DrawSetting("OverrideLightProbeProxyVolume", "LightProbeProxyVolume");
            DrawSetting("OverrideReflectionProbeUsage", "ReflectionProbes");
            DrawSetting("OverrideShadowCasting", "ShadowCasting");
            DrawSetting("OverrideReceiveShadows", "ReceiveShadows");
            DrawSetting("OverrideStaticShadowCaster", "StaticShadowCaster");
            EditorGUILayout.PropertyField(serializedObject.FindProperty("PreserveRenderers"), true);
            serializedObject.ApplyModifiedProperties();
            var component = (ALTOMeshRendererSettingsOverride)target;
            var scope = component.ScopeRoot != null ? component.ScopeRoot : component.transform;
            EditorGUILayout.HelpBox("All MeshRenderers and SkinnedMeshRenderers in '" + scope.name +
                "' are considered, including inactive and generated renderers. Only checked settings change on the selected platforms. " +
                "Existing animation remains active. Multiple components may select different fields or scopes; conflicting assignments stop the build.", MessageType.Info);
            if (component.OverrideProbeAnchor && component.AnchorSelection == MeshAnchorSelection.Automatic)
                EditorGUILayout.HelpBox("Automatic Anchor: use the most common retained anchor among included renderers. " +
                    "Ties use avatar-relative path order. If none exists, create one at their combined bounds center, attached to the avatar root.", MessageType.Info);
            if (component.OverrideProbeAnchor && component.AnchorSelection == MeshAnchorSelection.Explicit && component.AnchorOverride == null)
                EditorGUILayout.HelpBox("Explicit Anchor requires a transform. It does not fall back to automatic selection.", MessageType.Error);
        }

        private void DrawSetting(string flag, string value)
        {
            var enabled = serializedObject.FindProperty(flag);
            EditorGUILayout.PropertyField(enabled);
            if (enabled.boolValue)
                using (new EditorGUI.IndentLevelScope())
                    EditorGUILayout.PropertyField(serializedObject.FindProperty(value));
        }
    }
}
