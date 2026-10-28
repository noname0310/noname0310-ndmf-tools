using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using nadena.dev.ndmf;
using Noname.AvatarTools.AnimatorOptimization;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.ParameterLayout;
using Noname.AvatarTools.ParameterPolicy;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDKBase;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private StateMachineBehaviour[] PersistenceBehaviours(AnimatorController controller)
        {
            var machine = controller.layers.Single(l => l.name == "Base").stateMachine;
            return machine.behaviours.Concat(machine.defaultState.behaviours).ToArray();
        }

        private AnimatorController PersistenceController()
        {
            var source = Controller(Clip()); source.name = "Existing behaviour controller";
            source.AddParameter("Mode", AnimatorControllerParameterType.Int);
            source.AddParameter("Input", AnimatorControllerParameterType.Float);
            source.AddParameter("Output", AnimatorControllerParameterType.Float);
            var machine = source.layers[0].stateMachine;
            var driver = Track(ScriptableObject.CreateInstance<VRCAvatarParameterDriver>());
            driver.name = "Existing Driver"; driver.localOnly = true;
            driver.parameters = new List<VRC_AvatarParameterDriver.Parameter> { new()
            { name = "Output", source = "Input", type = VRC_AvatarParameterDriver.ChangeType.Copy, convertRange = true,
                sourceMin = 0, sourceMax = 1, destMin = -1, destMax = 2 } };
            var stateTracking = Track(ScriptableObject.CreateInstance<VRCAnimatorTrackingControl>());
            stateTracking.name = "Existing State Tracking";
            stateTracking.trackingEyes = VRC_AnimatorTrackingControl.TrackingType.Animation;
            var machineTracking = Track(ScriptableObject.CreateInstance<VRCAnimatorTrackingControl>());
            machineTracking.name = "Existing Machine Tracking";
            machineTracking.trackingMouth = VRC_AnimatorTrackingControl.TrackingType.Animation;
            machine.defaultState.behaviours = new StateMachineBehaviour[] { driver, stateTracking };
            machine.behaviours = new StateMachineBehaviour[] { machineTracking };
            return source;
        }

        private static void AssertPersistenceBehaviourData(AnimatorController controller, bool specialized)
        {
            var machine = controller.layers.Single(l => l.name == "Base").stateMachine;
            var driver = machine.defaultState.behaviours.OfType<VRC_AvatarParameterDriver>().Single();
            Assert.That(driver.localOnly, Is.True);
            var operation = driver.parameters.Single();
            Assert.That(operation.name, Is.EqualTo("Output"));
            Assert.That(operation.source, Is.EqualTo(specialized ? "__PersistenceFrozenInput" : "Input"));
            Assert.That(operation.type, Is.EqualTo(VRC_AvatarParameterDriver.ChangeType.Copy));
            Assert.That(operation.convertRange, Is.True);
            Assert.That((operation.sourceMin, operation.sourceMax, operation.destMin, operation.destMax), Is.EqualTo((0f, 1f, -1f, 2f)));
            Assert.That(machine.defaultState.behaviours.OfType<VRC_AnimatorTrackingControl>().Single().trackingEyes,
                Is.EqualTo(VRC_AnimatorTrackingControl.TrackingType.Animation));
            Assert.That(machine.behaviours.OfType<VRC_AnimatorTrackingControl>().Single().trackingMouth,
                Is.EqualTo(VRC_AnimatorTrackingControl.TrackingType.Animation));
        }

        [TestCase("Output")] [TestCase("Freeze")] [TestCase("Integer")] [TestCase("OutputThenInteger")]
        public void LateControllerCommitsPersistExistingBehavioursAcrossReload(string boundary)
        {
            string folderName = "ALTOAnimatorPersistence_" + Guid.NewGuid().ToString("N"), folder = "Assets/" + folderName;
            AssetDatabase.CreateFolder("Assets", folderName);
            try
            {
                var source = PersistenceController();
                var sourceBehaviours = PersistenceBehaviours(source);
                var machine = source.layers[0].stateMachine;
                AssetDatabase.StartAssetEditing();
                try
                {
                    AssetDatabase.CreateAsset(source, folder + "/Source.controller");
                    foreach (var asset in new Object[] { machine, machine.defaultState, machine.defaultState.motion }.Concat(sourceBehaviours))
                        AssetDatabase.AddObjectToAsset(asset, source);
                }
                finally { AssetDatabase.StopAssetEditing(); }
                AssetDatabase.SaveAssets();
                var sourceData = sourceBehaviours.Select(EditorJsonUtility.ToJson).ToArray();

                var descriptor = LayoutDescriptor();
                descriptor.customizeAnimationLayers = true;
                descriptor.baseAnimationLayers = new[] { new VRCAvatarDescriptor.CustomAnimLayer
                    { type = VRCAvatarDescriptor.AnimLayerType.FX, animatorController = source, isDefault = false } };
                var parameters = Track(ScriptableObject.CreateInstance<VRCExpressionParameters>());
                parameters.parameters = new[] { LayoutParameter("Mode", VRCExpressionParameters.ValueType.Int), LayoutParameter("Input") };
                parameters.parameters[0].defaultValue = 3; descriptor.expressionParameters = parameters;
                var context = new BuildContext(root, folder + "/Generated");
                context.AssetSaver.Dispose(); // The ordinary NDMF serialization boundary has already finished.

                if (boundary is "Output" or "OutputThenInteger")
                {
                    context.GetState<AnimatorOptimizationState>().Settings = new() { Unreachable = true };
                    AvatarOutputSdkFinalizer.Begin(root); AvatarOutputSdkFinalizer.Capture(context);
                    AvatarOutputSdkFinalizer.Complete(root);
                }
                if (boundary == "Freeze")
                {
                    var policy = context.GetState<ParameterPolicyState>();
                    policy.Enabled = policy.FreezeResolved = true;
                    policy.Scope = new OptimizationBuildScope { Root = root.transform };
                    policy.Constants.Add("Input", new ParameterConstant { Name = "Input", InternalName = "__PersistenceFrozenInput",
                        Type = AnimatorControllerParameterType.Float, Types = new() { AnimatorControllerParameterType.Float }, Value = .25f });
                    ParameterFreezeSdkFinalizer.Begin(root); ParameterFreezeSdkFinalizer.Capture(context);
                    ParameterFreezeSdkFinalizer.Complete(root);
                }
                if (boundary is "Integer" or "OutputThenInteger")
                {
                    var settings = new ParameterCompressionSettings();
                    settings.Integers.Add(new CompressedIntegerParameter { Name = "Mode", Maximum = 7 });
                    using var saver = new SingleAssetSaver(context.AssetSaver.CurrentContainer);
                    ParameterCompressionPass.Apply(context, settings, null, context.GetState<OptimizationBuildReport>(), saver.SaveAsset);
                }

                var built = (AnimatorController)descriptor.baseAnimationLayers.Single().animatorController;
                Assert.That(built, Is.Not.SameAs(source));
                var behaviours = PersistenceBehaviours(built);
                Assert.That(behaviours, Has.Length.EqualTo(3));
                Assert.That(behaviours.All(EditorUtility.IsPersistent), Is.True, "Existing cloned behaviours must be assets before final serialization.");
                Assert.That(behaviours.Intersect(sourceBehaviours), Is.Empty);
                AssertPersistenceBehaviourData(built, boundary == "Freeze");
                Assert.That(sourceBehaviours.Select(EditorJsonUtility.ToJson), Is.EqualTo(sourceData));
                Assert.That(PersistenceBehaviours(source), Is.EqualTo(sourceBehaviours), "Source references must not be replaced.");

                // Import a disk copy into a new asset identity. Reading the live graph
                // alone would hide transient references missing from serialized output.
                AssetDatabase.SaveAssets();
                Assert.That(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(built, out string _, out long fileId), Is.True);
                string copyPath = folder + "/Reloaded.asset";
                File.Copy(AssetDatabase.GetAssetPath(built), copyPath);
                AssetDatabase.ImportAsset(copyPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                var reloaded = AssetDatabase.LoadAllAssetsAtPath(copyPath).OfType<AnimatorController>().Single(c =>
                    AssetDatabase.TryGetGUIDAndLocalFileIdentifier(c, out string _, out long id) && id == fileId);
                Assert.That(reloaded, Is.Not.SameAs(built));
                AssertPersistenceBehaviourData(reloaded, boundary == "Freeze");
                Assert.That(PersistenceBehaviours(reloaded).All(EditorUtility.IsPersistent), Is.True);
            }
            finally { AssetDatabase.DeleteAsset(folder); }
        }

        [Test]
        public void FinalControllerCommitIncludesClonedBehavioursInTestSaveHook()
        {
            var source = PersistenceController();
            var originals = PersistenceBehaviours(source);
            foreach (var behaviour in originals) behaviour.hideFlags = HideFlags.HideAndDontSave;
            var sourceData = originals.Select(EditorJsonUtility.ToJson).ToArray();
            var context = ExpressionBuild(null, source);
            context.GetState<AnimatorOptimizationState>().Settings = new() { Unreachable = true };
            var saved = new HashSet<Object>();
            AvatarOutputSdkFinalizer.Begin(root); AvatarOutputSdkFinalizer.Capture(context);
            AvatarOutputSdkFinalizer.Complete(root, asset => { saved.Add(asset); Track(asset); });
            var built = (AnimatorController)root.GetComponent<VRCAvatarDescriptor>().baseAnimationLayers.Single().animatorController;
            TrackControllerAssets(built);
            var behaviours = PersistenceBehaviours(built);
            Assert.That(behaviours.All(saved.Contains), Is.True, "Save hooks must observe existing clones as well as newly generated transport drivers.");
            Assert.That(behaviours.All(b => (b.hideFlags & HideFlags.DontSave) == 0), Is.True);
            Assert.That(behaviours.Intersect(originals), Is.Empty);
            Assert.That(originals.Select(EditorJsonUtility.ToJson), Is.EqualTo(sourceData));
            AssertPersistenceBehaviourData(built, false);
        }
    }
}
