using System;
using System.Collections.Generic;
using System.Linq;
using Noname.AvatarTools.Analysis;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.Contact.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;
using VRC.SDKBase;

namespace Noname.AvatarTools.Analysis
{
    // Backward liveness from observable outputs. Edges point from a computed parameter to the
    // parameters required to calculate it. A dead strongly connected component has no live root.
    internal sealed class AnimatorParameterDependencies
    {
        internal HashSet<string> ObservableInputs;
        private Dictionary<string, HashSet<string>> dependencies;
        private Dictionary<VirtualClip, HashSet<string>> declarations;
        private Dictionary<VirtualClip, HashSet<string>> motionInputs;
        private AnimatorParameterDependencies[] instances;
        private HashSet<string> networkParameters;
        private HashSet<StateMachineBehaviour> activeBehaviours;
        internal VirtualClip[] Clips;

        internal bool MotionDependsOn(VirtualClip clip, ISet<string> inputs) =>
            motionInputs.TryGetValue(clip, out var controls) && controls.Overlaps(inputs);

        internal IEnumerable<string> MotionInputs(VirtualClip clip) =>
            motionInputs.TryGetValue(clip, out var controls) ? controls : Array.Empty<string>();

        internal bool IsParameter(VirtualClip clip, EditorCurveBinding binding) =>
            binding.type == typeof(Animator) && binding.path == "" && !binding.isDiscreteCurve &&
            declarations.TryGetValue(clip, out var names) && names.Contains(binding.propertyName);

        internal HashSet<string> Expand(IEnumerable<string> roots)
        {
            if (instances != null) return ExpandInstances(roots, false);
            var result = new HashSet<string>((roots ?? Array.Empty<string>()).Where(n => !string.IsNullOrEmpty(n)), StringComparer.Ordinal);
            var pending = new Queue<string>(result);
            while (pending.Count > 0)
                if (dependencies.TryGetValue(pending.Dequeue(), out var inputs))
                    foreach (string input in inputs)
                        if (!string.IsNullOrEmpty(input) && result.Add(input)) pending.Enqueue(input);
            return result;
        }

        internal HashSet<string> ExpandObservable(IEnumerable<string> additionalRoots = null) => instances != null
            ? ExpandInstances(additionalRoots, true) : Expand(ObservableInputs.Concat(additionalRoots ?? Array.Empty<string>()));

        private HashSet<string> ExpandInstances(IEnumerable<string> roots, bool observable)
            => new(ExpandInstanceSets(roots, observable).SelectMany(s => s), StringComparer.Ordinal);

        private HashSet<string>[] ExpandInstanceSets(IEnumerable<string> roots, bool observable)
        {
            var extra = (roots ?? Array.Empty<string>()).ToArray();
            var remote = instances[1].Expand(observable ? instances[1].ObservableInputs.Concat(extra) : extra);
            // A remote reader of a synchronized value also observes its local producer.
            // Private names do not cross the instance boundary, nor do remote writes flow back.
            var local = new HashSet<string>(extra, StringComparer.Ordinal);
            if (observable) local.UnionWith(instances[0].ObservableInputs);
            local.UnionWith(remote.Where(networkParameters.Contains));
            return new[] { instances[0].Expand(local), remote };
        }

        internal sealed class Liveness
        {
            private readonly AnimatorParameterDependencies[] parts;
            private readonly HashSet<string>[] live;
            internal readonly HashSet<string> Names;
            internal Liveness(AnimatorParameterDependencies[] parts, HashSet<string>[] live)
            {
                this.parts = parts; this.live = live;
                Names = new(live.SelectMany(s => s), StringComparer.Ordinal);
            }
            internal ISet<string> ForBehaviour(StateMachineBehaviour behaviour) => Select(p => p.activeBehaviours.Contains(behaviour));
            internal ISet<string> ForClip(VirtualClip clip) => Select(p => p.motionInputs.ContainsKey(clip));
            private ISet<string> Select(Func<AnimatorParameterDependencies, bool> active)
            {
                if (parts.Length == 1) return Names;
                bool local = active(parts[0]), remote = active(parts[1]);
                return local && remote ? Names : local ? live[0] : remote ? live[1] : new HashSet<string>(StringComparer.Ordinal);
            }
        }

        internal Liveness FindLive(IEnumerable<string> additionalRoots = null) => instances == null
            ? new(new[] { this }, new[] { ExpandObservable(additionalRoots) })
            : new(instances, ExpandInstanceSets(additionalRoots, true));

