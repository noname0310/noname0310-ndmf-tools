using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.Contact.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;
using VRC.SDKBase;

namespace Noname.AvatarTools.Analysis
{
    // Conditional value propagation over the final controller graphs. The value environment is
    // deliberately shared across layers/controllers: this over-approximates cross-layer writes and
    // asynchronous inputs without enumerating the product of all active state machines.
    internal sealed class AnimatorReachability
    {
        private sealed class Cell
        {
            internal readonly HashSet<AnimatorControllerParameterType> Types = new();
            internal ParameterValues Values;
            internal int Changes;
            internal readonly List<Action> Readers = new();
        }

        private sealed class Node
        {
            internal bool Reached;
            internal readonly List<Action> Work = new();
        }

        private sealed class Machine
        {
            internal VirtualStateMachine Source;
            internal Machine Parent;
            internal readonly Node Entry = new(), Active = new(), Exit = new();
        }

        // VRChat-owned inputs, including the default AV3 aliases. Never infer constants from their defaults.
        // https://creators.vrchat.com/avatars/animator-parameters/
        internal static readonly string[] BuiltIns = {
            "IsLocal", "PreviewMode", "Viseme", "Voice", "GestureLeft", "GestureRight",
            "GestureLeftWeight", "GestureRightWeight", "AngularY", "VelocityX", "VelocityY", "VelocityZ",
            "VelocityMagnitude", "Upright", "Grounded", "Seated", "AFK", "TrackingType", "VRMode",
            "MuteSelf", "InStation", "Earmuffs", "IsOnFriendsList", "AvatarVersion", "IsAnimatorEnabled",
            "ScaleModified", "ScaleFactor", "ScaleFactorInverse", "EyeHeightAsMeters", "EyeHeightAsPercent",
            "VRCEmote", "VRCFaceBlendH", "VRCFaceBlendV"
        };

        private readonly VirtualAnimatorController[] controllers;
        private readonly Dictionary<string, Cell> parameters = new(StringComparer.Ordinal);
        private readonly Dictionary<VirtualAnimatorController, HashSet<VirtualClip>> live = new();
        private readonly Dictionary<VirtualMotion, VirtualClip[]> motionClips = new();
        private readonly Queue<Action> pending = new();
        private readonly HashSet<Action> scheduled = new();
        private readonly List<Node> entries = new();
        private readonly HashSet<VirtualState> reachableStates = new();
        private readonly HashSet<VirtualStateMachine> reachableMachines = new();
        internal bool? LocalInstance { get; private set; }
        private bool fallback;
        internal int StateCount { get; private set; }
        internal int ReachableStateCount { get; private set; }
        internal string FallbackReason { get; private set; }
        internal HashSet<VirtualClip> Clips(VirtualAnimatorController controller) => live[controller];
        internal bool IsReachable(VirtualState state) => fallback || reachableStates.Contains(state);
        internal bool IsReachable(VirtualStateMachine machine) => fallback || reachableMachines.Contains(machine);
        internal ParameterValues Values(string name) => fallback ? ParameterValues.Unknown : Get(name);
        internal bool CanTake(VirtualTransitionBase transition) => fallback || transition.Conditions
            .GroupBy(c => c.parameter).All(g => Get(g.Key).CanSatisfy(g));

        internal static AnimatorReachability Analyze(GameObject avatar, IEnumerable<VirtualAnimatorController> source,
            IEnumerable<string> preservedParameters = null)
            => AnalyzeCore(avatar, source, preservedParameters, null);

        // Dependency contexts specialize only the runtime-owned instance identity. All
        // other values stay unknown, including private parameters explicitly preserved
        // by a caller whose input selection has not been resolved yet.
        internal static AnimatorReachability AnalyzeInstance(GameObject avatar,
            VirtualAnimatorController[] source, bool local)
            => AnalyzeCore(avatar, source, source.SelectMany(c => c.Parameters.Keys), local);

