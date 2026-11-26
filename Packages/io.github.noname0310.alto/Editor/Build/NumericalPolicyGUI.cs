using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.Build
{
    internal static class NumericalPolicyGUI
    {
        private static readonly (NumericalOptimization Feature, GUIContent Label)[] choices =
        {
            (NumericalOptimization.AnimationOutputs, new GUIContent("Animation evaluation", "Allow ordinary roundoff when simplifying continuous animation outputs, including eligible normalized blends. Parameter and transition analysis stay exact.")),
            (NumericalOptimization.BlendShapes, new GUIContent("BlendShape baking", "Allow small deformation arithmetic differences when baking or combining BlendShapes.")),
            (NumericalOptimization.TransformMerging, new GUIContent("Transform merging", "Allow small reconstruction differences when removing static hierarchy frames and relocating their children.")),
            (NumericalOptimization.ComponentHoisting, new GUIContent("Component hoisting", "Allow small local-frame conversion differences when moving supported components to a parent or folding their local pose.")),
            (NumericalOptimization.Constraints, new GUIContent("Constraint optimization", "Allow small pose or offset reconstruction differences in supported constraint replacement and source simplification.")),
            (NumericalOptimization.MeshMerging, new GUIContent("Mesh merging", "Allow small differences in eligible renderer-frame equivalence checks. Other mesh, visibility and shader permissions remain independent.")),
            (NumericalOptimization.Skinning, new GUIContent("Skinning optimization", "Allow small arithmetic differences in static bone rebinding and rigid mesh conversion."))
        };

        internal static NumericalOptimization Draw(NumericalOptimization allowed)
        {
            EditorGUILayout.LabelField("Allow small numerical differences", EditorStyles.boldLabel);
            foreach (var choice in choices)
            {
                bool enabled = (allowed & choice.Feature) != 0;
                bool selected = EditorGUILayout.Toggle(choice.Label, enabled);
                if (selected != enabled) allowed = selected ? allowed | choice.Feature : allowed & ~choice.Feature;
            }
            return allowed;
        }

        internal static string Summary(NumericalOptimization allowed)
        {
            allowed &= NumericalOptimization.All;
            if (allowed == NumericalOptimization.All) return "All optimization features.";
            if (allowed == NumericalOptimization.None) return "None; use stricter proofs.";
            var selected = new List<string>();
            foreach (var choice in choices)
                if ((allowed & choice.Feature) != 0) selected.Add(choice.Label.text);
            return string.Join(", ", selected) + ".";
        }
    }
}
