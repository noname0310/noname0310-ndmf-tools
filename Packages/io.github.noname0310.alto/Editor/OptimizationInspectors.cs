using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.BlendShapeOptimization
{
    [CustomEditor(typeof(ALTOTraceAndOptimizeBlendShapes))]
    internal sealed class ALTOTraceAndOptimizeBlendShapesEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("ScopeRoot"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("PreserveMmdBlendShapes"), new GUIContent("Preserve MMD BlendShapes"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("OptimizeZeroResetAnimations"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("IgnoreUnreachableStates"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("ExcludedAnimations"), true);
            if (serializedObject.FindProperty("ExcludedAnimations").arraySize > 0)
                EditorGUILayout.HelpBox("Excluded animations do not prevent BlendShape optimization in this scope. " +
                    "Their bindings to frozen or merged shapes are removed from the build copies, so those facial motions may change. " +
                    "Other tracks and protected shapes are kept. Assign the original clip assets.", MessageType.Warning);
            serializedObject.ApplyModifiedProperties();
            var component = (ALTOTraceAndOptimizeBlendShapes)target;
            var descriptor = component.FindDescriptor();
            if (descriptor == null)
                EditorGUILayout.HelpBox("Place this component inside an avatar with a VRC Avatar Descriptor.", MessageType.Error);
            else if (component.ScopeRoot != null && !component.ScopeRoot.IsChildOf(descriptor.transform))
                EditorGUILayout.HelpBox("Scope Root must belong to the same avatar.", MessageType.Error);
            EditorGUILayout.HelpBox("An empty Scope Root uses this object's subtree. Animation analysis always covers the entire avatar. " +
                "Unanimated shapes are baked at their current weights. Shapes animated only to zero are grouped by shared clip usage, " +
                "with their current proportions stored in a new shape at weight 100. Changes apply during NDMF builds.", MessageType.Info);
            EditorGUILayout.HelpBox("Use BlendShape Optimization Exclude on a Skinned Mesh Renderer to preserve individual shapes. " +
                "Descriptor eyelids and visemes are preserved automatically. Additive and nonlinear animated shapes are kept.", MessageType.None);
        }
    }

    [CustomEditor(typeof(BlendShapeOptimizationExclude))]
    internal sealed class BlendShapeOptimizationExcludeEditor : UnityEditor.Editor
    {
        private string search = "";
        private Vector2 scroll;
        public override void OnInspectorGUI()
        {
            var component = (BlendShapeOptimizationExclude)target;
            var renderer = component.GetComponent<SkinnedMeshRenderer>();
            var mesh = renderer != null ? renderer.sharedMesh : null;
            serializedObject.Update();
            var property = serializedObject.FindProperty("BlendShapes");
            if (mesh == null)
            {
                EditorGUILayout.HelpBox("Assign a mesh to this object's Skinned Mesh Renderer.", MessageType.Info);
                EditorGUILayout.PropertyField(property);
                serializedObject.ApplyModifiedProperties();
                return;
            }
            var selected = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < property.arraySize; i++) selected.Add(property.GetArrayElementAtIndex(i).stringValue);
            var names = Enumerable.Range(0, mesh.blendShapeCount).Select(mesh.GetBlendShapeName).ToArray();
            EditorGUILayout.LabelField("Preserve selected BlendShapes", EditorStyles.boldLabel);
            search = EditorGUILayout.TextField("Search", search);
            bool changed = false;
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Select All")) { selected.UnionWith(names); changed = true; }
                if (GUILayout.Button("Clear")) { selected.Clear(); changed = true; }
            }
            scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.MaxHeight(350));
            foreach (var name in names.Concat(selected.Except(names).OrderBy(n => n, StringComparer.Ordinal)).ToArray())
            {
                if (name.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0) continue;
                bool previous = selected.Contains(name);
                bool current = EditorGUILayout.ToggleLeft(mesh.GetBlendShapeIndex(name) < 0 ? name + " (missing)" : name, previous);
                if (previous == current) continue;
                if (current) selected.Add(name); else selected.Remove(name);
                changed = true;
            }
            EditorGUILayout.EndScrollView();
            if (changed)
            {
                var values = selected.OrderBy(n => n, StringComparer.Ordinal).ToArray();
                property.arraySize = values.Length;
                for (int i = 0; i < values.Length; i++) property.GetArrayElementAtIndex(i).stringValue = values[i];
            }
            serializedObject.ApplyModifiedProperties();
            EditorGUILayout.HelpBox("Selections are stored by name. Missing names are retained when the mesh changes. " +
                "These names are also protected from automatic freezing when Avatar Optimizer is installed.", MessageType.None);
        }
    }
}