        private static AnimatorReachability AnalyzeCore(GameObject avatar, IEnumerable<VirtualAnimatorController> source,
            IEnumerable<string> preservedParameters, bool? local)
        {
            var result = new AnimatorReachability(source.Distinct().ToArray()) { LocalInstance = local };
            result.InitializeInputs(avatar);
            if (preservedParameters != null)
                foreach (string name in preservedParameters) result.AddValue(name, ParameterValues.Unknown);
            foreach (var controller in result.controllers) result.BuildController(controller);
            foreach (var entry in result.entries) result.Reach(entry);
            while (result.pending.Count > 0 && !result.fallback)
            {
                var action = result.pending.Dequeue(); result.scheduled.Remove(action); action();
            }
            if (result.fallback)
            {
                foreach (var controller in result.controllers)
                    result.live[controller].UnionWith(controller.AllReachableNodes().OfType<VirtualClip>());
                result.ReachableStateCount = result.StateCount;
            }
            return result;
        }

        private AnimatorReachability(VirtualAnimatorController[] source)
        {
            controllers = source;
            foreach (var controller in controllers)
            {
                live.Add(controller, new());
                foreach (var parameter in controller.Parameters.Values)
                {
                    double initial = parameter.type == AnimatorControllerParameterType.Bool ? (parameter.defaultBool ? 1 : 0) :
                        parameter.type == AnimatorControllerParameterType.Int ? parameter.defaultInt :
                        parameter.type == AnimatorControllerParameterType.Float ? parameter.defaultFloat : 0;
                    if (!parameters.TryGetValue(parameter.name, out var cell))
                        parameters.Add(parameter.name, cell = new Cell { Values = ParameterValues.Constant(initial) });
                    else cell.Values = cell.Values.Union(ParameterValues.Constant(initial));
                    cell.Types.Add(parameter.type);
                }
            }
        }

        private void InitializeInputs(GameObject avatar)
        {
            foreach (string name in BuiltIns) AddValue(name, ParameterValues.Unknown);
            foreach (string name in AvatarParameterInputs.ExternalNames(avatar)) AddValue(name, ParameterValues.Unknown);
            var external = ExternalParameterAccess.Collect();
            foreach (string name in parameters.Keys)
                if (external.IsWritten(name)) AddValue(name, ParameterValues.Unknown);
        }

        private ParameterValues Get(string name)
            => LocalInstance.HasValue && name == "IsLocal" ? ParameterValues.Constant(LocalInstance.Value ? 1 : 0) :
                name != null && parameters.TryGetValue(name, out var cell) ? cell.Values : ParameterValues.Unknown;

        private void AddValue(string name, ParameterValues value)
        {
            if (string.IsNullOrEmpty(name) || !parameters.TryGetValue(name, out var cell)) return;
            if (LocalInstance.HasValue && name == "IsLocal") return;
            var union = cell.Values;
            // A shared name can be Bool in one playable controller and Float in another.
            // Union every typed interpretation; identical read-only defaults remain constants.
            foreach (var type in cell.Types) union = union.Union(value.ConvertTo(type));
            value = union;
            if (cell.Values.Same(value)) return;
            if (++cell.Changes > 16) value = ParameterValues.Unknown; // Widen cyclic dependencies.
            cell.Values = value;
            foreach (var reader in cell.Readers) Enqueue(reader);
        }

        private void Watch(string name, Action reader)
        {
            if (name != null && parameters.TryGetValue(name, out var cell)) cell.Readers.Add(reader);
        }

        private void Enqueue(Action action) { if (scheduled.Add(action)) pending.Enqueue(action); }
        private void Reach(Node node)
        {
            if (node.Reached) return;
            node.Reached = true;
            foreach (var action in node.Work) Enqueue(action);
        }

        private void PreserveAll(string reason) { fallback = true; FallbackReason = reason; }

