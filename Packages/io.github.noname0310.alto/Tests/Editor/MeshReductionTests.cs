using System;
using System.Linq;
using Noname.AvatarTools.MeshReduction;
using NUnit.Framework;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        [TestCase(false)] [TestCase(true)]
        public void EmptyRigidRenderersAreSafeReductionAndOcclusionInputs(bool removedFilter)
        {
            var obj = new GameObject("Empty mesh renderer"); obj.transform.SetParent(root.transform, false);
            var renderer = obj.AddComponent<MeshRenderer>();
            if (removedFilter)
            {
                var filter = obj.AddComponent<MeshFilter>(); filter.sharedMesh = OcclusionMaskMesh();
                UnityEngine.Object.DestroyImmediate(filter);
            }
            Assert.That(MeshReductionPass.MeshOf(renderer), Is.Null);
            using var snapshot = new Noname.AvatarTools.Occlusion.OcclusionPoseSnapshot(root, new OcclusionPose());
            Assert.That(snapshot.WorldMesh(renderer), Is.Null);
        }

        [Test]
        public void TriangleAllocationSharesQualityAndRedistributesSaturatedPriorities()
        {
            Assert.That(TriangleBudgetAllocator.Allocate(new[] { new TriangleAllocationInput(100, 0), new TriangleAllocationInput(300, 0) }, 200),
                Is.EqualTo(new[] { 50, 150 }));
            Assert.That(TriangleBudgetAllocator.Allocate(new[] { new TriangleAllocationInput(100, 0, 100), new TriangleAllocationInput(300, 0) }, 200),
                Is.EqualTo(new[] { 100, 100 }));
        }

        [Test]
        public void TriangleAllocationKeepsMinimaAndReportsOverBudgetWithoutInventingCapacity()
        {
            var inputs = new[] { new TriangleAllocationInput(100, 70), new TriangleAllocationInput(300, 40) };
            Assert.That(TriangleBudgetAllocator.Allocate(inputs, 90), Is.EqualTo(new[] { 70, 40 }));
            Assert.That(TriangleBudgetAllocator.Allocate(inputs, -10), Is.EqualTo(new[] { 70, 40 }));
            Assert.That(TriangleBudgetAllocator.Allocate(inputs, 1000), Is.EqualTo(new[] { 100, 300 }));
        }

        [Test]
        public void TriangleAllocationIsDeterministicAndNeverSpendsRoundingBeyondBudget()
        {
            var inputs = Enumerable.Range(0, 11).Select(_ => new TriangleAllocationInput(100, 1)).ToArray();
            var first = TriangleBudgetAllocator.Allocate(inputs, 26);
            Assert.That(first.Sum(), Is.EqualTo(26));
            Assert.That(first, Is.EqualTo(TriangleBudgetAllocator.Allocate(inputs, 26)));
            Assert.That(first.Take(4), Is.All.EqualTo(3));
            Assert.That(first.Skip(4), Is.All.EqualTo(2));
        }

        [Test]
        public void TriangleAllocationHandlesLargeCountsWithoutIntegerOverflow()
        {
            var inputs = new[] { new TriangleAllocationInput(int.MaxValue, 0, 0.01), new TriangleAllocationInput(int.MaxValue, 0, 100) };
            var result = TriangleBudgetAllocator.Allocate(inputs, int.MaxValue);
            Assert.That(result.Sum(v => (long)v), Is.EqualTo(int.MaxValue));
            Assert.That(result, Is.All.GreaterThanOrEqualTo(0));
        }

        [Test]
        public void DescriptorPreservationIsAnEditableInitialSelection()
        {
            var descriptor = root.AddComponent<VRCAvatarDescriptor>();
            descriptor.VisemeSkinnedMesh = renderer;
            var eyes = descriptor.customEyeLookSettings; eyes.eyelidsSkinnedMesh = renderer; descriptor.customEyeLookSettings = eyes;
            var settings = root.AddComponent<ALTOMeshReduction>(); settings.AddDescriptorRenderers();
            Assert.That(settings.PreserveRenderers, Is.EquivalentTo(new[] { renderer }));
            settings.PreserveRenderers.Clear();
            MeshReductionPass.ValidateSettings(settings);
            Assert.That(settings.PreserveRenderers, Is.Empty);
            Assert.That(settings.ApplyOnPlatforms, Is.EqualTo(OptimizationPlatforms.Android));
        }

        [Test]
        public void DescriptorInitialSelectionRespectsTheComponentSubtreeAndExplicitScope()
        {
            var descriptor = root.AddComponent<VRCAvatarDescriptor>();
            descriptor.VisemeSkinnedMesh = renderer;
            var child = new GameObject("Clothing settings"); child.transform.SetParent(root.transform, false);
            var settings = child.AddComponent<ALTOMeshReduction>(); settings.AddDescriptorRenderers();
            Assert.That(settings.PreserveRenderers, Is.Empty);
            settings.ScopeRoot = root.transform; settings.AddDescriptorRenderers();
            Assert.That(settings.PreserveRenderers, Is.EquivalentTo(new[] { renderer }));
        }

        [Test]
        public void ReductionRejectsAmbiguousOrInvalidPriorities()
        {
            var settings = root.AddComponent<ALTOMeshReduction>();
            settings.ReductionPriorities.Add(new MeshReductionPriority { Renderer = renderer, Weight = float.NaN });
            Assert.Throws<InvalidOperationException>(() => MeshReductionPass.ValidateSettings(settings));
            settings.ReductionPriorities[0].Weight = 1;
            settings.ReductionPriorities.Add(new MeshReductionPriority { Renderer = renderer });
            Assert.Throws<InvalidOperationException>(() => MeshReductionPass.ValidateSettings(settings));
        }
    }
}
