using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase;

namespace Noname.AvatarTools.Analysis
{
    internal sealed class FxPropertyOutput
    {
        private readonly IDictionary<object, VirtualAnimatorController> entries;
        private readonly AnimatorReachability analysis;
        private readonly VirtualAnimatorController controller;
        private readonly VirtualLayer[] layers;
        private readonly Dictionary<VirtualLayer, bool> unsafeClips = new(), blendingStates = new(), finiteTransitions = new(), laterDefaults = new();
        private readonly Dictionary<(VirtualAnimatorController, VirtualLayer), bool> changingWeights = new();
        private readonly Dictionary<VirtualLayer, VirtualState[]> outputs = new(), reachableStates = new(), allStates = new();
        private WeightControls weightControls;

        internal FxPropertyOutput(IDictionary<object, VirtualAnimatorController> entries, AnimatorReachability analysis)
        {
            this.entries = entries; this.analysis = analysis;
            entries.TryGetValue(VRCAvatarDescriptor.AnimLayerType.FX, out controller);
            layers = controller?.Layers.ToArray() ?? System.Array.Empty<VirtualLayer>();
        }

        internal static bool IsFx(object owner) => owner is VRCAvatarDescriptor.AnimLayerType type &&
            type == VRCAvatarDescriptor.AnimLayerType.FX;

        internal bool MatchesFinalOverride(IEnumerable<(object Owner, VirtualLayer Layer)> writers,
            EditorCurveBinding binding, float value, Dictionary<VirtualMotion, HashSet<EditorCurveBinding>> bindings,
            ConstantOutputKind kind = ConstantOutputKind.Scalar)
        {
            var contexts = writers.ToArray();
            if (!TryGetFinalOutputStates(contexts, out var states, out _) ||
                kind == ConstantOutputKind.Scalar && MotionAnalysis.NegativeZero(value) && FinalOutputCanBlendStates(contexts)) return false;
            return states.All(s => ConstantPropertyOutput.Matches(s.Motion, binding, value,
                false, analysis, bindings, new Dictionary<VirtualMotion, bool>(), new HashSet<VirtualMotion>(),
                writeDefaults: s.WriteDefaultValues, kind: kind));
        }

        internal bool FinalOutputCanBlendStates(IEnumerable<(object Owner, VirtualLayer Layer)> writers)
        {
            var writerLayers = new HashSet<VirtualLayer>(writers.Select(c => c.Layer));
            var layer = layers.LastOrDefault(writerLayers.Contains);
            if (layer == null) return true;
            return CanBlendStates(layer);
        }

        internal bool CanBlendStates(VirtualLayer layer)
        {
            if (!blendingStates.TryGetValue(layer, out bool result))
                blendingStates[layer] = result = StateTransitionsCanBlend(layer, analysis);
            return result;
        }

        internal bool HasFiniteTransitions(VirtualLayer layer)
        {
            if (!finiteTransitions.TryGetValue(layer, out bool result))
                finiteTransitions[layer] = result = FiniteTransitions(layer, analysis);
            return result;
        }

        internal VirtualState[] ReachableStates(VirtualLayer layer)
        {
            if (!reachableStates.TryGetValue(layer, out var states))
            {
                using var cost = AnalysisCost.Measure("FX layer/reachable state enumeration");
                reachableStates[layer] = states = layer.AllReachableNodes().OfType<VirtualState>()
                    .Where(analysis.IsReachable).Distinct().ToArray();
            }
            return states;
        }

        internal bool WeightCanChange(VirtualAnimatorController writerController, VirtualLayer writerLayer)
        {
            var key = (writerController, writerLayer);
            if (!changingWeights.TryGetValue(key, out bool result))
                changingWeights[key] = result = (weightControls ??= new WeightControls(entries, analysis))
                    .CanChange(writerController, writerLayer);
            return result;
        }

        internal static bool StateTransitionsCanBlend(VirtualLayer layer, AnimatorReachability analysis)
        {
            using var cost = AnalysisCost.Measure("FX layer/blending transition scan");
            // Even two -0 clips can produce +0 while a transition blends their
            // output. Zero-duration transitions do not perform that accumulation.
            foreach (var node in layer.AllReachableNodes())
            {
                var transitions = node is VirtualState state && analysis.IsReachable(state) ? state.Transitions :
                    node is VirtualStateMachine machine ? machine.AnyStateTransitions : null;
                if (transitions != null && Enabled(transitions).Any(t => t.Duration != 0 && analysis.CanTake(t))) return true;
            }
            return false;
        }

        internal static bool FiniteTransitions(VirtualLayer layer, AnimatorReachability analysis)
        {
            using var cost = AnalysisCost.Measure("FX layer/finite transition scan");
            foreach (var node in layer.AllReachableNodes())
            {
                var transitions = node is VirtualState state && analysis.IsReachable(state) ? state.Transitions :
                    node is VirtualStateMachine machine ? machine.AnyStateTransitions : null;
                if (transitions != null && Enabled(transitions).Where(analysis.CanTake).Any(t =>
                    !MotionAnalysis.Finite(t.Duration) || t.Duration < 0 || !MotionAnalysis.Finite(t.Offset))) return false;
            }
            return true;
        }

