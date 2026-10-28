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
        private static Operation SetOperation(string name, float value) => new() { name = name, type = Change.Set, value = value };
        private static Operation CopyOperation(string name, string source) => new() { name = name, source = source, type = Change.Copy };

        private (AnimatorController source, VRCAvatarParameterDriver driver) OrderedDriver(params Operation[] operations)
        {
            var source = Controller(Clip());
            foreach (string name in new[] { "Input", "Scratch", "Output", "Other" })
                source.AddParameter(name, AnimatorControllerParameterType.Float);
            var driver = Track(source.layers[0].stateMachine.defaultState.AddStateMachineBehaviour<VRCAvatarParameterDriver>());
            driver.parameters = operations.ToList();
            return (source, driver);
        }

        private (VirtualAnimatorController controller, VRC_AvatarParameterDriver driver, AnimatorOptimizationReport report)
            PruneOrderedDriver(AnimatorController source, params string[] preserved)
        {
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var report = new AnimatorOptimizationReport();
            ParameterLiveness.RemoveUnusedCurves(root, new[] { controller }, report, preserved, removeDeadDriverOperations: true);
            var driver = controller.AllReachableNodes().OfType<VirtualState>().SelectMany(s => s.Behaviours)
                .OfType<VRC_AvatarParameterDriver>().First();
            return (controller, driver, report);
        }

        [TestCase(false)] [TestCase(true)]
        public void OrderedDriverRemovesAnOverwrittenInputAndItsExpressionDependency(bool localOnly)
        {
            var fixture = OrderedDriver(CopyOperation("Scratch", "Input"), SetOperation("Scratch", .25f), CopyOperation("Output", "Scratch"));
            fixture.driver.localOnly = localOnly;
            var optimized = PruneOrderedDriver(fixture.source, "Output");
            Assert.That(optimized.report.DriverOperations, Is.EqualTo(1));
            Assert.That(optimized.driver.parameters.Select(p => p.type), Is.EqualTo(new[] { Change.Set, Change.Copy }));
            Assert.That(optimized.driver.localOnly, Is.EqualTo(localOnly));
            Assert.That(fixture.driver.parameters.Count, Is.EqualTo(3), "The source asset remains intact.");
            var graph = AnimatorParameterDependencies.Collect(root, new[] { optimized.controller }, true);
            Assert.That(graph.Expand(new[] { "Output" }), Does.Not.Contain("Input"));
            var again = new AnimatorOptimizationReport();
            ParameterLiveness.RemoveUnusedCurves(root, new[] { optimized.controller }, again, new[] { "Output" }, removeDeadDriverOperations: true);
            Assert.That(again.DriverOperations, Is.Zero);
        }

        [TestCase("CopyBeforeOverwrite")] [TestCase("AddReadsOldValue")] [TestCase("SelfCopy")]
        public void OrderedDriverKeepsReadsOfEarlierVersions(string kind)
        {
            var fixture = OrderedDriver(CopyOperation("Scratch", "Input"),
                kind == "AddReadsOldValue" ? new Operation { name = "Scratch", type = Change.Add, value = 2 } :
                    CopyOperation(kind == "SelfCopy" ? "Scratch" : "Output", "Scratch"),
                kind == "CopyBeforeOverwrite" ? SetOperation("Scratch", 0) : CopyOperation("Output", "Scratch"));
            var optimized = PruneOrderedDriver(fixture.source, "Output");
            Assert.That(optimized.report.DriverOperations, Is.EqualTo(kind == "CopyBeforeOverwrite" ? 1 : 0));
            Assert.That(optimized.driver.parameters[0].source, Is.EqualTo("Input"));
        }

        [TestCase("Network")] [TestCase("LocalExpression")] [TestCase("Sensor")]
        [TestCase("Preserved")] [TestCase("ExternalRead")] [TestCase("ExternalWrite")]
        public void OrderedDriverKeepsIntermediateValuesWithObservers(string observer)
        {
            var fixture = OrderedDriver(CopyOperation("Scratch", "Input"), SetOperation("Scratch", 0), CopyOperation("Output", "Scratch"));
            if (observer == "Network" || observer == "LocalExpression")
            {
                root.AddComponent<VRCAvatarDescriptor>();
                FreezeExpressions(new VRCExpressionParameters.Parameter { name = "Scratch", networkSynced = observer == "Network" });
            }
            if (observer == "Sensor") root.AddComponent<VRC.SDK3.Dynamics.Contact.Components.VRCContactReceiver>().parameter = "Scratch";
            ExternalParameterAccess.Register("Ordered Driver fixture", access =>
            {
                if (observer == "ExternalRead") access.Readers.Add(n => n == "Scratch");
                if (observer == "ExternalWrite") access.Writers.Add(n => n == "Scratch");
            });
            try
            {
                var optimized = PruneOrderedDriver(fixture.source,
                    observer == "Preserved" ? new[] { "Output", "Scratch" } : new[] { "Output" });
                Assert.That(optimized.report.DriverOperations, Is.Zero);
            }
            finally { ExternalParameterAccess.Register("Ordered Driver fixture", _ => { }); }
        }

        [TestCase("Missing")] [TestCase("Trigger")] [TestCase("BoolNoncanonical")]
        [TestCase("IntFraction")] [TestCase("IntOverflow")] [TestCase("Nonfinite")]
        [TestCase("UnknownOperation")]
        public void OrderedDriverRequiresAValidDefiniteOverwrite(string reason)
        {
            var fixture = OrderedDriver(CopyOperation("Scratch", "Input"), SetOperation("Scratch", 0), CopyOperation("Output", "Scratch"));
            var parameters = fixture.source.parameters;
            var scratch = parameters.Single(p => p.name == "Scratch");
            if (reason == "Trigger") scratch.type = AnimatorControllerParameterType.Trigger;
            if (reason == "BoolNoncanonical") { scratch.type = AnimatorControllerParameterType.Bool; fixture.driver.parameters[1].value = 2; }
            if (reason.StartsWith("Int"))
            {
                scratch.type = AnimatorControllerParameterType.Int;
                fixture.driver.parameters[1].value = reason == "IntFraction" ? .5f : float.MaxValue;
            }
            if (reason == "Nonfinite") fixture.driver.parameters[1].value = float.NaN;
            if (reason == "UnknownOperation") fixture.driver.parameters[1].type = (Change)1000;
            fixture.source.parameters = reason == "Missing" ? parameters.Where(p => p.name != "Scratch").ToArray() : parameters;
            var optimized = PruneOrderedDriver(fixture.source, "Output");
            Assert.That(optimized.report.DriverOperations, Is.Zero);
        }

        [Test]
        public void OrderedDriverDoesNotJoinSeparateBehaviourExecutions()
        {
            var fixture = OrderedDriver(CopyOperation("Scratch", "Input"));
            var second = Track(fixture.source.layers[0].stateMachine.defaultState.AddStateMachineBehaviour<VRCAvatarParameterDriver>());
            second.parameters = new() { CopyOperation("Output", "Scratch"), SetOperation("Scratch", 0) };
            var optimized = PruneOrderedDriver(fixture.source, "Output");
            Assert.That(optimized.report.DriverOperations, Is.Zero);
        }

        [TestCase("Null")] [TestCase("EmptyDestination")] [TestCase("EmptyCopySource")]
        public void OrderedDriverDoesNotAssumeExecutionContinuesPastMalformedOperations(string kind)
        {
            var fixture = OrderedDriver(CopyOperation("Scratch", "Input"), SetOperation("Scratch", 0), CopyOperation("Output", "Scratch"));
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(fixture.source);
            var driver = controller.Layers.First().StateMachine.DefaultState.Behaviours.OfType<VRC_AvatarParameterDriver>().Single();
            driver.parameters.Insert(1, kind == "Null" ? null : kind == "EmptyDestination" ?
                SetOperation("", 0) : CopyOperation("Other", ""));
            var context = new DriverOperationLiveness(root, new[] { controller }, new HashSet<string>());
            Assert.That(context.RemoveUnused(driver, new HashSet<string> { "Input", "Scratch", "Output" }), Is.Zero);
            Assert.That(driver.parameters.Count, Is.EqualTo(4));
        }

        [TestCase("Missing")] [TestCase("Trigger")] [TestCase("Valid")]
        public void OrderedDriverChecksAllOwnersOfASharedBehaviour(string otherOwner)
        {
            var fixture = OrderedDriver(CopyOperation("Scratch", "Input"), SetOperation("Scratch", 0), CopyOperation("Output", "Scratch"));
            var otherSource = Controller(Clip());
            if (otherOwner != "Missing") otherSource.AddParameter("Scratch",
                otherOwner == "Trigger" ? AnimatorControllerParameterType.Trigger : AnimatorControllerParameterType.Float);
            var clone = new CloneContext(GenericPlatformAnimatorBindings.Instance);
            var controllers = new[] { clone.Clone(fixture.source), clone.Clone(otherSource) };
            var driver = controllers[0].Layers.First().StateMachine.DefaultState.Behaviours.OfType<VRC_AvatarParameterDriver>().Single();
            controllers[1].Layers.First().StateMachine.DefaultState.Behaviours = ImmutableList.Create<StateMachineBehaviour>(driver);
            var context = new DriverOperationLiveness(root, controllers, new HashSet<string>());
            Assert.That(context.RemoveUnused(driver, new HashSet<string> { "Input", "Scratch", "Output" }),
                Is.EqualTo(otherOwner == "Valid" ? 1 : 0));
        }

        [TestCase(AnimatorControllerParameterType.Bool)] [TestCase(AnimatorControllerParameterType.Int)]
        [TestCase(AnimatorControllerParameterType.Float)]
        public void OrderedDriverSupportsValidTypedSetOverwrites(AnimatorControllerParameterType type)
        {
            var fixture = OrderedDriver(CopyOperation("Scratch", "Input"), SetOperation("Scratch", 1), CopyOperation("Output", "Scratch"));
            var parameters = fixture.source.parameters; parameters.Single(p => p.name == "Scratch").type = type;
            fixture.source.parameters = parameters;
            Assert.That(PruneOrderedDriver(fixture.source, "Output").report.DriverOperations, Is.EqualTo(1));
        }

        [TestCase(false, false)] [TestCase(false, true)] [TestCase(true, false)] [TestCase(true, true)]
        public void OrderedDriverCleanupReachesExpressionMenusThroughTheBuildFixedPoint(bool sdk, bool protectScratch)
        {
            Mesh("Visible");
            var source = TreeController(("Output", StaticCurve(Binding("Visible"), 100)));
            source.AddParameter("Input", AnimatorControllerParameterType.Float);
            source.AddParameter("Scratch", AnimatorControllerParameterType.Float);
            var driver = Track(source.layers[0].stateMachine.defaultState.AddStateMachineBehaviour<VRCAvatarParameterDriver>());
            driver.parameters = new() { CopyOperation("Scratch", "Input"), SetOperation("Scratch", .25f), CopyOperation("Output", "Scratch") };
            root.AddComponent<ALTOTraceAndOptimizeExpressions>();
            ObjectSettings(root);
            var menu = ExpressionMenu(ExpressionControl("Unused input", "Input"));
            var context = ExpressionBuild(menu, source);
            ObjectOptimizationPass.Capture(context);
            context.GetState<AnimatorOptimizationState>().Settings = new() { StateLogic = true };
            if (protectScratch)
            {
                var policy = context.GetState<ParameterPolicyState>(); policy.Enabled = true; policy.Names.Add("Scratch");
            }
            var parameters = FreezeExpressions(new VRCExpressionParameters.Parameter {
                name = "Input", networkSynced = false, valueType = VRCExpressionParameters.ValueType.Float
            }).expressionParameters;
            if (sdk) AvatarOutputSdkFinalizer.Begin(root);
            ExpressionOptimizationPass.Apply(context);
            if (sdk)
            {
                TrackControllerAssets((AnimatorController)root.GetComponent<VRCAvatarDescriptor>().baseAnimationLayers.Single().animatorController);
                AvatarOutputSdkFinalizer.Complete(root, o => Track(o));
            }
            var descriptor = root.GetComponent<VRCAvatarDescriptor>();
            var built = (AnimatorController)descriptor.baseAnimationLayers.Single().animatorController;
            TrackControllerAssets(built);
            Track(descriptor.expressionParameters);
            if (descriptor.expressionsMenu != null) Track(descriptor.expressionsMenu);
            Assert.That(descriptor.expressionsMenu != null && descriptor.expressionsMenu.controls.Any(c => c.parameter.name == "Input"),
                Is.EqualTo(protectScratch));
            Assert.That(descriptor.expressionParameters.parameters.Any(p => p.name == "Input"), Is.EqualTo(protectScratch));
            Assert.That(built.layers[0].stateMachine.defaultState.behaviours.OfType<VRC_AvatarParameterDriver>().Single().parameters.Count,
                Is.EqualTo(protectScratch ? 3 : 2));
            Assert.That(driver.parameters.Count, Is.EqualTo(3));
            Assert.That(parameters.parameters.Length, Is.EqualTo(1));
            Assert.That(menu.controls.Count, Is.EqualTo(1));
        }

        [TestCase(false)] [TestCase(true)]
        public void OrderedDriverRetainsRandomAndItsPreventRepeatInput(bool preventRepeats)
        {
            var fixture = OrderedDriver(CopyOperation("Scratch", "Input"),
                new Operation { name = "Scratch", type = Change.Random, valueMin = 0, valueMax = 1, preventRepeats = preventRepeats },
                SetOperation("Scratch", 0), CopyOperation("Output", "Scratch"));
            var optimized = PruneOrderedDriver(fixture.source, "Output");
            Assert.That(optimized.report.DriverOperations, Is.EqualTo(preventRepeats ? 0 : 1));
            Assert.That(optimized.driver.parameters.Count(p => p.type == Change.Random), Is.EqualTo(1));
        }

        [TestCase(false)] [TestCase(true)]
        public void RandomPreventRepeatsKeepsCrossLayerInputsEvenWithoutAValueConsumer(bool preventRepeats)
        {
            var source = TreeController(("Input", StaticCurve(ParameterBinding("Scratch"), 1)));
            source.AddParameter("Scratch", AnimatorControllerParameterType.Float);
            var driver = Track(source.layers[0].stateMachine.defaultState.AddStateMachineBehaviour<VRCAvatarParameterDriver>());
            driver.parameters = new() { new Operation { name = "Scratch", type = Change.Random, preventRepeats = preventRepeats } };
            var graph = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var report = new AnimatorOptimizationReport();
            ParameterLiveness.RemoveUnusedCurves(root, new[] { graph }, report, removeDeadDriverOperations: true);
            Assert.That(report.ParameterCurves, Is.EqualTo(preventRepeats ? 0 : 1));
            Assert.That(report.DriverOperations, Is.Zero);
        }

        [Test]
        public void OrderedDriverRewritePreservesFinalValuesAcrossGeneratedPrograms()
        {
            var fixture = OrderedDriver();
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(fixture.source);
            var driver = controller.Layers.First().StateMachine.DefaultState.Behaviours.OfType<VRC_AvatarParameterDriver>().Single();
            var context = new DriverOperationLiveness(root, new[] { controller }, new HashSet<string>());
            var random = new System.Random(539149);
            string[] names = { "Input", "Scratch", "Output", "Other" };
            Dictionary<string, float> Run(IEnumerable<Operation> operations, float[] initial)
            {
                var values = names.Select((name, i) => (name, i)).ToDictionary(p => p.name, p => initial[p.i]);
                foreach (var operation in operations)
                    if (operation.type == Change.Set) values[operation.name] = operation.value;
                    else if (operation.type == Change.Copy) values[operation.name] = values[operation.source];
                    else values[operation.name] += operation.value;
                return values;
            }
            int eliminated = 0;
            for (int program = 0; program < 2000; program++)
            {
                var original = Enumerable.Range(0, 16).Select(_ => new Operation {
                    name = names[random.Next(names.Length)], source = names[random.Next(names.Length)],
                    type = new[] { Change.Set, Change.Add, Change.Copy }[random.Next(3)], value = random.Next(-8, 9) / 4f
                }).ToList();
                driver.parameters = original.ToList();
                var live = new HashSet<string>(names.Where(_ => random.Next(2) == 0));
                eliminated += context.RemoveUnused(driver, live);
                for (int sample = 0; sample < 4; sample++)
                {
                    var initial = names.Select(_ => random.Next(-16, 17) / 4f).ToArray();
                    var expected = Run(original, initial); var actual = Run(driver.parameters, initial);
                    foreach (string name in live)
                        Assert.That(actual[name], Is.EqualTo(expected[name]), "Program " + program + ", output " + name);
                }
                Assert.That(context.RemoveUnused(driver, live), Is.Zero, "Each block reaches a fixed point.");
            }
            Assert.That(eliminated, Is.GreaterThan(1000));
        }
    }
}