        // Forward influence is used to validate the scope of a destructive parameter policy.
        internal HashSet<string> AffectedBy(IEnumerable<string> inputs, Func<string, bool> canVary = null)
        {
            if (instances != null)
            {
                var seeds = inputs.ToArray();
                var local = instances[0].AffectedBy(seeds, canVary);
                var remote = instances[1].AffectedBy(seeds.Concat(local.Where(networkParameters.Contains)), canVary);
                local.UnionWith(remote);
                return local;
            }
            var reverse = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var pair in dependencies)
                foreach (var input in pair.Value.Where(n => !string.IsNullOrEmpty(n)))
                {
                    if (!reverse.TryGetValue(input, out var outputs)) reverse.Add(input, outputs = new());
                    outputs.Add(pair.Key);
                }
            var result = new HashSet<string>(inputs.Where(n => canVary == null || canVary(n)), StringComparer.Ordinal);
            var pending = new Queue<string>(result);
            while (pending.Count > 0)
                if (reverse.TryGetValue(pending.Dequeue(), out var outputs))
                    foreach (var output in outputs)
                        if ((canVary == null || canVary(output)) && result.Add(output)) pending.Enqueue(output);
            return result;
        }

        // Writer-originated influence starts only in the instances where that writer
        // executes. Unioning writer names first would manufacture cross-instance paths
        // through private temporary values. External inputs remain possible in both.
        internal HashSet<string> AffectedByWriters(IEnumerable<string> externalInputs,
            Func<VirtualClip, IEnumerable<string>> clipSources,
            Func<StateMachineBehaviour, IEnumerable<string>> behaviourSources)
        {
            var external = (externalInputs ?? Array.Empty<string>()).ToArray();
            if (instances != null)
            {
                var local = instances[0].AffectedByWriters(external, clipSources, behaviourSources);
                var remote = instances[1].AffectedByWriters(external.Concat(local.Where(networkParameters.Contains)),
                    clipSources, behaviourSources);
                local.UnionWith(remote);
                return local;
            }
            return AffectedBy(external.Concat(motionInputs.Keys.SelectMany(clipSources))
                .Concat(activeBehaviours.SelectMany(behaviourSources)).Where(n => !string.IsNullOrEmpty(n)));
        }

        internal static AnimatorParameterDependencies Collect(GameObject avatar, VirtualAnimatorController[] controllers,
            bool forExpressions = false, Func<VirtualClip, EditorCurveBinding, bool> observableBinding = null,
            AnimatorReachability analysis = null, IDictionary<object, VirtualAnimatorController> controllerEntries = null)
        {
            var contexts = AnimatorInstanceContexts.Analyze(avatar, controllers, controllerEntries);
            if (contexts == null) return CollectInstance(avatar, controllers, forExpressions, observableBinding, analysis);
            var parts = contexts.Select(c => CollectInstance(avatar, controllers, forExpressions, observableBinding, analysis, c)).ToArray();
            var result = parts[0];
            // Keep the combined wrapper separate: each closure must traverse one instance
            // graph before crossing an explicit network edge.
            var combined = new AnimatorParameterDependencies {
                instances = parts, declarations = result.declarations, Clips = result.Clips,
                ObservableInputs = new(parts.SelectMany(p => p.ObservableInputs), StringComparer.Ordinal),
                motionInputs = new(), networkParameters = new((avatar.GetComponent<VRCAvatarDescriptor>()
                    .expressionParameters?.parameters ?? Array.Empty<VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters.Parameter>())
                    .Where(p => p != null && p.networkSynced && !string.IsNullOrEmpty(p.name)).Select(p => p.name), StringComparer.Ordinal)
            };
            foreach (var part in parts)
                foreach (var pair in part.motionInputs)
                {
                    if (!combined.motionInputs.TryGetValue(pair.Key, out var inputs)) combined.motionInputs.Add(pair.Key, inputs = new());
                    inputs.UnionWith(pair.Value);
                }
            return combined;
        }

