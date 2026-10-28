using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEditor.Animations;

namespace Noname.AvatarTools.Analysis
{
    internal static class ConstantPropertyOutput
    {
        // Shared by material and object output folding. Missing tracks retain the
        // initial value; a constant-valued track alone is not a constant-output proof.
        internal static bool Matches(VirtualMotion motion, EditorCurveBinding binding, float value, bool defaultMatches,
            AnimatorReachability analysis, Dictionary<VirtualMotion, HashSet<EditorCurveBinding>> bindings,
            Dictionary<VirtualMotion, bool> cache, HashSet<VirtualMotion> stack, bool allowInterpolation = false)
        {
            if (motion == null) return defaultMatches;
            if (cache.TryGetValue(motion, out bool cached)) return cached;
            if (!stack.Add(motion)) return false;
            bool result = false;
            if (motion is VirtualClip clip)
            {
                var curve = clip.GetFloatCurve(binding);
                result = curve == null ? defaultMatches : MotionAnalysis.Constant(curve, out float sample) && sample == value;
            }
            else if (motion is VirtualBlendTree tree && tree.Children.Count > 0)
            {
                if (tree.BlendType != BlendTreeType.Direct)
                {
                    IEnumerable<VirtualBlendTree.VirtualChildMotion> contributing = MotionAnalysis.Constant1DChildren(tree, analysis);
                    result = (contributing ?? tree.Children).All(c =>
                        Matches(c.Motion, binding, value, defaultMatches, analysis, bindings, cache, stack, allowInterpolation));
                }
                else if (!tree.NormalizedBlendValues)
                {
                    var writers = tree.Children.Where(c =>
                    {
                        if (c.Motion == null) return false;
                        if (!bindings.TryGetValue(c.Motion, out var set)) bindings.Add(c.Motion, set = MotionAnalysis.Bindings(c.Motion));
                        // A zero-weight child contributes the property's default,
                        // even if its stored curve is nonconstant or differs from
                        // that default. Keep the tree and its other bindings intact.
                        return set.Contains(binding) &&
                            (!analysis.Values(c.DirectBlendParameter).TryGetConstant(out float weight) || weight != 0);
                    }).ToArray();
                    result = writers.Length == 0 ? defaultMatches : writers.Length == 1 &&
                        analysis.Values(writers[0].DirectBlendParameter).TryGetConstant(out float weight) && weight == 1 &&
                        Matches(writers[0].Motion, binding, value, defaultMatches, analysis, bindings, cache, stack, allowInterpolation);
                }
            }
            if (!result && allowInterpolation && ConstantScalarOutput.TryEvaluate(motion, binding,
                defaultMatches ? value : (float?)null, analysis, out float interpolated)) result = interpolated == value;
            stack.Remove(motion); cache[motion] = result; return result;
        }
    }
}
