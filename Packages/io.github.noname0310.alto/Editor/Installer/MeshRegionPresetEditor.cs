using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.Installer
{
    [CustomEditor(typeof(MeshRegionPreset))]
    internal sealed class MeshRegionPresetEditor : Editor
    {
        [NonSerialized] private Mesh referenceMesh;
        private string message;

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            EditorGUILayout.Space(10);
            EditorGUILayout.HelpBox("Enter the BlendShape regions to remove, then capture a reference mesh fingerprint. Only hashes and region names are stored. The reference mesh and its geometry are not saved in this preset.", MessageType.Info);
            referenceMesh = (Mesh)EditorGUILayout.ObjectField("Reference mesh (not saved)", referenceMesh, typeof(Mesh), false);
            using (new EditorGUI.DisabledScope(referenceMesh == null))
                if (GUILayout.Button("Add Reference Fingerprint"))
                {
                    var preset = (MeshRegionPreset)target;
                    var fingerprint = MeshRegionPresetDiscovery.Fingerprint(referenceMesh);
                    if (!MeshRegionPresetDiscovery.HasRegions(referenceMesh, preset)) message = "The reference mesh must contain every selected BlendShape region.";
                    else if (fingerprint == null) message = "Choose a readable mesh with finite base positions.";
                    else if (preset.Fingerprints.Any(f => f != null && f.PositionsSha256 == fingerprint.PositionsSha256)) message = "This reference is already registered.";
                    else
                    {
                        Undo.RecordObject(preset, "Add mesh region fingerprint");
                        preset.Fingerprints.Add(fingerprint);
                        EditorUtility.SetDirty(preset);
                        message = "Fingerprint added. No geometry or mesh reference was stored.";
                    }
                }
            if (!string.IsNullOrEmpty(message)) EditorGUILayout.HelpBox(message, MessageType.Info);
        }
    }
}
