using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Noname.AvatarTools.AnimatorOptimization
{
    // An unreachable motion still registers properties in Unity's Write Defaults domain.
    // Preserve that per-layer domain without retaining every unreachable motion graph.
    internal static class UnreachableBindingCoverage
    {
        // Awake/OnEnable on an opaque behaviour can inspect other states or controllers,
        // even if its own state is unreachable. Do not compact any graph in that context.
        internal static bool HasKnownBehaviourEffects(IEnumerable<VirtualAnimatorController> controllers) =>
            controllers.SelectMany(c => c.AllReachableNodes()).SelectMany(node => node is VirtualState state ? state.Behaviours :
                node is VirtualStateMachine machine ? machine.Behaviours : Enumerable.Empty<StateMachineBehaviour>()).All(StateBoundSdkBehaviour);

        internal static void Consolidate(VirtualState[] states, VirtualState[] candidates, ISet<VirtualState> removed)
        {
            if (candidates.Length < 2) return;
            var eligible = new List<VirtualState>();
            var values = new Dictionary<EditorCurveBinding, float>();
            foreach (var state in candidates)
            {
                // Unknown behaviour initialization can be observable without state entry.
                if (state.Behaviours.Any(b => !StateBoundSdkBehaviour(b)) || !TryBindings(state.Motion, out var bindings)) continue;
                eligible.Add(state);
                foreach (var pair in bindings)
                    if (!values.ContainsKey(pair.Key)) values.Add(pair.Key, pair.Value);
            }
            // Each successful rewrite removes a state, so repeated final analysis reaches
            // a fixed point without names, hidden markers or a persistent side table.
            if (eligible.Count < 2) return;
            var replacing = new HashSet<VirtualState>(eligible);
            var retainedCoverage = new HashSet<EditorCurveBinding>(states
                .Where(s => !removed.Contains(s) && !replacing.Contains(s)).SelectMany(s => MotionAnalysis.Bindings(s.Motion)));
            var required = values.Where(p => !retainedCoverage.Contains(p.Key)).ToArray();
            if (required.Length == 0)
            {
                foreach (var state in eligible) removed.Add(state);
                return;
            }

            var carrier = VirtualClip.Create("ALTO Write Defaults Binding Coverage");
            foreach (var pair in required)
                carrier.SetFloatCurve(pair.Key, new AnimationCurve(new Keyframe(0, pair.Value)));
            // Keep an existing unreachable state's identity and owner. No new transition,
            // default state, layer, mask or Write Defaults setting is introduced.
            eligible[0].Motion = carrier;
            foreach (var state in eligible.Skip(1)) removed.Add(state);
        }

        private static bool StateBoundSdkBehaviour(StateMachineBehaviour behaviour)
        {
            // These SDK effects require state execution. Their initialization installs
            // runtime callbacks; it does not apply the tracking/parameter/layer output.
            // Exact types deliberately exclude user subclasses with extra callbacks.
            // https://creators.vrchat.com/avatars/state-behaviors/
            if (behaviour == null) return false;
            var type = behaviour.GetType();
            return type == typeof(VRCAnimatorTrackingControl) || type == typeof(VRCAvatarParameterDriver) ||
                type == typeof(VRCAnimatorLayerControl) || type == typeof(VRCPlayableLayerControl) ||
                type == typeof(VRCAnimatorLocomotionControl) || type == typeof(VRCAnimatorTemporaryPoseSpace) ||
                type == typeof(VRCAnimatorPlayAudio) || type == typeof(VRCAnimatorFaceTrackingBlendshapesSet);
        }

        private static bool TryBindings(VirtualMotion motion, out Dictionary<EditorCurveBinding, float> values)
        {
            var collected = new Dictionary<EditorCurveBinding, float>();
            var visiting = new HashSet<VirtualMotion>();
            var visited = new HashSet<VirtualMotion>();
            bool Visit(VirtualMotion node)
            {
                if (node == null || visiting.Contains(node)) return false;
                if (visited.Contains(node)) return true;
                visiting.Add(node);
                if (node is VirtualClip clip)
                {
                    var source = MotionAnalysis.Source(clip);
                    // PPtr curves also retain asset dependencies. Animator curves can
                    // change parameter/control semantics; discrete, humanoid, proxy and
                    // event-bearing motions need separate equivalence proofs.
                    if (clip.IsMarkerClip || clip.Legacy || clip.AdditiveReferencePoseClip != null ||
                        source == null || source.humanMotion || source.events.Length != 0 ||
                        clip.GetObjectCurveBindings().Any()) return false;
                    foreach (var binding in clip.GetFloatCurveBindings())
                    {
                        if (binding.type == null || binding.type == typeof(Animator) || binding.isDiscreteCurve) return false;
                        // Unity can alias/repack Euler and quaternion curves when they
                        // share a clip. Position and scale do not have this ambiguity.
                        if (typeof(Transform).IsAssignableFrom(binding.type) &&
                            !binding.propertyName.StartsWith("m_LocalPosition.", StringComparison.Ordinal) &&
                            !binding.propertyName.StartsWith("m_LocalScale.", StringComparison.Ordinal)) return false;
                        var curve = clip.GetFloatCurve(binding);
                        if (curve == null || curve.length == 0 || !MotionAnalysis.Finite(curve[0].value)) return false;
                        if (!collected.ContainsKey(binding)) collected.Add(binding, curve[0].value);
                    }
                }
                else if (node is VirtualBlendTree tree)
                {
                    foreach (var child in tree.Children)
                        if (!Visit(child.Motion)) return false;
                }
                else return false;
                visiting.Remove(node);
                visited.Add(node);
                return true;
            }
            bool valid = Visit(motion);
            values = collected;
            return valid;
        }
    }
}
