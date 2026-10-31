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
        internal bool Unreachable, UnusedParameters, ConstantTrees, MergeClips, Materials, MaterialOutputs, StateLogic, ParameterDeclarations;
        internal bool Enabled => Unreachable || UnusedParameters || ConstantTrees || MergeClips || Materials || MaterialOutputs || StateLogic || ParameterDeclarations;
        internal static AnimatorOptimizationSettings From(ALTOTraceAndOptimizeAnimator component) => new() {
            Unreachable = component.RemoveUnreachableStates, UnusedParameters = component.RemoveUnusedParameterCurves,
            ConstantTrees = component.SimplifyConstantBlendTrees, MergeClips = component.MergeSameWeightClips,
            Materials = component.BakeConstantMaterialProperties, MaterialOutputs = component.RemoveUnusedMaterialProperties,
            StateLogic = component.SimplifyUnusedStateLogic, ParameterDeclarations = component.RemoveUnusedParameterDeclarations };
        internal bool Same(AnimatorOptimizationSettings other) => (Unreachable, UnusedParameters, ConstantTrees, MergeClips, Materials, MaterialOutputs, StateLogic, ParameterDeclarations)
            == (other.Unreachable, other.UnusedParameters, other.ConstantTrees, other.MergeClips, other.Materials, other.MaterialOutputs, other.StateLogic, other.ParameterDeclarations);
    }

    internal sealed class AnimatorOptimizationReport
    {
        internal int Layers, States, Transitions, ParameterCurves, DriverOperations, Behaviours, Branches, Trees, MergedClips, MaterialCurves, StateMachines, ParameterDeclarations;
        internal int Changes => Layers + States + Transitions + ParameterCurves + DriverOperations + Behaviours + Branches + Trees + MergedClips + MaterialCurves + StateMachines + ParameterDeclarations;
        internal string Fallback;
        public override string ToString() => $"removed {States} states, {Transitions} transitions, {ParameterCurves} unused parameter curves, " +
            $"{Layers} unused layers, {ParameterDeclarations} unused parameter declarations, {DriverOperations} unused Driver operations, {Behaviours} empty behaviours, {Branches} empty branches; simplified {StateMachines} unused state machines and {Trees} trees; merged {MergedClips} clips; baked {MaterialCurves} material curves" +
            (Fallback == null ? "" : "; conservative fallback: " + Fallback);
    }

    internal static class AnimatorOptimizer
    {
        internal static AnimatorOptimizationReport Optimize(GameObject avatar, VirtualAnimatorController[] controllers,
            AnimatorOptimizationSettings settings, ISet<VirtualAnimatorController> additiveControllers = null,
            AnimatorReachability analysis = null, IEnumerable<string> preservedParameters = null,
            Func<VirtualClip, EditorCurveBinding, bool> observableBinding = null, IEnumerable<string> dependencyRoots = null,
            IDictionary<object, VirtualAnimatorController> controllerEntries = null, bool finalParameterDeclarations = false)
        {
            analysis ??= AnimatorReachability.Analyze(avatar, controllers, preservedParameters);
            var report = new AnimatorOptimizationReport { Fallback = analysis.FallbackReason };
            if (analysis.FallbackReason != null) return report;
            if ((settings.StateLogic || settings.ParameterDeclarations) && (report.Fallback = ObjectOptimization.ObjectOptimizationSafety.UnsupportedRuntime(avatar)) != null) return report;
            if (settings.Unreachable) PruneStates(controllers, analysis, report);
            if (settings.UnusedParameters || settings.StateLogic) ParameterLiveness.RemoveUnusedCurves(avatar, controllers, report, preservedParameters, observableBinding,
                removeDeadDriverOperations: settings.StateLogic || settings.MaterialOutputs && observableBinding != null,
                followStateOutputs: settings.StateLogic, analysis: analysis, dependencyRoots: dependencyRoots, controllerEntries: controllerEntries);
            if (settings.StateLogic) UnusedStateLogic.Apply(controllers, report, observableBinding);
            RewriteTrees(controllers, analysis, settings, report, additiveControllers, observableBinding);
            if (settings.StateLogic && finalParameterDeclarations)
                UnusedLayers.Apply(controllers.Where(c => additiveControllers?.Contains(c) != true).ToArray(), report, observableBinding, controllerEntries);
            // Late SDK generators may still read declarations/defaults that currently have
            // no consumers. Only a caller at the final runtime boundary can close this set.
            if (settings.ParameterDeclarations && finalParameterDeclarations)
                UnusedParameterDeclarations.Apply(avatar, controllers, preservedParameters, report);
            return report;
        }

        private static void PruneStates(IEnumerable<VirtualAnimatorController> controllers, AnimatorReachability analysis,
            AnimatorOptimizationReport report)
        {
            foreach (var controller in controllers)
            {
                var removed = new HashSet<VirtualState>();
                foreach (var layer in controller.Layers)
                {
                    if (layer.SyncedLayerIndex != -1) continue;
                    var machines = layer.AllReachableNodes().OfType<VirtualStateMachine>().ToArray();
                    var defaults = new HashSet<VirtualState>(machines.Select(m => m.DefaultState));
                    var states = machines.SelectMany(m => m.States).Select(c => c.State).Where(s => s != null).Distinct().ToArray();
                    var candidates = new HashSet<VirtualState>(states.Where(s => !analysis.IsReachable(s) && !defaults.Contains(s)));
                    var coverage = new HashSet<EditorCurveBinding>(states.Where(s => !candidates.Contains(s))
                        .SelectMany(s => MotionAnalysis.Bindings(s.Motion)));
                    // Unreachable clips still contribute to Unity's Write Defaults binding set. Retain
                    // a representative whenever deleting it would change that set, including PPtr curves.
                    foreach (var state in candidates)
                    {
                        var bindings = MotionAnalysis.Bindings(state.Motion);
                        if (!bindings.IsSubsetOf(coverage)) coverage.UnionWith(bindings);
                        else removed.Add(state);
                    }
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
            Func<VirtualClip, EditorCurveBinding, bool> observableBinding)
        {
            var blocked = new HashSet<VirtualMotion>();
            foreach (var controller in controllers)
            foreach (var layer in controller.Layers)
            foreach (var state in layer.AllReachableNodes().OfType<VirtualState>())
                if (state.Motion != null && (!state.WriteDefaultValues || layer.AvatarMask != null || layer.SyncedLayerIndex != -1 ||
                    layer.BlendingMode == AnimatorLayerBlendingMode.Additive || additive?.Contains(controller) == true))
                    blocked.UnionWith(state.Motion.AllReachableNodes().OfType<VirtualMotion>());

            var replacements = new Dictionary<VirtualMotion, VirtualMotion>();
            var visiting = new HashSet<VirtualMotion>();
            VirtualMotion Rewrite(VirtualMotion motion)
            {
                if (!(motion is VirtualBlendTree tree) || blocked.Contains(motion)) return motion;
                if (replacements.TryGetValue(motion, out var cached)) return cached;
                if (!visiting.Add(motion)) return motion;
                var children = tree.Children.Select(child => Copy(child, Rewrite(child.Motion))).ToList();
                tree.Children = children.ToImmutableList();
                if (tree.BlendType == BlendTreeType.Direct && !tree.NormalizedBlendValues &&
                    MotionAnalysis.Clips(tree).All(MotionAnalysis.StaticClip))
                {
                    if (settings.UnusedParameters)
                        report.Branches += children.RemoveAll(c => c.Motion is VirtualClip clip && MotionAnalysis.StaticClip(clip) &&
                            (!clip.GetFloatCurveBindings().Any() || settings.StateLogic && observableBinding != null &&
                                !clip.GetFloatCurveBindings().Any(b => observableBinding(clip, b))));
                    if (settings.MergeClips)
                    {
                        for (int i = 0; i < children.Count; i++)
                        {
                            if (!(children[i].Motion is VirtualClip first) || !MotionAnalysis.StaticClip(first) || !Identity(children[i])) continue;
                            var bindings = MotionAnalysis.Bindings(first);
                            VirtualClip merged = null;
                            for (int j = i + 1; j < children.Count; j++)
                            {
                                var child = children[j];
                                if (child.DirectBlendParameter != children[i].DirectBlendParameter || !Identity(child) ||
                                    !(child.Motion is VirtualClip next) || !MotionAnalysis.StaticClip(next)) continue;
                                var other = MotionAnalysis.Bindings(next);
                                if (bindings.Overlaps(other)) continue;
                                if (merged == null) { merged = first.Clone(); merged.Name = first.Name + " (merged)"; }
                                foreach (var binding in next.GetFloatCurveBindings()) merged.SetFloatCurve(binding, next.GetFloatCurve(binding));
                                bindings.UnionWith(other); children.RemoveAt(j--); report.MergedClips++;
                            }
                            if (merged != null) children[i] = Copy(children[i], merged);
                        }
                    }
                }
                tree.Children = children.ToImmutableList();
                VirtualMotion result = tree;
                if (settings.ConstantTrees && children.Count > 0 && children.All(Identity) &&
                    MotionAnalysis.Clips(tree).All(MotionAnalysis.StaticClip))
                {
                    VirtualMotion selected = null;
                    var contributing = MotionAnalysis.Constant1DChildren(tree, analysis);
                    if (contributing?.Length == 1) selected = contributing[0].Motion;
                    if (tree.BlendType == BlendTreeType.Direct && children.All(c => analysis.Values(c.DirectBlendParameter).TryGetConstant(out _)))
                    {
                        var active = children.Where(c => { analysis.Values(c.DirectBlendParameter).TryGetConstant(out float w); return w != 0; }).ToArray();
                        if (active.Length == 1 && analysis.Values(active[0].DirectBlendParameter).TryGetConstant(out float weight) && weight == 1)
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