        internal bool TryGetFinalOutputStates(IEnumerable<(object Owner, VirtualLayer Layer)> writers,
            out VirtualState[] states, out string reason, bool includeInitialOutputs = false, bool includeUnreachableOutputs = false)
        {
            using var cost = AnalysisCost.Measure("FX layer/final output query");
            states = System.Array.Empty<VirtualState>(); reason = null;
            var contexts = writers.Distinct().ToArray();
            if (analysis.FallbackReason != null || contexts.Length == 0 || contexts.Any(c => !IsFx(c.Owner)))
            { reason = analysis.FallbackReason ?? "missing or non-FX writer context"; return false; }
            if (layers.Length == 0 || layers[0].AvatarMask != null)
            { reason = "missing or masked FX controller"; return false; }
            foreach (var context in contexts)
            {
                if (!unsafeClips.TryGetValue(context.Layer, out bool unsafeLayer))
                    unsafeClips[context.Layer] = unsafeLayer = context.Layer.AllReachableNodes().OfType<VirtualClip>().Any(c => c.IsMarkerClip || c.Legacy ||
                        c.AdditiveReferencePoseClip != null || MotionAnalysis.Source(c) == null || MotionAnalysis.Source(c).events.Length != 0);
                if (unsafeLayer) { reason = "a writer layer has unsupported clip metadata or events"; return false; }
            }
            var layer = layers.LastOrDefault(l => contexts.Any(c => c.Layer == l));
            if (layer == null || contexts.Any(c => !layers.Contains(c.Layer)) || layer.AvatarMask != null ||
                layer.BlendingMode != AnimatorLayerBlendingMode.Override || layer.SyncedLayerIndex != -1 ||
                layer != layers[0] && layer.DefaultWeight != 1)
            { reason = "no full-weight unmasked final override"; return false; }
            if (WeightCanChange(controller, layer)) { reason = "the final writer weight can change"; return false; }
            if (includeInitialOutputs)
            {
                if (!laterDefaults.TryGetValue(layer, out bool readsDefaults))
                {
                    using var defaultCost = AnalysisCost.Measure("FX layer/later Write Defaults scan");
                    laterDefaults[layer] = readsDefaults = layers.Skip(System.Array.IndexOf(layers, layer) + 1)
                        .Any(l => ReachableStates(l).Any(s => s.WriteDefaultValues));
                }
                if (readsDefaults) { reason = "a later Write Defaults layer can expose the original value"; return false; }
            }
            if (includeInitialOutputs || includeUnreachableOutputs)
            {
                using var stateCost = AnalysisCost.Measure("FX layer/initial or unreachable state enumeration");
                if (!includeUnreachableOutputs) states = ReachableStates(layer);
                else if (!allStates.TryGetValue(layer, out states))
                    allStates[layer] = states = layer.AllReachableNodes().OfType<VirtualState>().Distinct().ToArray();
            }
            else if (!outputs.TryGetValue(layer, out states))
            {
                AnalysisCost.Count("FX layer/final state cache miss");
                outputs[layer] = states = OutputStates(layer, analysis);
            }
            else AnalysisCost.Count("FX layer/final state cache hit");
            // Missing tracks can expose lower layers, even with Write Defaults.
            // Require explicit output instead of assuming the original scene value.
            if (states.Length == 0) { reason = "no proven reachable writer state"; return false; }
            return true;
        }

        internal static VirtualState[] OutputStates(VirtualLayer layer, AnimatorReachability analysis)
        {
            using var cost = AnalysisCost.Measure("FX layer/final state proof");
            var states = layer.AllReachableNodes().OfType<VirtualState>().Where(analysis.IsReachable).Distinct().ToArray();
            var machine = layer.StateMachine;
            if (analysis.FallbackReason != null || machine == null || machine.StateMachines.Count != 0) return states;
            var any = Enabled(machine.AnyStateTransitions).Where(analysis.CanTake).ToArray();
            if (any.Length == 0) return AfterInitialTransition(machine, states, analysis);
            var first = any[0]; var target = first.DestinationState;
            if (target == null || !states.Contains(target) || !Immediate(first, analysis)) return states;
            bool GoesToTarget(VirtualTransitionBase transition) => !transition.IsExit &&
                transition.DestinationStateMachine == null && transition.DestinationState == target;
            if (any.Any(t => !GoesToTarget(t)) || states.Any(s => Enabled(s.Transitions).Any(t =>
                    analysis.CanTake(t) && !GoesToTarget(t)))) return states;
            // A permanently true, zero-duration Any State transition selects this
            // output on each evaluation. Keep states, transitions and behaviours:
            // their entry callbacks are not proved unreachable. Entry transitions
            // alone do not provide this output guarantee in Unity.
            return new[] { target };
        }

