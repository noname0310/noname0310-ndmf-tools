using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.AnimatorOptimization;
using NUnit.Framework;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase;
using Change = VRC.SDKBase.VRC_AvatarParameterDriver.ChangeType;
using Operation = VRC.SDKBase.VRC_AvatarParameterDriver.Parameter;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        [TestCase(false, false)] [TestCase(false, true)] [TestCase(true, false)] [TestCase(true, true)]
        public void InternalParameterPropagation_EqualInitialCurveIsConstantAcrossLayerOrder(bool producerFirst, bool writeDefaults)
        {
            Mesh("Visible");
            var source = TreeController(("Signal", StaticCurve(Binding("Visible"), 100)));
            var parameters = source.parameters; parameters.Single().defaultFloat = .25f; source.parameters = parameters;
            var consumer = source.layers[0]; consumer.name = "Consumer";
            var producer = Controller(StaticCurve(ParameterBinding("Signal"), .25f)).layers[0]; producer.name = "Producer";
            consumer.stateMachine.defaultState.writeDefaultValues = producer.stateMachine.defaultState.writeDefaultValues = writeDefaults;
            source.layers = producerFirst ? new[] { producer, consumer } : new[] { consumer, producer };
            var result = Analyze(source);
            Assert.That(result.analysis.Values("Signal").TryGetConstant(out float value), Is.True);
            Assert.That(value, Is.EqualTo(.25f));
            UnityEngine.Animations.AnimatorControllerPlayable active = default;
            var samples = SampleFrozen(source, (p, frame) => active = p, 8, () => active.GetFloat("Signal"));
            Assert.That(samples, Is.All.EqualTo(value).Within(.00001f));
        }

        [TestCase(BlendTreeType.SimpleDirectional2D)]
        [TestCase(BlendTreeType.FreeformDirectional2D)]
        [TestCase(BlendTreeType.FreeformCartesian2D)]
        public void InternalParameterPropagation_TwoDimensionalRoundingCanReachAThresholdTransition(BlendTreeType type)
        {
            Mesh("Visible");
            var source = CounterexampleTwoDimensionalController(type,
                Enumerable.Range(0, 5).Select(_ => StaticCurve(ParameterBinding("Signal"), .7f)).ToArray());
            source.AddParameter("Signal", AnimatorControllerParameterType.Float);
            var parameters = source.parameters; parameters.Single(p => p.name == "Signal").defaultFloat = .7f; source.parameters = parameters;
            var consumer = Controller(StaticCurve(Binding("Visible"), 10), StaticCurve(Binding("Visible"), 80)).layers[0];
            consumer.name = "Threshold consumer";
            var states = consumer.stateMachine.states;
            Edge(states[0].state, states[1].state, "Signal", .7f, AnimatorConditionMode.Greater).duration = 0;
            source.layers = new[] { source.layers[0], consumer };
            foreach (var layer in source.layers)
                foreach (var state in layer.stateMachine.states) state.state.writeDefaultValues = true;
            var external = new[] { "BlendX", "BlendY" };
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var analysis = AnimatorReachability.Analyze(root, new[] { controller }, external);
            Assert.That(analysis.Values("Signal").TryGetConstant(out _), Is.False,
                "Identical leaf literals do not establish exact parameter induction through native 2D accumulation.");
            var report = AnimatorOptimizer.Optimize(root, new[] { controller }, new AnimatorOptimizationSettings { Unreachable = true },
                preservedParameters: external);
            Assert.That(report.Transitions, Is.Zero);
            Assert.That(report.States, Is.Zero);
            var positions = new[] { 0f, .1f, .17f, .23f, .31f, .37f, .43f, .51f, .63f, .71f, .83f, 1f };
            var signals = new List<float>();
            float[] Sample(AnimatorController current)
            {
                UnityEngine.Animations.AnimatorControllerPlayable active = default;
                return SampleFrozen(current, (p, frame) =>
                {
                    active = p; int index = frame % positions.Length;
                    p.SetFloat("BlendX", positions[index]); p.SetFloat("BlendY", positions[(index + 5) % positions.Length] * .37f);
                }, positions.Length * 2, () => { signals.Add(active.GetFloat("Signal")); return renderer.GetBlendShapeWeight(0); });
            }
            var before = Sample(source);
            Assert.That(signals.Any(v => v > .7f), Is.True, "The native parameter crosses the serialized transition threshold.");
            Assert.That(before.Last(), Is.EqualTo(80).Within(.00001f));
            var originalBits = signals.Select(BitConverter.SingleToInt32Bits).ToArray(); signals.Clear();
            var after = Sample(CommitAnimator(controller, root));
            Assert.That(signals.Select(BitConverter.SingleToInt32Bits), Is.EqualTo(originalBits));
            Assert.That(after, Is.EqualTo(before).Within(.00001f));
        }

        [TestCase(false, 0f, false)] [TestCase(true, 0f, false)]
        [TestCase(false, .25f, false)] [TestCase(true, .25f, false)]
        [TestCase(false, 0f, true)] [TestCase(true, 0f, true)]
        [TestCase(false, .25f, true)] [TestCase(true, .25f, true)]
        public void InternalParameterPropagation_DirectCurveProducerRespectsDefaults(bool writeDefaults, float initial, bool zeroWriter)
        {
            Mesh("Visible");
            var source = TreeController(("Weight", StaticCurve(ParameterBinding("Signal"), zeroWriter ? 70 : initial)),
                ("OtherWeight", StaticCurve(Binding("Visible"), 100)));
            source.AddParameter("Signal", AnimatorControllerParameterType.Float);
            var parameters = source.parameters;
            parameters.Single(p => p.name == "Weight").defaultFloat = zeroWriter ? 0 : 1;
            parameters.Single(p => p.name == "Signal").defaultFloat = initial; source.parameters = parameters;
            source.layers[0].stateMachine.defaultState.writeDefaultValues = writeDefaults;
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var analysis = AnimatorReachability.Analyze(root, new[] { controller }, new[] { "OtherWeight" });
            bool expected = writeDefaults || initial == 0;
            Assert.That(analysis.Values("Signal").TryGetConstant(out float value), Is.EqualTo(expected));
            UnityEngine.Animations.AnimatorControllerPlayable active = default;
            var samples = SampleFrozen(source, (p, frame) => { active = p; p.SetFloat("OtherWeight", .5f); }, 6,
                () => active.GetFloat("Signal"));
            if (expected) Assert.That(samples, Is.All.EqualTo(value).Within(.00001f));
        }

        [TestCase(false, 0f)] [TestCase(true, 0f)] [TestCase(false, .25f)] [TestCase(true, .25f)]
        public void InternalParameterPropagation_FullyInactiveDirectProducerPreservesNativeDefault(bool writeDefaults, float initial)
        {
            Mesh("Visible");
            var source = TreeController(("Zero", StaticCurve(ParameterBinding("Signal"), 70)));
            source.AddParameter("Signal", AnimatorControllerParameterType.Float);
            var parameters = source.parameters; parameters.Single(p => p.name == "Signal").defaultFloat = initial; source.parameters = parameters;
            source.layers[0].stateMachine.defaultState.writeDefaultValues = writeDefaults;
            var result = Analyze(source);
            Assert.That(result.analysis.Values("Signal").TryGetConstant(out float value), Is.True);
            UnityEngine.Animations.AnimatorControllerPlayable active = default;
            var samples = SampleFrozen(source, (p, frame) => active = p, 4, () => active.GetFloat("Signal"));
            Assert.That(samples, Is.All.EqualTo(value).Within(.00001f));
        }

        [TestCase("DifferentInitial")] [TestCase("ExternalSignal")] [TestCase("LaterControlWrite")]
        public void InternalParameterPropagation_CurveInductionDoesNotAssumeStartupOrStableFeedback(string reason)
        {
            var source = reason == "LaterControlWrite"
                ? FixedOneDimensionalController(0, new[] { StaticCurve(ParameterBinding("Signal"), 0), StaticCurve(ParameterBinding("Signal"), 1) }, new[] { 0f, 1f })
                : Controller(StaticCurve(ParameterBinding("Signal"), reason == "DifferentInitial" ? 1 : 0));
            source.AddParameter("Signal", AnimatorControllerParameterType.Float);
            if (reason == "LaterControlWrite") Driver(source.layers[0].stateMachine.defaultState, "Selection", 1);
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var analysis = AnimatorReachability.Analyze(root, new[] { controller }, reason == "ExternalSignal" ? new[] { "Signal" } : null);
            Assert.That(analysis.Values("Signal").TryGetConstant(out _), Is.False);
        }

        [TestCase(false)] [TestCase(true)]
        public void InternalParameterPropagation_OrderedCopyUsesItsActualEarlierWrite(bool localOnly)
        {
            var fixture = OrderedDriver(SetOperation("Scratch", 0), CopyOperation("Output", "Scratch"), CopyOperation("Scratch", "Input"));
            fixture.driver.localOnly = localOnly;
            var parameters = fixture.source.parameters;
            parameters.Single(p => p.name == "Scratch").defaultFloat = 1;
            fixture.source.parameters = parameters;
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(fixture.source);
            var analysis = AnimatorReachability.Analyze(root, new[] { controller }, new[] { "Input" });
            Assert.That(analysis.Values("Output").TryGetConstant(out float value), Is.True);
            Assert.That(value, Is.Zero);
            Assert.That(analysis.Values("Scratch").TryGetConstant(out _), Is.False,
                "The overwritten external input and startup default remain observable outside this callback.");
            Assert.That(fixture.driver.parameters.Count, Is.EqualTo(3));
        }

        [TestCase(AnimatorControllerParameterType.Int)] [TestCase(AnimatorControllerParameterType.Bool)]
        public void InternalParameterPropagation_OrderedWritesRetainEveryNativeDeclarationType(AnimatorControllerParameterType otherType)
        {
            Mesh("Timing");
            var fixture = OrderedDriver(SetOperation("Scratch", 1.5f), CopyOperation("Output", "Scratch"));
            var parameters = fixture.source.parameters;
            parameters.Single(p => p.name == "Output").defaultFloat = 1.5f; fixture.source.parameters = parameters;
            var other = Controller(Clip()); other.parameters = new[] {
                new AnimatorControllerParameter { name = "Scratch", type = otherType },
                new AnimatorControllerParameter { name = "Output", type = AnimatorControllerParameterType.Float, defaultFloat = 1.5f }
            };
            var clones = new CloneContext(GenericPlatformAnimatorBindings.Instance);
            var analysis = AnimatorReachability.Analyze(root, new[] { clones.Clone(fixture.source), clones.Clone(other) });
            var output = analysis.Values("Output");
            Assert.That(output.TryGetConstant(out _), Is.False,
                "The callback has no proof selecting one controller's storage interpretation of the shared name.");
            // Exercise both native storage interpretations without claiming to
            // execute VRChat's avatar-level callback in an EditMode graph.
            foreach (var source in new[] { fixture.source, other })
            {
                UnityEngine.Animations.AnimatorControllerPlayable active = default;
                var samples = SampleFrozen(source, (p, frame) =>
                {
                    active = p;
                    var declaration = source.parameters.Single(v => v.name == "Scratch");
                    float stored;
                    if (declaration.type == AnimatorControllerParameterType.Int)
                    { p.SetInteger("Scratch", 1); stored = p.GetInteger("Scratch"); }
                    else if (declaration.type == AnimatorControllerParameterType.Bool)
                    { p.SetBool("Scratch", true); stored = p.GetBool("Scratch") ? 1 : 0; }
                    else { p.SetFloat("Scratch", 1.5f); stored = p.GetFloat("Scratch"); }
                    p.SetFloat("Output", stored);
                }, 3, () => active.GetFloat("Output"));
                foreach (float sample in samples)
                    Assert.That(output.CanSatisfy(new[] { new AnimatorCondition {
                        parameter = "Output", mode = AnimatorConditionMode.Equals, threshold = sample } }), Is.True);
            }
        }

        [TestCase(false)] [TestCase(true)]
        public void InternalParameterPropagation_MixedBooleanDeclarationRetainsRandomAndUnsupportedAddOutcomes(bool random)
        {
            var fixture = random ? OrderedDriver(new Operation { name = "Scratch", type = Change.Random,
                    valueMin = 2, valueMax = 3, chance = .5f }, CopyOperation("Output", "Scratch"))
                : OrderedDriver(SetOperation("Scratch", 0), new Operation { name = "Scratch", type = Change.Add, value = 2 },
                    CopyOperation("Output", "Scratch"));
            var parameters = fixture.source.parameters;
            parameters.Single(p => p.name == "Output").defaultFloat = 2; fixture.source.parameters = parameters;
            var other = Controller(Clip()); other.AddParameter("Scratch", AnimatorControllerParameterType.Bool);
            var clones = new CloneContext(GenericPlatformAnimatorBindings.Instance);
            var analysis = AnimatorReachability.Analyze(root, new[] { clones.Clone(fixture.source), clones.Clone(other) });
            Assert.That(analysis.Values("Output").CanSatisfy(new[] { new AnimatorCondition {
                parameter = "Output", mode = AnimatorConditionMode.Equals, threshold = 0 } }), Is.True,
                random ? "A Boolean random target can be false regardless of numeric min/max." :
                    "Add is unsupported for Boolean parameters, so the prior false value must remain possible.");
        }

        [Test]
        public void InternalParameterPropagation_UnsupportedSharedTriggerTypeCannotBecomeAnOrderedCopyFact()
        {
            var fixture = OrderedDriver(SetOperation("Scratch", 1), CopyOperation("Output", "Scratch"));
            var parameters = fixture.source.parameters;
            parameters.Single(p => p.name == "Output").defaultFloat = 1; fixture.source.parameters = parameters;
            var other = Controller(Clip()); other.AddParameter("Scratch", AnimatorControllerParameterType.Trigger);
            var clones = new CloneContext(GenericPlatformAnimatorBindings.Instance);
            var analysis = AnimatorReachability.Analyze(root, new[] { clones.Clone(fixture.source), clones.Clone(other) });
            Assert.That(analysis.Values("Output").TryGetConstant(out _), Is.False);
            Assert.That(analysis.Values("Output").CanSatisfy(new[] { new AnimatorCondition {
                parameter = "Output", mode = AnimatorConditionMode.Equals, threshold = 0 } }), Is.True,
                "A numeric owner declaration does not prove Driver support for another controller's Trigger storage.");
        }

        [TestCase("CopyBeforeSet")] [TestCase("InitialValue")]
        [TestCase("SeparateCallbacks")] [TestCase("IntermediateWrite")]
        [TestCase("ExternalOutput")] [TestCase("AnimatedOutput")]
        public void InternalParameterPropagation_RetainsConsumptionOrderAndAllRuntimeWriters(string reason)
        {
            var fixture = OrderedDriver(SetOperation("Scratch", 0), CopyOperation("Output", "Scratch"));
            var parameters = fixture.source.parameters;
            parameters.Single(p => p.name == "Scratch").defaultFloat = 1;
            if (reason == "InitialValue") parameters.Single(p => p.name == "Output").defaultFloat = 1;
            fixture.source.parameters = parameters;
            if (reason == "CopyBeforeSet") fixture.driver.parameters.Reverse();
            if (reason == "SeparateCallbacks")
            {
                fixture.driver.parameters = new() { CopyOperation("Output", "Scratch") };
                var second = Track(fixture.source.layers[0].stateMachine.defaultState.AddStateMachineBehaviour<VRCAvatarParameterDriver>());
                second.parameters = new() { SetOperation("Scratch", 0) };
            }
            if (reason == "IntermediateWrite") fixture.driver.parameters.Insert(0, SetOperation("Output", 1));
            if (reason == "AnimatedOutput")
                fixture.source.layers[0].stateMachine.defaultState.motion = StaticCurve(ParameterBinding("Output"), 1);
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(fixture.source);
            var analysis = AnimatorReachability.Analyze(root, new[] { controller }, reason == "ExternalOutput" ? new[] { "Output" } : null);
            Assert.That(analysis.Values("Output").TryGetConstant(out _), Is.False, reason);
        }

        [Test]
        public void InternalParameterPropagation_LocalSetAndAddDoNotMakeCrossEntryAccumulationConstant()
        {
            var fixture = OrderedDriver(SetOperation("Scratch", .25f),
                new Operation { name = "Scratch", type = Change.Add, value = .25f }, CopyOperation("Output", "Scratch"));
            var parameters = fixture.source.parameters;
            parameters.Single(p => p.name == "Output").defaultFloat = .5f;
            fixture.source.parameters = parameters;
            var result = Analyze(fixture.source);
            Assert.That(result.analysis.Values("Output").TryGetConstant(out float value), Is.True);
            Assert.That(value, Is.EqualTo(.5f));
            fixture.driver.parameters.RemoveAt(0);
            Assert.That(Analyze(fixture.source).analysis.Values("Output").TryGetConstant(out _), Is.False);
        }

        [Test]
        public void InternalParameterPropagation_ExistingOptimizerUsesFactsWithoutDroppingDriverCallbacks()
        {
            Mesh("Visible");
            var fixture = OrderedDriver(SetOperation("Scratch", 0), CopyOperation("Output", "Scratch"));
            var parameters = fixture.source.parameters; parameters.Single(p => p.name == "Scratch").defaultFloat = 1; fixture.source.parameters = parameters;
            var machine = fixture.source.layers[0].stateMachine;
            machine.defaultState.motion = StaticCurve(Binding("Visible"), 10);
            var unreachable = Track(machine.AddState("Impossible output")); unreachable.motion = StaticCurve(Binding("Visible"), 80);
            Edge(machine.defaultState, unreachable, "Output", .5f, AnimatorConditionMode.Greater).duration = 0;
            var before = SampleFrozen(fixture.source, (p, frame) => { }, 6);
            var optimized = OptimizeAnimator(fixture.source, new AnimatorOptimizationSettings { Unreachable = true });
            Assert.That(optimized.report.States, Is.EqualTo(1));
            Assert.That(optimized.report.Transitions, Is.EqualTo(1));
            var retained = optimized.controller.Layers.First().StateMachine.DefaultState.Behaviours.OfType<VRC_AvatarParameterDriver>().Single();
            Assert.That(retained.parameters.Select(p => p.type), Is.EqualTo(new[] { Change.Set, Change.Copy }));
            var after = SampleFrozen(CommitAnimator(optimized.controller, root), (p, frame) => { }, 6);
            Assert.That(after, Is.EqualTo(before).Within(.00001f));
            Assert.That(before, Is.All.EqualTo(10).Within(.00001f));
        }

        [TestCase(16777217)] [TestCase(-16777217)]
        public void InternalParameterPropagation_IntFloatCopyRoundTripRetainsNativeReachableTransition(int initial)
        {
            Mesh("Visible");
            var source = Controller(StaticCurve(Binding("Visible"), 10), StaticCurve(Binding("Visible"), 80));
            source.parameters = new[] {
                new AnimatorControllerParameter { name = "IntegerSource", type = AnimatorControllerParameterType.Int, defaultInt = initial },
                new AnimatorControllerParameter { name = "FloatCopy", type = AnimatorControllerParameterType.Float },
                new AnimatorControllerParameter { name = "IntegerCopy", type = AnimatorControllerParameterType.Int }
            };
            var states = source.layers[0].stateMachine.states;
            var driver = Track(states[0].state.AddStateMachineBehaviour<VRCAvatarParameterDriver>());
            driver.parameters = new() { CopyOperation("FloatCopy", "IntegerSource"), CopyOperation("IntegerCopy", "FloatCopy") };
            Edge(states[0].state, states[1].state, "IntegerCopy", (float)initial).duration = 0;
            var optimized = OptimizeAnimator(source, new AnimatorOptimizationSettings { Unreachable = true });
            Assert.That(optimized.report.Transitions, Is.Zero, "Float storage changes the exact integer represented by the Copy chain.");
            // The SDK callback is not executed by an EditMode controller graph.
            // Replay these two Copy effects through native parameter storage, then
            // compare the original and optimized native state-machine execution.
            void Copies(UnityEngine.Animations.AnimatorControllerPlayable playable, int frame)
            {
                if (frame != 0) return;
                playable.SetFloat("FloatCopy", playable.GetInteger("IntegerSource"));
                playable.SetInteger("IntegerCopy", (int)playable.GetFloat("FloatCopy"));
            }
            var before = SampleFrozen(source, Copies, 6);
            var after = SampleFrozen(CommitAnimator(optimized.controller, root), Copies, 6);
            Assert.That(before.Last(), Is.EqualTo(80).Within(.00001f));
            Assert.That(after, Is.EqualTo(before).Within(.00001f));
        }

        [Test]
        public void InternalParameterPropagation_OrderedValueFactsCoverEveryIntermediateWrite()
        {
            var fixture = OrderedDriver();
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(fixture.source);
            var driver = controller.Layers.First().StateMachine.DefaultState.Behaviours.OfType<VRC_AvatarParameterDriver>().Single();
            var names = new[] { "Input", "Scratch", "Output", "Other" };
            var random = new System.Random(561923);
            for (int program = 0; program < 300; program++)
            {
                driver.parameters = Enumerable.Range(0, 12).Select(_ => new Operation {
                    name = names[random.Next(names.Length)], source = names[random.Next(names.Length)],
                    type = new[] { Change.Set, Change.Add, Change.Copy }[random.Next(3)], value = random.Next(-8, 9) / 4f
                }).ToList();
                var facts = ParameterDriverDataflow.Evaluate(driver, new[] { controller }, _ => ParameterValues.Unknown);
                Assert.That(facts, Is.Not.Null);
                for (int execution = 0; execution < 5; execution++)
                {
                    var current = names.ToDictionary(n => n, _ => random.Next(-16, 17) / 4f);
                    for (int i = 0; i < driver.parameters.Count; i++)
                    {
                        var operation = driver.parameters[i];
                        float value = operation.type == Change.Set ? operation.value : operation.type == Change.Copy
                            ? current[operation.source] : current[operation.name] + operation.value;
                        current[operation.name] = value;
                        if (facts[i].TryGetConstant(out float proven)) Assert.That(value, Is.EqualTo(proven), "Program " + program + ", write " + i);
                        Assert.That((double)value, Is.InRange(facts[i].Min, facts[i].Max));
                    }
                }
            }
        }
    }
}
