using System;
using System.Linq;
using nadena.dev.ndmf;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.Meshes;
using Noname.AvatarTools.ObjectOptimization;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        [TestCase(false)] [TestCase(true)]
        public void LateMeshMergeUsesCurrentPathsAndPreservesNativeMorphOutput(bool collectObjects)
        {
            UnityEngine.Object.DestroyImmediate(renderer);
            var pair = MergePair();
            var originalFirst = pair.First.sharedMesh; var originalSecond = pair.Second.sharedMesh;
            pair.Second.sharedMesh.AddBlendShapeFrame("LateMorph", 100, Enumerable.Repeat(Vector3.up * .3f, 3).ToArray(), null, null);
            var metadata = PolicyChild("Installer").AddComponent<DependencyAuthoringFixture>(); metadata.Target = pair.Second;
            var descriptor = LayoutDescriptor();
            var settings = root.AddComponent<ALTOTraceAndOptimizeMeshes>();
            settings.RebindStaticBones = false; settings.RemoveUnusedMeshData = false;
            var context = new BuildContext(root, null);
            if (collectObjects) { ObjectSettings(); ObjectOptimizationPass.Capture(context); }
            MeshOptimizationPass.Capture(context); MeshOptimizationPass.Prepare(context);
            AvatarOutputSdkFinalizer.Begin(root); AvatarOutputSdkFinalizer.Capture(context);
            MeshOptimizationPass.Apply(context);
            Assert.That(pair.First != null && pair.Second != null, Is.True, "The installer has not finished in the NDMF mesh pass");
            Assert.That(metadata.Target, Is.SameAs(pair.Second));

            // A later SDK generator changes paths and creates a new controller. Never
            // reopen the old NDMF path map or edit the controller captured before it ran.
            var generated = PolicyChild("Generated namespace");
            pair.First.transform.SetParent(generated.transform, true);
            pair.Second.transform.SetParent(generated.transform, true);
            var source = Track(new AnimationClip());
            AnimationUtility.SetEditorCurve(source, EditorCurveBinding.FloatCurve("Generated namespace/Second", typeof(SkinnedMeshRenderer), "blendShape.LateMorph"),
                AnimationCurve.Linear(0, 0, 1, 100));
            var controller = Controller(source);
            descriptor.baseAnimationLayers = new[] { new VRCAvatarDescriptor.CustomAnimLayer
                { type = VRCAvatarDescriptor.AnimLayerType.FX, animatorController = controller, isDefault = false } };
            var samples = new[] { 0f, .2f, .7f, 1f };
            var expected = samples.Select(time =>
            {
                source.SampleAnimation(root, time);
                return AssemblyWorldVertices(pair.First).Concat(AssemblyWorldVertices(pair.Second)).ToArray();
            }).ToArray();
            source.SampleAnimation(root, 0);
            var sourceBindings = AnimationUtility.GetCurveBindings(source);
            AvatarOutputSdkFinalizer.Complete(root, value => Track(value));
            var merged = root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Single();
            Assert.That(merged, Is.SameAs(pair.First));
            Assert.That(merged.sharedMesh.vertexCount, Is.EqualTo(6));
            Assert.That(merged.sharedMaterials.Length, Is.EqualTo(1));
            var output = (AnimatorController)descriptor.baseAnimationLayers.Single().animatorController;
            TrackControllerAssets(output);
            var outputClip = output.animationClips.Single();
            var binding = AnimationUtility.GetCurveBindings(outputClip).Single(b => b.propertyName.StartsWith("blendShape.", StringComparison.Ordinal));
            Assert.That(binding.path, Is.EqualTo("Generated namespace/First"));
            for (int i = 0; i < samples.Length; i++)
            {
                outputClip.SampleAnimation(root, samples[i]);
                var actual = AssemblyWorldVertices(merged);
                Assert.That(actual.Zip(expected[i], Vector3.Distance).Max(), Is.LessThan(1e-5));
            }
            Assert.That(AnimationUtility.GetCurveBindings(source), Is.EqualTo(sourceBindings));
            Assert.That(originalFirst.vertexCount, Is.EqualTo(3)); Assert.That(originalSecond.vertexCount, Is.EqualTo(3));
            Assert.That(metadata == null, Is.EqualTo(collectObjects));

            var before = merged.sharedMesh;
            AvatarOutputSdkFinalizer.Begin(root); AvatarOutputSdkFinalizer.Capture(context);
            AvatarOutputSdkFinalizer.Complete(root, value => Track(value));
            Assert.That(merged.sharedMesh, Is.SameAs(before), "A second finalization must not rebuild an already optimized mesh");
        }

        [TestCase("None")] [TestCase("PreserveRenderer")] [TestCase("PreserveMetadata")]
        [TestCase("PreserveMetadataObject")] [TestCase("Scope")] [TestCase("LateToggle")]
        [TestCase("LateCloth")] [TestCase("LateEvent")] [TestCase("DisabledMerging")]
        public void LateMeshMergeRetainsIndependentVisibilityAndPreservation(string protection)
        {
            UnityEngine.Object.DestroyImmediate(renderer);
            var pair = MergePair();
            var metadataObject = PolicyChild("Installer");
            var metadata = metadataObject.AddComponent<DependencyAuthoringFixture>(); metadata.Target = pair.Second;
            var descriptor = LayoutDescriptor();
            var marker = root.AddComponent<ALTOTraceAndOptimizeMeshes>();
            marker.RebindStaticBones = marker.RemoveUnusedMeshData = false;
            if (protection == "PreserveRenderer") marker.PreserveRenderers.Add(pair.Second);
            if (protection == "Scope") marker.ScopeRoot = pair.Second.transform;
            if (protection == "DisabledMerging") marker.MergeCompatibleSkinnedMeshes = false;
            var objects = ObjectSettings().Component;
            if (protection == "PreserveMetadata") objects.PreserveComponents.Add(metadata);
            if (protection == "PreserveMetadataObject") objects.PreserveObjects.Add(metadataObject);
            var context = new BuildContext(root, null); ObjectOptimizationPass.Capture(context);
            MeshOptimizationPass.Capture(context); MeshOptimizationPass.Prepare(context);
            AvatarOutputSdkFinalizer.Begin(root); AvatarOutputSdkFinalizer.Capture(context);
            MeshOptimizationPass.Apply(context);
            Assert.That(pair.Second != null, Is.True);
            if (protection == "LateToggle")
            {
                var clip = StaticCurve(EditorCurveBinding.FloatCurve(pair.Second.name, typeof(GameObject), "m_IsActive"), 0);
                descriptor.baseAnimationLayers = new[] { new VRCAvatarDescriptor.CustomAnimLayer
                    { type = VRCAvatarDescriptor.AnimLayerType.FX, animatorController = Controller(clip), isDefault = false } };
            }
            if (protection == "LateCloth") pair.Second.gameObject.AddComponent<Cloth>();
            if (protection == "LateEvent")
            {
                var clip = Track(new AnimationClip());
                AnimationUtility.SetAnimationEvents(clip, new[] { new AnimationEvent { functionName = "UnknownObserver" } });
                descriptor.baseAnimationLayers = new[] { new VRCAvatarDescriptor.CustomAnimLayer
                    { type = VRCAvatarDescriptor.AnimLayerType.FX, animatorController = Controller(clip), isDefault = false } };
            }
            AvatarOutputSdkFinalizer.Complete(root, value => Track(value));
            Assert.That(root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length, Is.EqualTo(protection == "None" ? 1 : 2), protection);
            foreach (var layer in descriptor.baseAnimationLayers) if (layer.animatorController is AnimatorController controller) TrackControllerAssets(controller);
        }

        [TestCase(false)] [TestCase(true)]
        public void MappedMeshPreservationSurvivesRemovalOfItsAuthoringComponent(bool outsideScope)
        {
            var pair = MergePair();
            var state = new MeshOptimizationState { Component = root.AddComponent<ALTOTraceAndOptimizeMeshes>() };
            state.Inputs.Add((pair.First, true, false)); state.Inputs.Add((pair.Second, !outsideScope, !outsideScope));
            state.Component.BuildInputRenderers = new Renderer[] { pair.First, pair.First };
            MeshOptimizationPass.CaptureMappedInputs(state);
            UnityEngine.Object.DestroyImmediate(state.Component); UnityEngine.Object.DestroyImmediate(pair.Second);
            Assert.That(MeshOptimizationPass.InputProtected(state, pair.First, out string reason), Is.True);
            Assert.That(reason, Does.Contain(outsideScope ? "out-of-scope" : "preserved"));
        }
    }
}
