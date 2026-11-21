using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.Installer;
using Noname.AvatarTools.ParameterLayout;
using Noname.AvatarTools.ParameterPolicy;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDK3.Dynamics.Contact.Components;
using VRC.SDKBase;
using Parameter = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters.Parameter;
using ValueType = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters.ValueType;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private (VRCAvatarDescriptor Descriptor, AnimatorController Controller) PrecisionAvatar(params Parameter[] parameters)
        {
            var descriptor = LayoutDescriptor();
            descriptor.expressionParameters = Track(ScriptableObject.CreateInstance<VRCExpressionParameters>());
            descriptor.expressionParameters.parameters = parameters;
            var controller = Controller(Clip());
            foreach (var p in parameters) controller.AddParameter(p.name, p.valueType == ValueType.Float ? AnimatorControllerParameterType.Float :
                p.valueType == ValueType.Int ? AnimatorControllerParameterType.Int : AnimatorControllerParameterType.Bool);
            descriptor.customizeAnimationLayers = true;
            descriptor.baseAnimationLayers = new[] { new VRCAvatarDescriptor.CustomAnimLayer
                { type = VRCAvatarDescriptor.AnimLayerType.FX, isDefault = false, animatorController = controller } };
            descriptor.specialAnimationLayers = Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>();
            return (descriptor, controller);
        }

        [Test]
        public void InputAssumptionDefaultsDoNotAssumeAvatarDrivenWriters()
        {
            var compression = root.AddComponent<ALTOParameterCompression>();
            var policy = root.AddComponent<ALTOAnimatorParameterPolicy>();
            var draft = new InstallerDraft();
            var template = AssetDatabase.LoadAssetAtPath<GameObject>(AvatarInstaller.TemplatePath);
            Assert.That(compression.InputAssumption, Is.EqualTo(ParameterInputAssumption.ExternalWithCompression));
            Assert.That(compression.AllowUnsignedBitReduction, Is.True);
            Assert.That(policy.FreezeMode, Is.EqualTo(AnimatorParameterFreezeMode.AllExternalInputsExceptPreserved));
            Assert.That(policy.UsePreserveForCompression, Is.True);
            Assert.That(draft.InputAssumption, Is.EqualTo(compression.InputAssumption));
            Assert.That(draft.FreezeMode, Is.EqualTo(policy.FreezeMode));
            Assert.That(draft.UsePreserveForCompression, Is.True);
            Assert.That(template.GetComponentInChildren<ALTOParameterCompression>().InputAssumption, Is.EqualTo(compression.InputAssumption));
            Assert.That(template.GetComponentInChildren<ALTOAnimatorParameterPolicy>().UsePreserveForCompression, Is.True);
        }

        [TestCase(32, 0)] [TestCase(24, 2)] [TestCase(16, 2)]
        public void InputAssumptionKeepPrioritiesCompressLastButCanStillFitTheBudget(int budget, int listedCount)
        {
            var fixture = PrecisionAvatar(MultiplexInputs(8));
            var settings = new ParameterCompressionSettings { Multiplexing = ParameterMultiplexingMode.AllSynchronizedInputs, Budget = budget };
            var keep = new ResolvedParameterKeepRule(); keep.Names.UnionWith(new[] { "Float6", "Float7" }); settings.Keep.Add(keep);
            var result = ParameterCompressionPass.Apply(new BuildContext(root, null), settings, null, new OptimizationBuildReport(), o => Track(o));
            Assert.That(result.Multiplexing.Inputs.Count(p => keep.Names.Contains(p.Name)), Is.EqualTo(listedCount));
            Assert.That(MultiplexEncoding.Cost(fixture.Descriptor.expressionParameters.parameters), Is.LessThanOrEqualTo(budget));
        }

        [TestCase(ParameterInputAssumption.ExternalWithCompression, false)]
        [TestCase(ParameterInputAssumption.AvatarDrivenExceptListed, true)]
        public void InputAssumptionDeterminesWhetherListedInputsAreStrictlyProtected(ParameterInputAssumption assumption, bool fails)
        {
            PrecisionAvatar(MultiplexInputs(4));
            var settings = new ParameterCompressionSettings { Multiplexing = ParameterMultiplexingMode.AllSynchronizedInputs,
                Budget = 16, InputAssumption = assumption };
            var keep = new ResolvedParameterKeepRule(); keep.Names.Add("Float0"); settings.Keep.Add(keep);
            var context = new BuildContext(root, null);
            if (fails) Assert.Throws<InvalidOperationException>(() => ParameterCompressionPass.Apply(context, settings, null, new OptimizationBuildReport(), o => Track(o)));
            else Assert.That(ParameterCompressionPass.Apply(context, settings, null, new OptimizationBuildReport(), o => Track(o)).Multiplexing.Inputs.Count, Is.EqualTo(4));
        }

        [TestCase(AnimatorParameterFreezeMode.SelectedParameters, false)]
        [TestCase(AnimatorParameterFreezeMode.AllExternalInputsExceptPreserved, true)]
        [TestCase(AnimatorParameterFreezeMode.RespectCompressionExternalInputs, false)]
        public void InputAssumptionFreezeModesImportCompressionOnlyWhenRequested(AnimatorParameterFreezeMode mode, bool freezesExternal)
        {
            PrecisionAvatar(new[] { "External", "Mobile", "Other" }.Select(n => LayoutParameter(n)).ToArray());
            var compression = root.AddComponent<ALTOParameterCompression>();
            var keep = new ParameterKeepRule(); keep.Parameters.Names.Add("External"); compression.KeepParameters.Add(keep);
            var policy = Policy(); policy.FreezeMode = mode;
            policy.PreserveParameters.Names.Add("Mobile");
            policy.FreezeParameters.Add(new AnimatorParameterConstant { Name = "Other", Value = 0 });
            var context = new BuildContext(root, null); ParameterPolicyPass.Capture(context);
            var settings = ParameterCompressionPass.Capture(context, true);
            Assert.That(settings.Keep.Count(r => r.Names.Contains("External")), Is.EqualTo(1), "Imported inputs must not feed back through Policy sharing.");
            Assert.That(settings.Keep.Count(r => r.Names.Contains("Mobile")), Is.EqualTo(1));
            ParameterFreezePass.Apply(context);
            var frozen = context.GetState<ParameterPolicyState>().Constants;
            Assert.That(frozen.ContainsKey("External"), Is.EqualTo(freezesExternal));
            Assert.That(frozen.ContainsKey("Mobile"), Is.False);
            Assert.That(frozen.ContainsKey("Other"), Is.True);
        }

        [TestCase(false)] [TestCase(true)]
        public void InputAssumptionInstallerSharesPolicySelectionsWithoutEnablingAvatarDrivenMode(bool mobile)
        {
            var draft = InstallerChoices(mobile); draft.ParameterLayout = draft.ParameterCompression = true;
            draft.PreserveParameters.Names.Add("Face");
            var installed = InstallerSettings(draft);
            var policy = installed.GetComponentInChildren<ALTOAnimatorParameterPolicy>();
            Assert.That(policy, Is.Not.Null); Assert.That(policy.PreserveParameters.Names, Does.Contain("Face"));
            Assert.That(policy.UsePreserveForCompression, Is.True);
            if (!mobile) Assert.That(policy.ApplyOnPlatforms, Is.EqualTo(OptimizationPlatforms.None));
            var marker = installed.GetComponentInChildren<ALTOParameterCompression>();
            Assert.That(marker.KeepParameters, Is.Empty, "Installer stores the primary selection in Policy only.");
            Assert.That(marker.InputAssumption, Is.EqualTo(ParameterInputAssumption.ExternalWithCompression));
            var context = new BuildContext(root, null);
            ParameterPolicyPass.Capture(context); ParameterPolicyPass.RemoveMarkers(context);
            var settings = ParameterCompressionPass.Capture(context, true);
            Assert.That(settings.Keep.Single().Names, Does.Contain("Face"));
        }

        [TestCase("Set", true)] [TestCase("Random", true)] [TestCase("CopyConstant", true)] [TestCase("Menu", true)]
        [TestCase("Negative", false)] [TestCase("Saved", false)] [TestCase("Sensor", false)] [TestCase("CopyExternal", false)]
        [TestCase("Add", false)] [TestCase("ArtistBits", false)] [TestCase("ArtistUnsigned", false)]
        [TestCase("Explicit", false)] [TestCase("Protected", false)] [TestCase("TwoAxis", false)] [TestCase("Event", false)]
        public void AvatarDrivenEncodingRequiresACompleteNonnegativeRangeProof(string kind, bool allowed)
        {
            var input = LayoutParameter("Value"); input.saved = kind == "Saved";
            if (kind == "ArtistBits") input.bitWidth = 6;
            if (kind == "ArtistUnsigned") input.flippedRepresentation = true;
            var fixture = PrecisionAvatar(input);
            fixture.Controller.AddParameter(new AnimatorControllerParameter { name = "Raw", type = AnimatorControllerParameterType.Float, defaultFloat = .7f });
            var operation = new VRC_AvatarParameterDriver.Parameter { name = "Value", type = VRC_AvatarParameterDriver.ChangeType.Set, value = .75f };
            if (kind == "Negative") operation.value = -.25f;
            if (kind == "Random") { operation.type = VRC_AvatarParameterDriver.ChangeType.Random; operation.valueMin = 0; operation.valueMax = 1; }
            if (kind.StartsWith("Copy", StringComparison.Ordinal)) { operation.type = VRC_AvatarParameterDriver.ChangeType.Copy; operation.source = "Raw"; }
            if (kind == "Add") operation.type = VRC_AvatarParameterDriver.ChangeType.Add;
            Track(fixture.Controller.layers[0].stateMachine.defaultState.AddStateMachineBehaviour<VRCAvatarParameterDriver>()).parameters = new() { operation };
            if (kind == "Sensor") PolicyChild("Sensor").AddComponent<VRCContactReceiver>().parameter = "Value";
            if (kind is "Menu" or "TwoAxis")
            {
                var control = ExpressionControl("Value", "Value");
                control.type = kind == "Menu" ? VRCExpressionsMenu.Control.ControlType.RadialPuppet : VRCExpressionsMenu.Control.ControlType.TwoAxisPuppet;
                control.subParameters = new[] { new VRCExpressionsMenu.Control.Parameter { name = "Value" } };
                fixture.Descriptor.expressionsMenu = ExpressionMenu(control);
            }
            if (kind == "Event") AnimationUtility.SetAnimationEvents((AnimationClip)fixture.Controller.layers[0].stateMachine.defaultState.motion,
                new[] { new AnimationEvent { functionName = "UnknownWriter" } });
            var controller = new CloneContext(VRChatPlatformAnimatorBindings.Instance).Clone(fixture.Controller);
            var external = new HashSet<string>(); if (kind == "CopyExternal") external.Add("Raw"); if (kind == "Protected") external.Add("Value");
            var overridden = new HashSet<string>(); if (kind == "Explicit") overridden.Add("Value");
            var candidates = AvatarDrivenEncodingAnalysis.Analyze(root, new[] { controller }, new[] { input }, external, overridden, new List<string>());
            Assert.That(candidates.Any(p => p.Name == "Value"), Is.EqualTo(allowed));
            Assert.That(AnimatorReachability.Analyze(root, new[] { controller }).Values("Value").TryGetConstant(out _), Is.False,
                "Ordinary reachability must still treat expression inputs as unknown.");
        }

        [TestCase(ParameterInputAssumption.ExternalWithCompression, true, 8)]
        [TestCase(ParameterInputAssumption.AvatarDrivenExceptListed, false, 8)]
        [TestCase(ParameterInputAssumption.AvatarDrivenExceptListed, true, 7)]
        public void AvatarDrivenEncodingIsGatedByBothAssumptionAndOption(ParameterInputAssumption assumption, bool reduce, int bits)
        {
            var input = LayoutParameter("Value"); input.saved = false;
            var fixture = PrecisionAvatar(input);
            var result = ParameterCompressionPass.Apply(new BuildContext(root, null), new ParameterCompressionSettings
                { InputAssumption = assumption, AllowUnsignedBitReduction = reduce }, null, new OptimizationBuildReport(), o => Track(o));
            var output = fixture.Descriptor.expressionParameters.parameters.Single();
            Assert.That(output.GetBitWidthByType(), Is.EqualTo(bits)); Assert.That(output.flippedRepresentation, Is.EqualTo(bits == 7));
            var record = LayoutRecord(output); record.AvatarDrivenEncodings = result.AvatarDriven;
            ParameterLayoutModel.Validate(record);
        }

        [TestCase(127, 7)] [TestCase(128, 8)]
        public void AvatarDrivenIntegerReductionUsesProvenBounds(int maximum, int bits)
        {
            var input = LayoutParameter("Mode", ValueType.Int); input.saved = false; input.defaultValue = maximum;
            var fixture = PrecisionAvatar(input);
            ParameterCompressionPass.Apply(new BuildContext(root, null), new ParameterCompressionSettings
                { InputAssumption = ParameterInputAssumption.AvatarDrivenExceptListed }, null, new OptimizationBuildReport(), o => Track(o));
            Assert.That(fixture.Descriptor.expressionParameters.parameters.Single().GetBitWidthByType(), Is.EqualTo(bits));
        }

        [Test]
        public void AvatarDrivenPlanningRetainsSignedSlotsWhenSplittingWouldIncreaseLatency()
        {
            var fixture = PrecisionAvatar(MultiplexInputs(8));
            var input = fixture.Descriptor.expressionParameters.parameters;
            var candidate = ParameterLayoutEntry.Capture(input[7]); candidate.BitWidth = 7; candidate.FlippedRepresentation = true;
            var plan = ParameterTransportPlanning.Choose(root, Array.Empty<VirtualAnimatorController>(), new Dictionary<object, VirtualAnimatorController>(),
                new ParameterCompressionSettings { Multiplexing = ParameterMultiplexingMode.AllSynchronizedInputs, Budget = 40 }, input,
                new HashSet<string>(), new Dictionary<string, int>(), new HashSet<string>(), new HashSet<string>(), new() { candidate }, new OptimizationBuildReport());
            Assert.That(plan.AvatarDriven, Is.Empty); Assert.That(plan.Multiplexing.Batches, Is.EqualTo(2));
        }

        [TestCase(false)] [TestCase(true)]
        public void AvatarDrivenReferenceEncodingReplaysWithoutReinferringMobileRanges(bool removed)
        {
            var input = LayoutParameter("Value"); input.bitWidth = 7; input.flippedRepresentation = true;
            var record = LayoutRecord(input); record.AvatarDrivenEncodings.Add(ParameterLayoutEntry.Capture(input));
            var fixture = PrecisionAvatar(removed ? Array.Empty<Parameter>() : new[] { LayoutParameter("Value") });
            ParameterCompressionPass.Apply(new BuildContext(root, null), new ParameterCompressionSettings(), record, new OptimizationBuildReport(), o => Track(o));
            var aligned = ParameterLayoutModel.Align(record, fixture.Descriptor.expressionParameters.parameters, AdditionalSyncedParameterPolicy.Error);
            Assert.That(aligned.Padding, Is.EqualTo(removed ? 1 : 0)); Assert.That(aligned.Parameters.Single().GetBitWidthByType(), Is.EqualTo(7));
        }
    }
}
