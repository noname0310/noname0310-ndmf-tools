using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Noname.AvatarTools.Analysis
{
    // Callers must establish continuous scalar semantics. Transform quaternion,
    // discrete and object-reference curves do not use this interpolation proof.
    internal static class ConstantScalarOutput
    {
        internal static bool TryEvaluate(VirtualMotion motion, EditorCurveBinding binding, float? defaultValue,
            AnimatorReachability analysis, out float value)
        {
            value = 0;
            if (binding.isDiscreteCurve || binding.isPPtrCurve || analysis.FallbackReason != null ||
                defaultValue.HasValue && !MotionAnalysis.Finite(defaultValue.Value)) return false;
            if (binding.type != null && typeof(Transform).IsAssignableFrom(binding.type) &&
                !binding.propertyName.StartsWith("m_LocalPosition.", System.StringComparison.Ordinal) &&
                !binding.propertyName.StartsWith("m_LocalScale.", System.StringComparison.Ordinal)) return false;
            var cache = new Dictionary<VirtualMotion, (bool Known, float Value)>();
            var stack = new HashSet<VirtualMotion>();
            var bindings = new Dictionary<VirtualMotion, HashSet<EditorCurveBinding>>();

            bool Evaluate(VirtualMotion current, out float result)
            {
                result = 0;
                if (current == null)
                {
                    result = defaultValue.GetValueOrDefault();
                    return defaultValue.HasValue;
                }
                if (cache.TryGetValue(current, out var stored)) { result = stored.Value; return stored.Known; }
                if (!stack.Add(current)) return false;
                bool known = false;
                if (current is VirtualClip clip)
                {
                    var source = MotionAnalysis.Source(clip);
                    if (!clip.IsMarkerClip && !clip.Legacy && clip.AdditiveReferencePoseClip == null &&
                        source != null && !source.humanMotion && source.events.Length == 0)
                    {
                        var curve = clip.GetFloatCurve(binding);
                        known = curve == null ? Evaluate(null, out result) : MotionAnalysis.Constant(curve, out result);
                    }
                }
                else if (current is VirtualBlendTree tree)
                {
                    var children = MotionAnalysis.Constant1DChildren(tree, analysis);
                    if (children?.Length == 1) known = Evaluate(children[0].Motion, out result);
                    else if (children?.Length == 2 && Evaluate(children[0].Motion, out float left) &&
                        Evaluate(children[1].Motion, out float right) &&
                        analysis.Values(tree.BlendParameter).TryGetConstant(out float position))
                    {
                        float interval = children[1].Threshold - children[0].Threshold;
                        float offset = position - children[0].Threshold;
                        float weight = offset / interval;
                        if (MotionAnalysis.Finite(interval) && interval > 0 && MotionAnalysis.Finite(offset) &&
                            MotionAnalysis.Finite(weight) && weight > 0 && weight < 1)
                        {
                            result = left * (1 - weight) + right * weight;
                            known = MotionAnalysis.Finite(result);
                        }
                    }
                    else if (tree.BlendType == BlendTreeType.Direct && !tree.NormalizedBlendValues)
                    {
                        var writers = tree.Children.Where(c =>
                        {
                            if (c.Motion == null) return false;
                            if (!bindings.TryGetValue(c.Motion, out var set)) bindings.Add(c.Motion, set = MotionAnalysis.Bindings(c.Motion));
                            return set.Contains(binding) &&
                                (!analysis.Values(c.DirectBlendParameter).TryGetConstant(out float w) || w != 0);
                        }).ToArray();
                        if (writers.Length == 0) known = Evaluate(null, out result);
                        else if (writers.Length == 1 && !writers[0].Mirror &&
                            MotionAnalysis.Finite(writers[0].TimeScale) && MotionAnalysis.Finite(writers[0].CycleOffset) &&
                            analysis.Values(writers[0].DirectBlendParameter).TryGetConstant(out float weight) && weight == 1)
                            known = Evaluate(writers[0].Motion, out result);
                    }
                }
                stack.Remove(current); cache[current] = (known, result); return known;
            }

            return Evaluate(motion, out value);
        }
    }
}
