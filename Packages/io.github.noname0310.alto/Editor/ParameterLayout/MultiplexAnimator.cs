using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDKBase;
using Parameter = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters.Parameter;
using Operation = VRC.SDKBase.VRC_AvatarParameterDriver.Parameter;

namespace Noname.AvatarTools.ParameterLayout
{
    internal static class MultiplexAnimator
    {
        internal static void Append(CloneContext clone, VirtualAnimatorController controller, MultiplexEncoding encoding,
            IReadOnlyDictionary<string, Parameter> logical, ISet<string> active, IReadOnlyDictionary<string, float> fallback,
            ISet<string> occupied, Action<UnityEngine.Object> save)
        {
            encoding.Validate();
            var identityType = ParameterTransportIdentity.Ensure(controller);
            bool writeDefaults = ParameterTransportDefaults.Read(controller);
            AnimatorCondition Local(bool local) => ParameterTransportIdentity.Condition(identityType, local);
            Parameter(encoding.Tag, AnimatorControllerParameterType.Int, 0);
            foreach (var slot in encoding.Slots) Parameter(slot.Name, Type(slot.Type), 0);
            foreach (var input in encoding.Inputs.Where(p => active.Contains(p.Name)))
                Parameter(input.Name, Type(input.Type), logical[input.Name].defaultValue, allowNumericConversion: true);
            var captured = encoding.Inputs.ToDictionary(p => p.Name,
                p => Buffer("Captured", p), StringComparer.Ordinal);
            var pending = encoding.Inputs.Where(p => active.Contains(p.Name)).ToDictionary(p => p.Name,
                p => Buffer("Pending", p), StringComparer.Ordinal);
            string clock = MultiplexEncoding.Allocate(occupied, "__AdvancedMux/Clock");
            Parameter(clock, AnimatorControllerParameterType.Float, 0);
            var timer = VirtualClip.Create("Multiplex timer (no pose output)");
            timer.SetFloatCurve(EditorCurveBinding.FloatCurve("", typeof(Animator), clock), AnimationCurve.Constant(0, 1, 0));
            var layer = controller.AddLayer(new LayerPriority(int.MaxValue), "ALTO Time Multiplexing");
            layer.DefaultWeight = 1;
            var machine = layer.StateMachine = VirtualStateMachine.Create(clone, layer.Name);
            VirtualState State(string name) { var state = machine.AddState(name, timer); state.WriteDefaultValues = writeDefaults; return state; }
            var entry = State("Mux Initialize"); machine.DefaultState = entry;
            var seek = State("Mux Seek");
            var snapshot = new[] { State("Mux Snapshot E0"), State("Mux Snapshot E1") };
            Edge(entry, snapshot[0], Local(true)); Edge(entry, seek, Local(false));
            var localStates = new List<VirtualState>(snapshot);
            var remoteStates = new List<VirtualState> { seek };
            var firstWait = new VirtualState[2];

            for (int epoch = 0; epoch < 2; epoch++)
            {
                var capture = new List<Operation> { Set(encoding.Tag, 0) };
                foreach (var input in encoding.Inputs)
                    capture.Add(active.Contains(input.Name) ? Copy(input.Name, captured[input.Name]) :
                        Set(captured[input.Name], fallback.TryGetValue(input.Name, out var value) ? value : 0));
                Driver(snapshot[epoch], capture, true);
                VirtualState previousSend = snapshot[epoch], previousReceive = null;
                for (int batch = 0; batch < encoding.Batches; batch++)
                {
                    int code = encoding.Code(epoch, batch);
                    string suffix = " E" + epoch + " B" + batch;
                    var prepare = State("Mux Prepare" + suffix);
                    var ready = State("Mux Ready" + suffix);
                    var publish = State("Mux Publish" + suffix);
                    var hold = State("Mux Hold" + suffix);
                    localStates.AddRange(new[] { prepare, ready, publish, hold });
                    var payload = new List<Operation> { Set(encoding.Tag, 0) };
                    // Every slot is written on every batch, so unused cells never transmit stale data.
                    for (int slot = 0; slot < encoding.Slots.Count; slot++)
                    {
                        var input = encoding.Inputs.FirstOrDefault(p => p.Batch == batch && p.Slot == slot);
                        payload.Add(input == null ? Set(encoding.Slots[slot].Name, 0) : Copy(captured[input.Name], encoding.Slots[slot].Name));
                    }
                    Driver(prepare, payload, true);
                    Driver(publish, new[] { Set(encoding.Tag, code) }, true);
                    Timed(previousSend, prepare, .03f);
                    Timed(prepare, ready, encoding.HoldSeconds);
                    Timed(ready, publish, .03f);
                    Timed(publish, hold, encoding.HoldSeconds);
                    previousSend = hold;
                    if (active.Count == 0) continue;

                    var wait = State("Mux Await" + suffix);
                    var confirm = State("Mux Confirm" + suffix);
                    var receive = State("Mux Receive" + suffix);
                    remoteStates.AddRange(new[] { wait, confirm, receive });
                    if (batch == 0)
                    {
                        firstWait[epoch] = wait;
                        Edge(seek, confirm, Is(code));
                    }
                    else Edge(previousReceive, wait);
                    Edge(wait, confirm, Is(code));
                    int previousCode = batch == 0 ? encoding.Code(1 - epoch, encoding.Batches - 1) : code - 1;
                    Edge(wait, seek, Not(0), Not(previousCode), Not(code));
                    // An interrupted receiver must discard its partial snapshot before
                    // the finite epoch identifier can wrap around and match again.
                    Timed(wait, seek, Math.Max(.5f, 4 * encoding.HoldSeconds));
                    Edge(confirm, seek, Not(code));
                    Timed(confirm, receive, .03f, Is(code));
                    Edge(receive, seek, Not(code));
                    Driver(receive, encoding.Inputs.Where(p => p.Batch == batch && active.Contains(p.Name))
                        .Select(p => Copy(encoding.Slots[p.Slot].Name, pending[p.Name])), false);
                    previousReceive = receive;
                }
                Timed(previousSend, snapshot[1 - epoch], .03f);
                if (active.Count > 0)
                {
                    var commit = State("Mux Commit E" + epoch);
                    remoteStates.Add(commit);
                    Edge(previousReceive, commit);
                    Driver(commit, encoding.Inputs.Where(p => active.Contains(p.Name)).Select(p => Copy(pending[p.Name], p.Name)), false);
                    // Link both epochs only after both wait states have been constructed.
                }
            }
            if (active.Count > 0)
                for (int epoch = 0; epoch < 2; epoch++)
                    Edge(remoteStates.Single(s => s.Name == "Mux Commit E" + epoch), firstWait[1 - epoch]);
            foreach (var state in localStates) Prepend(state, seek, Local(false));
            foreach (var state in remoteStates) Prepend(state, snapshot[0], Local(true));

            string Buffer(string kind, MultiplexInput input)
            {
                string name = MultiplexEncoding.Allocate(occupied, "__AdvancedMux/" + kind + "/" + input.Name);
                Parameter(name, Type(input.Type), 0); return name;
            }
            void Parameter(string name, AnimatorControllerParameterType type, float initial, bool allowNumericConversion = false)
            {
                if (controller.Parameters.TryGetValue(name, out var present))
                {
                    if (present.type != type && (!allowNumericConversion || present.type == AnimatorControllerParameterType.Trigger))
                        throw new InvalidOperationException("Conflicting parameter type in multiplexing controller: " + name);
                    return;
                }
                controller.Parameters = controller.Parameters.Add(name, new AnimatorControllerParameter
                { name = name, type = type, defaultFloat = initial, defaultInt = (int)initial, defaultBool = initial != 0 });
            }
            void Driver(VirtualState state, IEnumerable<Operation> operations, bool local)
            {
                var driver = ScriptableObject.CreateInstance<VRCAvatarParameterDriver>();
                driver.localOnly = local; driver.parameters = operations.ToList();
                if (driver.parameters.Count == 0) { UnityEngine.Object.DestroyImmediate(driver); return; }
                state.Behaviours = state.Behaviours.Add(driver); save?.Invoke(driver);
            }
            AnimatorCondition Is(int value) => new() { parameter = encoding.Tag, mode = AnimatorConditionMode.Equals, threshold = value };
            AnimatorCondition Not(int value) => new() { parameter = encoding.Tag, mode = AnimatorConditionMode.NotEqual, threshold = value };
        }

