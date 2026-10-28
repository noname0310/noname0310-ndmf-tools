using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.ParameterLayout;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using Parameter = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters.Parameter;
using ValueType = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters.ValueType;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private static Parameter[] MultiplexInputs(int floats = 4, int ints = 0, int bools = 0) =>
            Enumerable.Range(0, floats).Select(i => new Parameter { name = "Float" + i, valueType = ValueType.Float, networkSynced = true, saved = true })
                .Concat(Enumerable.Range(0, ints).Select(i => new Parameter { name = "Int" + i, valueType = ValueType.Int, networkSynced = true, saved = true }))
                .Concat(Enumerable.Range(0, bools).Select(i => new Parameter { name = "Bool" + i, valueType = ValueType.Bool, networkSynced = true, saved = true })).ToArray();
        private static MultiplexEncoding MultiplexPlan(Parameter[] input, int budget = 16) =>
            MultiplexEncoding.Plan(input, input.Select(p => p.name).ToHashSet(), budget, .1f, input.Select(p => p.name).ToHashSet());

        [TestCase(4, 0, 0, 16)] [TestCase(15, 5, 33, 64)] [TestCase(0, 0, 60, 16)] [TestCase(127, 0, 0, 16)]
        public void MultiplexingChoosesTheShortestTypedScheduleThatFits(int floats, int ints, int bools, int budget)
        {
            var input = MultiplexInputs(floats, ints, bools); var plan = MultiplexPlan(input, budget);
            Assert.That(plan.WireCost, Is.LessThanOrEqualTo(budget));
            for (int batches = 2; batches < plan.Batches; batches++)
                Assert.That(8 + input.GroupBy(p => p.valueType).Sum(g => ((g.Count() + batches - 1) / batches) * MultiplexEncoding.Cost(g.Key)),
                    Is.GreaterThan(budget), "A shorter schedule should not fit the same budget");
            Assert.That(plan.Inputs.Count, Is.EqualTo(input.Length));
            Assert.That(plan.Inputs.All(p => plan.Slots[p.Slot].Type == p.Type), Is.True);
            Assert.That(plan.Code(1, plan.Batches - 1), Is.LessThanOrEqualTo(254));
            Assert.That(input.All(p => p.networkSynced && p.saved), Is.True);
        }

        [Test]
        public void MultiplexingDoesNotInferAllFromAnEmptySelectionOrCompressAnAvatarWithinBudget()
        {
            var input = MultiplexInputs();
            Assert.That(MultiplexEncoding.Plan(input, new HashSet<string>(), 32, .15f, new HashSet<string>()), Is.Null);
            Assert.Throws<InvalidOperationException>(() => MultiplexEncoding.Plan(input, new HashSet<string>(), 16, .15f, new HashSet<string>()));
            Assert.Throws<InvalidOperationException>(() => MultiplexPlan(MultiplexInputs(128), 16));
        }

        [TestCase("tag")] [TestCase("slot")] [TestCase("assignment")] [TestCase("epoch")]
        [TestCase("protocol")] [TestCase("hold")]
        public void MultiplexingRejectsMalformedContracts(string damage)
        {
            var plan = MultiplexPlan(MultiplexInputs());
            if (damage == "tag") plan.Tag = plan.Slots[0].Name;
            if (damage == "slot") plan.Inputs[0].Slot = 100;
            if (damage == "assignment") plan.Inputs[1].Batch = plan.Inputs[0].Batch;
            if (damage == "epoch") plan.Batches = 128;
            if (damage == "protocol") plan.Protocol = 99;
            if (damage == "hold") plan.HoldSeconds = float.NaN;
            Assert.Throws<InvalidOperationException>(() => plan.Validate());
        }

        [Test]
        public void MultiplexingRecordRejectsMissingWireDataAndLogicalInputsStillMarkedSynced()
        {
            var plan = MultiplexPlan(MultiplexInputs());
            var record = LayoutRecord(plan.WireDeclarations()); record.Multiplexing = plan;
            ParameterLayoutModel.Validate(record);
            using var fixture = new LayoutTestStore(); fixture.Store.Commit(record);
            Assert.That(JsonUtility.ToJson(fixture.Store.Read(LayoutId).Multiplexing), Is.EqualTo(JsonUtility.ToJson(plan)));
            record.Parameters.Add(new ParameterLayoutEntry { Name = "Float0", Type = ValueType.Float });
            Assert.Throws<InvalidOperationException>(() => ParameterLayoutModel.Validate(record));
            record.Parameters.RemoveAt(record.Parameters.Count - 1); record.Parameters[0].Type = ValueType.Bool;
            Assert.Throws<InvalidOperationException>(() => ParameterLayoutModel.Validate(record));
        }

        [TestCase(1)] [TestCase(2)] [TestCase(3)]
        public void MultiplexingDoesNotAppearWhenAnOrdinaryRecordIsDeserialized(int version)
        {
            var record = LayoutRecord(new[] { LayoutParameter("Native") }); record.Version = version;
            using var fixture = new LayoutTestStore(); fixture.Store.Commit(record);
            Assert.That(fixture.Store.Read(LayoutId).Multiplexing, Is.Null);
            Assert.That(fixture.Store.Read(LayoutId).Parameters.Single().Name, Is.EqualTo("Native"));
            record.UsesMultiplexing = true; record.MultiplexingMetadata = null;
            Assert.Throws<InvalidOperationException>(() => ParameterLayoutModel.Validate(record));
        }

        [Test]
        public void MultiplexingAutomaticSelectionKeepsMomentarySensorsAndComputedDependenciesNative()
        {
            var descriptor = LayoutDescriptor();
            var menu = Track(ScriptableObject.CreateInstance<VRCExpressionsMenu>()); descriptor.expressionsMenu = menu;
            VRCExpressionsMenu.Control Toggle(string name) => new() { name = name, type = VRCExpressionsMenu.Control.ControlType.Toggle,
                parameter = new VRCExpressionsMenu.Control.Parameter { name = name } };
            menu.controls = new() { Toggle("Persistent"), Toggle("Preserved"), Toggle("Momentary"), Toggle("Calculated") };
            menu.controls.Add(new VRCExpressionsMenu.Control { type = VRCExpressionsMenu.Control.ControlType.Button,
                parameter = new VRCExpressionsMenu.Control.Parameter { name = "Momentary" } });
            menu.controls.Add(new VRCExpressionsMenu.Control { type = VRCExpressionsMenu.Control.ControlType.RadialPuppet,
                parameter = new VRCExpressionsMenu.Control.Parameter { name = "PuppetActive" },
                subParameters = new[] { new VRCExpressionsMenu.Control.Parameter { name = "Slider" } } });
            menu.controls.Add(Toggle("PuppetActive"));
            var controller = Controller(Clip());
            foreach (var name in new[] { "Persistent", "Preserved", "Momentary", "Calculated", "Sensor", "Slider", "PuppetActive" })
                controller.AddParameter(name, AnimatorControllerParameterType.Float);
            var driver = Track(controller.layers[0].stateMachine.defaultState.AddStateMachineBehaviour<VRCAvatarParameterDriver>());
            driver.parameters = new() { CopyOperation("Calculated", "Sensor") };
            PolicyChild("Receiver").AddComponent<VRC.SDK3.Dynamics.Contact.Components.VRCContactReceiver>().parameter = "Sensor";
            var clone = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(controller);
            descriptor.expressionParameters = Track(ScriptableObject.CreateInstance<VRCExpressionParameters>());
            descriptor.expressionParameters.parameters = controller.parameters.Select(p => LayoutParameter(p.name, ValueType.Float)).ToArray();
            var result = MultiplexSelection.Analyze(root, new[] { clone }, new HashSet<string> { "Preserved" }, new List<string>());
            Assert.That(result.Eligible, Is.EquivalentTo(new[] { "Persistent", "Slider" }));
            Assert.That(result.Excluded["Preserved"], Is.EqualTo(MultiplexExclusion.Preserved));
            Assert.That(result.Excluded["Momentary"], Is.EqualTo(MultiplexExclusion.MomentaryControl));
            Assert.That(result.Excluded["Sensor"], Is.EqualTo(MultiplexExclusion.DynamicInput | MultiplexExclusion.NoPersistentMenu));
            Assert.That(result.Excluded["Calculated"], Is.EqualTo(MultiplexExclusion.DynamicInput));
            Assert.That(result.Summary(descriptor.expressionParameters.parameters),
                Is.EqualTo("DynamicInput=16 bits (2 inputs); MomentaryControl=16 bits (2 inputs); Preserved=8 bits (1 inputs); eligible=16 bits (2 inputs)"));
        }

        [TestCase(false, ParameterMultiplexingMode.SelectedInputs)] [TestCase(true, ParameterMultiplexingMode.SelectedInputs)]
        [TestCase(false, ParameterMultiplexingMode.AllSynchronizedInputs)] [TestCase(true, ParameterMultiplexingMode.AllSynchronizedInputs)]
        [TestCase(false, ParameterMultiplexingMode.AutomaticByPriority)] [TestCase(true, ParameterMultiplexingMode.AutomaticByPriority)]
        public void MultiplexingGeneratesTypedAssetsWithoutChangingSourceParameters(bool sdk, ParameterMultiplexingMode mode)
        {
            var descriptor = LayoutDescriptor();
            var input = Track(ScriptableObject.CreateInstance<VRCExpressionParameters>());
            input.parameters = MultiplexInputs(4, 4, 8); descriptor.expressionParameters = input;
            string original = EditorJsonUtility.ToJson(input);
            var settings = new ParameterCompressionSettings
            { Multiplexing = mode, Budget = 32, HoldSeconds = .1f };
            settings.MultiplexNames.UnionWith(input.parameters.Select(p => p.name));
            var context = new BuildContext(root, null); using var fixture = new LayoutTestStore();
            var layout = new ParameterLayoutSettings { Compression = settings };
            if (sdk)
            {
                ParameterLayoutSdkSession.Begin(root);
                Assert.That(ParameterLayoutSdkSession.Defer(context, layout, ParameterLayoutPlatform.PC, new OptimizationBuildReport()), Is.True);
                Assert.That(descriptor.expressionParameters.CalcTotalCost(), Is.LessThanOrEqualTo(32), "Late SDK processors must see the compressed budget");
                ParameterLayoutSdkSession.Complete(root, value => Track(value), fixture.Store);
            }
            else
            {
                var record = ParameterLayoutPass.ApplyFinal(root, layout, ParameterLayoutPlatform.PC,
                    new OptimizationBuildReport(), value => Track(value), fixture.Store, context);
                Assert.That(record.Multiplexing, Is.Not.Null); ParameterLayoutModel.Validate(record);
            }
            Assert.That(descriptor.expressionParameters.CalcTotalCost(), Is.LessThanOrEqualTo(32));
            Assert.That(input.parameters.All(p => descriptor.expressionParameters.parameters.Single(o => o.name == p.name).saved), Is.True);
            Assert.That(EditorJsonUtility.ToJson(input), Is.EqualTo(original));
            Track(descriptor.expressionParameters);
            TrackControllerAssets((AnimatorController)descriptor.baseAnimationLayers.Single(l => l.type == VRCAvatarDescriptor.AnimLayerType.FX).animatorController);
        }

        [TestCase(false, ParameterMultiplexingMode.AutomaticMenuInputs)] [TestCase(true, ParameterMultiplexingMode.AutomaticMenuInputs)]
        [TestCase(false, ParameterMultiplexingMode.AllSynchronizedInputs)] [TestCase(true, ParameterMultiplexingMode.AllSynchronizedInputs)]
        [TestCase(false, ParameterMultiplexingMode.AutomaticByPriority)] [TestCase(true, ParameterMultiplexingMode.AutomaticByPriority)]
        public void MultiplexingMobileKeepsTheProtocolAndOnlyDecodesSurvivingInputs(bool removeAll, ParameterMultiplexingMode mode)
        {
            var descriptor = LayoutDescriptor(); var original = MultiplexInputs();
            var plan = MultiplexPlan(original); var record = LayoutRecord(plan.WireDeclarations()); record.Multiplexing = plan;
            using var fixture = new LayoutTestStore(); fixture.Store.Commit(record);
            var input = Track(ScriptableObject.CreateInstance<VRCExpressionParameters>());
            input.parameters = removeAll ? Array.Empty<Parameter>() : original.Take(1).ToArray(); descriptor.expressionParameters = input;
            var settings = new ParameterLayoutSettings { Compression = new ParameterCompressionSettings
                { Multiplexing = mode, Budget = 16 } };
            var context = new BuildContext(root, null);
            ParameterLayoutPass.ApplyFinal(root, settings, ParameterLayoutPlatform.Android,
                new OptimizationBuildReport(), value => Track(value), fixture.Store, context);
            Assert.That(ParameterLayoutModel.Capture(descriptor.expressionParameters.parameters).Select(p => (p.Name, p.Type)),
                Is.EqualTo(record.Parameters.Select(p => (p.Name, p.Type))));
            var fx = (AnimatorController)descriptor.baseAnimationLayers.Single(l => l.type == VRCAvatarDescriptor.AnimLayerType.FX).animatorController;
            TrackControllerAssets(fx);
            Assert.That(fx.layers.Last().stateMachine.states.Any(s => s.state.name.StartsWith("Mux Commit")), Is.EqualTo(!removeAll));
            Assert.That(fx.parameters.Any(p => p.name == "Float3"), Is.False, "Removed features need no logical input or decoder");
            Assert.That(fx.layers.Last().stateMachine.states.Any(s => s.state.name.StartsWith("Mux Publish")), Is.True,
                "Android owners still send defaults for inputs that remote PC avatars may observe");
        }

        [Test]
        public void MultiplexingDisabledDoesNotEvaluateHiddenTimingAndBudgetSettings()
        {
            LayoutDescriptor();
            var component = root.AddComponent<ALTOParameterCompression>();
            component.TimeMultiplexing = ParameterMultiplexingMode.Disabled;
            component.TargetBitBudget = -1; component.PacketHoldSeconds = float.NaN;
            var settings = ParameterCompressionPass.Capture(new BuildContext(root, null), true);
            Assert.That(settings.Multiplexing, Is.EqualTo(ParameterMultiplexingMode.Disabled));
        }

        [Test]
        public void MultiplexingBudgetFailurePublishesSelectionReasonsWithoutChangingTheAvatar()
        {
            var descriptor = LayoutDescriptor();
            var original = Track(ScriptableObject.CreateInstance<VRCExpressionParameters>());
            original.parameters = MultiplexInputs(); descriptor.expressionParameters = original;
            var context = new BuildContext(root, null);
            context.GetState<ParameterLayoutState>().Settings = new ParameterLayoutSettings
            {
                ReferencePlatform = ParameterLayoutPass.Platform(context.GetState<OptimizationBuildEnvironment>().Target),
                Compression = new ParameterCompressionSettings { Budget = 16, Multiplexing = ParameterMultiplexingMode.AutomaticMenuInputs }
            };
            context.GetState<OptimizationBuildReport>().Initialize(root, "Fixture");
            string[] published = null;
            void Report(GameObject avatar, OptimizationBuildReport report)
            { if (avatar == root) published = report.Messages.ToArray(); }
            AvatarBuildAnalysis.ReportReady += Report;
            try
            {
                var exception = Assert.Throws<InvalidOperationException>(() => ParameterLayoutPass.Apply(context));
                Assert.That(exception.Message, Does.Contain("NoPersistentMenu=32 bits"));
                Assert.That(published, Is.Not.Null);
                Assert.That(published.Count(m => m.StartsWith("Automatic multiplexing kept native:", StringComparison.Ordinal)), Is.EqualTo(4));
                Assert.That(descriptor.expressionParameters, Is.SameAs(original));
                Assert.That(original.parameters.All(p => p.networkSynced), Is.True);
                Assert.That(descriptor.baseAnimationLayers.All(l => l.animatorController == null), Is.True);
            }
            finally { AvatarBuildAnalysis.ReportReady -= Report; }
        }

        [TestCase(false)] [TestCase(true)]
        public void MultiplexingSdkReusesTheMobileProtocolWithoutDoubleEncoding(bool rewrite)
        {
            var descriptor = LayoutDescriptor(); var logical = MultiplexInputs(); var plan = MultiplexPlan(logical);
            var record = LayoutRecord(plan.WireDeclarations()); record.Multiplexing = plan;
            using var fixture = new LayoutTestStore(); fixture.Store.Commit(record);
            string confirmed = JsonUtility.ToJson(fixture.Store.Read(LayoutId));
            descriptor.expressionParameters = Track(ScriptableObject.CreateInstance<VRCExpressionParameters>());
            descriptor.expressionParameters.parameters = logical;
            var context = new BuildContext(root, null);
            var settings = new ParameterLayoutSettings { Compression = new ParameterCompressionSettings
                { Multiplexing = ParameterMultiplexingMode.AutomaticMenuInputs, Budget = 16 } };
            ParameterLayoutSdkSession.Begin(root);
            ParameterLayoutSdkSession.Defer(context, settings, ParameterLayoutPlatform.Android, new OptimizationBuildReport(), fixture.Store);
            var before = descriptor.expressionParameters;
            Track(before);
            var fx = (AnimatorController)descriptor.baseAnimationLayers.Single(l => l.type == VRCAvatarDescriptor.AnimLayerType.FX).animatorController;
            TrackControllerAssets(fx);
            Assert.That(before.CalcTotalCost(), Is.EqualTo(16), "Late callbacks must already see the reference encoding");
            if (rewrite) before.parameters.Single(p => p.name == plan.Tag).name = "ExternalReplacement";
            else before.parameters = before.parameters.Reverse().ToArray();
            if (rewrite) Assert.Throws<InvalidOperationException>(() => ParameterLayoutSdkSession.Complete(root, p => Track(p), fixture.Store));
            else
            {
                ParameterLayoutSdkSession.Complete(root, p => Track(p), fixture.Store);
                Assert.That(ParameterLayoutModel.Capture(descriptor.expressionParameters.parameters).Select(p => (p.Name, p.Type)),
                    Is.EqualTo(record.Parameters.Select(p => (p.Name, p.Type))));
                Assert.That(descriptor.baseAnimationLayers.Single(l => l.type == VRCAvatarDescriptor.AnimLayerType.FX).animatorController, Is.SameAs(fx));
            }
            Assert.That(JsonUtility.ToJson(fixture.Store.Read(LayoutId)), Is.EqualTo(confirmed));
        }

        [Test]
        public void MultiplexingSdkPersistsGeneratedAssetsAcrossNdmfSaverDisposal()
        {
            string folderName = "AdvancedMultiplexSdkFixture_" + Guid.NewGuid().ToString("N"), folder = "Assets/" + folderName;
            AssetDatabase.CreateFolder("Assets", folderName);
            try
            {
                var descriptor = LayoutDescriptor();
                var input = Track(ScriptableObject.CreateInstance<VRCExpressionParameters>());
                input.parameters = MultiplexInputs(); descriptor.expressionParameters = input;
                root.AddComponent<ALTOParameterLayout>();
                var marker = root.AddComponent<ALTOParameterCompression>();
                marker.TimeMultiplexing = ParameterMultiplexingMode.SelectedInputs;
                marker.RemoveUnusedSynchronization = false; marker.TargetBitBudget = 16;
                marker.MultiplexedParameters.Names.AddRange(input.parameters.Select(p => p.name));
                var context = new BuildContext(root, folder); ParameterLayoutPass.Capture(context);
                ParameterLayoutSdkSession.Begin(root);
                ParameterLayoutSdkSession.Defer(context, context.GetState<ParameterLayoutState>().Settings,
                    ParameterLayoutPlatform.PC, new OptimizationBuildReport());
                var generated = descriptor.expressionParameters;
                context.AssetSaver.Dispose();
                using var fixture = new LayoutTestStore();
                ParameterLayoutSdkSession.Complete(root, storeForTests: fixture.Store);
                Assert.That(descriptor.expressionParameters, Is.SameAs(generated));
                Assert.That(EditorUtility.IsPersistent(generated), Is.True);
                Assert.That(generated.CalcTotalCost(), Is.EqualTo(16));
                var controller = (AnimatorController)descriptor.baseAnimationLayers.Single(l => l.type == VRCAvatarDescriptor.AnimLayerType.FX).animatorController;
                Assert.That(EditorUtility.IsPersistent(controller), Is.True);
                var states = controller.layers.Last().stateMachine.states.Select(s => s.state).ToArray();
                var drivers = states.SelectMany(s => s.behaviours).ToArray();
                Assert.That(drivers.Length, Is.GreaterThan(0));
                Assert.That(drivers.All(EditorUtility.IsPersistent), Is.True);
                Assert.That(states.Select(s => s.motion).Distinct().All(EditorUtility.IsPersistent), Is.True);
                Assert.That(AssetDatabase.GetAssetPath(controller), Does.StartWith(folder + "/"));
                Assert.That(fixture.Store.Read(LayoutId), Is.Null, "A build is not an upload confirmation");
            }
            finally { AssetDatabase.DeleteAsset(folder); }
        }
    }
}
