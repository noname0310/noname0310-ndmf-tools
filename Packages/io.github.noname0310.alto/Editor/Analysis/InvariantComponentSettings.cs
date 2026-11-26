using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Build;
using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.Analysis
{
    internal sealed class InvariantComponentSettings
    {
        private readonly AvatarAnimationIndex index;
        private readonly ConstantPropertyAnalysis constants;
        private readonly OptimizationPolicy numericalPolicy;
        private readonly NumericalOptimization numericalFeature;
        private readonly Func<Component, string, bool> permitsRoundoff;

        internal InvariantComponentSettings(AvatarAnimationIndex index, AnimatorReachability analysis,
            IDictionary<object, VirtualAnimatorController> controllers, OptimizationPolicy numericalPolicy = null,
            NumericalOptimization numericalFeature = NumericalOptimization.None,
            Func<Component, string, bool> permitsRoundoff = null, ConstantPropertyAnalysis sharedConstants = null)
        {
            this.index = index;
            // Sharing is opt-in at a read-only planning boundary. A different
            // reachability result must never borrow another parameter domain.
            constants = sharedConstants?.MatchesSnapshot(index, analysis) == true
                ? sharedConstants : new ConstantPropertyAnalysis(index, analysis, reuseLayerFacts: false);
            this.numericalPolicy = numericalPolicy;
            this.numericalFeature = numericalFeature;
            this.permitsRoundoff = permitsRoundoff;
        }

        internal bool IsConstant(Component component)
            => IsConstant(component, out _);

        internal bool IsConstant(Component component, out NumericalOptimization consumed)
            => IsConstant(component, index.ForObject(component), out consumed);

        internal bool IsConstantProperty(Component component, string propertyName)
            => IsConstant(component, index.ForObject(component).Where(u => u.Binding.propertyName == propertyName).ToArray(), out _);

        private bool IsConstant(Component component, IReadOnlyList<AvatarAnimationIndex.BindingUse> uses, out NumericalOptimization consumed)
        {
            using var cost = AnalysisCost.Measure("Invariant component configuration query");
            consumed = NumericalOptimization.None;
            bool rounded = false;
            if (uses.Count == 0) return true;
            foreach (var use in uses)
            {
                if (use.Immutable || !use.Complete || use.Binding.isPPtrCurve) return false;
                // Resolve once per query, never across component moves/deletions.
                using var objects = use.Objects().GetEnumerator();
                if (!objects.MoveNext() || objects.Current != component || objects.MoveNext()) return false;
            }
            using var serialized = new SerializedObject(component);
            foreach (var group in uses.GroupBy(u => u.Binding.propertyName))
            {
                if (group.Select(u => u.Binding).Distinct().Count() != 1) return false;
                var property = serialized.FindProperty(group.Key);
                if (!ConstantOutputValue.TryRead(property, out var initial) || initial.Kind == ConstantOutputKind.ObjectReference) return false;
                var writers = group.ToArray();
                if (constants.MatchesInitialValue(component, writers, initial.Number,
                    initial.Kind == ConstantOutputKind.Scalar, initial.Kind)) continue;
                // Equality used for a source-count, activation or shader/control
                // decision stays exact. A consumer must identify its own feature
                // and attest the continuous property and affected output readers.
                // Its policy can add a roundoff proof, never disable an exact one.
                if (initial.Kind != ConstantOutputKind.Scalar || numericalFeature == NumericalOptimization.None ||
                    numericalPolicy?.AllowsSmallNumericalDifferences(component.transform, numericalFeature) == false ||
                    permitsRoundoff?.Invoke(component, group.Key) != true || writers.Any(w => w.PreservedInput) ||
                    !constants.MatchesInitialValue(component, writers, initial.Number, true, initial.Kind,
                        allowSmallNumericalDifferences: true)) return false;
                rounded = true;
            }
            if (rounded) consumed = numericalFeature;
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
