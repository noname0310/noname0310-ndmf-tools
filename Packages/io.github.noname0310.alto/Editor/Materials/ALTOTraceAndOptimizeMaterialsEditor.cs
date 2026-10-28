using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.Materials
{
    [CustomEditor(typeof(ALTOTraceAndOptimizeMaterials))]
    internal sealed class ALTOTraceAndOptimizeMaterialsEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update(); DrawPropertiesExcluding(serializedObject, "m_Script");
            var settings = (ALTOTraceAndOptimizeMaterials)target;
            var scope = settings.ScopeRoot != null ? settings.ScopeRoot : settings.transform;
            EditorGUILayout.HelpBox("Mesh and skinned renderers in '" + scope.name + "', including generated renderers and safe material-swap clips, are considered. " +
                "Analysis covers the whole avatar. Source assets, Material Variants, external readers and protected/shared animation interfaces remain intact.", MessageType.Info);
            if (serializedObject.FindProperty("BakeLilToonMainTextures").boolValue)
                EditorGUILayout.HelpBox("lilToon baking applies on PC and mobile. Static color correction and supported UV0 secondary layers use generated textures. " +
                    "Animated settings, custom shaders and unsupported effects are retained. Texture filtering, mipmaps and Safety Fallback appearance can differ. " +
                    "The build report lists baked and retained inputs.", MessageType.Info);
            serializedObject.ApplyModifiedProperties();
        }
    }
}
