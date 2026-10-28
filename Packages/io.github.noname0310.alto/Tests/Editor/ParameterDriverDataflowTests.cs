using Noname.AvatarTools.Build;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.AnimatorOptimization;
using Noname.AvatarTools.Expressions;
using Noname.AvatarTools.ObjectOptimization;
using Noname.AvatarTools.ParameterPolicy;
using NUnit.Framework;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDKBase;
using Operation = VRC.SDKBase.VRC_AvatarParameterDriver.Parameter;
using Change = VRC.SDKBase.VRC_AvatarParameterDriver.ChangeType;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        [TestCase(false)] [TestCase(true)]
        public void DriverDataflowDoesNotAttachLaterWritesToEarlierReads(bool readBeforeSet)
        {
            var operations = readBeforeSet
                ? new[] { CopyOperation("Output", "Scratch"), SetOperation("Scratch", .25f), CopyOperation("Scratch", "Input") }
                : new[] { SetOperation("Scratch", .25f), CopyOperation("Output", "Scratch"), CopyOperation("Scratch", "Input") };
            var fixture = OrderedDriver(operations);
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(fixture.source);
            var graph = AnimatorParameterDependencies.Collect(root, new[] { controller }, true);
            Assert.That(graph.Expand(new[] { "Output" }).Contains("Input"), Is.EqualTo(readBeforeSet));
            Assert.That(graph.AffectedBy(new[] { "Input" }).Contains("Output"), Is.EqualTo(readBeforeSet));
            Assert.That(graph.Expand(new[] { "Scratch" }), Does.Contain("Input"), "Intermediate writes remain observable when Scratch is preserved");
            if (!readBeforeSet)
            {
                var optimized = PruneOrderedDriver(fixture.source, "Output");
                Assert.That(optimized.report.DriverOperations, Is.EqualTo(1));
                Assert.That(optimized.driver.parameters.Last().name, Is.EqualTo("Output"));
                Assert.That(fixture.driver.parameters.Count, Is.EqualTo(3));
            }
        }

        [Test]
        public void DriverDataflowDoesNotInlineValuesAcrossBehaviourBoundaries()
        {
            var fixture = OrderedDriver(SetOperation("Scratch", .25f));
            var second = Track(fixture.source.layers[0].stateMachine.defaultState.AddStateMachineBehaviour<VRCAvatarParameterDriver>());
            second.parameters = new() { CopyOperation("Output", "Scratch") };
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(fixture.source);
            var drivers = controller.Layers.First().StateMachine.DefaultState.Behaviours.OfType<VRC_AvatarParameterDriver>().ToArray();
            Assert.That(ParameterDriverDataflow.Analyze(drivers[1], new[] { controller })[0], Is.EquivalentTo(new[] { "Scratch" }));
        }

        [TestCase(false)] [TestCase(true)]
        public void DriverDataflowPreservesAllWritesAndStateControlDependencies(bool preserveScratch)
        {
            var fixture = OrderedDriver(CopyOperation("Scratch", "Input"), SetOperation("Scratch", .25f), CopyOperation("Output", "Scratch"));
            fixture.source.AddParameter("Enter", AnimatorControllerParameterType.Bool);
            var machine = fixture.source.layers[0].stateMachine;
            var other = machine.AddState("Other"); other.motion = Clip();
            Edge(other, machine.defaultState, "Enter", 0, AnimatorConditionMode.If);
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(fixture.source);
            var graph = AnimatorParameterDependencies.Collect(root, new[] { controller }, true);
            var live = graph.Expand(preserveScratch ? new[] { "Output", "Scratch" } : new[] { "Output" });
            Assert.That(live.Contains("Input"), Is.EqualTo(preserveScratch));
            Assert.That(live, Does.Contain("Enter"));
        }

        [TestCase("Missing")] [TestCase("Trigger")] [TestCase("Unknown")]
        [TestCase("Nonfinite")] [TestCase("CopyRange")] [TestCase("RandomRange")]
        [TestCase("RandomNoChoice")] [TestCase("RandomFloatRepeat")] [TestCase("RandomOverflow")]
        [TestCase("IntAdd")] [TestCase("FloatToInt")]
        public void DriverDataflowFallsBackForUnverifiedExecutions(string invalid)
        {
            var fixture = OrderedDriver(SetOperation("Scratch", 0), CopyOperation("Output", "Scratch"), CopyOperation("Scratch", "Input"));
            if (invalid == "Missing") fixture.source.parameters = fixture.source.parameters.Where(p => p.name != "Input").ToArray();
            if (invalid == "Trigger")
            {
                var parameters = fixture.source.parameters; parameters.Single(p => p.name == "Input").type = AnimatorControllerParameterType.Trigger;
                fixture.source.parameters = parameters;
            }
            if (invalid == "Unknown") fixture.driver.parameters[0].type = (Change)1000;
            if (invalid == "Nonfinite") fixture.driver.parameters[0].value = float.NaN;
            if (invalid == "CopyRange") fixture.driver.parameters[1].convertRange = true; // Both source endpoints are zero.
            if (invalid == "RandomRange") fixture.driver.parameters[0] = new Operation { name = "Scratch", type = Change.Random, valueMin = 1, valueMax = -1 };
            if (invalid.StartsWith("Random") && invalid != "RandomRange")
                fixture.driver.parameters[0] = new Operation { name = "Scratch", type = Change.Random,
                    valueMin = invalid == "RandomOverflow" ? -float.MaxValue : 0,
                    valueMax = invalid == "RandomOverflow" ? float.MaxValue : invalid == "RandomNoChoice" ? 0 : 1,
                    preventRepeats = invalid != "RandomOverflow" };
            if (invalid == "IntAdd" || invalid == "RandomNoChoice" || invalid == "FloatToInt")
            {
                var parameters = fixture.source.parameters; parameters.Single(p => p.name == "Scratch").type = AnimatorControllerParameterType.Int;
                if (invalid != "FloatToInt") parameters.Single(p => p.name == "Input").type = AnimatorControllerParameterType.Int;
                fixture.source.parameters = parameters;
                if (invalid == "IntAdd") fixture.driver.parameters.Insert(1, new Operation { name = "Scratch", type = Change.Add, value = 1 });
            }
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(fixture.source);
            var driver = controller.Layers.First().StateMachine.DefaultState.Behaviours.OfType<VRC_AvatarParameterDriver>().Single();
            Assert.That(ParameterDriverDataflow.Analyze(driver, new[] { controller }), Is.Null);
            Assert.That(AnimatorParameterDependencies.Collect(root, new[] { controller }, true).Expand(new[] { "Output" }), Does.Contain("Input"));
        }

        [TestCase(false)] [TestCase(true)]
        public void DriverDataflowDoesNotAssumeBuiltinReadsOrWritesWork(bool source)
        {
            var fixture = OrderedDriver(SetOperation("Scratch", 0), CopyOperation("Output", "Scratch"), CopyOperation("Scratch", "Input"));
            fixture.source.AddParameter("GestureLeftWeight", AnimatorControllerParameterType.Float);
            fixture.driver.parameters.Insert(1, source ? CopyOperation("Scratch", "GestureLeftWeight") : SetOperation("GestureLeftWeight", 0));
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(fixture.source);
            var driver = controller.Layers.First().StateMachine.DefaultState.Behaviours.OfType<VRC_AvatarParameterDriver>().Single();
            Assert.That(ParameterDriverDataflow.Analyze(driver, new[] { controller }), Is.Null);
            Assert.That(ParameterDriverDataflow.ValidSet(new[] { controller }, SetOperation("GestureLeftWeight", 0)), Is.False);
            Assert.That(AnimatorParameterDependencies.Collect(root, new[] { controller }, true).Expand(new[] { "Output" }), Does.Contain("Input"));
        }

        [Test]
        public void DriverDataflowValidatesEveryOwnerOfASharedBehaviour()
        {
            var fixture = OrderedDriver(SetOperation("Scratch", 0), CopyOperation("Output", "Scratch"), CopyOperation("Scratch", "Input"));
            var other = Controller(Clip());
            foreach (string name in new[] { "Scratch", "Output" }) other.AddParameter(name, AnimatorControllerParameterType.Float);
            var clone = new CloneContext(GenericPlatformAnimatorBindings.Instance);
            var controllers = new[] { clone.Clone(fixture.source), clone.Clone(other) };
            var driver = controllers[0].Layers.First().StateMachine.DefaultState.Behaviours.Single();
            controllers[1].Layers.First().StateMachine.DefaultState.Behaviours = ImmutableList.Create(driver);
            Assert.That(AnimatorParameterDependencies.Collect(root, controllers, true).Expand(new[] { "Output" }), Does.Contain("Input"));
        }

        [Test]
        public void DriverDataflowRandomRepeatReadsTheEarlierValueVersion()
        {
            var fixture = OrderedDriver(SetOperation("Scratch", 1),
                new Operation { name = "Scratch", type = Change.Random, valueMin = 0, valueMax = 2, preventRepeats = true },
                CopyOperation("Output", "Scratch"), CopyOperation("Scratch", "Input"));
            var parameters = fixture.source.parameters;
            foreach (var parameter in parameters.Where(p => p.name == "Scratch" || p.name == "Input")) parameter.type = AnimatorControllerParameterType.Int;
            fixture.source.parameters = parameters;
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(fixture.source);
            var graph = AnimatorParameterDependencies.Collect(root, new[] { controller }, true);
            Assert.That(graph.Expand(graph.ObservableInputs.Concat(new[] { "Output" })), Does.Not.Contain("Input"));
            Assert.That(graph.Expand(new[] { "Scratch" }), Does.Contain("Input"));
            var optimized = PruneOrderedDriver(fixture.source, "Output");
            Assert.That(optimized.driver.parameters.Any(p => p.type == Change.Random), Is.True);
            Assert.That(optimized.driver.parameters.Any(p => p.source == "Input"), Is.False);
        }

        [TestCase(false, false)] [TestCase(false, true)] [TestCase(true, false)] [TestCase(true, true)]
        public void DriverDataflowRemovesADeadFeatureMenuThroughBuildCleanup(bool sdk, bool protectScratch)
        {
            Mesh("Visible");
            var source = TreeController(("Output", StaticCurve(Binding("Visible"), 100)));
            source.AddParameter("Input", AnimatorControllerParameterType.Float);
            source.AddParameter("Scratch", AnimatorControllerParameterType.Float);
            var driver = Track(source.layers[0].stateMachine.defaultState.AddStateMachineBehaviour<VRCAvatarParameterDriver>());
            driver.parameters = new() { SetOperation("Scratch", .25f), CopyOperation("Output", "Scratch"), CopyOperation("Scratch", "Input") };
            root.AddComponent<ALTOTraceAndOptimizeExpressions>(); ObjectSettings(root);
            var menu = ExpressionMenu(ExpressionControl("Dead feature input", "Input"));
            var context = ExpressionBuild(menu, source); ObjectOptimizationPass.Capture(context);
            context.GetState<AnimatorOptimizationState>().Settings = new() { StateLogic = true };
            if (protectScratch) { var policy = context.GetState<ParameterPolicyState>(); policy.Enabled = true; policy.Names.Add("Scratch"); }
            var originalParameters = FreezeExpressions(new VRCExpressionParameters.Parameter
                { name = "Input", networkSynced = false, valueType = VRCExpressionParameters.ValueType.Float }).expressionParameters;
            if (sdk) AvatarOutputSdkFinalizer.Begin(root);
            ExpressionOptimizationPass.Apply(context);
            if (sdk)
            {
                TrackControllerAssets((AnimatorController)root.GetComponent<VRCAvatarDescriptor>().baseAnimationLayers.Single().animatorController);
                AvatarOutputSdkFinalizer.Complete(root, o => Track(o));
            }
            var descriptor = root.GetComponent<VRCAvatarDescriptor>();
            TrackControllerAssets((AnimatorController)descriptor.baseAnimationLayers.Single().animatorController);
            Track(descriptor.expressionParameters); if (descriptor.expressionsMenu != null) Track(descriptor.expressionsMenu);
            Assert.That(descriptor.expressionsMenu != null && descriptor.expressionsMenu.controls.Any(c => c.parameter.name == "Input"), Is.EqualTo(protectScratch));
            Assert.That(descriptor.expressionParameters.parameters.Any(p => p.name == "Input"), Is.EqualTo(protectScratch));
            Assert.That(driver.parameters.Count, Is.EqualTo(3));
            Assert.That(originalParameters.parameters.Length, Is.EqualTo(1));
            Assert.That(menu.controls.Count, Is.EqualTo(1));
        }

        [Test]
        public void DriverDataflowPredictionsMatchEveryWriteAcrossGeneratedPrograms()
        {
            var fixture = OrderedDriver();
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(fixture.source);
            var driver = controller.Layers.First().StateMachine.DefaultState.Behaviours.OfType<VRC_AvatarParameterDriver>().Single();
            string[] names = { "Input", "Scratch", "Output", "Other" };
            var random = new System.Random(84931);
            float[] Run(float[] initial)
            {
                var values = names.Select((name, index) => (name, index)).ToDictionary(p => p.name, p => initial[p.index]);
                var writes = new List<float>();
                foreach (var operation in driver.parameters)
                {
                    if (operation.type == Change.Set) values[operation.name] = operation.value;
                    else if (operation.type == Change.Copy) values[operation.name] = values[operation.source];
                    else values[operation.name] += operation.value;
                    writes.Add(values[operation.name]);
                }
                return writes.ToArray();
            }
            for (int program = 0; program < 1200; program++)
            {
                driver.parameters = Enumerable.Range(0, 12).Select(_ => new Operation {
                    name = names[random.Next(names.Length)], source = names[random.Next(names.Length)],
                    type = new[] { Change.Set, Change.Add, Change.Copy }[random.Next(3)], value = random.Next(-8, 9) / 4f
                }).ToList();
                var dependency = ParameterDriverDataflow.Analyze(driver, new[] { controller });
                Assert.That(dependency, Is.Not.Null);
                var initial = names.Select(_ => random.Next(-16, 17) / 4f).ToArray(); var expected = Run(initial);
                for (int input = 0; input < names.Length; input++)
                {
                    var changed = initial.ToArray(); changed[input] += 3.25f;
                    var actual = Run(changed);
                    for (int write = 0; write < expected.Length; write++)
                        if (!dependency[write].Contains(names[input]))
                            Assert.That(actual[write], Is.EqualTo(expected[write]), "Program " + program + ", write " + write + ", input " + names[input]);
                }
            }
        }
    }
}
