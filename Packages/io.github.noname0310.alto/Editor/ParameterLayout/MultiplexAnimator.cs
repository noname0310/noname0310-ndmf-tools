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
            var snapshot = Enumerable.Range(0, encoding.EpochCount).Select(e => State("Mux Snapshot E" + e)).ToArray();
            Edge(entry, snapshot[0], Local(true)); Edge(entry, seek, Local(false));
            var localStates = new List<VirtualState>(snapshot);
            var remoteStates = new List<VirtualState> { seek };
            if (encoding.Protocol == 2)
            {
                AppendAdaptive();
                return;
            }
            var receiveStates = new VirtualState[2, encoding.Batches];
            var awaitStates = new VirtualState[2, encoding.Batches];
            var expiredStates = new VirtualState[2, encoding.Batches];

            for (int epoch = 0; epoch < 2; epoch++)
            {
                var capture = new List<Operation> { Set(encoding.Tag, 0) };
                foreach (var input in encoding.Inputs)
                    capture.Add(active.Contains(input.Name) ? Copy(input.Name, captured[input.Name]) :
                        Set(captured[input.Name], fallback.TryGetValue(input.Name, out var value) ? value : 0));
                Driver(snapshot[epoch], capture, true);
                VirtualState previousSend = snapshot[epoch];
                for (int batch = 0; batch < encoding.Batches; batch++)
                {
                    int code = encoding.Code(epoch, batch);
                    string suffix = " E" + epoch + " B" + batch;
                    var invalidate = State("Mux Invalidate" + suffix);
                    var cleared = State("Mux Cleared" + suffix);
                    var prepare = State("Mux Prepare" + suffix);
                    var ready = State("Mux Ready" + suffix);
                    var publish = State("Mux Publish" + suffix);
                    var hold = State("Mux Hold" + suffix);
                    localStates.AddRange(new[] { invalidate, cleared, prepare, ready, publish, hold });
                    var payload = new List<Operation>();
                    // Every slot is written on every batch, so unused cells never transmit stale data.
                    for (int slot = 0; slot < encoding.Slots.Count; slot++)
                    {
                        var input = encoding.Inputs.FirstOrDefault(p => p.Batch == batch && p.Slot == slot);
                        payload.Add(input == null ? Set(encoding.Slots[slot].Name, 0) : Copy(captured[input.Name], encoding.Slots[slot].Name));
                    }
                    // Invalidate before reusing slots. Clearing the tag and changing
                    // payloads together lets independently delayed values expose the
                    // next payload while the previous publication tag is still visible.
                    Driver(invalidate, new[] { Set(encoding.Tag, 0) }, true);
                    Driver(prepare, payload, true);
                    Driver(publish, new[] { Set(encoding.Tag, code) }, true);
                    Edge(previousSend, invalidate, Local(true));
                    Timed(invalidate, cleared, encoding.HoldSeconds);
                    Edge(cleared, prepare, Local(true));
                    Timed(prepare, ready, encoding.HoldSeconds);
                    Edge(ready, publish, Local(true));
                    Timed(publish, hold, encoding.PublicationHoldSeconds);
                    previousSend = hold;
                    if (active.Count == 0) continue;

                    var wait = State("Mux Await" + suffix);
                    bool lastBatch = batch == encoding.Batches - 1;
                    var receive = State(lastBatch ? "Mux Commit E" + epoch : "Mux Receive" + suffix);
                    awaitStates[epoch, batch] = wait;
                    receiveStates[epoch, batch] = receive;
                    expiredStates[epoch, batch] = Expired(suffix, code);
                    remoteStates.AddRange(new[] { wait, receive });
                    if (batch == 0) Edge(seek, receive, Is(code));
                    Edge(wait, receive, Is(code));
                    int previousCode = batch == 0 ? encoding.Code(1 - epoch, encoding.Batches - 1) : code - 1;
                    Edge(wait, seek, Not(0), Not(previousCode), Not(code));
                    var received = encoding.Inputs.Where(p => p.Batch == batch && active.Contains(p.Name))
                        .Select(p => Copy(encoding.Slots[p.Slot].Name, pending[p.Name])).ToList();
                    // Sample on the frame that recognizes the tag. Requiring a second
                    // observation can reject every packet on a low-frame-rate receiver.
                    // Driver operations run in order, so the last batch can commit the
                    // complete snapshot in the same entry without another timed state.
                    if (lastBatch) received.AddRange(encoding.Inputs.Where(p => active.Contains(p.Name))
                        .Select(p => Copy(pending[p.Name], p.Name)));
                    Driver(receive, received, false);
                }
                Edge(previousSend, snapshot[1 - epoch], Local(true));
            }
            if (active.Count > 0)
                for (int epoch = 0; epoch < 2; epoch++)
                    for (int batch = 0; batch < encoding.Batches; batch++)
                    {
                        int nextEpoch = batch == encoding.Batches - 1 ? 1 - epoch : epoch;
                        int nextBatch = (batch + 1) % encoding.Batches;
                        int code = encoding.Code(epoch, batch), nextCode = encoding.Code(nextEpoch, nextBatch);
                        var receive = receiveStates[epoch, batch];
                        // The next publication can arrive without a rendered frame at
                        // tag zero. Consume it directly instead of losing an extra frame
                        // moving to an intermediate waiting state.
                        Edge(receive, receiveStates[nextEpoch, nextBatch], Is(nextCode));
                        Edge(receive, awaitStates[nextEpoch, nextBatch], Is(0));
                        Edge(receive, seek, Not(code), Not(0), Not(nextCode));
                        // Each observed preparation/publication phase has its own timeout.
                        // Slow sender frames must not use up the following phase's timeout.
                        Timed(receive, expiredStates[epoch, batch], encoding.ReceiveTimeoutSeconds);
                        Timed(awaitStates[nextEpoch, nextBatch], expiredStates[epoch, batch], encoding.ReceiveTimeoutSeconds);
                    }
            foreach (var state in localStates) Prepend(state, seek, Local(false));
            foreach (var state in remoteStates) Prepend(state, snapshot[0], Local(true));

            void AppendAdaptive()
            {
                int epochs = encoding.EpochCount;
                var receives = new VirtualState[epochs, encoding.Batches];
                var expired = new VirtualState[epochs, encoding.Batches];
                for (int epoch = 0; epoch < epochs; epoch++)
                {
                    var capture = new List<Operation> { Set(encoding.Tag, 0) };
                    foreach (var input in encoding.Inputs)
                        capture.Add(active.Contains(input.Name) ? Copy(input.Name, captured[input.Name]) :
                            Set(captured[input.Name], fallback.TryGetValue(input.Name, out var value) ? value : 0));
                    Driver(snapshot[epoch], capture, true);
                    VirtualState previous = snapshot[epoch];
                    for (int batch = 0; batch < encoding.Batches; batch++)
                    {
                        string suffix = " E" + epoch + " B" + batch;
                        int code = encoding.Code(epoch, batch);
                        var publish = State("Mux Publish" + suffix);
                        var hold = State("Mux Hold" + suffix);
                        localStates.AddRange(new[] { publish, hold });
                        Edge(previous, publish, Local(true));
                        var payload = new List<Operation>();
                        for (int slot = 0; slot < encoding.Slots.Count; slot++)
                        {
                            var input = encoding.Inputs.FirstOrDefault(p => p.Batch == batch && p.Slot == slot);
                            payload.Add(input == null ? Set(encoding.Slots[slot].Name, 0)
                                : Copy(captured[input.Name], encoding.Slots[slot].Name));
                        }
                        payload.Add(Set(encoding.Tag, code));
                        Driver(publish, payload, true);
                        Timed(publish, hold, encoding.PublicationSeconds(epoch));
                        previous = hold;
                        if (active.Count == 0) continue;
                        var receive = State(batch == encoding.Batches - 1 ? "Mux Commit E" + epoch : "Mux Receive" + suffix);
                        receives[epoch, batch] = receive; remoteStates.Add(receive);
                        expired[epoch, batch] = Expired(suffix, code);
                        if (batch == 0) Edge(seek, receive, Is(code));
                        var received = encoding.Inputs.Where(p => p.Batch == batch && active.Contains(p.Name))
                            .Select(p => Copy(encoding.Slots[p.Slot].Name, pending[p.Name])).ToList();
                        if (batch == encoding.Batches - 1)
                            received.AddRange(encoding.Inputs.Where(p => active.Contains(p.Name)).Select(p => Copy(pending[p.Name], p.Name)));
                        Driver(receive, received, false);
                    }
                    Edge(previous, snapshot[(epoch + 1) % epochs], Local(true));
                }
                if (active.Count > 0)
                    for (int epoch = 0; epoch < epochs; epoch++)
                        for (int batch = 0; batch < encoding.Batches; batch++)
                        {
                            int nextEpoch = batch == encoding.Batches - 1 ? (epoch + 1) % epochs : epoch;
                            int nextBatch = (batch + 1) % encoding.Batches;
                            int code = encoding.Code(epoch, batch), nextCode = encoding.Code(nextEpoch, nextBatch);
                            var receive = receives[epoch, batch];
                            Edge(receive, receives[nextEpoch, nextBatch], Is(nextCode));
                            Edge(receive, seek, Not(code), Not(0), Not(nextCode));
                            Timed(receive, expired[epoch, batch], encoding.ReceiveTimeoutSeconds);
                        }
                foreach (var state in localStates) Prepend(state, seek, Local(false));
                foreach (var state in remoteStates) Prepend(state, snapshot[0], Local(true));
            }

            VirtualState Expired(string suffix, int code)
            {
                // During an outage, the last synchronized tag remains visible. Seeking
                // immediately would recapture that stale first batch after every timeout
                // and could mix generations when the finite epoch identifier wraps.
                // Require a different publication before seeking a fresh snapshot.
                var state = State("Mux Expired" + suffix);
                remoteStates.Add(state);
                Edge(state, seek, Not(0), Not(code));
                return state;
            }

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
        private static void Edge(VirtualState from, VirtualState to, params AnimatorCondition[] conditions)
        {
            // Explicit conditions advance on the next Animator update. A fractional
            // exit time shorter than one frame can be missed until the next clip loop.
            if (conditions.Length == 0) throw new ArgumentException("A frame-step transition requires a condition.");
            from.Transitions = from.Transitions.Add(Transition(to, conditions));
        }
        private static void Timed(VirtualState from, VirtualState to, float seconds, params AnimatorCondition[] conditions)
        {
            // All states use a one-second timer clip. A full normalized cycle avoids
            // fractional exit windows while retaining the requested wall-clock hold.
            from.Speed = 1 / seconds;
            from.Transitions = from.Transitions.Add(Transition(to, conditions, 1));
        }
        private static void Prepend(VirtualState from, VirtualState to, params AnimatorCondition[] conditions) =>
            from.Transitions = from.Transitions.Insert(0, Transition(to, conditions));
    }
}
