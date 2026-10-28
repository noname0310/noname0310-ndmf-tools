using System;
using System.Collections.Generic;
using System.Linq;
using Noname.AvatarTools.ParameterPolicy;
using Noname.AvatarTools.Analysis;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDK3.Dynamics.Contact.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;
using VRC.SDKBase;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private VRCAvatarDescriptor FreezeExpressions(params VRCExpressionParameters.Parameter[] parameters)
        {
            var descriptor = root.GetComponent<VRCAvatarDescriptor>();
            if (descriptor == null) descriptor = root.AddComponent<VRCAvatarDescriptor>();
            descriptor.expressionParameters = Track(ScriptableObject.CreateInstance<VRCExpressionParameters>());
            descriptor.expressionParameters.parameters = parameters;
            return descriptor;
        }

        [Test]
        public void FreezeDefaultUsesFinalExpressionDefaultInsteadOfOriginalControllerDefault()
        {
            FreezeExpressions(new VRCExpressionParameters.Parameter { name = "Input", valueType = VRCExpressionParameters.ValueType.Float, defaultValue = 0.5f, saved = true });
            var source = Controller(Clip()); source.AddParameter("Input", AnimatorControllerParameterType.Float);
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            Assert.That(ParameterFreezeSelection.DefaultValue(root, new[] { controller }, "Input"), Is.EqualTo(0.5f));
            Assert.That(controller.Parameters["Input"].defaultFloat, Is.Zero);
        }

        [Test]
        public void FreezeDefaultRejectsInconsistentControllerDefaultsAndTypeConversions()
        {
            var a = Controller(Clip()); a.AddParameter("Input", AnimatorControllerParameterType.Float);
            var b = Controller(Clip()); b.parameters = new[] { new AnimatorControllerParameter { name = "Input", type = AnimatorControllerParameterType.Float, defaultFloat = 1 } };
            var clone = new CloneContext(GenericPlatformAnimatorBindings.Instance);
            var controllers = new[] { clone.Clone(a), clone.Clone(b) };
            Assert.Throws<InvalidOperationException>(() => ParameterFreezeSelection.DefaultValue(root, controllers, "Input"));
            FreezeExpressions(new VRCExpressionParameters.Parameter { name = "Input", valueType = VRCExpressionParameters.ValueType.Float, defaultValue = 0.5f });
            Assert.That(ParameterFreezeSelection.DefaultValue(root, controllers, "Input"), Is.EqualTo(0.5f));
            root.GetComponent<VRCAvatarDescriptor>().expressionParameters.parameters[0].valueType = VRCExpressionParameters.ValueType.Int;
            Assert.Throws<InvalidOperationException>(() => ParameterFreezeSelection.DefaultValue(root, controllers, "Input"));
        }

        [TestCase(VRCExpressionParameters.ValueType.Bool, AnimatorControllerParameterType.Float, 1f, 1f)]
        [TestCase(VRCExpressionParameters.ValueType.Float, AnimatorControllerParameterType.Int, 0.5f, 0f)]
        [TestCase(VRCExpressionParameters.ValueType.Float, AnimatorControllerParameterType.Int, 1.5f, 2f)]
        [TestCase(VRCExpressionParameters.ValueType.Float, AnimatorControllerParameterType.Bool, -0.5f, 1f)]
        public void FreezeDefaultConvertsExpressionTypesUsingVrchatRules(VRCExpressionParameters.ValueType expressionType,
            AnimatorControllerParameterType animatorType, float value, float expected)
        {
            FreezeExpressions(new VRCExpressionParameters.Parameter { name = "Input", valueType = expressionType, defaultValue = value });
            var source = Controller(Clip()); source.AddParameter("Input", animatorType);
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            Assert.That(ParameterFreezeSelection.DefaultValue(root, new[] { controller }, "Input"), Is.EqualTo(expected));
        }

        [Test]
        public void FreezeDefaultRejectsLossyConversionWhenDriverCopyNeedsOriginalValue()
        {
            FreezeExpressions(new VRCExpressionParameters.Parameter { name = "Input", valueType = VRCExpressionParameters.ValueType.Float, defaultValue = 0.6f });
            var source = Controller(Clip()); source.AddParameter("Input", AnimatorControllerParameterType.Int);
            source.AddParameter("Output", AnimatorControllerParameterType.Float);
            Driver(source.layers[0].stateMachine.defaultState, "Output", 0, VRC_AvatarParameterDriver.ChangeType.Copy, "Input");
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            Assert.Throws<InvalidOperationException>(() => ParameterFreezeSelection.DefaultValue(root, new[] { controller }, "Input"));
        }

        [TestCase(0)] [TestCase(1)]
        public void FreezeSupportsEqualConstantsAcrossBoolAndFloatConsumers(int value)
        {
            FreezeExpressions(new VRCExpressionParameters.Parameter { name = "Input", valueType = VRCExpressionParameters.ValueType.Bool, defaultValue = value });
            var a = Controller(Clip(), Clip()); a.AddParameter("Input", AnimatorControllerParameterType.Bool);
            Edge(a.layers[0].stateMachine.states[0].state, a.layers[0].stateMachine.states[1].state, "Input", 0, AnimatorConditionMode.If);
            var b = TreeController(("Input", StaticCurve(Binding("a"), 100)));
            var unrelated = Controller(Clip());
            var clone = new CloneContext(GenericPlatformAnimatorBindings.Instance);
            var controllers = new[] { clone.Clone(a), clone.Clone(b), clone.Clone(unrelated) };
            Assert.That(ParameterFreezeSelection.DefaultValue(root, controllers, "Input"), Is.EqualTo(value));
            var constants = ParameterSpecialization.Prepare(controllers, new Dictionary<string, float> { ["Input"] = value }, new HashSet<string>());
            ParameterSpecialization.Apply(controllers, constants);
            string name = constants["Input"].InternalName;
            Assert.That(controllers[0].Parameters[name].type, Is.EqualTo(AnimatorControllerParameterType.Bool));
            Assert.That(controllers[1].Parameters[name].type, Is.EqualTo(AnimatorControllerParameterType.Float));
            Assert.That(controllers[2].Parameters.ContainsKey(name), Is.False);
            Assert.That(AnimatorReachability.Analyze(root, controllers).Values(name).TryGetConstant(out var actual), Is.True);
            Assert.That(actual, Is.EqualTo(value));
        }

        [Test]
        public void MixedTypeAnalysisUnionsNumericAndBooleanDriverWrites()
        {
            var a = Controller(Clip()); a.AddParameter("Input", AnimatorControllerParameterType.Bool);
            Driver(a.layers[0].stateMachine.defaultState, "Input", 0, VRC_AvatarParameterDriver.ChangeType.Random).parameters[0].valueMin = 2;
            ((VRC_AvatarParameterDriver)a.layers[0].stateMachine.defaultState.behaviours[0]).parameters[0].valueMax = 3;
            var b = Controller(Clip()); b.AddParameter("Input", AnimatorControllerParameterType.Float);
            var clone = new CloneContext(GenericPlatformAnimatorBindings.Instance);
            var values = AnimatorReachability.Analyze(root, new[] { clone.Clone(a), clone.Clone(b) }).Values("Input");
            Assert.That(values.CanSatisfy(new[] { new AnimatorCondition { parameter = "Input", mode = AnimatorConditionMode.Greater, threshold = 1.5f } }), Is.True);
            Assert.That(values.CanSatisfy(new[] { new AnimatorCondition { parameter = "Input", mode = AnimatorConditionMode.IfNot } }), Is.True);
        }

        [Test]
        public void FreezeDefaultCaptureDefersValueUntilAfterGenerators()
        {
            var policy = Policy();
            policy.FreezeParameters.Add(new AnimatorParameterConstant { Name = "Generated", UseDefaultValue = true, Value = float.NaN });
            var context = new BuildContext(root, null); ParameterPolicyPass.Capture(context);
            Assert.That(context.GetState<ParameterPolicyState>().DefaultFrozen, Does.Contain("Generated"));
            Assert.That(context.GetState<ParameterPolicyState>().Frozen, Is.Empty);
        }

        [TestCase(AnimatorParameterFreezeMode.SelectedParameters, false)]
        [TestCase(AnimatorParameterFreezeMode.SelectedParameters, true)]
        [TestCase(AnimatorParameterFreezeMode.AllExternalInputsExceptPreserved, false)]
        [TestCase(AnimatorParameterFreezeMode.AllExternalInputsExceptPreserved, true)]
        public void FreezeModeControlsSelectionIndependentlyOfTheExplicitRowCount(AnimatorParameterFreezeMode mode, bool hasRow)
        {
            var descriptor = FreezeExpressions(new[] { "Input", "Other" }.Select(n => new VRCExpressionParameters.Parameter
                { name = n, valueType = VRCExpressionParameters.ValueType.Float, defaultValue = 1 }).ToArray());
            var source = Controller(Clip()); source.AddParameter("Input", AnimatorControllerParameterType.Float);
            source.AddParameter("Other", AnimatorControllerParameterType.Float);
            descriptor.customizeAnimationLayers = true;
            descriptor.baseAnimationLayers = new[] { new VRCAvatarDescriptor.CustomAnimLayer
                { type = VRCAvatarDescriptor.AnimLayerType.FX, isDefault = false, animatorController = source } };
            descriptor.specialAnimationLayers = Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>();
            var policy = Policy(); policy.FreezeMode = mode;
            if (hasRow) policy.FreezeParameters.Add(new AnimatorParameterConstant { Name = "Other", Value = 0 });
            var before = EditorJsonUtility.ToJson(source);
            var context = new BuildContext(root, null); ParameterPolicyPass.Capture(context);
            ParameterFreezePass.Apply(context);
            var constants = context.GetState<ParameterPolicyState>().Constants;
            Assert.That(constants.ContainsKey("Input"), Is.EqualTo(mode == AnimatorParameterFreezeMode.AllExternalInputsExceptPreserved));
            Assert.That(constants.ContainsKey("Other"), Is.EqualTo(hasRow || mode == AnimatorParameterFreezeMode.AllExternalInputsExceptPreserved));
            Assert.That(EditorJsonUtility.ToJson(source), Is.EqualTo(before));
        }

        [Test]
        public void AutomaticFreezeIncludesCalculatedExpressionsButKeepsPreservedAndInternalParameters()
        {
            var descriptor = FreezeExpressions(new[] { "Input", "Resettable", "Calculated", "AnimatedOutput", "Preserved", "IsLocal", "Unused" }
                .Select(n => new VRCExpressionParameters.Parameter { name = n, valueType = VRCExpressionParameters.ValueType.Float }).ToArray());
            var clip = StaticCurve(ParameterBinding("AnimatedOutput"), 1);
            var source = Controller(clip);
            foreach (var name in new[] { "Input", "Resettable", "Calculated", "AnimatedOutput", "Preserved", "IsLocal", "Sensor", "Bone_Angle", "InternalClock" })
                source.AddParameter(name, AnimatorControllerParameterType.Float);
            var state = source.layers[0].stateMachine.defaultState;
            Driver(state, "Calculated", 0, VRC_AvatarParameterDriver.ChangeType.Copy, "Input");
            Driver(state, "Resettable", 0);
            AnimationUtility.SetEditorCurve(clip, ParameterBinding("InternalClock"), AnimationCurve.Linear(0, 0, 1, 1));
            descriptor.expressionsMenu = Track(ScriptableObject.CreateInstance<VRCExpressionsMenu>());
            var submenu = Track(ScriptableObject.CreateInstance<VRCExpressionsMenu>());
            descriptor.expressionsMenu.controls.Add(new VRCExpressionsMenu.Control { subMenu = submenu });
            submenu.controls.Add(new VRCExpressionsMenu.Control { parameter = new VRCExpressionsMenu.Control.Parameter { name = "Resettable" }, subMenu = descriptor.expressionsMenu });
            PolicyChild("Contact").AddComponent<VRCContactReceiver>().parameter = "Sensor";
            PolicyChild("Bone").AddComponent<VRCPhysBone>().parameter = "Bone";
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var result = ParameterFreezeSelection.AutomaticInputs(root, new[] { controller }, new HashSet<string> { "Preserved" });
            Assert.That(result, Is.EquivalentTo(new[] { "Input", "Resettable", "Calculated", "AnimatedOutput", "Sensor", "Bone_Angle" }));
        }

        [Test]
        public void ExpressionOnlyDriverSourceCanBeSpecializedWithItsDeclaredType()
        {
            FreezeExpressions(new VRCExpressionParameters.Parameter { name = "ExpressionOnly", valueType = VRCExpressionParameters.ValueType.Float, defaultValue = 0.4f });
            var source = Controller(Clip()); source.AddParameter("Output", AnimatorControllerParameterType.Float);
            Driver(source.layers[0].stateMachine.defaultState, "Output", 0, VRC_AvatarParameterDriver.ChangeType.Copy, "ExpressionOnly");
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var selection = ParameterFreezeSelection.AutomaticInputs(root, new[] { controller }, new HashSet<string>());
            Assert.That(selection, Does.Contain("ExpressionOnly"));
            var constants = ParameterSpecialization.Prepare(new[] { controller }, new Dictionary<string, float> { ["ExpressionOnly"] = 0.4f },
                new HashSet<string>(), externalTypes: ParameterFreezeSelection.ExternalTypes(root));
            ParameterSpecialization.Apply(new[] { controller }, constants);
            Assert.That(ParameterSpecialization.Reads(controller.AllReachableNodes()), Does.Not.Contain("ExpressionOnly"));
            Assert.That(controller.Parameters[constants["ExpressionOnly"].InternalName].defaultFloat, Is.EqualTo(0.4f));
        }

        [Test]
        public void AutomaticModeRetainsInternalClockAnimationWhileFreezingItsExternalEnable()
        {
            FreezeExpressions(new VRCExpressionParameters.Parameter { name = "Enable", valueType = VRCExpressionParameters.ValueType.Bool, defaultValue = 1 });
            var clip = StaticCurve(ParameterBinding("Clock"), 0);
            AnimationUtility.SetEditorCurve(clip, ParameterBinding("Clock"), AnimationCurve.Linear(0, 0, 0.2f, 1));
            var source = Controller(clip, Clip()); source.AddParameter("Enable", AnimatorControllerParameterType.Bool); source.AddParameter("Clock", AnimatorControllerParameterType.Float);
            Edge(source.layers[0].stateMachine.states[0].state, source.layers[0].stateMachine.states[1].state, "Enable", 0, AnimatorConditionMode.IfNot);
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var inputs = ParameterFreezeSelection.AutomaticInputs(root, new[] { controller }, new HashSet<string>());
            var constants = ParameterSpecialization.Prepare(new[] { controller }, inputs.ToDictionary(n => n, n => ParameterFreezeSelection.DefaultValue(root, new[] { controller }, n)), new HashSet<string>());
            ParameterSpecialization.Apply(new[] { controller }, constants);
            Assert.That(constants.Keys, Is.EquivalentTo(new[] { "Enable" }));
            Assert.That(controller.AllReachableNodes().OfType<VirtualClip>().Any(c => c.GetFloatCurve(ParameterBinding("Clock")) != null), Is.True);
        }

        [Test]
        public void RaycastOutputsAreDynamicUntilAnExplicitPolicySpecializesThem()
        {
            var raycast = PolicyChild("Raycast").AddComponent<VRCRaycast>(); raycast.Parameter = "Probe";
            var source = Controller(Clip(), Clip());
            foreach (var name in AvatarParameterInputs.RaycastSuffixes) source.AddParameter("Probe" + name, AnimatorControllerParameterType.Float);
            string hit = "Probe" + VRCRaycast.PARAM_HIT;
            Edge(source.layers[0].stateMachine.states[0].state, source.layers[0].stateMachine.states[1].state, hit, 0.5f, AnimatorConditionMode.Greater);
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var before = AnimatorReachability.Analyze(root, new[] { controller });
            Assert.That(before.ReachableStateCount, Is.EqualTo(2));
            Assert.That(before.Values(hit).TryGetConstant(out _), Is.False);
            var selection = ParameterFreezeSelection.AutomaticInputs(root, new[] { controller }, new HashSet<string>());
            Assert.That(selection, Is.EquivalentTo(AvatarParameterInputs.RaycastSuffixes.Select(s => "Probe" + s)));
            var constants = ParameterSpecialization.Prepare(new[] { controller }, selection.ToDictionary(n => n, n => 0f), new HashSet<string>());
            ParameterSpecialization.Apply(new[] { controller }, constants);
            Assert.That(AnimatorReachability.Analyze(root, new[] { controller }).ReachableStateCount, Is.EqualTo(1));
            Assert.That(raycast.Parameter, Is.EqualTo("Probe"));
        }

        [Test]
        public void RaycastPreservationAndEmptyPrefixesDoNotInventInputs()
        {
            var raycast = PolicyChild("Raycast").AddComponent<VRCRaycast>(); raycast.Parameter = "Probe";
            PolicyChild("Unconfigured raycast").AddComponent<VRCRaycast>();
            var source = Controller(Clip()); source.AddParameter("Probe" + VRCRaycast.PARAM_RATIO, AnimatorControllerParameterType.Float);
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var outputs = AvatarParameterInputs.ExternalNames(root).ToArray();
            Assert.That(outputs, Is.EquivalentTo(AvatarParameterInputs.RaycastSuffixes.Select(s => "Probe" + s)));
            Assert.That(ParameterFreezeSelection.AutomaticInputs(root, new[] { controller }, new HashSet<string>(outputs)), Is.Empty);
        }

        [Test]
        public void AutomaticFreezeRespectsTheComputationsSupplyingPreservedExpressionOutputs()
        {
            var names = new[] { "KeepInput", "KeepOutput", "OtherInput", "OtherOutput" };
            var descriptor = FreezeExpressions(names.Select(name => new VRCExpressionParameters.Parameter
                { name = name, valueType = VRCExpressionParameters.ValueType.Float, defaultValue = 0.25f, networkSynced = true }).ToArray());
            var source = Controller(Clip());
            foreach (var name in names.Concat(new[] { "InternalOnly" })) source.AddParameter(name, AnimatorControllerParameterType.Float);
            var initial = source.layers[0].stateMachine.defaultState;
            Driver(initial, "KeepOutput", 0, VRC_AvatarParameterDriver.ChangeType.Copy, "KeepInput");
            Driver(initial, "OtherOutput", 0, VRC_AvatarParameterDriver.ChangeType.Copy, "OtherInput");
            Driver(initial, "InternalOnly", 0, VRC_AvatarParameterDriver.ChangeType.Copy, "OtherInput");
            descriptor.customizeAnimationLayers = true;
            descriptor.baseAnimationLayers = new[] { new VRCAvatarDescriptor.CustomAnimLayer
                { type = VRCAvatarDescriptor.AnimLayerType.FX, isDefault = false, animatorController = source } };
            descriptor.specialAnimationLayers = Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>();
            var policy = Policy(); policy.FreezeMode = AnimatorParameterFreezeMode.AllExternalInputsExceptPreserved;
            policy.PreserveParameters.Names.Add("KeepOutput");
            var before = EditorJsonUtility.ToJson(source);
            var context = new BuildContext(root, null);
            ParameterPolicyPass.Capture(context);
            ParameterFreezePass.Apply(context);
            var constants = context.GetState<ParameterPolicyState>().Constants;
            Assert.That(constants.Keys, Is.EquivalentTo(new[] { "OtherInput", "OtherOutput" }));
            Assert.That(constants["OtherOutput"].Value, Is.EqualTo(0.25f));
            Assert.That(EditorJsonUtility.ToJson(source), Is.EqualTo(before));
        }

        [TestCase("GestureLeft", AnimatorControllerParameterType.Int)]
        [TestCase("GestureRight", AnimatorControllerParameterType.Int)]
        [TestCase("GestureLeftWeight", AnimatorControllerParameterType.Float)]
        [TestCase("GestureRightWeight", AnimatorControllerParameterType.Float)]
        [TestCase("VelocityX", AnimatorControllerParameterType.Float)]
        [TestCase("Grounded", AnimatorControllerParameterType.Bool)]
        public void AutomaticFreezeKeepsGestureAndLocomotionBranchesDynamic(string input, AnimatorControllerParameterType type)
        {
            var expressionType = type == AnimatorControllerParameterType.Bool ? VRCExpressionParameters.ValueType.Bool :
                type == AnimatorControllerParameterType.Int ? VRCExpressionParameters.ValueType.Int : VRCExpressionParameters.ValueType.Float;
            FreezeExpressions(new VRCExpressionParameters.Parameter { name = input, valueType = expressionType },
                new VRCExpressionParameters.Parameter { name = "EffectEnable", valueType = VRCExpressionParameters.ValueType.Bool });
            var source = Controller(StaticCurve(Binding("a"), 0), StaticCurve(Binding("a"), 100));
            source.AddParameter(input, type);
            source.AddParameter("EffectEnable", AnimatorControllerParameterType.Bool);
            Edge(source.layers[0].stateMachine.states[0].state, source.layers[0].stateMachine.states[1].state,
                input, 0.5f, type == AnimatorControllerParameterType.Bool ? AnimatorConditionMode.If : AnimatorConditionMode.Greater);
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var selected = ParameterFreezeSelection.AutomaticInputs(root, new[] { controller }, new HashSet<string>());
            Assert.That(selected, Is.EquivalentTo(new[] { "EffectEnable" }));
            var constants = ParameterSpecialization.Prepare(new[] { controller }, selected.ToDictionary(n => n, n => 0f), new HashSet<string>());
            ParameterSpecialization.Apply(new[] { controller }, constants);
            var analysis = AnimatorReachability.Analyze(root, new[] { controller });
            Assert.That(analysis.Values(input).TryGetConstant(out _), Is.False);
            Assert.That(analysis.ReachableStateCount, Is.EqualTo(2));
            Assert.That(ParameterSpecialization.Reads(controller.AllReachableNodes()), Does.Contain(input));
        }
    }
}