        private void BuildController(VirtualAnimatorController controller)
        {
            foreach (var layer in controller.Layers)
            {
                if (layer.SyncedLayerIndex != -1)
                {
                    // Synced layers can replace both motions and behaviours. Keep their entire dependency graph.
                    PreserveAll("A controller uses synchronized layers.");
                }
                if (layer.StateMachine == null) continue;
                var machines = new Dictionary<VirtualStateMachine, Machine>();
                var states = new Dictionary<VirtualState, Node>();
                var owners = new Dictionary<VirtualState, Machine>();
                IndexMachine(layer.StateMachine, null, machines, states, owners);
                var root = machines[layer.StateMachine];
                entries.Add(root.Entry);
                foreach (var machine in machines.Values)
                {
                    if (machine.Source.DefaultState != null && !states.ContainsKey(machine.Source.DefaultState))
                        PreserveAll("A default state is outside the indexed state machine.");
                    machine.Active.Work.Add(() => {
                        reachableMachines.Add(machine.Source);
                        if (machine.Parent != null) Reach(machine.Parent.Active);
                        Behaviours(machine.Source.Behaviours);
                    });
                    machine.Entry.Work.Add(() => {
                        Reach(machine.Active);
                        if (machine.Source.DefaultState != null && states.TryGetValue(machine.Source.DefaultState, out var node)) Reach(node);
                        else
                        {
                            // An invalid/missing default must not make an entire machine appear dead.
                            foreach (var child in machine.Source.States)
                                if (child.State != null && states.TryGetValue(child.State, out var possible)) Reach(possible);
                            foreach (var child in machine.Source.StateMachines)
                                if (child.StateMachine != null) Reach(machines[child.StateMachine].Entry);
                        }
                    });
                    Connect(machine.Entry, machine.Source.EntryTransitions, machine, machines, states);
                    Connect(machine.Active, machine.Source.AnyStateTransitions, machine, machines, states);
                    if (machine.Parent == null) machine.Exit.Work.Add(() => Reach(machine.Entry));
                    else
                    {
                        if (machine.Parent.Source.StateMachineTransitions.TryGetValue(machine.Source, out var transitions))
                            Connect(machine.Exit, transitions, machine.Parent, machines, states);
                        // Preserve possible enclosing fallback/re-entry paths as well as explicit exits.
                        machine.Exit.Work.Add(() => { Reach(machine.Parent.Entry); Reach(machine.Parent.Exit); });
                    }
                }
                foreach (var pair in states)
                {
                    var state = pair.Key; var node = pair.Value; var owner = owners[state];
                    node.Work.Add(() => {
                        ReachableStateCount++;
                        reachableStates.Add(state);
                        Reach(owner.Active);
                        Behaviours(state.Behaviours);
                        Motion(controller, state.Motion);
                    });
                    Connect(node, state.Transitions, owner, machines, states);
                }
            }
        }

        private void IndexMachine(VirtualStateMachine source, Machine parent, Dictionary<VirtualStateMachine, Machine> machines,
            Dictionary<VirtualState, Node> states, Dictionary<VirtualState, Machine> owners)
        {
            if (machines.ContainsKey(source)) { PreserveAll("A state machine has multiple owners or a cycle."); return; }
            var machine = new Machine { Source = source, Parent = parent }; machines.Add(source, machine);
            foreach (var child in source.States)
            {
                if (child.State == null) continue;
                if (states.ContainsKey(child.State)) { PreserveAll("A state has multiple owners."); continue; }
                states.Add(child.State, new Node()); owners.Add(child.State, machine); StateCount++;
            }
            foreach (var child in source.StateMachines)
                if (child.StateMachine != null) IndexMachine(child.StateMachine, machine, machines, states, owners);
        }

