using System;
using System.Linq;
using nadena.dev.ndmf;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.Expressions;
using Noname.AvatarTools.ParameterPolicy;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDKBase;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private BuildContext FreezeSdkBuild(AnimatorController source,
            Action<ALTOAnimatorParameterPolicy> configure = null, bool begin = true)
        {
            var descriptor = FreezeExpressions(new VRCExpressionParameters.Parameter
            {
                name = "Input", valueType = VRCExpressionParameters.ValueType.Float,
                defaultValue = 0.25f, saved = true, networkSynced = true
            });
            root.AddComponent<Animator>();
            descriptor.customExpressions = descriptor.customizeAnimationLayers = true;
            descriptor.baseAnimationLayers = new[] { new VRCAvatarDescriptor.CustomAnimLayer
            { type = VRCAvatarDescriptor.AnimLayerType.FX, animatorController = source, isDefault = false } };
            descriptor.specialAnimationLayers = Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>();
            var policy = Policy();
            policy.FreezeParameters.Add(new AnimatorParameterConstant { Name = "Input", Value = 0.25f });
            configure?.Invoke(policy);
            if (begin) new AvatarOutputSdkBuildStart().OnPreprocessAvatar(root);
            var context = new BuildContext(root, null);
            ParameterPolicyPass.Capture(context);
            ParameterFreezePass.Apply(context);
            TrackControllerAssets(FreezeSdkController());
            Assert.That(root.GetComponentInChildren<ALTOTraceAndOptimizeExpressions>(), Is.Null);
            return context;
        }

        private AnimatorController FreezeSdkController() =>
            (AnimatorController)root.GetComponent<VRCAvatarDescriptor>().baseAnimationLayers[0].animatorController;

        private void FreezeSdkReplace(AnimatorController controller)
        {
            var descriptor = root.GetComponent<VRCAvatarDescriptor>();
            var layers = descriptor.baseAnimationLayers;
            layers[0].animatorController = controller;
            descriptor.baseAnimationLayers = layers;
        }

        private AnimatorController FreezeSdkFinish()
        {
            ParameterFreezeSdkFinalizer.Complete(root, allowTransientAssetsForTests: true);
            var result = FreezeSdkController();
            TrackControllerAssets(result);
            return result;
        }

        [Test]
        public void SdkFreezeSpecializesLateControllersWithoutAnExpressionsComponent()
        {
            Mesh("a");
            var original = TreeController(("Input", StaticCurve(Binding("a"), 100)));
            var originalJson = EditorJsonUtility.ToJson(original);
            var context = FreezeSdkBuild(original);
            var constant = context.GetState<ParameterPolicyState>().Constants["Input"];
            var descriptor = root.GetComponent<VRCAvatarDescriptor>();
            descriptor.expressionsMenu = ExpressionMenu(ExpressionControl("Input", "Input"));
            var parameters = descriptor.expressionParameters;
            var menu = descriptor.expressionsMenu;
            var parameterJson = EditorJsonUtility.ToJson(parameters);
            var menuJson = EditorJsonUtility.ToJson(menu);

            var late = TreeController(("Input", StaticCurve(Binding("a"), 100)),
                ("Transport", StaticCurve(Binding("a"), 200)));
            late.AddParameter("Output", AnimatorControllerParameterType.Float);
            var state = late.layers[0].stateMachine.defaultState;
            Driver(state, "Input", 1);
            var copy = Driver(state, "Output", 0, VRC_AvatarParameterDriver.ChangeType.Copy, "Input");
            copy.localOnly = true;
            copy.parameters[0].convertRange = true;
            copy.parameters[0].sourceMax = 1; copy.parameters[0].destMax = 10;
            var lateJson = EditorJsonUtility.ToJson(late);
            FreezeSdkReplace(late);
            var result = FreezeSdkFinish();
            Assert.That(result, Is.Not.SameAs(late));
            Assert.That(result.parameters.Single(p => p.name == constant.InternalName).defaultFloat, Is.EqualTo(0.25f));
            var drivers = result.layers[0].stateMachine.defaultState.behaviours.OfType<VRC_AvatarParameterDriver>().ToArray();
            Assert.That(drivers.SelectMany(d => d.parameters).Any(p => p.name == "Input"), Is.False);
            var output = drivers.Single(d => d.parameters.Any(p => p.name == "Output"));
            Assert.That(output.localOnly, Is.True);
            Assert.That(output.parameters[0].source, Is.EqualTo(constant.InternalName));
            Assert.That(output.parameters[0].convertRange && output.parameters[0].destMax == 10, Is.True);
            Assert.That(descriptor.expressionParameters, Is.SameAs(parameters));
            Assert.That(descriptor.expressionsMenu, Is.SameAs(menu));
            Assert.That(EditorJsonUtility.ToJson(parameters), Is.EqualTo(parameterJson));
            Assert.That(EditorJsonUtility.ToJson(menu), Is.EqualTo(menuJson));
            Assert.That(EditorJsonUtility.ToJson(original), Is.EqualTo(originalJson));
            Assert.That(EditorJsonUtility.ToJson(late), Is.EqualTo(lateJson));
            Assert.That(copy.parameters[0].source, Is.EqualTo("Input"));
            Assert.That(state.behaviours.OfType<VRC_AvatarParameterDriver>().First().parameters[0].name, Is.EqualTo("Input"));

            UnityEngine.Object.DestroyImmediate(root.GetComponent<Animator>());
            var expected = SampleFrozen(late, (p, i) => { p.SetFloat("Input", 0.25f); p.SetFloat("Transport", i / 5f); });
            var actual = SampleFrozen(result, (p, i) => { p.SetFloat("Input", i % 2); p.SetFloat("Transport", i / 5f); });
            Assert.That(actual, Is.EqualTo(expected).Within(0.001f));
            Assert.That(actual.Last() - actual.First(), Is.GreaterThan(50), "Late transport input must remain dynamic.");
            Assert.That(context.GetState<ParameterPolicyState>().Constants.Keys, Is.EquivalentTo(new[] { "Input" }));
        }

        [Test]
        public void SdkFreezeKeepsTheResolvedDefaultAfterLatePublicDefaultsChange()
        {
            var source = TreeController(("Input", Clip()));
            var context = FreezeSdkBuild(source, p => p.FreezeParameters[0].UseDefaultValue = true);
            var descriptor = root.GetComponent<VRCAvatarDescriptor>();
            descriptor.expressionParameters.parameters[0].defaultValue = 0.9f;
            var late = TreeController(("Input", Clip()));
            var declarations = late.parameters;
            declarations[0].defaultFloat = 0.8f; late.parameters = declarations;
            FreezeSdkReplace(late);
            var result = FreezeSdkFinish();
            var constant = context.GetState<ParameterPolicyState>().Constants["Input"];
            Assert.That(result.parameters.Single(p => p.name == constant.InternalName).defaultFloat, Is.EqualTo(0.25f));
            Assert.That(result.parameters.Single(p => p.name == "Input").defaultFloat, Is.EqualTo(0.8f));
            Assert.That(descriptor.expressionParameters.parameters[0].defaultValue, Is.EqualTo(0.9f));
        }

        [Test]
        public void SdkFreezeCommitsRestoredConstantDeclarationsEvenWithoutNewPublicReaders()
        {
            var context = FreezeSdkBuild(TreeController(("Input", Clip())));
            var name = context.GetState<ParameterPolicyState>().Constants["Input"].InternalName;
            var before = FreezeSdkController();
            before.parameters = before.parameters.Where(p => p.name != name).ToArray();
            var result = FreezeSdkFinish();
            Assert.That(result, Is.Not.SameAs(before));
            Assert.That(result.parameters.Single(p => p.name == name).defaultFloat, Is.EqualTo(0.25f));
            Assert.That(before.parameters.Any(p => p.name == name), Is.False);
            Assert.That(context.GetState<OptimizationBuildReport>().Messages.Last(), Does.Contain("1 constants declared"));
        }

        [Test]
        public void SdkFreezeRemovesLatePublicParameterCurvesAndRetainsClipDuration()
        {
            FreezeSdkBuild(TreeController(("Input", Clip())));
            var clip = Clip();
            AnimationUtility.SetEditorCurve(clip, ParameterBinding("Input"), AnimationCurve.Linear(0, 0, 4, 1));
            var late = Controller(clip); late.AddParameter("Input", AnimatorControllerParameterType.Float);
            FreezeSdkReplace(late);
            var result = FreezeSdkFinish();
            var output = (AnimationClip)result.layers[0].stateMachine.defaultState.motion;
            Assert.That(AnimationUtility.GetEditorCurve(output, ParameterBinding("Input")), Is.Null);
            Assert.That(output.length, Is.EqualTo(4).Within(0.001));
            Assert.That(AnimationUtility.GetEditorCurve(clip, ParameterBinding("Input")), Is.Not.Null);
        }

        [TestCase("driver")]
        [TestCase("curve")]
        [TestCase("default")]
        [TestCase("type")]
        [TestCase("expression")]
        public void SdkFreezeRejectsLatePrivateConstantChangesWithoutPublishingControllers(string mutation)
        {
            var context = FreezeSdkBuild(TreeController(("Input", Clip())));
            var constant = context.GetState<ParameterPolicyState>().Constants["Input"];
            var clip = Clip();
            var late = Controller(clip); late.AddParameter("Input", AnimatorControllerParameterType.Float);
            if (mutation == "driver") Driver(late.layers[0].stateMachine.defaultState, constant.InternalName, 1);
            if (mutation == "curve") AnimationUtility.SetEditorCurve(clip, ParameterBinding(constant.InternalName), AnimationCurve.Constant(0, 1, 1));
            if (mutation == "default" || mutation == "type")
                late.AddParameter(new AnimatorControllerParameter { name = constant.InternalName,
                    type = mutation == "type" ? AnimatorControllerParameterType.Int : AnimatorControllerParameterType.Float,
                    defaultFloat = 0.7f });
            if (mutation == "expression")
            {
                var parameters = root.GetComponent<VRCAvatarDescriptor>().expressionParameters;
                parameters.parameters = parameters.parameters.Concat(new[] { new VRCExpressionParameters.Parameter
                { name = constant.InternalName, valueType = VRCExpressionParameters.ValueType.Float } }).ToArray();
            }
            FreezeSdkReplace(late);
            var before = EditorJsonUtility.ToJson(late);
            Assert.Throws<InvalidOperationException>(() => FreezeSdkFinish());
            Assert.That(FreezeSdkController(), Is.SameAs(late));
            Assert.That(EditorJsonUtility.ToJson(late), Is.EqualTo(before));
            Assert.That(context.GetState<OptimizationBuildReport>().Messages.Last(), Does.StartWith("Final parameter specialization failed:"));
            Assert.DoesNotThrow(() => FreezeSdkFinish(), "Failed sessions must not be replayed.");
        }

        [Test]
        public void SdkFreezeRejectsANewDependencyOfAPreservedInput()
        {
            var source = TreeController(("Input", Clip()), ("Kept", Clip()));
            FreezeSdkBuild(source, p => p.PreserveParameters.Names.Add("Kept"));
            var late = TreeController(("Input", Clip()), ("Kept", Clip()));
            Driver(late.layers[0].stateMachine.defaultState, "Kept", 0, VRC_AvatarParameterDriver.ChangeType.Copy, "Input");
            FreezeSdkReplace(late);
            var error = Assert.Throws<InvalidOperationException>(() => FreezeSdkFinish());
            Assert.That(error.Message, Does.Contain("preserved dependency"));
            Assert.That(FreezeSdkController(), Is.SameAs(late));
        }

        [Test]
        public void SdkFreezeChecksLateConsumersAgainstTheOriginalScope()
        {
            var scope = PolicyChild("Scope");
            var source = TreeController(("Input", Clip()));
            FreezeSdkBuild(source, p => p.ScopeRoot = scope.transform);
            var late = TreeController(("Input", StaticCurve(Binding("a"), 100)));
            FreezeSdkReplace(late);
            Assert.Throws<InvalidOperationException>(() => FreezeSdkFinish());
            Assert.That(FreezeSdkController(), Is.SameAs(late));
        }

        [Test]
        public void SdkFreezeRejectsUnknownLateRuntimeConsumers()
        {
            FreezeSdkBuild(TreeController(("Input", Clip())));
            var late = TreeController(("Input", Clip()));
            FreezeSdkReplace(late);
            root.AddComponent<ParameterPolicySourceFixture>();
            Assert.Throws<InvalidOperationException>(() => FreezeSdkFinish());
            Assert.That(FreezeSdkController(), Is.SameAs(late));
        }

        [TestCase(false)] [TestCase(true)]
        public void SdkFreezeRejectsUnknownLateStateMachineBehaviours(bool machineBehaviour)
        {
            FreezeSdkBuild(TreeController(("Input", Clip())));
            var late = TreeController(("Input", Clip()));
            var behaviour = Track(ScriptableObject.CreateInstance<UnsupportedReachabilityBehaviour>());
            var machine = late.layers[0].stateMachine;
            if (machineBehaviour) machine.behaviours = new StateMachineBehaviour[] { behaviour };
            else machine.defaultState.behaviours = new StateMachineBehaviour[] { behaviour };
            Assert.That(machineBehaviour ? machine.behaviours.Single() : machine.defaultState.behaviours.Single(), Is.SameAs(behaviour));
            FreezeSdkReplace(late);
            Assert.Throws<InvalidOperationException>(() => FreezeSdkFinish());
            Assert.That(FreezeSdkController(), Is.SameAs(late));
        }

        [TestCase(false)] [TestCase(true)]
        public void SdkFreezeDiscoversLateChildControllersAndEnforcesTheirScope(bool limitedScope)
        {
            var scope = PolicyChild("Scope");
            var context = FreezeSdkBuild(TreeController(("Input", Clip())),
                p => p.ScopeRoot = limitedScope ? scope.transform : root.transform);
            var child = PolicyChild("Late child");
            child.AddComponent<SkinnedMeshRenderer>();
            var animator = child.AddComponent<Animator>();
            var source = TreeController(("Input", StaticCurve(Binding("a"), 100)));
            animator.runtimeAnimatorController = source;
            if (limitedScope)
            {
                Assert.Throws<InvalidOperationException>(() => FreezeSdkFinish());
                Assert.That(animator.runtimeAnimatorController, Is.SameAs(source));
            }
            else
            {
                FreezeSdkFinish();
                var result = (AnimatorController)animator.runtimeAnimatorController;
                TrackControllerAssets(result);
                var name = context.GetState<ParameterPolicyState>().Constants["Input"].InternalName;
                Assert.That(result.parameters.Single(p => p.name == name).defaultFloat, Is.EqualTo(0.25f));
                Assert.That(((BlendTree)result.layers[0].stateMachine.defaultState.motion).children[0].directBlendParameter, Is.EqualTo(name));
                Assert.That(source.parameters.Any(p => p.name == name), Is.False);
            }
        }

        [Test]
        public void SdkFreezeUsesCurrentPathsForOriginallyScopedObjectsAfterReparenting()
        {
            var scope = PolicyChild("Scope");
            var inside = PolicyChild("Inside", scope.transform);
            inside.AddComponent<SkinnedMeshRenderer>();
            var context = FreezeSdkBuild(TreeController(("Input", Clip())), p => p.ScopeRoot = scope.transform);
            inside.transform.SetParent(root.transform, false);
            inside.name = "Moved";
            var late = TreeController(("Input", StaticCurve(Binding("a", "Moved"), 100)));
            FreezeSdkReplace(late);
            var result = FreezeSdkFinish();
            var name = context.GetState<ParameterPolicyState>().Constants["Input"].InternalName;
            Assert.That(((BlendTree)result.layers[0].stateMachine.defaultState.motion).children[0].directBlendParameter, Is.EqualTo(name));
        }

        [Test]
        public void SdkFreezeDoesNotApplyOneAvatarsSessionToAnotherAvatar()
        {
            FreezeSdkBuild(TreeController(("Input", Clip())));
            var other = PolicyChild("Other avatar");
            var descriptor = other.AddComponent<VRCAvatarDescriptor>();
            var source = TreeController(("Input", Clip()));
            descriptor.baseAnimationLayers = new[] { new VRCAvatarDescriptor.CustomAnimLayer
            { type = VRCAvatarDescriptor.AnimLayerType.FX, animatorController = source, isDefault = false } };
            ParameterFreezeSdkFinalizer.Begin(other);
            ParameterFreezeSdkFinalizer.Complete(other, allowTransientAssetsForTests: true);
            Assert.That(descriptor.baseAnimationLayers[0].animatorController, Is.SameAs(source));
            UnityEngine.Object.DestroyImmediate(other);
            FreezeSdkReplace(TreeController(("Input", Clip())));
            Assert.That(FreezeSdkFinish().parameters.Any(p => p.name.StartsWith("__AAO_Constant_", StringComparison.Ordinal)), Is.True);
        }

        [TestCase(false)] [TestCase(true)]
        public void SdkFreezeRequiresCaptureForTheCurrentBuild(bool previouslyCaptured)
        {
            FreezeSdkBuild(TreeController(("Input", Clip())), begin: previouslyCaptured);
            if (previouslyCaptured) ParameterFreezeSdkFinalizer.Begin(root);
            var late = TreeController(("Input", Clip()));
            FreezeSdkReplace(late);
            Assert.That(FreezeSdkFinish(), Is.SameAs(late));
        }

        [Test]
        public void SdkFreezeDoesNotReplayCompletedSessions()
        {
            FreezeSdkBuild(TreeController(("Input", Clip())));
            FreezeSdkReplace(TreeController(("Input", Clip())));
            FreezeSdkFinish();
            var after = TreeController(("Input", Clip()));
            FreezeSdkReplace(after);
            Assert.That(FreezeSdkFinish(), Is.SameAs(after));
        }

        [Test]
        public void SdkFreezeRequiresPersistentBuildAssetsOutsideTests()
        {
            FreezeSdkBuild(TreeController(("Input", Clip())));
            var before = FreezeSdkController();
            var error = Assert.Throws<InvalidOperationException>(() => ParameterFreezeSdkFinalizer.Complete(root));
            Assert.That(error.Message, Does.Contain("asset container"));
            Assert.That(FreezeSdkController(), Is.SameAs(before));
        }
    }
}