        private static AnimatorParameterDependencies CollectInstance(GameObject avatar, VirtualAnimatorController[] controllers,
            bool forExpressions, Func<VirtualClip, EditorCurveBinding, bool> observableBinding, AnimatorReachability analysis,
            AnimatorInstanceContexts.Context instance = null)
        {
            bool contextual = instance != null;
            bool StateActive(VirtualState s) => !contextual || instance.IsReachable(s);
            bool MachineActive(VirtualStateMachine m) => !contextual || instance.IsReachable(m);
            IEnumerable<VirtualTransitionBase> Enabled(IEnumerable<VirtualTransitionBase> source)
            {
                var transitions = source.ToArray(); bool solo = transitions.Any(t => t.Solo);
                return transitions.Where(t => !t.Mute && (!solo || t.Solo) && instance.CanTake(t));
            }
            var names = new HashSet<string>(controllers.SelectMany(c => c.Parameters.Keys), StringComparer.Ordinal);
            var declarations = new Dictionary<VirtualClip, HashSet<string>>();
            var motionInputs = new Dictionary<VirtualClip, HashSet<string>>();
            var driverOwners = new Dictionary<VRC_AvatarParameterDriver, HashSet<VirtualAnimatorController>>();
            foreach (var controller in controllers)
            {
                var controllerNodes = controller.AllReachableNodes().ToArray();
                var declared = controller.Parameters.Values.Where(p => p.type == AnimatorControllerParameterType.Float).Select(p => p.name).ToArray();
                foreach (var clip in controllerNodes.OfType<VirtualClip>())
                    if (declarations.TryGetValue(clip, out var existing)) existing.IntersectWith(declared);
                    else declarations.Add(clip, new HashSet<string>(declared, StringComparer.Ordinal));
                foreach (var driver in controllerNodes.OfType<VirtualState>().SelectMany(s => s.Behaviours)
                    .Concat(controllerNodes.OfType<VirtualStateMachine>().SelectMany(s => s.Behaviours))
                    .OfType<VRC_AvatarParameterDriver>())
                {
                    if (!driverOwners.TryGetValue(driver, out var owners)) driverOwners.Add(driver, owners = new());
                    owners.Add(controller);
                }
            }
            var live = new HashSet<string>(AnimatorReachability.BuiltIns, StringComparer.Ordinal);
            var dependencies = names.ToDictionary(n => n, _ => new HashSet<string>(StringComparer.Ordinal));
            // Menu and Expression declarations are input sources, not evidence of observable use.
            // Existing animation-curve pruning keeps its stronger external-output contract.
            if (!forExpressions) live.UnionWith(AvatarParameterInputs.ExternalNames(avatar));
            var external = ExternalParameterAccess.Collect();
            live.UnionWith(names.Where(n => external.IsRead(n) || external.IsWritten(n)));

            bool Parameter(VirtualClip clip, EditorCurveBinding binding) => binding.type == typeof(Animator) && binding.path == "" &&
                !binding.isDiscreteCurve && declarations[clip].Contains(binding.propertyName);
            void Output(VirtualClip clip, EditorCurveBinding binding, IEnumerable<string> inputs)
            {
                if (Parameter(clip, binding)) dependencies[binding.propertyName].UnionWith(inputs);
                else if (observableBinding == null || observableBinding(clip, binding)) live.UnionWith(inputs);
            }
            void Visit(VirtualMotion motion, HashSet<string> inputs, HashSet<VirtualMotion> stack)
            {
                if (motion == null || !stack.Add(motion)) return;
                if (motion is VirtualClip clip)
                {
                    if (!motionInputs.TryGetValue(clip, out var controls)) motionInputs.Add(clip, controls = new());
                    controls.UnionWith(inputs);
                    foreach (var binding in clip.GetFloatCurveBindings()) Output(clip, binding, inputs);
                    var source = MotionAnalysis.Source(clip);
                    if (clip.GetObjectCurveBindings().Any(b => observableBinding == null || observableBinding(clip, b)) || clip.IsMarkerClip || source == null ||
                        source.humanMotion || source.events.Length > 0) live.UnionWith(inputs);
                    if (clip.IsMarkerClip) live.UnionWith(clip.GetFloatCurveBindings().Where(b => Parameter(clip, b)).Select(b => b.propertyName));
                }
                else if (motion is VirtualBlendTree tree)
                {
                    var shared = new HashSet<string>(inputs);
                    if (tree.BlendType != BlendTreeType.Direct)
                    {
                        shared.Add(tree.BlendParameter);
                        // Simple1D retains a serialized Y name, but Unity does not read it.
                        if (tree.BlendType != BlendTreeType.Simple1D) shared.Add(tree.BlendParameterY);
                    }
                    else if (tree.NormalizedBlendValues) shared.UnionWith(tree.Children.Select(c => c.DirectBlendParameter));
                    // Tree weights also influence effective clip duration, and therefore exit times
                    // and normalized transition durations, even when the sampled values are unused.
                    if (!forExpressions && MotionAnalysis.Clips(tree).Any(c => MotionAnalysis.EndTime(c) != 0 || c.Settings.stopTime != 0))
                    {
                        if (tree.BlendType == BlendTreeType.Direct) live.UnionWith(tree.Children.Select(c => c.DirectBlendParameter));
                        else
                        {
                            live.Add(tree.BlendParameter);
                            if (tree.BlendType != BlendTreeType.Simple1D) live.Add(tree.BlendParameterY);
                        }
                    }
                    foreach (var child in tree.Children)
                    {
                        var childInputs = new HashSet<string>(shared);
                        if (tree.BlendType == BlendTreeType.Direct) childInputs.Add(child.DirectBlendParameter);
                        Visit(child.Motion, childInputs, stack);
                    }
                }
                stack.Remove(motion);
            }
            var stateInputs = new Dictionary<VirtualState, HashSet<string>>();
            var behaviourInputs = new Dictionary<StateMachineBehaviour, HashSet<string>>();
            var activeTransitions = new HashSet<VirtualTransitionBase>();
            var defaults = new DefaultWriteAnalysis();
            bool hasPoseOutput = forExpressions && declarations.Keys.Any(c =>
                c.GetFloatCurveBindings().Any(b => observableBinding == null || observableBinding(c, b)) ||
                c.GetObjectCurveBindings().Any(b => observableBinding == null || observableBinding(c, b)) ||
                c.IsMarkerClip || MotionAnalysis.Source(c) == null || MotionAnalysis.Source(c).humanMotion);
            foreach (var layer in controllers.SelectMany(c => c.Layers))
            {
                var layerNodes = layer.AllReachableNodes().ToArray();
                var statesInContext = layerNodes.OfType<VirtualState>().Where(StateActive).ToArray();
                var machinesInContext = layerNodes.OfType<VirtualStateMachine>().Where(MachineActive).ToArray();
                var treesInContext = (contextual ? statesInContext.Where(s => s.Motion != null)
                    .SelectMany(s => s.Motion.AllReachableNodes()).Distinct() : layerNodes).OfType<VirtualBlendTree>().ToArray();
                var transitionsInContext = contextual ? statesInContext.SelectMany(s => Enabled(s.Transitions))
                    .Concat(machinesInContext.SelectMany(m => Enabled(m.EntryTransitions).Concat(Enabled(m.AnyStateTransitions))
                        .Concat(m.StateMachineTransitions.Values.SelectMany(Enabled)))).Distinct().ToArray() :
                    layerNodes.OfType<VirtualTransitionBase>().ToArray();
                activeTransitions.UnionWith(transitionsInContext);
                var controls = new HashSet<string>(transitionsInContext
                    .SelectMany(t => t.Conditions).Select(c => c.parameter), StringComparer.Ordinal);
                if (forExpressions)
                {
                    // Timing can reach any later state/behaviour in this layer. Include the entire
                    // layer's timing inputs as dependencies, but root them only when it has output.
                    foreach (var state in statesInContext)
                        controls.UnionWith(new[] { state.TimeParameter, state.SpeedParameter, state.CycleOffsetParameter, state.MirrorParameter });
                    foreach (var tree in treesInContext)
                    {
                        if (!MotionAnalysis.Clips(tree).Any(c => MotionAnalysis.EndTime(c) != 0 || c.Settings.stopTime != 0)) continue;
                        if (tree.BlendType == BlendTreeType.Direct) controls.UnionWith(tree.Children.Select(c => c.DirectBlendParameter));
                        else
                        {
                            controls.Add(tree.BlendParameter);
                            if (tree.BlendType != BlendTreeType.Simple1D) controls.Add(tree.BlendParameterY);
                        }
                    }
                    // Empty clips with Write Defaults can reset bindings owned by other layers,
                    // including Animator parameter curves whose values can escape to the network.
                    // Ordinary WD-on motions write the same implicit defaults; mixed WD/null
                    // states and unsupported motions do not provide that guarantee. Treat their inputs
                    // as observable even when this layer has no explicit pose binding itself.
                    var states = statesInContext;
                    bool WritesDefaults(VirtualState s) => s.WriteDefaultValues && s.Motion != null;
                    if (hasPoseOutput && states.Any(WritesDefaults) && !defaults.Uniform(states))
                    {
                        live.UnionWith(controls);
                        foreach (var tree in treesInContext)
                            if (tree.BlendType == BlendTreeType.Direct) live.UnionWith(tree.Children.Select(c => c.DirectBlendParameter));
                            else
                            {
                                live.Add(tree.BlendParameter);
                                if (tree.BlendType != BlendTreeType.Simple1D) live.Add(tree.BlendParameterY);
                            }
                    }
                }
                foreach (var state in statesInContext)
                {
                    if (!stateInputs.TryGetValue(state, out var inputs)) stateInputs.Add(state, inputs = new());
                    inputs.UnionWith(controls);
                    inputs.UnionWith(new[] { state.TimeParameter, state.SpeedParameter, state.CycleOffsetParameter, state.MirrorParameter });
                }
                foreach (var behaviour in statesInContext.SelectMany(s => s.Behaviours)
                    .Concat(machinesInContext.SelectMany(s => s.Behaviours)))
                {
                    if (behaviour == null) continue;
                    if (!behaviourInputs.TryGetValue(behaviour, out var inputs)) behaviourInputs.Add(behaviour, inputs = new());
                    inputs.UnionWith(controls);
                }
            }
            var nodes = controllers.SelectMany(c => c.AllReachableNodes()).Distinct().ToArray();
            if (!forExpressions)
                foreach (var transition in activeTransitions)
                    live.UnionWith(transition.Conditions.Select(c => c.parameter));
            foreach (var state in nodes.OfType<VirtualState>().Where(StateActive))
            {
                // State timing can control transitions and behaviour execution without any pose output.
                if (!forExpressions) live.UnionWith(new[] { state.TimeParameter, state.SpeedParameter, state.CycleOffsetParameter, state.MirrorParameter });
                Visit(state.Motion, stateInputs.TryGetValue(state, out var inputs) ? inputs : new(), new());
            }
            var behaviours = behaviourInputs.Keys;
            foreach (var behaviour in behaviours)
            {
                if (behaviour is VRC_AvatarParameterDriver driver)
                {
                    if (instance != null && !instance.Executes(driver)) continue;
                    driverOwners.TryGetValue(driver, out var owners);
                    var ordered = ParameterDriverDataflow.Analyze(driver, owners);
                    var operations = driver.parameters ?? new();
                    for (int index = 0; index < operations.Count; index++)
                    {
                        var operation = operations[index];
                        if (operation != null && !string.IsNullOrEmpty(operation.name))
                        {
                            if (!dependencies.TryGetValue(operation.name, out var inputs)) dependencies.Add(operation.name, inputs = new());
                            if (behaviourInputs.TryGetValue(behaviour, out var controls))
                            {
                                inputs.UnionWith(controls);
                                // Random operations may advance a shared runtime random sequence.
                                // Their execution is observable even when the destination is unused.
                                if (operation.type == VRC_AvatarParameterDriver.ChangeType.Random) live.UnionWith(controls);
                            }
                            if (ordered != null) inputs.UnionWith(ordered[index]);
                            else
                            {
                                if (operation.type == VRC_AvatarParameterDriver.ChangeType.Copy) inputs.Add(operation.source);
                                if (operation.type == VRC_AvatarParameterDriver.ChangeType.Add) inputs.Add(operation.name);
                            }
                            if (operation.type == VRC_AvatarParameterDriver.ChangeType.Random && operation.preventRepeats)
                            {
                                // The old value can affect rejection sampling and thus the shared
                                // random sequence, even when the destination has no later reader.
                                if (ordered != null) live.UnionWith(ordered[index]);
                                else { inputs.Add(operation.name); live.Add(operation.name); }
                            }
                        }
                    }
                }
                else if (behaviour != null)
                {
                    // SDK behaviours can change tracking, locomotion, layer weights or audio
                    // even when they do not contain a parameter name themselves.
                    if (forExpressions && behaviourInputs.TryGetValue(behaviour, out var controls)) live.UnionWith(controls);
                    // Preserve parameter-looking strings in platform behaviours, including audio controls.
                    using var serialized = new SerializedObject(behaviour);
                    var property = serialized.GetIterator();
                    while (property.Next(true))
                        if (property.propertyType == SerializedPropertyType.String && names.Contains(property.stringValue)) live.Add(property.stringValue);
                }
            }
            return new AnimatorParameterDependencies
            {
                ObservableInputs = live, dependencies = dependencies, declarations = declarations, motionInputs = motionInputs,
                activeBehaviours = new(behaviourInputs.Keys.Where(b => instance == null || instance.Executes(b))),
                Clips = nodes.OfType<VirtualClip>().ToArray()
            };
        }
    }
}
