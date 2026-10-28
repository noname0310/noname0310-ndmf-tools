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
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDKBase;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        [Test]
        public void SdkSensorCleanupPersistsControllersAndLayerControlsAfterNdmfSerialization()
        {
            Mesh("Face");
            string folderName = "AdvancedOptimizerSdkFixture_" + Guid.NewGuid().ToString("N");
            string folder = "Assets/" + folderName;
            AssetDatabase.CreateFolder("Assets", folderName);
            try
            {
                var owner = PolicyChild("Probe");
                var ray = owner.AddComponent<VRCRaycast>(); ray.Parameter = "Probe";
                ray.ResultTransform = PolicyChild("Result", owner.transform).transform;
                var names = AvatarParameterInputs.RaycastSuffixes.Select(s => "Probe" + s).ToArray();
                var face = StaticCurve(Binding("Face"), 70); face.name = "Face pose";
                var fx = Controller(face); var gesture = Controller(Clip());
                fx.AddLayer("Second layer"); Track(fx.layers[1].stateMachine);
                foreach (var name in names) fx.AddParameter(name, AnimatorControllerParameterType.Float);
                foreach (var controller in new[] { fx, gesture })
                {
                    var control = Track(controller.layers[0].stateMachine.defaultState.AddStateMachineBehaviour<VRCAnimatorLayerControl>());
                    control.playable = VRC_AnimatorLayerControl.BlendableLayer.FX; control.layer = 1;
                }
                root.AddComponent<Animator>();
                var descriptor = root.AddComponent<VRCAvatarDescriptor>();
                descriptor.customizeAnimationLayers = true;
                descriptor.specialAnimationLayers = Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>();
                descriptor.baseAnimationLayers = new[] {
                    new VRCAvatarDescriptor.CustomAnimLayer { type = VRCAvatarDescriptor.AnimLayerType.FX, animatorController = fx },
                    new VRCAvatarDescriptor.CustomAnimLayer { type = VRCAvatarDescriptor.AnimLayerType.Gesture, animatorController = gesture }
                };
                var parameters = Track(ScriptableObject.CreateInstance<VRCExpressionParameters>());
                parameters.parameters = names.Select(n => new VRCExpressionParameters.Parameter
                    { name = n, networkSynced = false, valueType = VRCExpressionParameters.ValueType.Float }).ToArray();
                descriptor.expressionParameters = parameters;
                root.AddComponent<ALTOTraceAndOptimizeExpressions>();
                root.AddComponent<ALTOTraceAndOptimizeObjects>();
                var context = new BuildContext(root, folder);
                ObjectOptimizationPass.Capture(context); ExpressionOptimizationPass.Capture(context);
                var input = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(fx);
                foreach (var name in names) context.GetState<ParameterPolicyState>().Constants.Add(name, Constants(input, name, 0)[name]);
                AvatarOutputSdkFinalizer.Begin(root);
                ExpressionOptimizationPass.Apply(context);
                context.AssetSaver.Dispose();
                Assert.That(ray != null, Is.True);
                AvatarOutputSdkFinalizer.Complete(root);
                Assert.That(ray == null, Is.True);
                var measurement = context.GetState<OptimizationBuildReport>().Measurements.Single();
                Assert.That(measurement.Name, Is.EqualTo("runtime output cleanup"));
                Assert.That(measurement.Completed && measurement.Succeeded, Is.True);
                Assert.That(measurement.After, Is.Not.Null);
                Assert.That(EditorUtility.IsPersistent(descriptor.expressionParameters), Is.True);
                foreach (var layer in descriptor.baseAnimationLayers)
                {
                    var built = (AnimatorController)layer.animatorController;
                    Assert.That(EditorUtility.IsPersistent(built), Is.True);
                    foreach (var dependency in EditorUtility.CollectDependencies(new UnityEngine.Object[] { built })
                        .Where(o => o is AnimatorController || o is AnimatorStateMachine || o is AnimatorState ||
                            o is AnimationClip || o is StateMachineBehaviour))
                        Assert.That(EditorUtility.IsPersistent(dependency), Is.True, dependency.name);
                    Assert.That(built.layers[0].stateMachine.defaultState.behaviours.OfType<VRCAnimatorLayerControl>().Single().layer,
                        Is.EqualTo(1), "Both same-controller and cross-controller layer references remain valid");
                }
                Assert.That(fx.parameters.Select(p => p.name), Is.EqualTo(names));
                Assert.That(parameters.parameters.Length, Is.EqualTo(3));
            }
            finally { AssetDatabase.DeleteAsset(folder); }
        }

        [Test]
        public void ExpressionSensorCleanupUsesFinalObserversAndOriginalNetworkContract(
            [Values("None", "LateReader", "Reobserved", "LateWriter", "Synced", "BecameLocal", "LateDeclaration", "Mixed",
                "ResultBone", "PreserveComponent", "PreserveInput", "OutsideScope", "LocalDisabled",
                "SensorDisabled", "ObjectDisabled", "NoEvidence", "External", "ProtectedMenu")] string observer,
            [Values(false, true)] bool sdk)
        {
            Mesh("Face");
            var owner = PolicyChild("Probe"); var result = PolicyChild("Result", owner.transform);
            var ray = owner.AddComponent<VRCRaycast>(); ray.Parameter = "Probe"; ray.ResultTransform = result.transform;
            var names = AvatarParameterInputs.RaycastSuffixes.Select(s => "Probe" + s).ToArray();
            string input = "Probe" + VRCRaycast.PARAM_HIT;
            var face = StaticCurve(Binding("Face"), 70); face.name = "Face pose";
            var source = Controller(face);
            foreach (var name in names) source.AddParameter(name, AnimatorControllerParameterType.Float);
            var expression = root.AddComponent<ALTOTraceAndOptimizeExpressions>();
            expression.PruneUnusedLocalParameters = observer != "LocalDisabled";
            var menu = observer == "ProtectedMenu" ? ExpressionMenu(ExpressionControl("Probe", input)) : null;
            if (menu != null) expression.PreserveMenus.Add(menu);
            var settings = ObjectSettings(observer == "OutsideScope" ? PolicyChild("Different scope") : root).Component;
            settings.PruneUnusedSensorParameters = observer != "SensorDisabled";
            settings.RemoveUnusedObjects = observer != "ObjectDisabled";
            if (observer == "PreserveComponent") settings.PreserveComponents.Add(ray);
            if (observer == "ResultBone") renderer.bones = new[] { result.transform };
            var context = ExpressionBuild(menu, source);
            ObjectOptimizationPass.Capture(context);
            if (observer == "Reobserved") context.GetState<ObjectOptimizationState>().PrunedSensorInputs.UnionWith(names);
            var policy = context.GetState<ParameterPolicyState>();
            policy.Enabled = true;
            if (observer == "PreserveInput") policy.Names.Add(input);
            var virtualSource = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            if (observer != "NoEvidence")
                foreach (var name in names) policy.Constants.Add(name, Constants(virtualSource, name, 0)[name]);
            var descriptor = root.GetComponent<VRCAvatarDescriptor>();
            var parameters = Track(ScriptableObject.CreateInstance<VRCExpressionParameters>());
            parameters.parameters = names.Where(n => observer != "LateDeclaration" || n != input)
                .Select(n => new VRCExpressionParameters.Parameter { name = n, valueType = VRCExpressionParameters.ValueType.Float,
                    networkSynced = n == input && (observer == "Synced" || observer == "BecameLocal" || observer == "Mixed") }).ToArray();
            if (observer == "Mixed") parameters.parameters = parameters.parameters.Concat(new[] {
                new VRCExpressionParameters.Parameter { name = input, networkSynced = false }
            }).ToArray();
            descriptor.expressionParameters = parameters;
            string originalParameters = JsonUtility.ToJson(parameters);
            string originalClip = EditorJsonUtility.ToJson(face);
            if (sdk)
            {
                AvatarOutputSdkFinalizer.Begin(root);
                ExpressionOptimizationPass.Apply(context);
                Assert.That(ray != null, Is.True, "Sensors survive until late SDK consumers have run");
                Assert.That(descriptor.expressionParameters, Is.SameAs(parameters));
                TrackControllerAssets((AnimatorController)descriptor.baseAnimationLayers.Single().animatorController);
            }
            if (observer == "BecameLocal" || observer == "LateDeclaration")
            {
                var changed = Track(UnityEngine.Object.Instantiate(parameters));
                if (observer == "BecameLocal") changed.parameters.Single(p => p.name == input).networkSynced = false;
                else changed.parameters = changed.parameters.Concat(new[] {
                    new VRCExpressionParameters.Parameter { name = input, networkSynced = false }
                }).ToArray();
                descriptor.expressionParameters = changed;
            }
            if (observer == "LateReader" || observer == "Reobserved" || observer == "LateWriter")
            {
                var late = observer != "LateWriter" ? TreeController((input, face)) : Controller(face);
                if (observer == "LateWriter")
                {
                    late.AddParameter(input, AnimatorControllerParameterType.Float);
                    Track(late.layers[0].stateMachine.defaultState.AddStateMachineBehaviour<VRCAvatarParameterDriver>())
                        .parameters.Add(new VRC_AvatarParameterDriver.Parameter { name = input, value = 1 });
                }
                var layers = descriptor.baseAnimationLayers; layers[0].animatorController = late;
                descriptor.baseAnimationLayers = layers;
            }
            if (observer == "External") ExternalParameterAccess.Register("Sensor cleanup fixture", a => a.Readers.Add(n => n == input));
            try
            {
                if (sdk) AvatarOutputSdkFinalizer.Complete(root, o => Track(o));
                else ExpressionOptimizationPass.Apply(context);
            }
            finally { ExternalParameterAccess.Register("Sensor cleanup fixture", _ => { }); }
            var built = (AnimatorController)descriptor.baseAnimationLayers.Single().animatorController;
            TrackControllerAssets(built);
            bool removed = observer == "None" || !sdk && (observer == "BecameLocal" || observer == "LateDeclaration");
            Assert.That(ray == null, Is.EqualTo(removed));
            Assert.That(owner == null && result == null, Is.EqualTo(removed));
            Assert.That(descriptor.expressionParameters.parameters.Any(p => p.name == input), Is.EqualTo(!removed));
            if (removed) Assert.That(built.parameters.Select(p => p.name), Does.Not.Contain(input));
            Assert.That(JsonUtility.ToJson(parameters), Is.EqualTo(originalParameters), "Source parameter assets are immutable");
            Assert.That(EditorJsonUtility.ToJson(face), Is.EqualTo(originalClip));
            Assert.That(source.parameters.Select(p => p.name), Is.EqualTo(names));
            Assert.That(built.animationClips.SelectMany(AnimationUtility.GetCurveBindings).Any(b => b.propertyName == "blendShape.Face"), Is.True);
            if (sdk)
            {
                var committed = descriptor.baseAnimationLayers.Single().animatorController;
                AvatarOutputSdkFinalizer.Complete(root, _ => Assert.Fail("The finalizer must not run twice"));
                Assert.That(descriptor.baseAnimationLayers.Single().animatorController, Is.SameAs(committed));
            }
        }
    }
}