        private static AnimatorControllerParameterType Type(VRCExpressionParameters.ValueType type) => type switch
        {
            VRCExpressionParameters.ValueType.Float => AnimatorControllerParameterType.Float,
            VRCExpressionParameters.ValueType.Int => AnimatorControllerParameterType.Int,
            _ => AnimatorControllerParameterType.Bool
        };
        private static Operation Set(string name, float value) => new() { name = name, type = VRC_AvatarParameterDriver.ChangeType.Set, value = value };
        private static Operation Copy(string source, string name) => new() { name = name, source = source, type = VRC_AvatarParameterDriver.ChangeType.Copy };
        private static VirtualStateTransition Transition(VirtualState target, AnimatorCondition[] conditions, float? time = null)
        {
            var transition = VirtualStateTransition.Create(); transition.SetDestination(target);
            transition.Duration = 0; transition.ExitTime = time; transition.HasFixedDuration = true;
            transition.Conditions = conditions.ToImmutableList(); transition.CanTransitionToSelf = false;
            return transition;
        }
        private static void Edge(VirtualState from, VirtualState to, params AnimatorCondition[] conditions) =>
            // Unity ignores transitions with neither a condition nor an exit time.
            // The short hold also allows entry-time Driver operations to run.
            from.Transitions = from.Transitions.Add(Transition(to, conditions, conditions.Length == 0 ? .03f : null));
        private static void Timed(VirtualState from, VirtualState to, float seconds, params AnimatorCondition[] conditions) =>
            from.Transitions = from.Transitions.Add(Transition(to, conditions, seconds));
        private static void Prepend(VirtualState from, VirtualState to, params AnimatorCondition[] conditions) =>
            from.Transitions = from.Transitions.Insert(0, Transition(to, conditions));
    }
}
