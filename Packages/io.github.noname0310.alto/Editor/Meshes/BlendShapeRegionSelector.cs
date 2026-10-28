using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.Meshes
{
    internal sealed class BlendShapeRegionSelector : PopupWindowContent
    {
        private readonly UnityEngine.Object owner;
        private readonly string path;
        private string search = "";
        private Vector2 scroll;
        internal BlendShapeRegionSelector(UnityEngine.Object owner, string path) { this.owner = owner; this.path = path; }
        public override Vector2 GetWindowSize() => new(380, 430);

        public override void OnGUI(Rect rect)
        {
            if (owner == null) { editorWindow.Close(); return; }
            using var serialized = new SerializedObject(owner);
            var target = serialized.FindProperty(path);
            if (target == null) { editorWindow.Close(); return; }
            var mesh = (target.FindPropertyRelative("Renderer").objectReferenceValue as SkinnedMeshRenderer)?.sharedMesh;
            if (mesh == null) { editorWindow.Close(); return; }
            var values = target.FindPropertyRelative("BlendShapes");
            var selected = new HashSet<string>(Enumerable.Range(0, values.arraySize).Select(i => values.GetArrayElementAtIndex(i).stringValue), StringComparer.Ordinal);
            var names = Enumerable.Range(0, mesh.blendShapeCount).Select(mesh.GetBlendShapeName).ToArray();
            search = EditorGUILayout.TextField("Search", search);
            var visible = names.Where(n => n.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
            bool changed = false;
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Select Filtered")) { selected.UnionWith(visible); changed = true; }
            if (GUILayout.Button("Clear Filtered")) { selected.ExceptWith(visible); changed = true; }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.LabelField(selected.Count + " regions selected", EditorStyles.miniLabel);
            scroll = EditorGUILayout.BeginScrollView(scroll);
            foreach (string name in visible.Concat(selected.Except(names)).ToArray())
            {
                bool old = selected.Contains(name);
                bool next = EditorGUILayout.ToggleLeft(name + (Array.IndexOf(names, name) < 0 ? " (missing)" : ""), old);
                if (old == next) continue;
                if (next) selected.Add(name); else selected.Remove(name);
                changed = true;
            }
            EditorGUILayout.EndScrollView();
            if (!changed) return;
            var ordered = names.Where(selected.Contains).Concat(selected.Except(names)).ToArray();
            values.arraySize = ordered.Length;
            for (int i = 0; i < ordered.Length; i++) values.GetArrayElementAtIndex(i).stringValue = ordered[i];
            serialized.ApplyModifiedProperties();
        }
    }
}