        private void Connect(Node source, IEnumerable<VirtualTransitionBase> transitions, Machine owner,
            Dictionary<VirtualStateMachine, Machine> machines, Dictionary<VirtualState, Node> states)
        {
            var all = transitions.ToArray();
            bool solo = all.Any(t => t.Solo);
            foreach (var transition in all.Where(t => !t.Mute && (!solo || t.Solo)))
            {
                Node target;
                if (transition.IsExit) target = owner.Exit;
                else if (transition.DestinationState != null && states.TryGetValue(transition.DestinationState, out var state)) target = state;
                else if (transition.DestinationStateMachine != null && machines.TryGetValue(transition.DestinationStateMachine, out var machine)) target = machine.Entry;
                else { PreserveAll("A transition has an unsupported destination."); continue; }
                var groups = transition.Conditions.GroupBy(c => c.parameter).Select(g => (name: g.Key, tests: g.ToArray())).ToArray();
                Action evaluate = () => {
                    if (source.Reached && groups.All(g => Get(g.name).CanSatisfy(g.tests))) Reach(target);
                };
                source.Work.Add(evaluate);
                foreach (var group in groups) Watch(group.name, evaluate);
            }
        }

        private void Behaviours(IEnumerable<StateMachineBehaviour> behaviours)
        {
            foreach (var behaviour in behaviours)
            {
                if (behaviour is VRC_AvatarParameterDriver driver)
                {
                    if (driver.localOnly && LocalInstance == false) continue;
                    if (driver.parameters == null) continue;
                    // Include both local and remote execution. Ordered assignments are over-approximated by union.
                    foreach (var operation in driver.parameters)
                    {
                        if (operation == null) continue;
                        switch (operation.type)
                        {
                            case VRC_AvatarParameterDriver.ChangeType.Set:
                                AddValue(operation.name, ParameterValues.Constant(operation.value)); break;
                            case VRC_AvatarParameterDriver.ChangeType.Random:
                                var value = ParameterValues.Range(operation.valueMin, operation.valueMax);
                                if (parameters.TryGetValue(operation.name ?? "", out var destination) && destination.Types.Any(t =>
                                    t == AnimatorControllerParameterType.Bool || t == AnimatorControllerParameterType.Trigger))
                                {
                                    var booleans = ParameterValues.Constant(0).Union(ParameterValues.Constant(1));
                                    value = destination.Types.All(t => t == AnimatorControllerParameterType.Bool || t == AnimatorControllerParameterType.Trigger)
                                        ? booleans : value.Union(booleans);
                                }
                                AddValue(operation.name, value); break;
                            case VRC_AvatarParameterDriver.ChangeType.Copy:
                                Action copy = () => AddValue(operation.name, operation.convertRange ? ParameterValues.Unknown : Get(operation.source));
                                Watch(operation.source, copy); Enqueue(copy); break;
                            case VRC_AvatarParameterDriver.ChangeType.Add:
                                // Repeated state entry can accumulate without bound, including across other layers.
                                if (operation.value != 0) AddValue(operation.name, ParameterValues.Unknown);
                                break;
                            default: AddValue(operation.name, ParameterValues.Unknown); break;
                        }
                    }
                }
                else if (!(behaviour is VRC_AnimatorLayerControl) && !(behaviour is VRC_PlayableLayerControl) &&
                         !(behaviour is VRC_AnimatorTrackingControl) && !(behaviour is VRC_AnimatorLocomotionControl) &&
                         !(behaviour is VRC_AnimatorTemporaryPoseSpace) && !(behaviour is VRC_AnimatorPlayAudio))
                    PreserveAll("A reachable state uses an unsupported state machine behaviour.");
            }
        }

        private void Motion(VirtualAnimatorController controller, VirtualMotion motion)
        {
            if (motion == null) return;
            if (!motionClips.TryGetValue(motion, out var clips))
                motionClips.Add(motion, clips = motion.AllReachableNodes().OfType<VirtualClip>().Distinct().ToArray());
            foreach (var clip in clips)
            {
                if (!live[controller].Add(clip)) continue;
                // Include every Blend Tree child. Animated parameters can be blended or extrapolated, so
                // endpoint/keyframe ranges alone are insufficient to prove a constant or a bound.
                foreach (var binding in clip.GetFloatCurveBindings())
                    if (binding.type == typeof(Animator)) AddValue(binding.propertyName, ParameterValues.Unknown);
            }
        }
    }
}
