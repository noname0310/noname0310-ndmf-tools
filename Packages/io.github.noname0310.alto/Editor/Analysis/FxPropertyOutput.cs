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
        private readonly Dictionary<VirtualLayer, bool> unsafeClips = new(), changingWeights = new();
        private readonly Dictionary<VirtualLayer, VirtualState[]> outputs = new();

        internal FxPropertyOutput(IDictionary<object, VirtualAnimatorController> entries, AnimatorReachability analysis)
        {
            this.entries = entries; this.analysis = analysis;
            entries.TryGetValue(VRCAvatarDescriptor.AnimLayerType.FX, out controller);
            layers = controller?.Layers.ToArray() ?? System.Array.Empty<VirtualLayer>();
        }

        internal static bool IsFx(object owner) => owner is VRCAvatarDescriptor.AnimLayerType type &&
            type == VRCAvatarDescriptor.AnimLayerType.FX;

        internal bool MatchesFinalOverride(IEnumerable<(object Owner, VirtualLayer Layer)> writers,
            EditorCurveBinding binding, float value, Dictionary<VirtualMotion, HashSet<EditorCurveBinding>> bindings)
        {
            var contexts = writers.Distinct().ToArray();
            if (analysis.FallbackReason != null || contexts.Length == 0 || contexts.Any(c => !IsFx(c.Owner))) return false;
            if (layers.Length == 0 || layers[0].AvatarMask != null) return false;
            foreach (var context in contexts)
            {
                if (!unsafeClips.TryGetValue(context.Layer, out bool unsafeLayer))
                    unsafeClips[context.Layer] = unsafeLayer = context.Layer.AllReachableNodes().OfType<VirtualClip>().Any(c => c.IsMarkerClip || c.Legacy ||
                        c.AdditiveReferencePoseClip != null || MotionAnalysis.Source(c) == null || MotionAnalysis.Source(c).events.Length != 0);
                if (unsafeLayer) return false;
            }
            var layer = layers.LastOrDefault(l => contexts.Any(c => c.Layer == l));
            if (layer == null || contexts.Any(c => !layers.Contains(c.Layer)) || layer.AvatarMask != null ||
                layer.BlendingMode != AnimatorLayerBlendingMode.Override || layer.SyncedLayerIndex != -1 ||
                layer != layers[0] && layer.DefaultWeight != 1) return false;
            if (!changingWeights.TryGetValue(layer, out bool changing))
                changingWeights[layer] = changing = WeightCanChange(entries, controller, layer, analysis);
            if (changing) return false;
            if (!outputs.TryGetValue(layer, out var states)) outputs[layer] = states = OutputStates(layer, analysis);
            // Missing tracks can expose lower layers, even with Write Defaults.
            // Require explicit output instead of assuming the original scene value.
            return states.Length > 0 && states.All(s => ConstantPropertyOutput.Matches(s.Motion, binding, value,
                false, analysis, bindings, new Dictionary<VirtualMotion, bool>(), new HashSet<VirtualMotion>()));
        }

        internal static VirtualState[] OutputStates(VirtualLayer layer, AnimatorReachability analysis)
        {
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
        {
            foreach (var entry in entries)
            foreach (var node in entry.Value.AllReachableNodes())
            {
                var behaviours = node is VirtualState state ? analysis.IsReachable(state) ? state.Behaviours : Enumerable.Empty<StateMachineBehaviour>() :
                    node is VirtualStateMachine machine ? machine.Behaviours : Enumerable.Empty<StateMachineBehaviour>();
                foreach (var behaviour in behaviours)
                {
                    if (behaviour is VRC_PlayableLayerControl playable && playable.layer == VRC_PlayableLayerControl.BlendableLayer.FX &&
                        (playable.goalWeight != 1 || !MotionAnalysis.Finite(playable.blendDuration))) return true;
                    if (!(behaviour is VRC_AnimatorLayerControl layer) || layer.playable != VRC_AnimatorLayerControl.BlendableLayer.FX ||
                        layer.goalWeight == 1 && MotionAnalysis.Finite(layer.blendDuration)) continue;
                    // NDMF virtualizes same-playable references. Other playables
                    // keep their physical index in the final ordered controller.
                    if (IsFx(entry.Key) && entry.Value == writerController)
                    {
                        if (layer.layer == writerLayer.VirtualLayerIndex) return true;
                    }
                    else if (layer.layer >= 0 && writerController.Layers.ElementAtOrDefault(layer.layer) == writerLayer) return true;
                }
            }
            return false;
        }
    }
}