        private static VirtualState[] AfterInitialTransition(VirtualStateMachine machine, VirtualState[] states,
            AnimatorReachability analysis)
        {
            var initial = machine.DefaultState;
            if (initial == null || !states.Contains(initial)) return states;
            var first = Enabled(initial.Transitions).FirstOrDefault(analysis.CanTake);
            if (first == null || !Immediate(first, analysis)) return states;
            var entry = Enabled(machine.EntryTransitions).FirstOrDefault(analysis.CanTake);
            // Entry selection is used only after an actual Exit transition. Unity
            // does not necessarily select the matching Entry edge on startup.
            VirtualState Destination(VirtualTransitionBase transition)
            {
                if (!transition.IsExit) return transition.DestinationStateMachine == null ? transition.DestinationState : null;
                if (entry == null) return initial;
                return Guaranteed(entry, analysis) && !entry.IsExit && entry.DestinationStateMachine == null
                    ? entry.DestinationState : null;
            }
            var target = Destination(first);
            if (target == null || target == initial || !states.Contains(target)) return states;
            // Exclude only this transient motion output. Keep every state and
            // behaviour, and reject a possible return to the startup state.
            if (states.Where(s => s != initial).Any(s => Enabled(s.Transitions).Where(analysis.CanTake)
                .Any(t => Destination(t) == null || Destination(t) == initial))) return states;
            return states.Where(s => s != initial).ToArray();
        }

        private static bool Immediate(VirtualStateTransition transition, AnimatorReachability analysis)
            => !transition.ExitTime.HasValue && transition.Duration == 0 && MotionAnalysis.Finite(transition.Offset) &&
                Guaranteed(transition, analysis);

        private static bool Guaranteed(VirtualTransitionBase transition, AnimatorReachability analysis)
            => transition.Conditions.All(c => System.Enum.IsDefined(typeof(AnimatorConditionMode), c.mode) &&
                MotionAnalysis.Finite(c.threshold) && analysis.Values(c.parameter).TryGetConstant(out _) &&
                analysis.Values(c.parameter).CanSatisfy(new[] { c }));

        private static IEnumerable<T> Enabled<T>(IEnumerable<T> transitions) where T : VirtualTransitionBase
        {
            var all = transitions.ToArray(); bool solo = all.Any(t => t.Solo);
            return all.Where(t => !t.Mute && (!solo || t.Solo));
        }

        internal static bool WeightCanChange(IDictionary<object, VirtualAnimatorController> entries,
            VirtualAnimatorController writerController, VirtualLayer writerLayer, AnimatorReachability analysis)
            => new WeightControls(entries, analysis).CanChange(writerController, writerLayer);

        // The reachable behaviour inventory is shared by all layer queries in
        // this analysis only. Keep ownership: FX references use virtual indices,
        // while controls in other playables use the writer's physical ordering.
        private sealed class WeightControls
        {
            private readonly bool playable;
            private readonly List<(object Owner, VirtualAnimatorController Controller, int Layer)> layers = new();

            internal WeightControls(IDictionary<object, VirtualAnimatorController> entries, AnimatorReachability analysis)
            {
                using var cost = AnalysisCost.Measure("FX layer/all-controller weight scan");
                foreach (var entry in entries)
                foreach (var node in entry.Value.AllReachableNodes())
                {
                    var behaviours = node is VirtualState state ? analysis.IsReachable(state) ? state.Behaviours : Enumerable.Empty<StateMachineBehaviour>() :
                        node is VirtualStateMachine machine ? machine.Behaviours : Enumerable.Empty<StateMachineBehaviour>();
                    foreach (var behaviour in behaviours)
                    {
                        if (behaviour is VRC_PlayableLayerControl control && control.layer == VRC_PlayableLayerControl.BlendableLayer.FX &&
                            (control.goalWeight != 1 || !MotionAnalysis.Finite(control.blendDuration)))
                        { playable = true; return; }
                        if (behaviour is VRC_AnimatorLayerControl layer && layer.playable == VRC_AnimatorLayerControl.BlendableLayer.FX &&
                            (layer.goalWeight != 1 || !MotionAnalysis.Finite(layer.blendDuration)))
                            layers.Add((entry.Key, entry.Value, layer.layer));
                    }
                }
            }

            internal bool CanChange(VirtualAnimatorController writerController, VirtualLayer writerLayer)
            {
                if (playable) return true;
                foreach (var control in layers)
                    if (IsFx(control.Owner) && control.Controller == writerController)
                    {
                        if (control.Layer == writerLayer.VirtualLayerIndex) return true;
                    }
                    else if (control.Layer >= 0 && writerController.Layers.ElementAtOrDefault(control.Layer) == writerLayer) return true;
                return false;
            }
        }
    }
}
