using System;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.util;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.Meshes;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using VRC.SDK3.Avatars.Components;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private BuildContext CaptureRegionFilter(params BlendShapeMeshExclusion[] rules)
        {
            var filter = RenderingFilter(); filter.BlendShapeExclusions.AddRange(rules);
            var context = new BuildContext(root, null); BuildFilterPass.RemoveEarly(context); return context;
        }

        private void ApplyRegionFilter(BuildContext context)
        {
            try { BlendShapeExclusionPass.Apply(context); }
            finally
            {
                foreach (var asset in root.ReferencedAssets().Where(a => a != null && !(a is Component) && !(a is GameObject) &&
                    !(a is Shader) && !(a is MonoScript) && !EditorUtility.IsPersistent(a))) Track(asset);
            }
        }

        [TestCase(0f)] [TestCase(37.5f)] [TestCase(100f)] [TestCase(-50f)]
        public void ExplicitRegionRemovalIgnoresCurrentWeightsAndPreservesOtherMorphInterfaces(float weight)
        {
            MaterialDescriptor(); var source = renderer.sharedMesh = OcclusionMaskMesh();
            renderer.SetBlendShapeWeight(0, weight); renderer.SetBlendShapeWeight(1, 28);
            var descriptor = root.GetComponent<VRCAvatarDescriptor>(); var eyes = descriptor.customEyeLookSettings;
            eyes.eyelidsSkinnedMesh = renderer; eyes.eyelidsBlendshapes = new[] { 1, -1, -1 }; descriptor.customEyeLookSettings = eyes;
            var context = CaptureRegionFilter(new BlendShapeMeshExclusion { Renderer = renderer, BlendShapes = new() { "Region A" } });
            ApplyRegionFilter(context);
            Assert.That(renderer.sharedMesh, Is.Not.SameAs(source)); Assert.That(renderer.sharedMesh.GetIndices(0), Is.Empty);
            Assert.That(renderer.sharedMesh.GetIndices(1), Is.EqualTo(source.GetIndices(1)));
            Assert.That(source.GetIndices(0), Is.EqualTo(new[] { 0, 1, 2 }));
            Assert.That(renderer.sharedMesh.vertices, Is.EqualTo(source.vertices));
            Assert.That(renderer.sharedMesh.blendShapeCount, Is.EqualTo(source.blendShapeCount));
            Assert.That(renderer.GetBlendShapeWeight(0), Is.EqualTo(weight)); Assert.That(renderer.GetBlendShapeWeight(1), Is.EqualTo(28));
            Assert.That(descriptor.customEyeLookSettings.eyelidsBlendshapes[0], Is.EqualTo(1));
            var first = renderer.sharedMesh; ApplyRegionFilter(context);
            Assert.That(renderer.sharedMesh, Is.SameAs(first), "Already removed regions do not allocate another output");
        }

        [Test]
        public void MultipleRowsAndFramesUseTheUnionWithoutDoubleCounting()
        {
            MaterialDescriptor(); var source = renderer.sharedMesh = OcclusionMaskMesh();
            var context = CaptureRegionFilter(
                new BlendShapeMeshExclusion { Renderer = renderer, BlendShapes = new() { "Multiple frames", "Region B" } },
                new BlendShapeMeshExclusion { Renderer = renderer, BlendShapes = new() { "Region B", "Region B" } });
            ApplyRegionFilter(context);
            Assert.That(renderer.sharedMesh.triangles, Is.Empty); Assert.That(source.triangles.Length, Is.EqualTo(6));
            Assert.That(context.GetState<OptimizationBuildReport>().Messages.Any(m => m.StartsWith("BlendShape exclusion removed 2 triangles", StringComparison.Ordinal)), Is.True);
        }

        [Test]
        public void SharedMeshesCanHaveDifferentSelectionsWithoutChangingAnUnselectedRenderer()
        {
            MaterialDescriptor(); var source = renderer.sharedMesh = OcclusionMaskMesh();
            var second = PolicyChild("Second").AddComponent<SkinnedMeshRenderer>(); second.sharedMesh = source;
            var untouched = PolicyChild("Unselected").AddComponent<SkinnedMeshRenderer>(); untouched.sharedMesh = source;
            var context = CaptureRegionFilter(new BlendShapeMeshExclusion { Renderer = renderer, BlendShapes = new() { "Region A" } },
                new BlendShapeMeshExclusion { Renderer = second, BlendShapes = new() { "Region B" } });
            ApplyRegionFilter(context);
            Assert.That(renderer.sharedMesh.GetIndices(0), Is.Empty); Assert.That(second.sharedMesh.GetIndices(1), Is.Empty);
            Assert.That(renderer.sharedMesh.GetIndices(1).Length, Is.EqualTo(3)); Assert.That(second.sharedMesh.GetIndices(0).Length, Is.EqualTo(3));
            Assert.That(untouched.sharedMesh, Is.SameAs(source)); Assert.That(source.triangles.Length, Is.EqualTo(6));
        }

        [Test]
        public void EmptyRegionListsDoNotSelectEveryMorph()
        {
            MaterialDescriptor(); var source = renderer.sharedMesh = OcclusionMaskMesh();
            var context = CaptureRegionFilter(new BlendShapeMeshExclusion { Renderer = renderer }, new BlendShapeMeshExclusion());
            ApplyRegionFilter(context); Assert.That(renderer.sharedMesh, Is.SameAs(source));
        }

        [Test]
        public void RegionFilteringUsesTheGeneratedMeshBeforeMorphFreezing()
        {
            MaterialDescriptor(); var original = renderer.sharedMesh = OcclusionMaskMesh();
            var context = CaptureRegionFilter(new BlendShapeMeshExclusion { Renderer = renderer, BlendShapes = new() { "Region A" } });
            var generated = Track(UnityEngine.Object.Instantiate(original));
            generated.SetIndices(new[] { 0, 1, 2, 0, 2, 1 }, MeshTopology.Triangles, 0); renderer.sharedMesh = generated;
            ApplyRegionFilter(context);
            Assert.That(renderer.sharedMesh.GetIndices(0), Is.Empty); Assert.That(renderer.sharedMesh.GetIndices(1).Length, Is.EqualTo(3));
            Assert.That(context.GetState<OptimizationBuildReport>().Messages.Any(m => m.StartsWith("BlendShape exclusion removed 2 triangles", StringComparison.Ordinal)), Is.True);
            Assert.That(generated.GetIndices(0).Length, Is.EqualTo(6)); Assert.That(original.GetIndices(0).Length, Is.EqualTo(3));
        }

        [Test]
        public void MissingMorphOnAnotherTargetRejectsAllPlansBeforeAssignment()
        {
            MaterialDescriptor(); var source = renderer.sharedMesh = OcclusionMaskMesh();
            var second = PolicyChild("Second").AddComponent<SkinnedMeshRenderer>(); second.sharedMesh = source;
            var context = CaptureRegionFilter(new BlendShapeMeshExclusion { Renderer = renderer, BlendShapes = new() { "Region A" } },
                new BlendShapeMeshExclusion { Renderer = second, BlendShapes = new() { "Missing" } });
            Assert.Throws<InvalidOperationException>(() => ApplyRegionFilter(context));
            Assert.That(renderer.sharedMesh, Is.SameAs(source)); Assert.That(second.sharedMesh, Is.SameAs(source));
        }

        [Test]
        public void RemovedGeneratorTargetIsReportedInsteadOfGuessingAnotherRenderer()
        {
            MaterialDescriptor(); var target = PolicyChild("Target").AddComponent<SkinnedMeshRenderer>(); target.sharedMesh = OcclusionMaskMesh();
            var context = CaptureRegionFilter(new BlendShapeMeshExclusion { Renderer = target, BlendShapes = new() { "Region A" } });
            UnityEngine.Object.DestroyImmediate(target);
            Assert.That(Assert.Throws<InvalidOperationException>(() => ApplyRegionFilter(context)).Message, Does.Contain("removed or merged"));
        }

        [Test]
        public void AnimatedMeshReferencesAreRejectedWithoutChangingTheSourceClip()
        {
            var source = renderer.sharedMesh = OcclusionMaskMesh(); var clip = Clip();
            var binding = EditorCurveBinding.PPtrCurve("", typeof(SkinnedMeshRenderer), "m_Mesh");
            AnimationUtility.SetObjectReferenceCurve(clip, binding, new[] { new ObjectReferenceKeyframe { time = 0, value = source } });
            MaterialDescriptor(Controller(clip)); string before = EditorJsonUtility.ToJson(clip);
            var context = CaptureRegionFilter(new BlendShapeMeshExclusion { Renderer = renderer, BlendShapes = new() { "Region A" } });
            Assert.Throws<InvalidOperationException>(() => ApplyRegionFilter(context));
            Assert.That(renderer.sharedMesh, Is.SameAs(source)); Assert.That(EditorJsonUtility.ToJson(clip), Is.EqualTo(before));
        }

        [TestCase("scope")] [TestCase("nullRenderer")] [TestCase("negativeThreshold")]
        [TestCase("nonfiniteThreshold")] [TestCase("emptyName")]
        public void RegionSelectionValidationRejectsInvalidConfigurations(string invalid)
        {
            MaterialDescriptor(); var filter = RenderingFilter(); renderer.sharedMesh = OcclusionMaskMesh();
            var row = new BlendShapeMeshExclusion { Renderer = renderer, BlendShapes = new() { "Region A" } }; filter.BlendShapeExclusions.Add(row);
            if (invalid == "scope") filter.ScopeRoot = PolicyChild("Scope").transform;
            if (invalid == "nullRenderer") row.Renderer = null;
            if (invalid == "negativeThreshold") row.DisplacementThreshold = -1;
            if (invalid == "nonfiniteThreshold") row.DisplacementThreshold = float.NaN;
            if (invalid == "emptyName") row.BlendShapes.Add("");
            Assert.Throws<InvalidOperationException>(() => BuildFilterPass.RemoveEarly(new BuildContext(root, null)));
            Assert.That(renderer.sharedMesh.triangles.Length, Is.EqualTo(6));
        }

        [Test]
        public void DisabledPlatformFilterDoesNotRemoveSelectedRegions()
        {
            MaterialDescriptor(); var filter = RenderingFilter(); filter.ApplyOnPlatforms = OptimizationPlatforms.None;
            var source = renderer.sharedMesh = OcclusionMaskMesh();
            filter.BlendShapeExclusions.Add(new BlendShapeMeshExclusion { Renderer = renderer, BlendShapes = new() { "Region A" } });
            var context = new BuildContext(root, null); BuildFilterPass.RemoveEarly(context); ApplyRegionFilter(context);
            Assert.That(renderer.sharedMesh, Is.SameAs(source));
        }

        [Test]
        public void SharedMaskExtractionIgnoresNormalOnlyMorphsAndHonorsThresholds()
        {
            var mesh = Mesh(); var positions = new Vector3[mesh.vertexCount]; positions[0] = Vector3.right * 0.0001f;
            mesh.AddBlendShapeFrame("Tiny", 100, positions, null, null);
            mesh.AddBlendShapeFrame("Normal", 100, new Vector3[mesh.vertexCount], Enumerable.Repeat(Vector3.one, mesh.vertexCount).ToArray(), null);
            var stored = new Vector3[mesh.vertexCount]; mesh.GetBlendShapeFrameVertices(0, 0, stored, null, null);
            Assert.That(stored[0].sqrMagnitude, Is.GreaterThan(0), "Unity must retain the fixture's displacement before threshold analysis");
            var masks = BlendShapeRegionMasks.Create(mesh, new[] { "Tiny", "Normal" }, 0.001f);
            Assert.That(masks.Regions.All(r => r.Triangles.Length == 0), Is.True);
            Assert.That(BlendShapeRegionMasks.Create(mesh, new[] { "Tiny" }, 0).Regions[0].Triangles, Is.EqualTo(new[] { 0 }));
        }
    }
}
