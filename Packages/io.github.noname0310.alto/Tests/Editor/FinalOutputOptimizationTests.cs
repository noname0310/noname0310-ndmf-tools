using System;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.AnimatorOptimization;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.Expressions;
using Noname.AvatarTools.ObjectOptimization;
using Noname.AvatarTools.ParameterPolicy;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Animations;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDK3.Dynamics.Constraint.Components;
using VRC.SDKBase;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        [TestCase(false)] [TestCase(true)]
        public void FinalOutputsRevisitExpressionsAfterConstraintSourcesDisappear(bool preserveInput)
        {
            Mesh("Face"); ObjectSettings(); root.AddComponent<ALTOTraceAndOptimizeExpressions>();
            var input = PolicyChild("Live source").transform;
            var unused = PolicyChild("Unused source").transform;
            var target = PolicyChild("Observed bone").transform; renderer.bones = new[] { target };
            var constraint = ConstraintWithZeroSources(typeof(VRCParentConstraint), input, unused, target, "Constraint");
            var output = StaticCurve(EditorCurveBinding.FloatCurve(unused.name, typeof(Transform), "m_LocalPosition.y"), 2);
            var source = TreeController(("Unused", output), ("FaceInput", StaticCurve(Binding("Face"), 70)));
            var expected = SampleFrozen(source, (p, frame) =>
                { p.SetFloat("Unused", frame % 2); p.SetFloat("FaceInput", frame / 15f); }, 16);
            unused.localPosition = Vector3.zero; renderer.SetBlendShapeWeight(0, 0);
            var menu = ExpressionMenu(ExpressionControl("Unused", "Unused"), ExpressionControl("Face", "FaceInput"));
            var context = ExpressionBuild(menu, source); ObjectOptimizationPass.Capture(context);
            context.GetState<AnimatorOptimizationState>().Settings = new AnimatorOptimizationSettings
                { StateLogic = true, UnusedParameters = true };
            FreezeExpressions(new VRCExpressionParameters.Parameter { name = "Unused", valueType = VRCExpressionParameters.ValueType.Float, networkSynced = false },
                new VRCExpressionParameters.Parameter { name = "FaceInput", valueType = VRCExpressionParameters.ValueType.Float, networkSynced = false });
            if (preserveInput)
            {
                var policy = context.GetState<ParameterPolicyState>(); policy.Enabled = true; policy.Names.Add("Unused");
            }
            AvatarOutputSdkFinalizer.Begin(root); AvatarOutputSdkFinalizer.Capture(context);
            ExpressionOptimizationPass.Apply(context);
            Assert.That(constraint.Sources.Count, Is.EqualTo(3), "The later output pass has not pruned sources yet");
            AvatarOutputSdkFinalizer.Complete(root, o => Track(o));
            var descriptor = root.GetComponent<VRCAvatarDescriptor>();
            Assert.That(constraint != null, Is.True); Assert.That(constraint.Sources.Count, Is.EqualTo(1));
            Assert.That(unused == null, Is.True, "Preserving an input does not keep its unobserved former output");
            Assert.That(descriptor.expressionsMenu.controls.Select(c => c.name),
                Is.EqualTo(preserveInput ? new[] { "Unused", "Face" } : new[] { "Face" }));
            Assert.That(descriptor.expressionParameters.parameters.Select(p => p.name),
                Is.EqualTo(preserveInput ? new[] { "Unused", "FaceInput" } : new[] { "FaceInput" }));
            var report = context.GetState<OptimizationBuildReport>().Expressions;
            Assert.That(report.InputControls, Is.EqualTo(2)); Assert.That(report.OutputControls, Is.EqualTo(preserveInput ? 2 : 1));
            Assert.That(report.RemovedLocalParameters, Is.EqualTo(preserveInput ? Array.Empty<string>() : new[] { "Unused" }));
            var built = (AnimatorController)descriptor.baseAnimationLayers.Single().animatorController; TrackControllerAssets(built);
            UnityEngine.Object.DestroyImmediate(root.GetComponent<Animator>());
            renderer.SetBlendShapeWeight(0, 0);
            var actual = SampleFrozen(built, (p, frame) =>
                { if (preserveInput) p.SetFloat("Unused", frame % 2); p.SetFloat("FaceInput", frame / 15f); }, 16);
            Assert.That(actual, Is.EqualTo(expected).Within(1e-5), "Unrelated face output remains identical");
            Assert.That(menu.controls.Count, Is.EqualTo(2));
            Assert.That(AnimationUtility.GetCurveBindings(output).Length, Is.EqualTo(1));
        }

        [TestCase("Absent")] [TestCase("Disabled")] [TestCase("LocalDisabled")]
        [TestCase("SensorsDisabled")] [TestCase("TransformsOnly")] [TestCase("AllDisabled")]
        public void FinalOutputsDoNotDependOnExpressionOrSensorOptions(string option)
        {
            Mesh("Face");
            var marker = ObjectSettings().Component;
            marker.PruneUnusedSensorParameters = option != "SensorsDisabled";
            marker.RemoveUnusedObjects = option != "TransformsOnly";
            if (option == "AllDisabled")
            {
                marker.RemoveUnusedObjects = marker.RemoveUnusedTransformOutputs = false;
                marker.PruneUnusedSensorParameters = marker.RemoveRedundantConstraints = false;
            }
            if (option != "Absent" && option != "AllDisabled")
            {
                var expressions = root.AddComponent<ALTOTraceAndOptimizeExpressions>();
                expressions.PruneUnusedLocalParameters = option != "LocalDisabled" && option != "Disabled";
                if (option == "Disabled") expressions.RemoveDeadControls = expressions.RemoveFrozenControls = expressions.RemoveEmptySubMenus = false;
            }
            var target = PolicyChild("Generated output");
            var constraint = target.AddComponent<PositionConstraint>();
            var metadata = target.AddComponent<DependencyAuthoringFixture>();
            var clip = InitialTransformClip(target.transform);
            var source = Controller(clip);
            var context = ExpressionBuild(null, source); ObjectOptimizationPass.Capture(context);
            AvatarOutputSdkFinalizer.Begin(root); AvatarOutputSdkFinalizer.Capture(context);
            ExpressionOptimizationPass.Apply(context);
            ObjectOptimizationPass.ApplyFinal(context);
            Assert.That(target != null && constraint != null && metadata != null, Is.True, "Authoring references survive before SDK completion");
            ObjectOptimizationPass.RemoveMarkers(context);
            AvatarOutputSdkFinalizer.Complete(root, o => Track(o));
            bool disabled = option == "AllDisabled";
            Assert.That(constraint == null, Is.EqualTo(!disabled));
            Assert.That(target == null, Is.EqualTo(!disabled && option != "TransformsOnly"));
            var descriptor = root.GetComponent<VRCAvatarDescriptor>();
            var built = (AnimatorController)descriptor.baseAnimationLayers.Single().animatorController;
            TrackControllerAssets(built);
            Assert.That(built.animationClips.SelectMany(AnimationUtility.GetCurveBindings).Any(b => b.path == "Generated output"), Is.EqualTo(disabled));
            Assert.That(AnimationUtility.GetCurveBindings(clip).Length, Is.GreaterThan(0), "Source clips remain unchanged");
            AvatarOutputSdkFinalizer.Complete(root, _ => Assert.Fail("A completed session cannot replay"));
            Assert.That(descriptor.baseAnimationLayers.Single().animatorController, Is.SameAs(built));
            Assert.That(context.GetState<ObjectOptimizationState>().RuntimeDependenciesOnly, Is.False);
        }

        [TestCase("None")] [TestCase("Component")] [TestCase("Subtree")] [TestCase("OutsideScope")]
        [TestCase("SkinBone")] [TestCase("LateOutput")] [TestCase("PreservedInput")]
        [TestCase("ObjectKey")] [TestCase("UnknownRuntime")] [TestCase("Event")] [TestCase("Behaviour")]
        public void FinalOutputsUseRuntimeReferencesAndExplicitPreservation(string observer)
        {
            Mesh("Face");
            var target = PolicyChild("Target");
            var constraint = target.AddComponent<PositionConstraint>();
            var metadataOwner = PolicyChild("Authoring");
            var metadata = metadataOwner.AddComponent<DependencyAuthoringFixture>(); metadata.Target = constraint;
            var marker = ObjectSettings(observer == "OutsideScope" ? target : root).Component;
            if (observer == "Component") marker.PreserveComponents.Add(metadata);
            if (observer == "Subtree") marker.PreserveObjects.Add(metadataOwner);
            if (observer == "SkinBone") renderer.bones = new[] { target.transform };
            var clip = InitialTransformClip(target.transform);
            if (observer == "ObjectKey") AnimationUtility.SetObjectReferenceCurve(clip,
                EditorCurveBinding.PPtrCurve("", typeof(SkinnedMeshRenderer), "m_RootBone"),
                new[] { new ObjectReferenceKeyframe { time = 0, value = metadata } });
            var source = observer == "PreservedInput" ? TreeController(("Preserved", clip)) : Controller(clip);
            var context = ExpressionBuild(null, source); ObjectOptimizationPass.Capture(context);
            if (observer == "PreservedInput")
            {
                var policy = context.GetState<ParameterPolicyState>(); policy.Enabled = true; policy.Names.Add("Preserved");
            }
            AvatarOutputSdkFinalizer.Begin(root); AvatarOutputSdkFinalizer.Capture(context);
            ObjectOptimizationPass.ApplyFinal(context);
            Assert.That(target != null && constraint != null, Is.True);
            if (observer == "LateOutput") target.AddComponent<MeshRenderer>();
            if (observer == "UnknownRuntime") root.AddComponent<ParameterPolicySourceFixture>();
            var finalSource = (AnimatorController)root.GetComponent<VRCAvatarDescriptor>().baseAnimationLayers.Single().animatorController;
            if (observer == "Event") AnimationUtility.SetAnimationEvents(finalSource.animationClips.Single(),
                new[] { new AnimationEvent { functionName = "ReadHierarchy" } });
            if (observer == "Behaviour")
            {
                var behaviour = Track(ScriptableObject.CreateInstance<UnsupportedReachabilityBehaviour>());
                finalSource.layers[0].stateMachine.defaultState.behaviours = new StateMachineBehaviour[] { behaviour };
                Assert.That(finalSource.layers[0].stateMachine.defaultState.behaviours.Single(), Is.SameAs(behaviour));
            }
            AvatarOutputSdkFinalizer.Complete(root, o => Track(o));
            Assert.That(target == null && constraint == null, Is.EqualTo(observer == "None" || observer == "PreservedInput"), observer);
            if (observer == "PreservedInput") Assert.That(((AnimatorController)root.GetComponent<VRCAvatarDescriptor>()
                .baseAnimationLayers.Single().animatorController).parameters.Select(p => p.name), Does.Contain("Preserved"));
            if (observer == "Event" || observer == "Behaviour" || observer == "UnknownRuntime")
                Assert.That(root.GetComponent<VRCAvatarDescriptor>().baseAnimationLayers.Single().animatorController, Is.SameAs(finalSource));
            foreach (var layer in root.GetComponent<VRCAvatarDescriptor>().baseAnimationLayers)
                TrackControllerAssets((AnimatorController)layer.animatorController);
        }

        [TestCase(false)] [TestCase(true)]
        public void FinalOutputsRespectRequiredComponentRemovalOrder(bool liveBone)
        {
            Mesh("Face");
            var marker = ObjectSettings().Component;
            var target = PolicyChild("Generated owner");
            var metadata = target.AddComponent<RequiredSdkAuthoringFixture>();
            var constraint = target.GetComponent<PositionConstraint>();
            if (liveBone) renderer.bones = new[] { target.transform };
            var context = ExpressionBuild(null, Controller(Clip())); ObjectOptimizationPass.Capture(context);
            AvatarOutputSdkFinalizer.Begin(root); AvatarOutputSdkFinalizer.Capture(context);
            ObjectOptimizationPass.ApplyFinal(context);
            Assert.That(metadata != null && constraint != null, Is.True);
            AvatarOutputSdkFinalizer.Complete(root, o => Track(o));
            Assert.That(metadata == null, Is.True);
            Assert.That(target != null && constraint != null, Is.EqualTo(liveBone));
            foreach (var layer in root.GetComponent<VRCAvatarDescriptor>().baseAnimationLayers)
                TrackControllerAssets((AnimatorController)layer.animatorController);
        }

        [TestCase(false, false)] [TestCase(false, true)] [TestCase(true, false)] [TestCase(true, true)]
        public void FinalOutputsCleanTheCurrentControllerAndItsExpressionConsumers(bool writeDefaults, bool duration)
        {
            Mesh("Face");
            var marker = ObjectSettings().Component;
            root.AddComponent<ALTOTraceAndOptimizeExpressions>();
            var target = PolicyChild("Authoring output"); target.AddComponent<DependencyAuthoringFixture>();
            var source = TreeController(("GeneratedInput", InitialTransformClip(target.transform)));
            var menu = ExpressionMenu(ExpressionControl("Generated", "GeneratedInput"), ExpressionControl("Late face", "LateFace"));
            var context = ExpressionBuild(menu, source); ObjectOptimizationPass.Capture(context);
            AvatarOutputSdkFinalizer.Begin(root); AvatarOutputSdkFinalizer.Capture(context);
            ExpressionOptimizationPass.Apply(context);
            var descriptor = root.GetComponent<VRCAvatarDescriptor>();
            var output = duration ? InitialTransformClip(target.transform) :
                StaticCurve(EditorCurveBinding.FloatCurve(target.name, typeof(Transform), "m_LocalPosition.x"), 0);
            var late = TreeController(("GeneratedInput", output), ("LateFace", StaticCurve(Binding("Face"), 70)));
            late.layers[0].stateMachine.defaultState.writeDefaultValues = writeDefaults;
            var layers = descriptor.baseAnimationLayers; layers[0].animatorController = late; descriptor.baseAnimationLayers = layers;
            AvatarOutputSdkFinalizer.Complete(root, o => Track(o));
            Assert.That(target == null, Is.True);
            bool retained = duration;
            Assert.That(descriptor.expressionsMenu.controls.Select(c => c.name), Is.EqualTo(retained ?
                new[] { "Generated", "Late face" } : new[] { "Late face" }), "Duration remains observable; ordinary implicit defaults do not depend on dead branch weights");
            var report = context.GetState<OptimizationBuildReport>().Expressions;
            Assert.That(report.InputControls, Is.EqualTo(2)); Assert.That(report.OutputControls, Is.EqualTo(retained ? 2 : 1));
            Assert.That(report.Controls.Count(c => c.Removed && c.Name == "Generated"), Is.EqualTo(retained ? 0 : 1));
            Assert.That(menu.controls.Count, Is.EqualTo(2));
            TrackControllerAssets((AnimatorController)descriptor.baseAnimationLayers.Single().animatorController);
        }

        [Test]
        public void SensorConsumerHistoryDoesNotCrossChildAnimatorNamespaces(
            [Values(false, true)] bool avatarReader, [Values(false, true)] bool earlyCapture)
        {
            Mesh("Face"); ObjectSettings();
            var receiver = PolicyChild("External sensor").AddComponent<VRC.SDK3.Dynamics.Contact.Components.VRCContactReceiver>();
            receiver.parameter = "Input";
            var source = Controller(Clip(), Clip());
            source.AddParameter("Input", AnimatorControllerParameterType.Float);
            Edge(source.layers[0].stateMachine.states[0].state, source.layers[0].stateMachine.states[1].state,
                "Input", 0.5f, AnimatorConditionMode.Greater);
            var child = PolicyChild("Private animator").AddComponent<Animator>();
            child.runtimeAnimatorController = source;
            var context = ExpressionBuild(null, avatarReader ? source : Controller(Clip()));
            ObjectOptimizationPass.Capture(context);
            context.GetState<AnimatorOptimizationState>().Settings = new AnimatorOptimizationSettings
                { StateLogic = true, UnusedParameters = true };
            if (earlyCapture)
                OptimizationSession.RunBoundary(context, "sensor ownership fixture", () => OptimizationFixedPoint.Apply(context));
            AvatarOutputSdkFinalizer.Begin(root); AvatarOutputSdkFinalizer.Capture(context);
            AvatarOutputSdkFinalizer.Complete(root, o => Track(o));
            Assert.That(receiver != null, Is.EqualTo(!avatarReader),
                "An external-only sensor survives removal of an unrelated private input with the same name");
            Assert.That(context.GetState<ObjectOptimizationState>().InitialReaders.Contains("Input"), Is.EqualTo(avatarReader),
                "Only a consumer on the avatar's parameter namespace proves a former sensor reader");
            foreach (var layer in root.GetComponent<VRCAvatarDescriptor>().baseAnimationLayers)
                TrackControllerAssets((AnimatorController)layer.animatorController);
            if (child != null && child.runtimeAnimatorController is AnimatorController built) TrackControllerAssets(built);
            Assert.That(source.parameters.Select(p => p.name), Does.Contain("Input"));
            Assert.That(source.layers[0].stateMachine.states[0].state.transitions, Has.Length.EqualTo(1));
        }

        [Test]
        public void FrozenSensorCollectionKeepsPreservedParameterValuesAndVisibleOutputs(
            [Values(false, true)] bool active, [Values(false, true)] bool freezeSensor,
            [Values(false, true)] bool preserveObject)
        {
            Mesh("Face"); var marker = ObjectSettings().Component;
            var input = PolicyChild("Sensor source"); var owner = PolicyChild("Sensor target");
            var constraint = owner.AddComponent<PositionConstraint>();
            constraint.AddSource(new ConstraintSource { sourceTransform = input.transform, weight = 1 });
            var receiver = owner.AddComponent<VRC.SDK3.Dynamics.Contact.Components.VRCContactReceiver>();
            receiver.parameter = "Sensor"; owner.SetActive(active);
            if (preserveObject) marker.PreserveObjects.Add(owner);
            var clip = InitialTransformClip(owner.transform, "m_LocalScale");
            AnimationUtility.SetEditorCurve(clip, Binding("Face"), AnimationCurve.Linear(0, 0, 0.3f, 100));
            var source = TreeController(("Preserved", clip)); source.AddParameter("Sensor", AnimatorControllerParameterType.Float);
            var originalClip = EditorJsonUtility.ToJson(clip);
            var expected = SampleFrozen(source, (p, frame) => p.SetFloat("Preserved", (frame % 7) / 6f), 32);
            renderer.SetBlendShapeWeight(0, 0);
            var menu = ExpressionMenu(ExpressionControl("Keep input", "Preserved"));
            root.AddComponent<ALTOTraceAndOptimizeExpressions>();
            var context = ExpressionBuild(menu, source); ObjectOptimizationPass.Capture(context);
            FreezeExpressions(new VRCExpressionParameters.Parameter
                { name = "Preserved", valueType = VRCExpressionParameters.ValueType.Float,
                    networkSynced = true, saved = true, defaultValue = 0.375f });
            var policy = context.GetState<ParameterPolicyState>(); policy.Enabled = true; policy.Names.Add("Preserved");
            if (freezeSensor)
            {
                var virtualSource = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
                policy.Constants.Add("Sensor", Constants(virtualSource, "Sensor", 0)["Sensor"]);
            }
            AvatarOutputSdkFinalizer.Begin(root); AvatarOutputSdkFinalizer.Capture(context);
            AvatarOutputSdkFinalizer.Complete(root, o => Track(o));
            bool collected = freezeSensor && !preserveObject;
            Assert.That(receiver == null, Is.EqualTo(collected));
            Assert.That(owner == null && input == null && constraint == null, Is.EqualTo(collected));
            var descriptor = root.GetComponent<VRCAvatarDescriptor>();
            var declaration = descriptor.expressionParameters.parameters.Single(p => p.name == "Preserved");
            Assert.That(declaration.networkSynced && declaration.saved, Is.True);
            Assert.That(declaration.defaultValue, Is.EqualTo(0.375f));
            Assert.That(descriptor.expressionsMenu.controls.Single().parameter.name, Is.EqualTo("Preserved"));
            var built = (AnimatorController)descriptor.baseAnimationLayers.Single().animatorController; TrackControllerAssets(built);
            Assert.That(built.parameters.Select(p => p.name), Does.Contain("Preserved"));
            Assert.That(built.animationClips.SelectMany(AnimationUtility.GetCurveBindings)
                .Any(b => b.type == typeof(Transform)), Is.EqualTo(!collected));
            UnityEngine.Object.DestroyImmediate(root.GetComponent<Animator>());
            renderer.SetBlendShapeWeight(0, 0);
            var actual = SampleFrozen(built, (p, frame) => p.SetFloat("Preserved", (frame % 7) / 6f), 32);
            Assert.That(actual, Is.EqualTo(expected).Within(0.00001f));
            Assert.That(actual.Max() - actual.Min(), Is.GreaterThan(10));
            Assert.That(EditorJsonUtility.ToJson(clip), Is.EqualTo(originalClip));
        }

        [TestCase(false)] [TestCase(true)]
        public void FinalOutputsHonorFrozenSensorInputsWithoutExpressionCleanup(bool frozen)
        {
            Mesh("Face"); ObjectSettings();
            var owner = PolicyChild("Sensor"); var ray = owner.AddComponent<VRCRaycast>(); ray.Parameter = "Probe";
            var names = AvatarParameterInputs.RaycastSuffixes.Select(s => "Probe" + s).ToArray();
            var source = Controller(Clip());
            foreach (var name in names) source.AddParameter(name, AnimatorControllerParameterType.Float);
            var context = ExpressionBuild(null, source); ObjectOptimizationPass.Capture(context);
            if (frozen)
            {
                var input = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
                foreach (var name in names) context.GetState<ParameterPolicyState>().Constants.Add(name, Constants(input, name, 0)[name]);
            }
            AvatarOutputSdkFinalizer.Begin(root); AvatarOutputSdkFinalizer.Capture(context);
            AvatarOutputSdkFinalizer.Complete(root, o => Track(o));
            Assert.That(ray == null && owner == null, Is.EqualTo(frozen));
            TrackControllerAssets((AnimatorController)root.GetComponent<VRCAvatarDescriptor>().baseAnimationLayers.Single().animatorController);
        }
    }
}
