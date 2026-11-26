using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Noname.AvatarTools.Analysis;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase;

namespace Noname.AvatarTools.AnimatorOptimization
{
    internal sealed class AnimatorOptimizationSettings
    {
        internal bool Unreachable, UnusedParameters, ConstantTrees, MergeClips, Materials, MaterialSlots, MaterialOutputs, StateLogic, ParameterDeclarations;
        internal bool Enabled => Unreachable || UnusedParameters || ConstantTrees || MergeClips || Materials || MaterialSlots || MaterialOutputs || StateLogic || ParameterDeclarations;
        internal static AnimatorOptimizationSettings From(ALTOTraceAndOptimizeAnimator component) => new() {
            Unreachable = component.RemoveUnreachableStates, UnusedParameters = component.RemoveUnusedParameterCurves,
            ConstantTrees = component.SimplifyConstantBlendTrees, MergeClips = component.MergeSameWeightClips,
            Materials = component.BakeConstantMaterialProperties, MaterialOutputs = component.RemoveUnusedMaterialProperties,
            MaterialSlots = component.BakeConstantMaterialSlots,
            StateLogic = component.SimplifyUnusedStateLogic, ParameterDeclarations = component.RemoveUnusedParameterDeclarations };
        internal bool Same(AnimatorOptimizationSettings other) => (Unreachable, UnusedParameters, ConstantTrees, MergeClips, Materials, MaterialSlots, MaterialOutputs, StateLogic, ParameterDeclarations)
            == (other.Unreachable, other.UnusedParameters, other.ConstantTrees, other.MergeClips, other.Materials, other.MaterialSlots, other.MaterialOutputs, other.StateLogic, other.ParameterDeclarations);
    }

    internal sealed class AnimatorOptimizationReport
    {
        internal int Layers, States, Transitions, ParameterCurves, DriverOperations, NativeFaceOperations, Behaviours, Branches, Trees, MergedClips, MaterialCurves, MaterialSlotCurves, InitialOutputs, StateMachines, ParameterDeclarations, MissingBindings;
        internal int Changes => Layers + States + Transitions + ParameterCurves + DriverOperations + NativeFaceOperations + Behaviours + Branches + Trees + MergedClips + MaterialCurves + MaterialSlotCurves + StateMachines + ParameterDeclarations + MissingBindings;
        internal string Fallback;
        internal readonly AppliedProofReport ConstantOutputProofs = new();
        public override string ToString() => $"removed {States} states, {Transitions} transitions, {ParameterCurves} unused parameter curves, " +
            $"{Layers} unused layers, {ParameterDeclarations} unused parameter declarations, {DriverOperations} unused Driver operations, {NativeFaceOperations} unused native FT operations, {Behaviours} empty behaviours, {Branches} empty branches, {MissingBindings} missing-target curves; simplified {StateMachines} unused state machines and {Trees} trees; merged {MergedClips} clips; baked {MaterialCurves} material curves" +
            $" and {MaterialSlotCurves} material-slot curves ({InitialOutputs} changed initial outputs)" +
            (ConstantOutputProofs.HasApplications ? "; constant-output proof assumptions: " + ConstantOutputProofs : "") +
            (Fallback == null ? "" : "; conservative fallback: " + Fallback);
    }

