using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.Analysis
{
    internal readonly struct ConstantPropertyProof
    {
        internal readonly ConstantOutputValue Value;
        internal readonly AvatarAnimationIndex.BindingUse[] Writers;
        internal readonly bool ChangesInitialValue;
        // Null is deliberate: the vendor interpolation contract permits only
        // roundoff of one mathematical output, but does not certify a universal
        // machine-code error bound for Unity's mixer implementation.
        internal readonly bool UsesVendorRoundoff;
        internal readonly double? AbsoluteErrorBound;
        internal ConstantPropertyProof(ConstantOutputValue value, AvatarAnimationIndex.BindingUse[] writers,
            ConstantOutputValue initial, bool usesVendorRoundoff = false, bool hasMixerArithmetic = false)
        {
            Value = value; Writers = writers; ChangesInitialValue = !value.Equals(initial); UsesVendorRoundoff = usesVendorRoundoff;
            AbsoluteErrorBound = usesVendorRoundoff || hasMixerArithmetic ? (double?)null : 0;
        }
    }

    internal readonly struct ConstantPropertyQuery
    {
        internal readonly string Name;
        internal readonly ConstantOutputValue Initial;
        internal ConstantPropertyQuery(string name, ConstantOutputValue initial) { Name = name; Initial = initial; }
    }

    // Animation-only evidence. Consumers must independently check native writers,
    // preserved interfaces, initial-value policy and persistent side effects before
    // applying a rewrite. Rebuild this snapshot after any animation/hierarchy edit.
    internal sealed class ConstantPropertyAnalysis
    {
        private readonly AvatarAnimationIndex index;
        private readonly AnimatorReachability reachability;
        private readonly FxPropertyOutput finalOutputs;
        private readonly bool reuseLayerFacts;
        private readonly Dictionary<VirtualClip, List<(object Owner, VirtualLayer Layer)>> owners = new();
        private readonly Dictionary<VirtualMotion, HashSet<EditorCurveBinding>> bindings = new();

        internal ConstantPropertyAnalysis(AvatarAnimationIndex index, AnimatorReachability analysis = null, bool reuseLayerFacts = true)
        {
            using var cost = AnalysisCost.Measure("Constant output writer index");
            this.index = index; reachability = analysis ?? index.Reachability;
            this.reuseLayerFacts = reuseLayerFacts;
            finalOutputs = new FxPropertyOutput(index.ControllerEntries, reachability);
            foreach (var entry in index.ControllerEntries)
            foreach (var layer in entry.Value.Layers)
            foreach (var clip in layer.AllReachableNodes().OfType<VirtualClip>().Distinct())
            {
                if (!owners.TryGetValue(clip, out var contexts)) owners.Add(clip, contexts = new());
                contexts.Add((entry.Key, layer));
            }
        }

        internal bool MatchesSnapshot(AvatarAnimationIndex candidate, AnimatorReachability analysis) =>
            ReferenceEquals(index, candidate) && ReferenceEquals(reachability, analysis ?? candidate.Reachability);

        // Ordinary invariant consumers can inspect another component after an
        // in-batch deletion callback. Their legacy query refreshed these facts;
        // only an explicitly read-only planning interval may retain them.
        private bool CanBlendStates(VirtualLayer layer) => reuseLayerFacts ? finalOutputs.CanBlendStates(layer)
            : FxPropertyOutput.StateTransitionsCanBlend(layer, reachability);
        private bool FiniteTransitions(VirtualLayer layer) => reuseLayerFacts ? finalOutputs.HasFiniteTransitions(layer)
            : FxPropertyOutput.FiniteTransitions(layer, reachability);
        private bool WeightCanChange(VirtualAnimatorController controller, VirtualLayer layer) => reuseLayerFacts
            ? finalOutputs.WeightCanChange(controller, layer)
            : FxPropertyOutput.WeightCanChange(index.ControllerEntries, controller, layer, reachability);
        private IEnumerable<VirtualState> ReachableStates(VirtualLayer layer) => reuseLayerFacts ? finalOutputs.ReachableStates(layer)
            : layer.AllReachableNodes().OfType<VirtualState>().Where(reachability.IsReachable);

        // The invariant query deliberately has a different default-write contract:
        // missing output already matches the serialized default, and partial layer
        // weights are harmless when every contributor equals that same default.
        // Retain this coverage when consolidating the earlier component analysis.
        internal bool MatchesInitialValue(Component target, IReadOnlyList<AvatarAnimationIndex.BindingUse> uses,
            float initial, bool allowInterpolation, ConstantOutputKind kind = ConstantOutputKind.Scalar,
            bool allowSmallNumericalDifferences = false)
        {
            using var cost = AnalysisCost.Measure("Constant output invariant query");
            if (uses.Count == 0) return true;
            if (!MotionAnalysis.Finite(initial) || reachability.FallbackReason != null) return false;
            var binding = uses[0].Binding;
            var contexts = new HashSet<(object Owner, VirtualLayer Layer)>();
            foreach (var use in uses)
            {
                if (use.Immutable || !use.Complete || use.Binding.isPPtrCurve || !binding.Equals(use.Binding)) return false;
                using var objects = use.Objects().GetEnumerator();
                if (!objects.MoveNext() || objects.Current != target || objects.MoveNext() ||
                    !owners.TryGetValue(use.Clip, out var found)) return false;
                contexts.UnionWith(found);
            }
            var caches = new[] { new Dictionary<VirtualMotion, bool>(), new Dictionary<VirtualMotion, bool>() };
            foreach (var context in contexts)
            {
                var layer = context.Layer;
                if (layer.AvatarMask != null || layer.SyncedLayerIndex != -1 ||
                    context.Owner is VRC.SDK3.Avatars.Components.VRCAvatarDescriptor.AnimLayerType type &&
                        type == VRC.SDK3.Avatars.Components.VRCAvatarDescriptor.AnimLayerType.Additive ||
                    layer.BlendingMode != AnimatorLayerBlendingMode.Override ||
                    kind == ConstantOutputKind.Scalar && !allowSmallNumericalDifferences && MotionAnalysis.NegativeZero(initial) &&
                        CanBlendStates(layer)) return false;
                if (allowSmallNumericalDifferences &&
                    (!MotionAnalysis.Finite(layer.DefaultWeight) || layer.DefaultWeight < 0 || layer.DefaultWeight > 1 ||
                     WeightCanChange(index.ControllerEntries[context.Owner], layer) || !FiniteTransitions(layer))) return false;
                bool any = false;
                foreach (var state in ReachableStates(layer))
                {
                    any = true;
                    if (!ConstantMotionOutput.Matches(state.Motion, binding, initial, true, reachability,
                        bindings, caches[state.WriteDefaultValues ? 1 : 0], new(), allowInterpolation, state.WriteDefaultValues, kind,
                        allowSmallNumericalDifferences)) return false;
                }
                if (!any) return false;
            }
            return true;
        }

        internal bool TryProve(Object target, string property, ConstantOutputValue initial,
            out ConstantPropertyProof proof, out string reason, bool includeUnreachableOutputs = false,
            bool allowSmallNumericalDifferences = false)
        {
            using var cost = AnalysisCost.Measure("Constant output explicit query");
            proof = default; reason = index.UnsafeReason ?? reachability.FallbackReason;
            if (reason != null) return false;
            if (!initial.Finite) { reason = "nonfinite initial property value"; return false; }
            var uses = index.ForObject(target).Where(u => u.Binding.propertyName == property).ToArray();
            if (uses.Length == 0) { reason = "no indexed animation writer"; return false; }
            var binding = uses[0].Binding;
            var writerContexts = new HashSet<(object Owner, VirtualLayer Layer)>();
            foreach (var use in uses)
            {
                if (use.Immutable || !use.Complete || use.Roots.Length != 1 || use.Roots[0] != index.Avatar.transform ||
                    use.Targets.Length != 1 || !binding.Equals(use.Binding))
                { reason = "immutable, shared or ambiguous animation binding"; return false; }
                using var objects = use.Objects().GetEnumerator();
                if (!objects.MoveNext() || objects.Current != target || objects.MoveNext())
                { reason = "binding does not resolve to one component"; return false; }
                foreach (var context in owners[use.Clip])
                {
                    if (!FxPropertyOutput.IsFx(context.Owner)) { reason = "a non-FX controller can write the property"; return false; }
                    writerContexts.Add(context);
                }
            }
            if (!finalOutputs.TryGetFinalOutputStates(writerContexts, out var outputs, out reason, includeInitialOutputs: true,
                includeUnreachableOutputs: includeUnreachableOutputs)) return false;
            allowSmallNumericalDifferences &= initial.Kind == ConstantOutputKind.Scalar;
            float? invariant = allowSmallNumericalDifferences && target is Component component &&
                MatchesInitialValue(component, uses, initial.Number, false, initial.Kind, true) ? initial.Number : (float?)null;
            var evaluators = new[] { new ConstantMotionOutput(binding, initial.Kind, reachability, bindings, false, allowSmallNumericalDifferences, invariant),
                new ConstantMotionOutput(binding, initial.Kind, reachability, bindings, true, allowSmallNumericalDifferences, invariant) };
            ConstantOutputValue value = default; bool first = true;
            bool vendorRoundoff = false;
            foreach (var state in outputs)
            {
                var evaluator = evaluators[state.WriteDefaultValues ? 1 : 0];
                if (!evaluator.TryEvaluate(state.Motion, out var next) || !first && !value.Equals(next) &&
                    !(allowSmallNumericalDifferences && value.Number == next.Number))
                { reason = "a reachable motion has a missing, variable or unsupported output"; return false; }
                vendorRoundoff |= evaluator.UsesVendorRoundoff(state.Motion) || !first && !value.Equals(next);
                value = next; first = false;
            }
            if (value.Kind == ConstantOutputKind.Scalar && MotionAnalysis.NegativeZero(value.Number) &&
                finalOutputs.FinalOutputCanBlendStates(writerContexts))
            {
                if (!allowSmallNumericalDifferences || writerContexts.Any(c => !finalOutputs.HasFiniteTransitions(c.Layer)))
                { reason = "state transition blending can change the scalar zero sign"; return false; }
                vendorRoundoff = true;
            }
            if (allowSmallNumericalDifferences && value.Number == initial.Number && !value.Equals(initial))
            { value = initial; vendorRoundoff = true; }
            if (allowSmallNumericalDifferences && finalOutputs.FinalOutputCanBlendStates(writerContexts))
            {
                if (writerContexts.Any(c => !finalOutputs.HasFiniteTransitions(c.Layer)))
                { reason = "a state transition has nonfinite or invalid interpolation settings"; return false; }
                vendorRoundoff = true;
            }
            // Earlier exact-path mixer proofs are retained, but their native
            // arithmetic does not establish a universal machine error bound.
            proof = new ConstantPropertyProof(value, uses, initial, vendorRoundoff,
                initial.Kind == ConstantOutputKind.Scalar && (outputs.Any(s => !(s.Motion is VirtualClip)) ||
                    finalOutputs.FinalOutputCanBlendStates(writerContexts)));
            return true;
        }

        internal bool TryProveGroup(Component target, IReadOnlyList<ConstantPropertyQuery> properties,
            out ConstantPropertyProof[] proofs, out string reason, bool allowSmallNumericalDifferences = false)
        {
            proofs = null; reason = null;
            if (properties.Count == 0) { reason = "an empty output group has no proof"; return false; }
            var result = new ConstantPropertyProof[properties.Count];
            HashSet<VirtualClip> footprint = null;
            for (int i = 0; i < properties.Count; i++)
            {
                var property = properties[i];
                if (!TryProve(target, property.Name, property.Initial, out result[i], out reason,
                    allowSmallNumericalDifferences: allowSmallNumericalDifferences)) return false;
                var clips = new HashSet<VirtualClip>(result[i].Writers.Select(w => w.Clip));
                if (footprint != null && !footprint.SetEquals(clips))
                { reason = "group channels have different writer coverage and may overwrite each other implicitly"; return false; }
                footprint = clips;
            }
            proofs = result; return true;
        }

        // Reporting only: a selected rounded evaluator can also cover a legacy
        // strict proof. Classify the accepted output, never the enabled option or
        // an unknown machine error bound. This does not alter acceptance/value.
        internal bool RequiresNumericalPermission(Object target, string property, ConstantOutputValue initial,
            ConstantPropertyProof accepted, bool includeUnreachableOutputs = false)
        {
            using var cost = AnalysisCost.Measure("Constant output reporting classification");
            if (accepted.UsesVendorRoundoff) AnalysisCost.Count("Constant output reporting/strict recheck");
            return accepted.UsesVendorRoundoff &&
                (!TryProve(target, property, initial, out var strict, out _, includeUnreachableOutputs) ||
                 !strict.Value.Equals(accepted.Value));
        }

        internal bool RequiresNumericalPermission(Component target, IReadOnlyList<ConstantPropertyQuery> queries,
            IReadOnlyList<ConstantPropertyProof> accepted)
        {
            for (int i = 0; i < queries.Count; i++)
                if (RequiresNumericalPermission(target, queries[i].Name, queries[i].Initial, accepted[i])) return true;
            return false;
        }

        internal bool MayBlendStates(ConstantPropertyProof proof) =>
            finalOutputs.FinalOutputCanBlendStates(proof.Writers.SelectMany(w => owners[w.Clip]));

        internal bool HasFiniteTransitions(ConstantPropertyProof proof) => proof.Writers.SelectMany(w => owners[w.Clip])
            .All(c => finalOutputs.HasFiniteTransitions(c.Layer));
    }
}
