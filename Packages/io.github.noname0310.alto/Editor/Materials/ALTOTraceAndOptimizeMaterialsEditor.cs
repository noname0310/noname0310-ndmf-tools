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
                "Analysis covers the whole avatar. Source assets, Material Variants, fallback inputs, external readers and protected/shared animation interfaces remain intact.", MessageType.Info);
            serializedObject.ApplyModifiedProperties();
        }
    }
}