    internal static class AnimatorOptimizer
    {
        internal static AnimatorOptimizationReport Optimize(GameObject avatar, VirtualAnimatorController[] controllers,
            AnimatorOptimizationSettings settings, ISet<VirtualAnimatorController> additiveControllers = null,
            AnimatorReachability analysis = null, IEnumerable<string> preservedParameters = null,
            Func<VirtualClip, EditorCurveBinding, bool> observableBinding = null, IEnumerable<string> dependencyRoots = null,
            IDictionary<object, VirtualAnimatorController> controllerEntries = null, bool finalParameterDeclarations = false,
            Func<VirtualClip, EditorCurveBinding, bool> allowNumericalDifferences = null)
        {
            using var cost = AnalysisCost.Measure("Animator optimization");
            analysis ??= AnimatorReachability.Analyze(avatar, controllers, preservedParameters);
            var report = new AnimatorOptimizationReport { Fallback = analysis.FallbackReason };
            if (analysis.FallbackReason != null) return report;
            if ((settings.StateLogic || settings.ParameterDeclarations) && (report.Fallback = ObjectOptimization.ObjectOptimizationSafety.UnsupportedRuntime(avatar)) != null) return report;
            if (settings.StateLogic && finalParameterDeclarations)
                MissingAnimationBindings.Apply(controllers, observableBinding, report);
            if (settings.Unreachable) PruneStates(controllers, analysis, report, finalParameterDeclarations);
            if (settings.UnusedParameters || settings.StateLogic) ParameterLiveness.RemoveUnusedCurves(avatar, controllers, report, preservedParameters, observableBinding,
                removeDeadDriverOperations: settings.StateLogic || settings.MaterialOutputs && observableBinding != null,
                followStateOutputs: settings.StateLogic, analysis: analysis, dependencyRoots: dependencyRoots, controllerEntries: controllerEntries);
            if (settings.StateLogic) UnusedStateLogic.Apply(controllers, report, observableBinding);
            RewriteTrees(controllers, analysis, settings, report, additiveControllers, observableBinding, allowNumericalDifferences);
            if (settings.StateLogic && finalParameterDeclarations)
                UnusedLayers.Apply(controllers.Where(c => additiveControllers?.Contains(c) != true).ToArray(), report, observableBinding, controllerEntries);
            // Late SDK generators may still read declarations/defaults that currently have
            // no consumers. Only a caller at the final runtime boundary can close this set.
            if (settings.ParameterDeclarations && finalParameterDeclarations)
                UnusedParameterDeclarations.Apply(avatar, controllers, preservedParameters, report);
            return report;
        }

        private static void PruneStates(VirtualAnimatorController[] controllers, AnimatorReachability analysis,
            AnimatorOptimizationReport report, bool finalRuntimeBoundary)
        {
            bool consolidate = finalRuntimeBoundary && UnreachableBindingCoverage.HasKnownBehaviourEffects(controllers);
            // A shared state is mutable through every owner. Its binding coverage must not
            // be narrowed or widened based on just one layer's requirements.
            var shared = new HashSet<VirtualState>(controllers.Distinct().SelectMany(c => c.Layers)
                .SelectMany(l => l.AllReachableNodes().OfType<VirtualStateMachine>())
                .SelectMany(m => m.States).Select(s => s.State).Where(s => s != null)
                .GroupBy(s => s).Where(g => g.Count() > 1).Select(g => g.Key));
            foreach (var controller in controllers)
            {
                var removed = new HashSet<VirtualState>();
                foreach (var layer in controller.Layers)
                {
                    if (layer.SyncedLayerIndex != -1) continue;
                    var machines = layer.AllReachableNodes().OfType<VirtualStateMachine>().ToArray();
                    var defaults = new HashSet<VirtualState>(machines.Select(m => m.DefaultState));
                    var states = machines.SelectMany(m => m.States).Select(c => c.State).Where(s => s != null).Distinct().ToArray();
                    var candidates = states.Where(s => !analysis.IsReachable(s) && !defaults.Contains(s) && !shared.Contains(s)).ToArray();
                    var candidateSet = new HashSet<VirtualState>(candidates);
                    var coverage = new HashSet<EditorCurveBinding>(states.Where(s => !candidateSet.Contains(s))
                        .SelectMany(s => MotionAnalysis.Bindings(s.Motion)));
                    // Unreachable clips still contribute to Unity's Write Defaults binding set. Retain
                    // a representative whenever deleting it would change that set, including PPtr curves.
                    foreach (var state in candidates)
                    {
                        var bindings = MotionAnalysis.Bindings(state.Motion);
                        if (!bindings.IsSubsetOf(coverage)) coverage.UnionWith(bindings);
                        else removed.Add(state);
                    }
                    // Late generators may inspect source motions. Compact the remaining
                    // coverage representatives only after all generators have completed.
                    if (consolidate)
                        UnreachableBindingCoverage.Consolidate(states, candidates.Where(s => !removed.Contains(s)).ToArray(), removed);
                    foreach (var machine in machines)
                        machine.States = machine.States.Where(s => !removed.Contains(s.State)).ToImmutableList();
                }
                report.States += removed.Count;
                foreach (var state in controller.AllReachableNodes().OfType<VirtualState>().ToArray())
                    state.Transitions = Filter(state.Transitions, removed, analysis, report);
                foreach (var machine in controller.AllReachableNodes().OfType<VirtualStateMachine>().ToArray())
                {
                    machine.EntryTransitions = Filter(machine.EntryTransitions, removed, analysis, report);
                    machine.AnyStateTransitions = Filter(machine.AnyStateTransitions, removed, analysis, report);
                    machine.StateMachineTransitions = machine.StateMachineTransitions.ToImmutableDictionary(p => p.Key,
                        p => Filter(p.Value, removed, analysis, report));
                }
            }
        }

