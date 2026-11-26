using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Noname.AvatarTools.Analysis
{
    // One property query against an immutable animation snapshot. This evaluates
    // explicit output only; a missing track must not silently expose a guessed
    // scene value or a lower layer. Native/component writers are a caller contract.
    internal sealed partial class ConstantMotionOutput
    {
        private readonly EditorCurveBinding binding;
        private readonly ConstantOutputKind kind;
        private readonly AnimatorReachability analysis;
        private readonly bool writeDefaults;
        private readonly bool allowSmallNumericalDifferences;
        private readonly float? invariantValue;
        private readonly HashSet<VirtualMotion> rounded = new();
        private readonly Dictionary<VirtualMotion, (bool Known, ConstantOutputValue Value)> values = new();
        private readonly HashSet<VirtualMotion> visiting = new();
        private readonly Dictionary<VirtualMotion, HashSet<EditorCurveBinding>> bindings;

        internal ConstantMotionOutput(EditorCurveBinding binding, ConstantOutputKind kind,
            AnimatorReachability analysis, Dictionary<VirtualMotion, HashSet<EditorCurveBinding>> bindings, bool writeDefaults,
            bool allowSmallNumericalDifferences = false, float? invariantValue = null)
        {
            this.binding = binding; this.kind = kind; this.analysis = analysis; this.bindings = bindings; this.writeDefaults = writeDefaults;
            this.allowSmallNumericalDifferences = allowSmallNumericalDifferences && kind == ConstantOutputKind.Scalar;
            this.invariantValue = invariantValue;
        }

        internal bool UsesVendorRoundoff(VirtualMotion motion) => motion != null && rounded.Contains(motion);

        internal bool TryEvaluate(VirtualMotion motion, out ConstantOutputValue value)
        {
            value = default;
            if (motion == null || analysis.FallbackReason != null) return false;
            if (values.TryGetValue(motion, out var cached)) { value = cached.Value; return cached.Known; }
            if (!visiting.Add(motion)) return false;
            bool known = false;
            if (motion is VirtualClip clip)
            {
                known = Clip(clip, binding, kind, out value);
                if (!known && allowSmallNumericalDifferences && Clip(clip, binding, kind, out value, preserveZeroSign: false))
                { known = true; rounded.Add(motion); }
            }
            else if (motion is VirtualBlendTree tree) known = Tree(tree, out value);
            if (known && motion is VirtualBlendTree parent && parent.Children.Any(c => UsesVendorRoundoff(c.Motion))) rounded.Add(motion);
            visiting.Remove(motion);
            values.Add(motion, (known, value));
            return known;
        }

        internal static bool Clip(VirtualClip clip, EditorCurveBinding binding, ConstantOutputKind kind,
            out ConstantOutputValue value, bool preserveZeroSign = true)
        {
            value = default;
            var source = MotionAnalysis.Source(clip);
            if (clip.IsMarkerClip || clip.Legacy || clip.AdditiveReferencePoseClip != null ||
                source == null || source.humanMotion || source.events.Length != 0) return false;
            if (kind == ConstantOutputKind.ObjectReference)
            {
                if (!binding.isPPtrCurve || !MotionAnalysis.Finite(clip.FrameRate) || clip.FrameRate <= 0) return false;
                var keys = clip.GetObjectCurve(binding);
                if (keys == null || keys.Length == 0) return false;
                value = ConstantOutputValue.Object(keys[0].value);
                for (int i = 0; i < keys.Length; i++)
                    if (!MotionAnalysis.Finite(keys[i].time) || i > 0 && keys[i - 1].time >= keys[i].time ||
                        !value.Equals(ConstantOutputValue.Object(keys[i].value))) return false;
                return true;
            }
            if (binding.isPPtrCurve || binding.isDiscreteCurve && kind != ConstantOutputKind.Discrete ||
                !MotionAnalysis.Constant(clip.GetFloatCurve(binding), out float number, preserveZeroSign)) return false;
            value = kind == ConstantOutputKind.Discrete ? ConstantOutputValue.Discrete(number) : ConstantOutputValue.Scalar(number);
            return true;
        }

        private bool Tree(VirtualBlendTree tree, out ConstantOutputValue value)
        {
            value = default;
            if (tree.Children.Count == 0) return false;
            foreach (var child in tree.Children)
                if (child.Motion == null || child.Mirror || !MotionAnalysis.Finite(child.TimeScale) ||
                    !MotionAnalysis.Finite(child.CycleOffset)) return false;

            if (tree.BlendType == BlendTreeType.Simple1D)
            {
                var thresholds = new HashSet<float>();
                foreach (var child in tree.Children)
                    if (!MotionAnalysis.Finite(child.Threshold) || !thresholds.Add(child.Threshold)) return false;
                var selected = MotionAnalysis.Constant1DChildren(tree, analysis);
                if (selected?.Length == 1) return TryEvaluate(selected[0].Motion, out value);
                if (selected?.Length == 2)
                {
                    if (!TryEvaluate(selected[0].Motion, out var left) || !TryEvaluate(selected[1].Motion, out var right)) return false;
                    if (SameValue(left, right))
                    {
                        if (!left.Equals(right) || allowSmallNumericalDifferences) rounded.Add(tree);
                        if (kind == ConstantOutputKind.Scalar && MotionAnalysis.NegativeZero(left.Number))
                        { if (!allowSmallNumericalDifferences) return false; rounded.Add(tree); }
                        value = left; return true;
                    }
                    if (kind != ConstantOutputKind.Scalar || !TryEvaluateScalar(tree, binding, null, analysis, out float result, writeDefaults, bindings)) return false;
                    value = ConstantOutputValue.Scalar(result); return true;
                }
                return IdenticalChildren(tree, out value);
            }
            if (TwoDimensional(tree))
            {
                var positions = new HashSet<(float X, float Y)>();
                foreach (var child in tree.Children)
                    if (!MotionAnalysis.Finite(child.Position.x) || !MotionAnalysis.Finite(child.Position.y) ||
                        !positions.Add((child.Position.x, child.Position.y))) return false;
                // Native 2D weight accumulation can move identical finite scalar
                // leaves by an ULP, and even +0 can retain a residual contribution
                // from the old value or a lower layer. Explicit scalar output has
                // no default premise; discrete and object selection stay exact.
                if (kind != ConstantOutputKind.Scalar) return IdenticalChildren(tree, out value);
                // This is the vendor's normalized linear-average contract, not
                // a machine-code error theorem. The caller must prove that the
                // authored, lower and retained contributors equal every leaf.
                // No residual coefficient or numerical error bound is guessed.
                if (!allowSmallNumericalDifferences || !invariantValue.HasValue || !WellFormedTwoDimensional(tree, analysis) ||
                    !IdenticalChildren(tree, out value) || value.Number != invariantValue.Value ||
                    !HasInterpolationHeadroom(value.Number, tree.Children.Count)) return false;
                rounded.Add(tree); return true;
            }
            if (tree.BlendType != BlendTreeType.Direct || tree.NormalizedBlendValues) return false;
            if (!writeDefaults && DirectReadsRetainedValue(tree, binding, analysis, bindings)) return false;
            VirtualMotion writer = null;
            foreach (var child in tree.Children)
            {
                if (!bindings.TryGetValue(child.Motion, out var outputs))
                    bindings.Add(child.Motion, outputs = MotionAnalysis.Bindings(child.Motion));
                if (!outputs.Contains(binding)) continue;
                if (!analysis.Values(child.DirectBlendParameter).TryGetConstant(out float weight)) return false;
                if (weight == 0) continue;
                if (weight != 1 || writer != null) return false;
                writer = child.Motion;
            }
            if (writer == null || !TryEvaluate(writer, out value)) return false;
            if (kind == ConstantOutputKind.Scalar && MotionAnalysis.NegativeZero(value.Number))
            { if (!allowSmallNumericalDifferences) return false; rounded.Add(tree); }
            return true;
        }

        private bool IdenticalChildren(VirtualBlendTree tree, out ConstantOutputValue value)
        {
            value = default; bool first = true;
            foreach (var child in tree.Children)
            {
                if (!TryEvaluate(child.Motion, out var next) || !first && !SameValue(value, next)) return false;
                if (!first && !value.Equals(next)) rounded.Add(tree);
                value = next; first = false;
            }
            // Native blend accumulation can produce +0 between thresholds but
            // retain -0 at a selected leaf. Identical -0 keys do not make the
            // blended shader output constant at the bit level.
            if (first) return false;
            if (allowSmallNumericalDifferences && kind == ConstantOutputKind.Scalar && tree.Children.Count > 1) rounded.Add(tree);
            if (kind == ConstantOutputKind.Scalar && MotionAnalysis.NegativeZero(value.Number))
            { if (!allowSmallNumericalDifferences) return false; rounded.Add(tree); }
            return true;
        }

        private bool SameValue(ConstantOutputValue first, ConstantOutputValue second) => first.Equals(second) ||
            allowSmallNumericalDifferences && first.Kind == ConstantOutputKind.Scalar && second.Kind == ConstantOutputKind.Scalar &&
            first.Number == second.Number;

        internal static bool WellFormedTwoDimensional(VirtualBlendTree tree, AnimatorReachability analysis)
        {
            if (!TwoDimensional(tree) || tree.Children.Count < 3) return false;
            var x = analysis.Values(tree.BlendParameter); var y = analysis.Values(tree.BlendParameterY);
            foreach (var domain in new[] { x, y })
            {
                if (!MotionAnalysis.Finite((float)domain.Min) || !MotionAnalysis.Finite((float)domain.Max)) return false;
            }
            // Finite endpoints alone do not exclude overflow in the mixer's
            // coordinate arithmetic. The rectangle corners bound squared
            // distance to every child, including dynamic bounded inputs.
            var corners = new[] { new Vector2((float)x.Min, (float)y.Min), new Vector2((float)x.Min, (float)y.Max),
                new Vector2((float)x.Max, (float)y.Min), new Vector2((float)x.Max, (float)y.Max) };
            if (corners.Any(c => !MotionAnalysis.Finite(Vector2.Dot(c, c)))) return false;
            bool area = false; var positions = new HashSet<(float, float)>();
            for (int i = 0; i < tree.Children.Count; i++)
            {
                var child = tree.Children[i]; var point = child.Position;
                if (child.Motion == null || child.Mirror || !MotionAnalysis.Finite(child.TimeScale) ||
                    !MotionAnalysis.Finite(child.CycleOffset) || !MotionAnalysis.Finite(point.x) || !MotionAnalysis.Finite(point.y) ||
                    !MotionAnalysis.Finite(Vector2.Dot(point, point)) || !positions.Add((point.x, point.y))) return false;
                foreach (var corner in corners)
                {
                    var distance = corner - point;
                    if (!MotionAnalysis.Finite(Vector2.Dot(distance, distance)) ||
                        !MotionAnalysis.Finite(Vector2.Dot(corner, point)) ||
                        !MotionAnalysis.Finite(corner.x * point.y - corner.y * point.x)) return false;
                }
                for (int j = 0; j < i; j++)
                {
                    Vector2 difference = point - tree.Children[j].Position;
                    float squared = Vector2.Dot(difference, difference);
                    if (!MotionAnalysis.Finite(squared) || squared <= 0) return false;
                    var previous = tree.Children[j].Position;
                    if (tree.BlendType == BlendTreeType.SimpleDirectional2D && (point.x != 0 || point.y != 0) &&
                        (previous.x != 0 || previous.y != 0))
                    {
                        float cross = point.x * previous.y - point.y * previous.x;
                        if (!MotionAnalysis.Finite(cross) || cross == 0 && Vector2.Dot(point, previous) > 0) return false;
                    }
                }
                if (i >= 2)
                {
                    Vector2 first = tree.Children[1].Position - tree.Children[0].Position;
                    Vector2 next = point - tree.Children[0].Position;
                    float cross = first.x * next.y - first.y * next.x;
                    if (!MotionAnalysis.Finite(cross)) return false;
                    area |= cross != 0;
                }
            }
            return area;
        }

        // Leave finite-endpoint overflow outside the roundoff-only contract.
        // This is headroom for convex contributors, not an error coefficient.
        private static bool HasInterpolationHeadroom(float value, int contributors) =>
            Math.Abs((double)value) <= float.MaxValue / (contributors + 1d);

        private static bool TwoDimensional(VirtualBlendTree tree) =>
            tree.BlendType == BlendTreeType.SimpleDirectional2D ||
            tree.BlendType == BlendTreeType.FreeformDirectional2D ||
            tree.BlendType == BlendTreeType.FreeformCartesian2D;

        // With WD off, an unbound Direct child can contribute a retained value
        // alongside another child's registered write. Repeated evaluation can
        // accumulate that value, even when the bound child's weight is zero.
        internal static bool DirectReadsRetainedValue(VirtualBlendTree tree, EditorCurveBinding binding,
            AnimatorReachability analysis, Dictionary<VirtualMotion, HashSet<EditorCurveBinding>> bindings)
        {
            bool bound = false, missing = false;
            foreach (var child in tree.Children)
            {
                bool active = !analysis.Values(child.DirectBlendParameter).TryGetConstant(out float weight) || weight != 0;
                if (child.Motion == null) { missing |= active; continue; }
                if (!bindings.TryGetValue(child.Motion, out var outputs))
                    bindings.Add(child.Motion, outputs = MotionAnalysis.Bindings(child.Motion));
                if (outputs.Contains(binding)) bound = true;
                else missing |= active;
            }
            return bound && missing;
        }
    }
}
