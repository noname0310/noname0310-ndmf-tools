using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.ParameterLayout;
using NUnit.Framework;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using VRC.SDKBase;
using Parameter = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters.Parameter;
using ValueType = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters.ValueType;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private AnimatorController NativeMultiplexController(MultiplexEncoding plan, Parameter[] logical, bool active = true,
            AnimatorControllerParameterType? identityType = null, bool writeDefaults = false)
        {
            var clone = new CloneContext(VRChatPlatformAnimatorBindings.Instance);
            var virtualController = VirtualAnimatorController.Create(clone, "Multiplex fixture");
            if (identityType.HasValue) virtualController.Parameters = virtualController.Parameters.Add("IsLocal",
                new AnimatorControllerParameter { name = "IsLocal", type = identityType.Value, defaultBool = true, defaultInt = 7, defaultFloat = .25f });
            var names = logical.Select(p => p.name).ToHashSet();
            MultiplexAnimator.Append(clone, virtualController, plan, logical.ToDictionary(p => p.name),
                active ? names : new HashSet<string>(), logical.ToDictionary(p => p.name, p => p.defaultValue),
                new HashSet<string>(names.Concat(plan.WireDeclarations().Select(p => p.name))), null);
            if (writeDefaults)
                foreach (var state in virtualController.AllReachableNodes().OfType<VirtualState>()) state.WriteDefaultValues = true;
            return CommitAnimator(virtualController, root);
        }

        // Actual native state transitions and timing, with SDK Set/Copy semantics modeled
        // explicitly. Packets are synthetic; this is not a VRChat client/network emulator.
        private sealed class NativeMultiplexPeer : IDisposable
        {
            private readonly PlayableGraph graph;
            internal readonly AnimatorControllerPlayable Playable;
            private readonly Dictionary<string, AnimatorControllerParameterType> types;
            private readonly Dictionary<int, AnimatorState> states;
            private readonly HashSet<string> synced;
            private int previous;
            internal int Commits;
            internal string State;

            internal NativeMultiplexPeer(GameObject owner, AnimatorController controller, MultiplexEncoding contract, bool local)
            {
                types = controller.parameters.ToDictionary(p => p.name, p => p.type);
                states = controller.layers.Single().stateMachine.states.Select(s => s.state).ToDictionary(s => Animator.StringToHash(s.name));
                synced = contract.WireDeclarations().Select(p => p.name).ToHashSet();
                graph = PlayableGraph.Create("Native multiplex peer"); graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                Playable = AnimatorControllerPlayable.Create(graph, controller);
                AnimationPlayableOutput.Create(graph, "Avatar", owner.AddComponent<Animator>()).SetSourcePlayable(Playable);
                graph.Play(); Write("IsLocal", local ? 1 : 0);
            }
            internal float Read(string name) => types[name] switch
            {
                AnimatorControllerParameterType.Bool => Playable.GetBool(name) ? 1 : 0,
                AnimatorControllerParameterType.Int => Playable.GetInteger(name),
                _ => Playable.GetFloat(name)
            };
            internal void Write(string name, float value)
            {
                if (synced.Contains(name)) value = types[name] switch
                { AnimatorControllerParameterType.Float => Mathf.Clamp(value, -1, 1), AnimatorControllerParameterType.Int => Mathf.Clamp(value, 0, 255), _ => value };
                switch (types[name])
                {
                    case AnimatorControllerParameterType.Bool: Playable.SetBool(name, value != 0); break;
                    case AnimatorControllerParameterType.Int: Playable.SetInteger(name, Mathf.FloorToInt(value)); break;
                    default: Playable.SetFloat(name, value); break;
                }
            }
            internal void Tick(float seconds)
            {
                graph.Evaluate(seconds);
                int state = Playable.GetCurrentAnimatorStateInfo(0).shortNameHash;
                State = states[state].name;
                if (state == previous) return;
                previous = state;
                if (State.StartsWith("Mux Commit", StringComparison.Ordinal)) Commits++;
                foreach (var driver in states[state].behaviours.OfType<VRC_AvatarParameterDriver>())
                {
                    if (driver.localOnly && Read("IsLocal") == 0) continue;
                    foreach (var operation in driver.parameters)
                    {
                        Assert.That(operation.type, Is.EqualTo(VRC_AvatarParameterDriver.ChangeType.Set).Or.EqualTo(VRC_AvatarParameterDriver.ChangeType.Copy));
                        Write(operation.name, operation.type == VRC_AvatarParameterDriver.ChangeType.Set ? operation.value : Read(operation.source));
                    }
                }
            }
            public void Dispose() => graph.Destroy();
        }

        private static void DeliverMultiplexFrame(NativeMultiplexPeer local, NativeMultiplexPeer remote, MultiplexEncoding plan)
        {
            // Delivery uses recorded slot order, including native Float quantization.
            var slots = plan.WireDeclarations();
            var values = slots.Select(p => p.valueType == ValueType.Float
                ? Mathf.Round(local.Read(p.name) * 127) / 127 : local.Read(p.name)).ToArray();
            for (int slot = 0; slot < slots.Length; slot++) remote.Write(slots[slot].name, values[slot]);
        }

        [TestCase(30, false)] [TestCase(60, false)] [TestCase(90, false)]
        [TestCase(30, true)] [TestCase(60, true)] [TestCase(90, true)]
        public void MultiplexingNativeSnapshotsStayCoherentWhenInputsChangeDuringTransmission(int fps, bool writeDefaults)
        {
            var input = MultiplexInputs(4, 4, 8); var plan = MultiplexPlan(input, 32);
            var controller = NativeMultiplexController(plan, input, writeDefaults: writeDefaults);
            using var local = new NativeMultiplexPeer(PolicyChild("Local"), controller, plan, true);
            using var remote = new NativeMultiplexPeer(PolicyChild("Remote"), controller, plan, false);
            float Value(Parameter p, int generation) => p.valueType switch
            { ValueType.Float => (generation == 0 ? 17 : -29) / 127f, ValueType.Int => generation == 0 ? 13 : 241, _ => generation == 0 ? 1 : 0 };
            foreach (var p in input) local.Write(p.name, Value(p, 0)); // Saved restoration before the first snapshot.
            bool changed = false; int previousCommits = 0;
            for (int frame = 0; frame < fps * 15 && remote.Commits < 2; frame++)
            {
                local.Tick(1f / fps);
                if (!changed && local.Read(plan.Tag) == plan.Code(0, 0))
                { foreach (var p in input) local.Write(p.name, Value(p, 1)); changed = true; }
                DeliverMultiplexFrame(local, remote, plan); remote.Tick(1f / fps);
                if (remote.Commits == 0)
                    foreach (var p in input) Assert.That(remote.Read(p.name), Is.Zero, "Incomplete snapshots cannot change logical outputs");
                if (remote.Commits != previousCommits)
                {
                    foreach (var p in input) Assert.That(remote.Read(p.name), Is.EqualTo(Value(p, previousCommits)).Within(1e-5f),
                        "All fields must come from one captured generation: " + p.name);
                    previousCommits = remote.Commits;
                }
            }
            Assert.That(remote.Commits, Is.EqualTo(2), "Receiver failed to converge; local=" + local.State + ", remote=" + remote.State);
            foreach (var p in input) Assert.That(local.Read(p.name), Is.EqualTo(Value(p, 1)).Within(1e-5f));
        }

        [TestCase(AnimatorControllerParameterType.Bool)]
        [TestCase(AnimatorControllerParameterType.Int)]
        [TestCase(AnimatorControllerParameterType.Float)]
        public void MultiplexingNativeUsesTheExistingIdentityTypeForOwnerAndRemote(AnimatorControllerParameterType identityType)
        {
            var input = MultiplexInputs(4, 4, 8); var plan = MultiplexPlan(input, 32);
            var controller = NativeMultiplexController(plan, input, identityType: identityType);
            Assert.That(controller.parameters.Single(p => p.name == "IsLocal").type, Is.EqualTo(identityType));
            using var local = new NativeMultiplexPeer(PolicyChild("Local"), controller, plan, true);
            using var remote = new NativeMultiplexPeer(PolicyChild("Remote"), controller, plan, false);
            float Value(Parameter p) => p.valueType switch { ValueType.Float => -29 / 127f, ValueType.Int => 241, _ => 1 };
            foreach (var p in input) local.Write(p.name, Value(p));
            for (int frame = 0; frame < 900 && remote.Commits < 2; frame++)
            { local.Tick(1f / 60); DeliverMultiplexFrame(local, remote, plan); remote.Tick(1f / 60); }
            Assert.That(remote.Commits, Is.EqualTo(2), "Typed identity did not route both peers to their transport states.");
            foreach (var p in input)
            {
                Assert.That(local.Read(p.name), Is.EqualTo(Value(p)).Within(1e-5));
                Assert.That(remote.Read(p.name), Is.EqualTo(Value(p)).Within(1e-5));
            }
        }

        [TestCase(false)] [TestCase(true)]
        public void ParameterTransportRejectsTriggerIdentityBeforeChangingTheController(bool multiplex)
        {
            var clone = new CloneContext(VRChatPlatformAnimatorBindings.Instance);
            var controller = VirtualAnimatorController.Create(clone, "Unsupported identity");
            controller.Parameters = controller.Parameters.Add("IsLocal", new AnimatorControllerParameter
                { name = "IsLocal", type = AnimatorControllerParameterType.Trigger });
            var original = controller.Parameters;
            if (multiplex)
            {
                var input = MultiplexInputs(); var plan = MultiplexPlan(input);
                Assert.Throws<InvalidOperationException>(() => MultiplexAnimator.Append(clone, controller, plan,
                    input.ToDictionary(p => p.name), input.Select(p => p.name).ToHashSet(), new Dictionary<string, float>(),
                    new HashSet<string>(), _ => Assert.Fail("No generated asset is allowed before identity validation.")));
            }
            else Assert.Throws<InvalidOperationException>(() => IntegerEncodingAnimator.Append(clone, controller, IntegerContract(), 0));
            Assert.That(controller.Parameters, Is.SameAs(original));
            Assert.That(controller.Layers, Is.Empty);
        }

        [TestCase(0, false)] [TestCase(25, false)] [TestCase(0, true)] [TestCase(25, true)]
        public void MultiplexingNativeReceiverResynchronizesAfterLateJoinOrAMissingBatch(int joinFrame, bool dropBatch)
        {
            var input = MultiplexInputs(); var plan = MultiplexPlan(input);
            var controller = NativeMultiplexController(plan, input);
            using var local = new NativeMultiplexPeer(PolicyChild("Local"), controller, plan, true);
            foreach (var p in input) local.Write(p.name, 63 / 127f);
            for (int i = 0; i < joinFrame; i++) local.Tick(1f / 60);
            using var remote = new NativeMultiplexPeer(PolicyChild("Remote"), controller, plan, false);
            bool skipped = false;
            for (int i = 0; i < 1200 && remote.Commits == 0; i++)
            {
                local.Tick(1f / 60);
                bool drop = dropBatch && local.Read(plan.Tag) == plan.Code(0, 1);
                if (drop) skipped = true;
                else DeliverMultiplexFrame(local, remote, plan);
                remote.Tick(1f / 60);
                if (remote.Commits == 0) foreach (var p in input) Assert.That(remote.Read(p.name), Is.Zero);
            }
            Assert.That(remote.Commits, Is.EqualTo(1), "A complete later snapshot must recover without reloading the avatar");
            Assert.That(skipped, Is.EqualTo(dropBatch));
            foreach (var p in input) Assert.That(remote.Read(p.name), Is.EqualTo(63 / 127f).Within(1e-5f));
        }

        [Test]
        public void MultiplexingAndroidOwnerSendsFrozenDefaultsForRemovedPcFeatures()
        {
            var input = MultiplexInputs(); var plan = MultiplexPlan(input);
            var senderController = NativeMultiplexController(plan, input, active: false);
            var receiverController = NativeMultiplexController(plan, input);
            using var local = new NativeMultiplexPeer(PolicyChild("Android Owner"), senderController, plan, true);
            using var remote = new NativeMultiplexPeer(PolicyChild("PC Observer"), receiverController, plan, false);
            foreach (var p in input) remote.Write(p.name, 1);
            for (int frame = 0; frame < 1200 && remote.Commits == 0; frame++)
            { local.Tick(1f / 60); DeliverMultiplexFrame(local, remote, plan); remote.Tick(1f / 60); }
            Assert.That(remote.Commits, Is.EqualTo(1));
            foreach (var p in input) Assert.That(remote.Read(p.name), Is.Zero);
        }

        [TestCase(30)] [TestCase(60)] [TestCase(90)]
        public void MultiplexingNativeRecoversFromAnOutageLongerThanAnEpochWrap(int fps)
        {
            var input = MultiplexInputs(); var plan = MultiplexPlan(input);
            var controller = NativeMultiplexController(plan, input);
            using var local = new NativeMultiplexPeer(PolicyChild("Local"), controller, plan, true);
            using var remote = new NativeMultiplexPeer(PolicyChild("Remote"), controller, plan, false);
            foreach (var p in input) local.Write(p.name, 17 / 127f);
            int outageStart = -1;
            for (int frame = 0; frame < fps * 20 && remote.Commits == 0; frame++)
            {
                local.Tick(1f / fps);
                if (outageStart < 0 && remote.State == "Mux Await E0 B1")
                {
                    outageStart = frame;
                    foreach (var p in input) local.Write(p.name, 63 / 127f);
                }
                if (outageStart < 0 || frame - outageStart >= fps * 4)
                    DeliverMultiplexFrame(local, remote, plan);
                remote.Tick(1f / fps);
                if (remote.Commits == 0) foreach (var p in input) Assert.That(remote.Read(p.name), Is.Zero);
            }
            Assert.That(outageStart, Is.GreaterThanOrEqualTo(0), "The outage must interrupt a partially received snapshot");
            Assert.That(remote.Commits, Is.EqualTo(1), "Reception must recover after the old epoch tag has wrapped");
            foreach (var p in input) Assert.That(remote.Read(p.name), Is.EqualTo(63 / 127f).Within(1e-5f),
                "No field from the interrupted snapshot may be committed: " + p.name);
        }

        [TestCase(30)] [TestCase(60)] [TestCase(90)]
        public void MultiplexingNativeHandlesIndependentlyDelayedWireChanges(int fps)
        {
            var input = MultiplexInputs(4, 4, 8); var plan = MultiplexPlan(input, 32);
            var controller = NativeMultiplexController(plan, input);
            using var local = new NativeMultiplexPeer(PolicyChild("Local"), controller, plan, true);
            using var remote = new NativeMultiplexPeer(PolicyChild("Remote"), controller, plan, false);
            var random = new System.Random(1907);
            var delivered = new Dictionary<string, float>();
            var changes = new List<(int Frame, string Name, float Value)>();
            var wire = plan.WireDeclarations();
            float Value(Parameter p, int generation) => p.valueType switch
            { ValueType.Float => (generation == 0 ? 17 : -29) / 127f, ValueType.Int => generation == 0 ? 13 : 241, _ => generation == 0 ? 1 : 0 };
            foreach (var p in input) local.Write(p.name, Value(p, 0));
            bool changed = false;
            for (int frame = 0; frame < fps * 20 && remote.Commits < 2; frame++)
            {
                local.Tick(1f / fps);
                if (!changed && local.Read(plan.Tag) == plan.Code(0, 0))
                { foreach (var p in input) local.Write(p.name, Value(p, 1)); changed = true; }
                foreach (var p in wire)
                {
                    float value = p.valueType == ValueType.Float ? Mathf.Round(local.Read(p.name) * 127) / 127 : local.Read(p.name);
                    if (delivered.TryGetValue(p.name, out var previous) && previous == value) continue;
                    delivered[p.name] = value;
                    // Independent jitter is bounded below the preparation hold. This
                    // is a stated transport assumption, not arbitrary network atomicity.
                    changes.Add((frame + random.Next(0, Math.Max(2, fps / 20)), p.name, value));
                }
                foreach (var change in changes.Where(c => c.Frame <= frame).OrderBy(c => c.Frame).ToArray())
                { remote.Write(change.Name, change.Value); changes.Remove(change); }
                int previousCommits = remote.Commits;
                remote.Tick(1f / fps);
                if (remote.Commits == 0) foreach (var p in input) Assert.That(remote.Read(p.name), Is.Zero);
                if (remote.Commits != previousCommits)
                    foreach (var p in input) Assert.That(remote.Read(p.name), Is.EqualTo(Value(p, previousCommits)).Within(1e-5f));
            }
            Assert.That(remote.Commits, Is.EqualTo(2), "Delayed wire values must converge without mixed snapshots");
        }
    }
}