        private static ImmutableList<T> Filter<T>(ImmutableList<T> transitions, HashSet<VirtualState> removed,
            AnimatorReachability analysis, AnimatorOptimizationReport report) where T : VirtualTransitionBase
        {
            bool solo = transitions.Any(t => t.Solo);
            var kept = transitions.Where(t => !t.Mute && (!solo || t.Solo) &&
                !removed.Contains(t.DestinationState) && analysis.CanTake(t)).ToImmutableList();
            report.Transitions += transitions.Count - kept.Count;
            return kept;
        }

        private static void RewriteTrees(VirtualAnimatorController[] controllers, AnimatorReachability analysis,
            AnimatorOptimizationSettings settings, AnimatorOptimizationReport report, ISet<VirtualAnimatorController> additive,
            Func<VirtualClip, EditorCurveBinding, bool> observableBinding,
            Func<VirtualClip, EditorCurveBinding, bool> allowNumericalDifferences)
        {
            using var cost = AnalysisCost.Measure("Animator tree rewrites");
            // Existing clips are not mutated by this rewrite. Merging builds a
            // fresh clip before publishing it to the tree. Keep native curve
            // queries once per clip, including clips shared by several trees.
            var constantClips = new Dictionary<VirtualClip, bool>();
            var staticClips = new Dictionary<VirtualClip, bool>();
            var negativeZeroClips = new Dictionary<VirtualClip, bool>();
            var roundedClips = new Dictionary<VirtualClip, bool>();
            var roundingSources = new Dictionary<VirtualClip, VirtualClip[]>();
            bool CanRoundClip(VirtualClip clip)
            {
                if (!roundedClips.TryGetValue(clip, out bool result))
                    roundedClips.Add(clip, result = roundingSources.TryGetValue(clip, out var sources) ? sources.All(CanRoundClip) :
                        allowNumericalDifferences != null &&
                        clip.GetFloatCurveBindings().Concat(clip.GetObjectCurveBindings()).All(binding =>
                            settings.StateLogic && observableBinding?.Invoke(clip, binding) == false ||
                            allowNumericalDifferences(clip, binding)));
                return result;
            }
            bool HasNegativeZero(VirtualClip clip)
            {
                if (!negativeZeroClips.TryGetValue(clip, out bool result))
                    negativeZeroClips.Add(clip, result = clip.GetFloatCurveBindings().Any(binding =>
                        MotionAnalysis.Constant(clip.GetFloatCurve(binding), out float value, preserveZeroSign: false) && value == 0 &&
                        clip.GetFloatCurve(binding).keys.Any(k => MotionAnalysis.NegativeZero(k.value))));
                return result;
            }
            bool ConstantPose(VirtualClip clip)
            {
                if (!constantClips.TryGetValue(clip, out bool result))
                    constantClips.Add(clip, result = MotionAnalysis.ConstantPoseClip(clip) ||
                        MotionAnalysis.ConstantPoseClip(clip, preserveZeroSign: false) && CanRoundClip(clip));
                return result;
            }
            bool StaticClip(VirtualClip clip)
            {
                if (!staticClips.TryGetValue(clip, out bool result))
                    staticClips.Add(clip, result = ConstantPose(clip) && clip.Settings.stopTime == 0 && MotionAnalysis.EndTime(clip) == 0);
                return result;
            }
            bool FiniteInputs(VirtualMotion motion)
            {
                foreach (var tree in motion.AllReachableNodes().OfType<VirtualBlendTree>())
                {
                    bool FiniteParameter(string name)
                    {
                        var values = analysis.Values(name);
                        return MotionAnalysis.Finite((float)values.Min) && MotionAnalysis.Finite((float)values.Max);
                    }
                    if (tree.BlendType == BlendTreeType.Direct)
                    { if (tree.Children.Any(c => !FiniteParameter(c.DirectBlendParameter))) return false; }
                    else if (tree.BlendType == BlendTreeType.Simple1D)
                    {
                        if (!FiniteParameter(tree.BlendParameter) || tree.Children.Count == 0) return false;
                        var values = analysis.Values(tree.BlendParameter);
                        foreach (var child in tree.Children)
                            if (!MotionAnalysis.Finite(child.Threshold) ||
                                !MotionAnalysis.Finite((float)(values.Min - child.Threshold)) ||
                                !MotionAnalysis.Finite((float)(values.Max - child.Threshold))) return false;
                        if (!MotionAnalysis.Finite(tree.Children.Max(c => c.Threshold) - tree.Children.Min(c => c.Threshold))) return false;
                    }
                    else if (!ConstantMotionOutput.WellFormedTwoDimensional(tree, analysis)) return false;
                }
                return true;
            }
            bool CanRoundMotion(VirtualMotion motion) => FiniteInputs(motion) && MotionAnalysis.Clips(motion).All(CanRoundClip);
            var blocked = new HashSet<VirtualMotion>();
            foreach (var controller in controllers)
            foreach (var layer in controller.Layers)
            foreach (var state in layer.AllReachableNodes().OfType<VirtualState>())
                if (state.Motion != null && (!state.WriteDefaultValues || layer.AvatarMask != null || layer.SyncedLayerIndex != -1 ||
                    layer.BlendingMode == AnimatorLayerBlendingMode.Additive || additive?.Contains(controller) == true))
                    blocked.UnionWith(state.Motion.AllReachableNodes().OfType<VirtualMotion>());

            var replacements = new Dictionary<VirtualMotion, VirtualMotion>();
            var visiting = new HashSet<VirtualMotion>();
            var outputs = new StaticMotionOutputs(settings.StateLogic ? observableBinding : null);
            var timeIndependentOutputs = new StaticMotionOutputs(settings.StateLogic ? observableBinding : null, ConstantPose);
            var timing = new MotionTimeAnalysis(controllers, ConstantPose);
            VirtualMotion Rewrite(VirtualMotion motion)
            {
                if (!(motion is VirtualBlendTree tree) || blocked.Contains(motion)) return motion;
                if (replacements.TryGetValue(motion, out var cached)) return cached;
                if (!visiting.Add(motion)) return motion;
                var children = tree.Children.Select(child => Copy(child, Rewrite(child.Motion))).ToList();
                tree.Children = children.ToImmutableList();
                bool timeIndependent = timing.CanChangeDuration(tree);
                Func<VirtualClip, bool> safeClip = timeIndependent ? ConstantPose : StaticClip;
                var safeOutputs = timeIndependent ? timeIndependentOutputs : outputs;
                if (tree.BlendType == BlendTreeType.Direct && !tree.NormalizedBlendValues &&
                    MotionAnalysis.Clips(tree).All(safeClip))
                {
                    // Missing children can affect the sign of a zero result even
                    // when their own tracks are unobserved. Native Direct blending
                    // is not bit-equivalent to a standalone negative-zero clip.
                    if (settings.UnusedParameters && (!MotionAnalysis.Clips(tree).Any(HasNegativeZero) || CanRoundMotion(tree)))
                        report.Branches += children.RemoveAll(c => Identity(c) && safeOutputs.IsUnobserved(c.Motion));
                    if (settings.MergeClips)
                    {
                        for (int i = 0; i < children.Count; i++)
                        {
                            if (!(children[i].Motion is VirtualClip first) || !safeClip(first) || !Identity(children[i])) continue;
                            var bindings = MotionAnalysis.Bindings(first);
                            VirtualClip merged = null;
                            bool negativeZero = HasNegativeZero(first);
                            var mergedSources = new List<VirtualClip> { first };
                            for (int j = i + 1; j < children.Count; j++)
                            {
                                var child = children[j];
                                if (child.DirectBlendParameter != children[i].DirectBlendParameter || !Identity(child) ||
                                    !(child.Motion is VirtualClip next) || !safeClip(next)) continue;
                                bool nextNegativeZero = HasNegativeZero(next);
                                if ((negativeZero || nextNegativeZero) &&
                                    !(mergedSources.All(CanRoundClip) && CanRoundClip(next) && FiniteInputs(tree))) continue;
                                var other = MotionAnalysis.Bindings(next);
                                if (bindings.Overlaps(other)) continue;
                                if (merged == null) { merged = first.Clone(); merged.Name = first.Name + " (merged)"; }
                                foreach (var binding in next.GetFloatCurveBindings()) merged.SetFloatCurve(binding, next.GetFloatCurve(binding));
                                bindings.UnionWith(other); children.RemoveAt(j--); report.MergedClips++;
                                negativeZero |= nextNegativeZero; mergedSources.Add(next);
                            }
                            if (merged != null)
                            {
                                // A fresh merged clip is absent from the caller's
                                // snapshot. Retain the meet of its source permissions.
                                roundingSources[merged] = mergedSources.ToArray(); negativeZeroClips[merged] = negativeZero;
                                children[i] = Copy(children[i], merged);
                            }
                        }
                    }
                }
                tree.Children = children.ToImmutableList();
                VirtualMotion result = tree;
                if (settings.ConstantTrees && children.Count > 0 && children.All(Identity) &&
                    MotionAnalysis.Clips(tree).All(safeClip))
                {
                    VirtualMotion selected = null;
                    var contributing = MotionAnalysis.Constant1DChildren(tree, analysis);
                    if (contributing?.Length == 1) selected = contributing[0].Motion;
                    // Valid 1D interpolation partitions unit weight. Repeated references
                    // to the same constant leaf therefore have the same output for every
                    // input value, including values outside the threshold range. Keep
                    // the leaf (and its zero-valued writes), not an empty motion. Direct
                    // trees and nested calculations need different equivalence proofs.
                    if (tree.BlendType == BlendTreeType.Simple1D && children[0].Motion is VirtualClip shared &&
                        (!HasNegativeZero(shared) || CanRoundMotion(tree)) &&
                        children.All(c => c.Motion == shared && MotionAnalysis.Finite(c.Threshold)) &&
                        children.Zip(children.Skip(1), (a, b) => a.Threshold < b.Threshold).All(ordered => ordered))
                        selected = shared;
                    if (tree.BlendType == BlendTreeType.Direct && children.All(c => analysis.Values(c.DirectBlendParameter).TryGetConstant(out _)))
                    {
                        var active = children.Where(c => { analysis.Values(c.DirectBlendParameter).TryGetConstant(out float w); return w != 0; }).ToArray();
                        if (active.Length == 1 && analysis.Values(active[0].DirectBlendParameter).TryGetConstant(out float weight) && weight == 1 &&
                            (!MotionAnalysis.Clips(tree).Any(HasNegativeZero) || CanRoundMotion(tree)))
                            selected = active[0].Motion;
                    }
                    if (selected != null && MotionAnalysis.Bindings(selected).SetEquals(MotionAnalysis.Bindings(tree)))
                    { result = selected; report.Trees++; }
                }
                visiting.Remove(motion); replacements.Add(motion, result); return result;
            }
            foreach (var state in controllers.SelectMany(c => c.AllReachableNodes()).OfType<VirtualState>().Distinct().ToArray())
                state.Motion = Rewrite(state.Motion);
        }

        private static bool Identity(VirtualBlendTree.VirtualChildMotion child)
            => child.TimeScale == 1 && child.CycleOffset == 0 && !child.Mirror;

        private static VirtualBlendTree.VirtualChildMotion Copy(VirtualBlendTree.VirtualChildMotion child, VirtualMotion motion)
            => new() { Motion = motion, CycleOffset = child.CycleOffset, DirectBlendParameter = child.DirectBlendParameter,
                Mirror = child.Mirror, Threshold = child.Threshold, Position = child.Position, TimeScale = child.TimeScale };
    }
}
