using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Noname.AvatarTools.Analysis
{
    internal sealed class InvariantComponentSettings
    {
        private readonly AvatarAnimationIndex index;
        private readonly AnimatorReachability analysis;
        private readonly Dictionary<VirtualClip, HashSet<VirtualLayer>> contexts = new();
        private readonly HashSet<VirtualLayer> additiveLayers;

        internal InvariantComponentSettings(AvatarAnimationIndex index, AnimatorReachability analysis,
            IDictionary<object, VirtualAnimatorController> controllers)
        {
            this.index = index; this.analysis = analysis;
            additiveLayers = controllers.Where(e => e.Key is VRCAvatarDescriptor.AnimLayerType type &&
                    type == VRCAvatarDescriptor.AnimLayerType.Additive).SelectMany(e => e.Value.Layers).ToHashSet();
            foreach (var controller in index.Controllers)
            foreach (var layer in controller.Layers)
            foreach (var clip in layer.AllReachableNodes().OfType<VirtualClip>())
            {
                if (!contexts.TryGetValue(clip, out var layers)) contexts.Add(clip, layers = new());
                layers.Add(layer);
            }
        }

        internal bool IsConstant(Component component)
            => IsConstant(component, index.ForObject(component));

        internal bool IsConstantProperty(Component component, string propertyName)
            => IsConstant(component, index.ForObject(component).Where(u => u.Binding.propertyName == propertyName).ToArray());

        private bool IsConstant(Component component, IReadOnlyList<AvatarAnimationIndex.BindingUse> uses)
        {
            if (uses.Count == 0) return true;
            if (uses.Any(u => u.Immutable || !u.Complete || u.Binding.isPPtrCurve ||
                u.Objects().Count() != 1 || u.Objects().Single() != component)) return false;
            using var serialized = new SerializedObject(component);
            var bindings = new Dictionary<VirtualMotion, HashSet<EditorCurveBinding>>();
            foreach (var group in uses.GroupBy(u => u.Binding.propertyName))
            {
                if (group.Select(u => u.Binding).Distinct().Count() != 1) return false;
                var property = serialized.FindProperty(group.Key);
                if (property == null) return false;
                float initial;
                switch (property.propertyType)
                {
                    case SerializedPropertyType.Boolean: initial = property.boolValue ? 1 : 0; break;
                    case SerializedPropertyType.Float: initial = property.floatValue; break;
                    case SerializedPropertyType.Integer: case SerializedPropertyType.Enum: initial = property.intValue; break;
                    default: return false;
                }
                if (!MotionAnalysis.Finite(initial)) return false;
                var cache = new Dictionary<VirtualMotion, bool>();
                foreach (var layer in group.SelectMany(u => contexts[u.Clip]).Distinct())
                {
                    if (layer.AvatarMask != null || layer.SyncedLayerIndex != -1 || additiveLayers.Contains(layer) ||
                        layer.BlendingMode != AnimatorLayerBlendingMode.Override) return false;
                    var states = layer.AllReachableNodes().OfType<VirtualState>().Where(analysis.IsReachable).ToArray();
                    if (states.Length == 0 || states.Any(s => !ConstantPropertyOutput.Matches(s.Motion, group.First().Binding,
                        initial, true, analysis, bindings, cache, new(), property.propertyType == SerializedPropertyType.Float))) return false;
                }
            }
            return true;
        }

        internal Action PrepareConstantCurveRemoval(Component component)
        {
            var curves = index.ForObject(component).GroupBy(u => u.Clip)
                .Select(g => (Clip: g.Key, Bindings: g.Select(u => u.Binding).ToArray())).ToArray();
            return () =>
            {
                foreach (var group in curves) MotionAnalysis.RemoveCurves(group.Clip, group.Bindings);
            };
        }
    }
}
