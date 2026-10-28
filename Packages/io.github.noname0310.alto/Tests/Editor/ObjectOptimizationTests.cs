using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.ObjectOptimization;
using Noname.AvatarTools.ParameterPolicy;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Animations;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDK3.Dynamics.Contact.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;
using VRC.SDKBase;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private ObjectOptimizationState ObjectSettings(GameObject scope = null)
        {
            if (root.GetComponent<VRCAvatarDescriptor>() == null) root.AddComponent<VRCAvatarDescriptor>();
            var component = (scope ?? root).AddComponent<ALTOTraceAndOptimizeObjects>();
            return new ObjectOptimizationState { Component = component, Scope = OptimizationBuildScope.Capture(component, root) };
        }

        private VirtualAnimatorController SensorController(string name)
        {
            var controller = Controller(Clip(), Clip()); controller.AddParameter(name, AnimatorControllerParameterType.Float);
            Edge(controller.layers[0].stateMachine.states[0].state, controller.layers[0].stateMachine.states[1].state, name, 0.5f, AnimatorConditionMode.Greater);
            return new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(controller);
        }

        [Test]
        public void SensorPruningRequiresAFormerConsumerAndPreservesExternalOnlyOutputs()
        {
            var settings = ObjectSettings();
            var sensor = PolicyChild("Sensor").AddComponent<VRCContactReceiver>(); sensor.parameter = "Input";
            PolicyChild("External output").AddComponent<VRCContactReceiver>().parameter = "ExternalOnly";
            var controller = SensorController("Input");
            controller.Parameters = controller.Parameters.Add("ExternalOnly", new AnimatorControllerParameter { name = "ExternalOnly", type = AnimatorControllerParameterType.Float });
            settings.InitialReaders.UnionWith(ParameterSpecialization.Reads(controller.AllReachableNodes()));
            ParameterSpecialization.Apply(new[] { controller }, Constants(controller, "Input", 0));
            SensorParameterPruning.Apply(root, new[] { controller }, settings, new HashSet<string>(), new ExternalParameterAccess(), new List<string>());
            Assert.That(controller.Parameters.ContainsKey("Input"), Is.False);
            Assert.That(controller.Parameters.ContainsKey("ExternalOnly"), Is.True);
            Assert.That(sensor != null, Is.True, "Physical removal belongs to the dependency backend, not a blind component delete");
        }

        [TestCase("Expression")] [TestCase("Menu")] [TestCase("Preserve")] [TestCase("External")]
        [TestCase("OutsideScope")] [TestCase("ProtectedComponent")] [TestCase("DriverWriter")] [TestCase("Reader")]
        public void SensorPruningRetainsObservableOrOutOfScopeInputs(string reason)
        {
            var subtree = PolicyChild("Scope");
            var settings = ObjectSettings(subtree);
            var sensor = subtree.AddComponent<VRCContactReceiver>(); sensor.parameter = "Input";
            var controller = SensorController("Input"); settings.InitialReaders.Add("Input");
            if (reason != "Reader") ParameterSpecialization.Apply(new[] { controller }, Constants(controller, "Input", 0));
            var preserved = new HashSet<string>(); var external = new ExternalParameterAccess();
            if (reason == "Expression") FreezeExpressions(new VRCExpressionParameters.Parameter { name = "Input" });
            if (reason == "Menu")
            {
                var menu = Track(ScriptableObject.CreateInstance<VRCExpressionsMenu>());
                menu.controls.Add(new VRCExpressionsMenu.Control { parameter = new VRCExpressionsMenu.Control.Parameter { name = "Input" } });
                root.GetComponent<VRCAvatarDescriptor>().expressionsMenu = menu;
            }
            if (reason == "Preserve") preserved.Add("Input");
            if (reason == "External") external.Readers.Add(n => n == "Input");
            if (reason == "OutsideScope") PolicyChild("Other sensor").AddComponent<VRCContactReceiver>().parameter = "Input";
            if (reason == "ProtectedComponent") settings.ProtectedComponents.Add(ObjectRegistry.GetReference(sensor));
            if (reason == "DriverWriter")
            {
                var driver = Track(ScriptableObject.CreateInstance<VRCAvatarParameterDriver>());
                driver.parameters.Add(new VRC_AvatarParameterDriver.Parameter { name = "Input", value = 1 });
                var state = controller.Layers.First().StateMachine.DefaultState; state.Behaviours = state.Behaviours.Add(driver);
            }
            SensorParameterPruning.Apply(root, new[] { controller }, settings, preserved, external, new List<string>());
            Assert.That(controller.Parameters.ContainsKey("Input"), Is.True);
        }

        private AnimationClip InitialTransformClip(Transform target, string property = "m_LocalPosition", bool complete = true)
        {
            var clip = Track(new AnimationClip());
            var path = AnimationUtility.CalculateTransformPath(target, root.transform);
            for (int i = 0; i < (complete ? 3 : 1); i++)
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, typeof(Transform), property + "." + "xyz"[i]),
                    AnimationCurve.Constant(0, 0.3f, property == "m_LocalScale" ? target.localScale[i] : target.localPosition[i]));
            return clip;
        }

        private List<string> OptimizeObjectCurves(VirtualAnimatorController controller, ObjectOptimizationState settings)
        {
            var messages = new List<string>();
            InvariantObjectCurves.Apply(root, new Dictionary<object, VirtualAnimatorController> { [VRCAvatarDescriptor.AnimLayerType.FX] = controller },
                null, settings, AnimatorReachability.Analyze(root, new[] { controller }), messages);
            return messages;
        }

        [TestCase(false)] [TestCase(true)]
        public void InitialTransformAndVisibilityCurvesPreserveNativeOutputAndDuration(bool writeDefaults)
        {
            Mesh("a");
            var settings = ObjectSettings(); var target = PolicyChild("Static output");
            target.transform.localPosition = new Vector3(0.2f, -0.3f, 0.4f);
            var visible = target.AddComponent<MeshRenderer>(); visible.enabled = false;
            var clip = InitialTransformClip(target.transform);
            var active = EditorCurveBinding.FloatCurve(target.name, typeof(GameObject), "m_IsActive");
            var enabled = EditorCurveBinding.FloatCurve(target.name, typeof(MeshRenderer), "m_Enabled");
            AnimationUtility.SetEditorCurve(clip, active, AnimationCurve.Constant(0, 0.3f, 1));
            AnimationUtility.SetEditorCurve(clip, enabled, AnimationCurve.Constant(0, 0.3f, 0));
            var source = Controller(clip, Clip());
            foreach (var state in source.layers[0].stateMachine.states) state.state.writeDefaultValues = writeDefaults;
            var edge = Edge(source.layers[0].stateMachine.states[0].state, source.layers[0].stateMachine.states[1].state);
            edge.hasExitTime = true; edge.exitTime = 1; edge.duration = 0;
            var clone = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            OptimizeObjectCurves(clone, settings);
            Assert.That(clone.AllReachableNodes().OfType<VirtualClip>().SelectMany(c => c.GetFloatCurveBindings()).Any(b => b.path == target.name), Is.False);
            Assert.That(clone.AllReachableNodes().OfType<VirtualClip>().Max(MotionAnalysis.EndTime), Is.EqualTo(0.3f));
            var built = CommitAnimator(clone, root);
            Func<float> sample = () => target.transform.localPosition.x + target.transform.localPosition.y * 3 + target.transform.localPosition.z * 7 +
                (target.activeSelf ? 100 : 0) + (visible.enabled ? 1000 : 0);
            var before = SampleFrozen(source, (p, i) => { }, 30, sample);
            target.transform.localPosition = new Vector3(0.2f, -0.3f, 0.4f); target.SetActive(true); visible.enabled = false;
            var after = SampleFrozen(built, (p, i) => { }, 30, sample);
            Assert.That(after, Is.EqualTo(before).Within(1e-5));
        }

        [TestCase("PhysBone")] [TestCase("Constraint")] [TestCase("Raycast")]
        [TestCase("Preserved")] [TestCase("PartialVector")] [TestCase("DifferentInitial")]
        [TestCase("Additive")] [TestCase("NormalizedTree")]
        public void InvariantTransformProofRejectsOtherWritersAndIncompleteGroups(string reason)
        {
            var settings = ObjectSettings(); var target = PolicyChild("Bone"); target.transform.localPosition = Vector3.one;
            var clip = InitialTransformClip(target.transform, complete: reason != "PartialVector");
            var source = reason == "NormalizedTree" ? TreeController(("Weight", clip)) : Controller(clip);
            if (reason == "NormalizedTree") using (var so = new SerializedObject(source.layers[0].stateMachine.defaultState.motion))
            { so.FindProperty("m_NormalizedBlendValues").boolValue = true; so.ApplyModifiedPropertiesWithoutUndo(); }
            if (reason == "PhysBone") target.AddComponent<VRCPhysBone>();
            if (reason == "Constraint") target.AddComponent<PositionConstraint>();
            if (reason == "Raycast") PolicyChild("Raycast").AddComponent<VRCRaycast>().ResultTransform = target.transform;
            if (reason == "Preserved") settings.ProtectedComponents.Add(ObjectRegistry.GetReference(target.transform));
            if (reason == "DifferentInitial") target.transform.localPosition = Vector3.zero;
            if (reason == "Additive") { var layers = source.layers; layers[0].blendingMode = AnimatorLayerBlendingMode.Additive; source.layers = layers; }
            var clone = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            OptimizeObjectCurves(clone, settings);
            Assert.That(clone.AllReachableNodes().OfType<VirtualClip>().SelectMany(c => c.GetFloatCurveBindings()).Any(b => b.path == "Bone"), Is.True);
        }

        [Test]
        public void VisibilityOptimizationDoesNotReplaceAnInitialOnTransientWithOff()
        {
            var settings = ObjectSettings(); var target = PolicyChild("Initially active");
            var source = Controller(StaticCurve(EditorCurveBinding.FloatCurve(target.name, typeof(GameObject), "m_IsActive"), 0));
            var clone = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            OptimizeObjectCurves(clone, settings);
            Assert.That(target.activeSelf, Is.True);
            Assert.That(clone.AllReachableNodes().OfType<VirtualClip>().Single().GetFloatCurveBindings().Count(), Is.EqualTo(1));
        }

        [Test]
        public void ObjectPreservationFollowsChildrenMovedOutOfTheirOriginalSubtree()
        {
            var marker = ObjectSettings().Component;
            var preserved = PolicyChild("Preserved"); var child = PolicyChild("Moved"); child.transform.SetParent(preserved.transform);
            marker.PreserveObjects.Add(preserved);
            var context = new BuildContext(root, null); ObjectOptimizationPass.Capture(context);
            child.transform.SetParent(root.transform);
            Assert.That(context.GetState<ObjectOptimizationState>().Protected(child.transform), Is.True);
            Assert.That(typeof(ALTOPlatformOptimizationComponent).IsAssignableFrom(marker.GetType()), Is.False);
        }

        [Test]
        public void UnknownRuntimeComponentsPreventUnprovenObjectOptimizations()
        {
            root.AddComponent<ParameterPolicySourceFixture>();
            Assert.That(ObjectOptimizationSafety.UnsupportedRuntime(root), Does.Contain(nameof(ParameterPolicySourceFixture)));
        }

        [TestCase(false)] [TestCase(true)]
        public void SdkParentChangeListenerFollowsPhysBoneLifetime(bool keepSimulation)
        {
            var settings = ObjectSettings();
            var target = PolicyChild("PhysBone target");
            var type = Type.GetType("VRC.Dynamics.ParentChangeDetector, VRC.Dynamics");
            Assert.That(type, Is.Not.Null);
            var listener = target.AddComponent(type);
            if (keepSimulation)
            {
                var bone = PolicyChild("Simulation").AddComponent<VRCPhysBone>();
                bone.rootTransform = target.transform;
            }
            Assert.That(ObjectOptimizationSafety.UnsupportedRuntime(root), Is.Null);
            CollectObjects(settings);
            Assert.That(listener != null, Is.EqualTo(keepSimulation));
            Assert.That(target != null, Is.EqualTo(keepSimulation));
        }

        [Test]
        public void ExplicitlyPreservedSdkParentChangeListenerSurvivesCollection()
        {
            var settings = ObjectSettings();
            var target = PolicyChild("Observed SDK helper");
            var listener = target.AddComponent(Type.GetType("VRC.Dynamics.ParentChangeDetector, VRC.Dynamics"));
            settings.ProtectedComponents.Add(ObjectRegistry.GetReference(listener));
            CollectObjects(settings);
            Assert.That(listener != null && target != null, Is.True);
        }
    }
}
