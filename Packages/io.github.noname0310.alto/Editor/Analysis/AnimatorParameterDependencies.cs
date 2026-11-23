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

        // Shared immutable facts for one liveness query. Local/remote execution
        // still has separate reachability and dependency sets; only structural
        // facts and pure output predicates are shared between those contexts.
        private sealed class Facts
        {
            internal readonly HashSet<string> Names = new(StringComparer.Ordinal);
            internal readonly Dictionary<VirtualClip, HashSet<string>> Declarations = new();
            internal readonly Dictionary<VRC_AvatarParameterDriver, HashSet<VirtualAnimatorController>> DriverOwners = new();
            internal readonly VirtualNode[] Nodes;
            private readonly Dictionary<VirtualClip, ClipOutputs> clipOutputs = new();
            private readonly Dictionary<VirtualLayer, VirtualNode[]> layers = new();
            private readonly Dictionary<VirtualClip, bool> clipDurations = new();
            private readonly Dictionary<VirtualMotion, bool> motionDurations = new();
            private readonly Func<VirtualClip, EditorCurveBinding, bool> observable;

            internal readonly struct ClipOutputs
            {
                internal readonly string[] Parameters;
                internal readonly bool Observable;
                internal ClipOutputs(string[] parameters, bool observable)
                { Parameters = parameters; Observable = observable; }
            }

            internal Facts(VirtualAnimatorController[] controllers, Func<VirtualClip, EditorCurveBinding, bool> observable)
            {
                this.observable = observable;
                var all = new HashSet<VirtualNode>();
                var ordered = new List<VirtualNode>();
                foreach (var controller in controllers)
                {
                    Names.UnionWith(controller.Parameters.Keys);
                    var declared = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var parameter in controller.Parameters.Values)
                        if (parameter.type == AnimatorControllerParameterType.Float) declared.Add(parameter.name);
                    foreach (var node in controller.AllReachableNodes())
                    {
                        if (all.Add(node)) ordered.Add(node);
                        if (node is VirtualClip clip)
                        {
                            if (Declarations.TryGetValue(clip, out var existing)) existing.IntersectWith(declared);
                            else Declarations.Add(clip, new HashSet<string>(declared, StringComparer.Ordinal));
                        }
                        IEnumerable<StateMachineBehaviour> behaviours = node is VirtualState state ? state.Behaviours :
                            node is VirtualStateMachine machine ? machine.Behaviours : Array.Empty<StateMachineBehaviour>();
                        foreach (var behaviour in behaviours)
                            if (behaviour is VRC_AvatarParameterDriver driver)
                            {
                                if (!DriverOwners.TryGetValue(driver, out var owners)) DriverOwners.Add(driver, owners = new());
                                owners.Add(controller);
                            }
                    }
                }
                Nodes = ordered.ToArray();
            }

            internal VirtualNode[] LayerNodes(VirtualLayer layer)
            {
                if (!layers.TryGetValue(layer, out var nodes)) layers.Add(layer, nodes = layer.AllReachableNodes().ToArray());
                return nodes;
            }

            internal bool HasDuration(VirtualMotion motion)
            {
                if (motionDurations.TryGetValue(motion, out bool result)) return result;
                foreach (var clip in MotionAnalysis.Clips(motion))
                {
                    if (!clipDurations.TryGetValue(clip, out bool timed))
                        clipDurations.Add(clip, timed = MotionAnalysis.EndTime(clip) != 0 || clip.Settings.stopTime != 0);
                    if (timed) { result = true; break; }
                }
                motionDurations.Add(motion, result); return result;
            }

            internal ClipOutputs Outputs(VirtualClip clip)
            {
                if (clipOutputs.TryGetValue(clip, out var previous)) return previous;
                var parameters = new List<string>(); bool output = false;
                foreach (var binding in clip.GetFloatCurveBindings())
                {
                    if (binding.type == typeof(Animator) && binding.path == "" && !binding.isDiscreteCurve &&
                        Declarations[clip].Contains(binding.propertyName)) parameters.Add(binding.propertyName);
                    else if (!output && (observable == null || observable(clip, binding))) output = true;
                }
                if (!output)
                    foreach (var binding in clip.GetObjectCurveBindings())
                        if (observable == null || observable(clip, binding)) { output = true; break; }
                var source = MotionAnalysis.Source(clip);
                output |= clip.IsMarkerClip || source == null || source.humanMotion || source.events.Length > 0;
                var result = new ClipOutputs(parameters.ToArray(), output);
                clipOutputs.Add(clip, result); return result;
            }
        }

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
            using var cost = AnalysisCost.Measure("Parameter dependencies");
            var contexts = AnimatorInstanceContexts.Analyze(avatar, controllers, controllerEntries);
            var facts = new Facts(controllers, observableBinding);
            if (contexts == null) return CollectInstance(avatar, controllers, forExpressions, observableBinding, analysis, facts);
            var parts = new AnimatorParameterDependencies[contexts.Length];
            for (int i = 0; i < parts.Length; i++)
                parts[i] = CollectInstance(avatar, controllers, forExpressions, observableBinding, analysis, facts, contexts[i]);
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
            Facts facts,
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
            var names = facts.Names;
            var declarations = facts.Declarations;
            var motionInputs = new Dictionary<VirtualClip, HashSet<string>>();
            var driverOwners = facts.DriverOwners;
            var live = new HashSet<string>(AnimatorReachability.BuiltIns, StringComparer.Ordinal);
            var dependencies = names.ToDictionary(n => n, _ => new HashSet<string>(StringComparer.Ordinal));
            // Menu and Expression declarations are input sources, not evidence of observable use.
            // Existing animation-curve pruning keeps its stronger external-output contract.
            if (!forExpressions) live.UnionWith(AvatarParameterInputs.ExternalNames(avatar));
            var external = ExternalParameterAccess.Collect();
            live.UnionWith(names.Where(n => external.IsRead(n) || external.IsWritten(n)));

            void Visit(VirtualMotion motion, HashSet<string> inputs, HashSet<VirtualMotion> stack)
            {
                if (motion == null || !stack.Add(motion)) return;
                if (motion is VirtualClip clip)
                {
                    if (!motionInputs.TryGetValue(clip, out var controls)) motionInputs.Add(clip, controls = new());
                    controls.UnionWith(inputs);
                    var output = facts.Outputs(clip);
                    foreach (string parameter in output.Parameters) dependencies[parameter].UnionWith(inputs);
                    if (output.Observable) live.UnionWith(inputs);
                    if (clip.IsMarkerClip) live.UnionWith(output.Parameters);
                }
                else if (motion is VirtualBlendTree tree)
                {
                    // Borrow one input set for this depth-first traversal. A
                    // branch only adds its own controls and restores them on
                    // return; no retained dependency summary owns this scratch.
                    bool addedX = false, addedY = false;
                    List<string> addedWeights = null;
                    if (tree.BlendType != BlendTreeType.Direct)
                    {
                        addedX = inputs.Add(tree.BlendParameter);
                        // Simple1D retains a serialized Y name, but Unity does not read it.
                        if (tree.BlendType != BlendTreeType.Simple1D) addedY = inputs.Add(tree.BlendParameterY);
                    }
                    else if (tree.NormalizedBlendValues)
                        foreach (var child in tree.Children)
                            if (inputs.Add(child.DirectBlendParameter)) (addedWeights ??= new()).Add(child.DirectBlendParameter);
                    // Tree weights also influence effective clip duration, and therefore exit times
                    // and normalized transition durations, even when the sampled values are unused.
                    if (!forExpressions && facts.HasDuration(tree))
                    {
                        if (tree.BlendType == BlendTreeType.Direct) live.UnionWith(tree.Children.Select(c => c.DirectBlendParameter));
                        else
                        {
                            live.Add(tree.BlendParameter);
                            if (tree.BlendType != BlendTreeType.Simple1D) live.Add(tree.BlendParameterY);
                        }
                    }
                    try
                    {
                        foreach (var child in tree.Children)
                        {
                            bool added = tree.BlendType == BlendTreeType.Direct && inputs.Add(child.DirectBlendParameter);
                            try { Visit(child.Motion, inputs, stack); }
                            finally { if (added) inputs.Remove(child.DirectBlendParameter); }
                        }
                    }
                    finally
                    {
                        if (addedX) inputs.Remove(tree.BlendParameter);
                        if (addedY) inputs.Remove(tree.BlendParameterY);
                        if (addedWeights != null) foreach (string weight in addedWeights) inputs.Remove(weight);
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
            using (AnalysisCost.Measure("Dependency layer summaries"))
            foreach (var layer in controllers.SelectMany(c => c.Layers))
            {
                var layerNodes = facts.LayerNodes(layer);
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
                        if (!facts.HasDuration(tree)) continue;
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
            var nodes = facts.Nodes;
            if (!forExpressions)
                foreach (var transition in activeTransitions)
                    live.UnionWith(transition.Conditions.Select(c => c.parameter));
            using (AnalysisCost.Measure("Dependency motion traversal"))
            {
                var motionStack = new HashSet<VirtualMotion>();
                var motionControls = new HashSet<string>(StringComparer.Ordinal);
                foreach (var node in nodes)
                {
                    if (!(node is VirtualState state) || !StateActive(state)) continue;
                    // State timing can control transitions and behaviour execution without any pose output.
                    if (!forExpressions) live.UnionWith(new[] { state.TimeParameter, state.SpeedParameter, state.CycleOffsetParameter, state.MirrorParameter });
                    motionControls.Clear();
                    if (stateInputs.TryGetValue(state, out var inputs)) motionControls.UnionWith(inputs);
                    Visit(state.Motion, motionControls, motionStack);
                }
            }
            var behaviours = behaviourInputs.Keys;
            var faceTracking = new NativeFaceTracking(avatar);
            using (AnalysisCost.Measure("Dependency behaviour summaries"))
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
                else if (behaviour is VRC_AnimatorFaceTrackingBlendshapesSet faceControl)
                {
                    foreach (var control in NativeFaceTracking.Controls(faceControl))
                    {
                        var outputs = faceTracking.SetOutputs(control.blendshapesSetName);
                        var inputs = new HashSet<string>(behaviourInputs[behaviour], StringComparer.Ordinal);
                        if (control.type == VRC_AnimatorFaceTrackingBlendshapesSet.SourceType.Parameter && !string.IsNullOrEmpty(control.sourceParameter))
                            inputs.Add(control.sourceParameter);
                        if (outputs.Geometry) live.UnionWith(inputs);
                        foreach (string name in outputs.Parameters)
                        {
                            if (!dependencies.TryGetValue(name, out var readers)) dependencies.Add(name, readers = new());
                            readers.UnionWith(inputs);
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
