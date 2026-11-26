using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Noname.AvatarTools.Analysis
{
    // Goal matching and explicit scalar evaluation have different default-write
    // contracts. Keep both queries in the shared motion analysis, preserving the
    // established callers while object-reference support uses the same snapshot.
    internal sealed partial class ConstantMotionOutput
    {
        internal static bool Matches(VirtualMotion motion, EditorCurveBinding binding, float value, bool defaultMatches,
            AnimatorReachability analysis, Dictionary<VirtualMotion, HashSet<EditorCurveBinding>> bindings,
            Dictionary<VirtualMotion, bool> cache, HashSet<VirtualMotion> stack, bool allowInterpolation = false,
            bool writeDefaults = false, ConstantOutputKind kind = ConstantOutputKind.Scalar, bool allowSmallNumericalDifferences = false)
        {
            if (kind == ConstantOutputKind.ObjectReference) return false;
            if (motion == null) return defaultMatches;
            if (cache.TryGetValue(motion, out bool cached)) return cached;
            if (!stack.Add(motion)) return false;
            bool result = false;
            if (motion is VirtualClip clip)
            {
                var curve = clip.GetFloatCurve(binding);
                result = curve == null ? defaultMatches :
                    MotionAnalysis.Constant(curve, out float sample, preserveZeroSign: kind != ConstantOutputKind.Discrete && !allowSmallNumericalDifferences) &&
                    SameOutput(sample, value, kind, allowSmallNumericalDifferences);
            }
            else if (motion is VirtualBlendTree tree && tree.Children.Count > 0)
            {
                if (tree.BlendType != BlendTreeType.Direct)
                {
                    if (!bindings.TryGetValue(tree, out var outputs))
                        bindings.Add(tree, outputs = MotionAnalysis.Bindings(tree));
                    var contributing = MotionAnalysis.Constant1DChildren(tree, analysis);
                    // An entirely unbound tree leaves the default untouched.
                    // A registered 2D scalar can retain some of the old/lower
                    // value. Its +0 invariant requires the caller to establish
                    // that default and every other contributor are also +0.
                    result = !outputs.Contains(binding) ? defaultMatches :
                        (!TwoDimensional(tree) || kind == ConstantOutputKind.Discrete || binding.isDiscreteCurve ||
                            allowSmallNumericalDifferences && defaultMatches && WellFormedTwoDimensional(tree, analysis) &&
                                HasInterpolationHeadroom(value, tree.Children.Count) ||
                            defaultMatches && MotionAnalysis.Same(value, 0)) &&
                        (kind == ConstantOutputKind.Discrete || allowSmallNumericalDifferences || !MotionAnalysis.NegativeZero(value) || contributing?.Length == 1) &&
                        (contributing?.AsEnumerable() ?? tree.Children).All(c =>
                            Matches(c.Motion, binding, value, defaultMatches, analysis, bindings, cache, stack, allowInterpolation, writeDefaults, kind, allowSmallNumericalDifferences));
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
                    bool retainedIsSafe = writeDefaults || defaultMatches && SameOutput(value, 0, kind, allowSmallNumericalDifferences) ||
                        !DirectReadsRetainedValue(tree, binding, analysis, bindings);
                    result = writers.Length == 0 ? defaultMatches && retainedIsSafe : writers.Length == 1 &&
                        retainedIsSafe && (kind == ConstantOutputKind.Discrete || allowSmallNumericalDifferences || !MotionAnalysis.NegativeZero(value)) &&
                        analysis.Values(writers[0].DirectBlendParameter).TryGetConstant(out float weight) && weight == 1 &&
                        Matches(writers[0].Motion, binding, value, defaultMatches, analysis, bindings, cache, stack, allowInterpolation, writeDefaults, kind, allowSmallNumericalDifferences);
                }
            }
            if (!result && kind == ConstantOutputKind.Scalar && allowInterpolation && TryEvaluateScalar(motion, binding,
                defaultMatches ? value : (float?)null, analysis, out float interpolated, writeDefaults, bindings)) result = MotionAnalysis.Same(interpolated, value);
            stack.Remove(motion); cache[motion] = result; return result;
        }

        // Serialized Boolean/integer consumers do not retain a floating zero
        // sign. Callers must establish that type; FloatCurve alone does not.
        private static bool SameOutput(float first, float second, ConstantOutputKind kind, bool allowSmallNumericalDifferences = false) =>
            kind == ConstantOutputKind.Discrete || allowSmallNumericalDifferences ? first == second : MotionAnalysis.Same(first, second);

        internal static bool TryEvaluateScalar(VirtualMotion motion, EditorCurveBinding binding, float? defaultValue,
            AnimatorReachability analysis, out float value, bool writeDefaults = false,
            Dictionary<VirtualMotion, HashSet<EditorCurveBinding>> bindings = null)
        {
            value = 0;
            if (binding.isDiscreteCurve || binding.isPPtrCurve || analysis.FallbackReason != null ||
                defaultValue.HasValue && !MotionAnalysis.Finite(defaultValue.Value)) return false;
            if (binding.type != null && typeof(Transform).IsAssignableFrom(binding.type) &&
                !binding.propertyName.StartsWith("m_LocalPosition.", System.StringComparison.Ordinal) &&
                !binding.propertyName.StartsWith("m_LocalScale.", System.StringComparison.Ordinal)) return false;
            var cache = new Dictionary<VirtualMotion, (bool Known, float Value)>();
            var stack = new HashSet<VirtualMotion>();
            // Binding footprints describe immutable motion structure, independent
            // of the queried scalar/default. Reuse the caller's same-snapshot
            // facts; the value cache stays private to this query and WD mode.
            bindings ??= new Dictionary<VirtualMotion, HashSet<EditorCurveBinding>>();

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
                            known = MotionAnalysis.Finite(result) && !MotionAnalysis.NegativeZero(result);
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
                        if (writers.Length == 0)
                        {
                            known = Evaluate(null, out result) && (writeDefaults || defaultValue.HasValue && MotionAnalysis.Same(defaultValue.Value, 0) ||
                                !DirectReadsRetainedValue(tree, binding, analysis, bindings));
                        }
                        else if (writers.Length == 1 && !writers[0].Mirror &&
                            MotionAnalysis.Finite(writers[0].TimeScale) && MotionAnalysis.Finite(writers[0].CycleOffset) &&
                            analysis.Values(writers[0].DirectBlendParameter).TryGetConstant(out float weight) && weight == 1)
                        {
                            known = Evaluate(writers[0].Motion, out result) && !MotionAnalysis.NegativeZero(result);
                            if (known && !writeDefaults && DirectReadsRetainedValue(tree, binding, analysis, bindings) &&
                                !(defaultValue.HasValue && MotionAnalysis.Same(defaultValue.Value, 0) && MotionAnalysis.Same(result, 0))) known = false;
                        }
                    }
                }
                stack.Remove(current); cache[current] = (known, result); return known;
            }

            return Evaluate(motion, out value);
        }
    }
}
