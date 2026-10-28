using System;
using nadena.dev.ndmf;
using Noname.AvatarTools.Build;
using NUnit.Framework;
using VRC.SDK3.Avatars.Components;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        [Test]
        public void AnalysisInvalidationTracksTheDomainsReadByEachFact()
        {
            var cache = new OptimizationAnalysis(); int animationReads = 0, shaderReads = 0;
            Func<int> animation = () => cache.Get("animation", AnalysisDomain.Animation | AnalysisDomain.Hierarchy, () => ++animationReads);
            Func<int> shader = () => cache.Get("shader", AnalysisDomain.ShaderSources, () => ++shaderReads);
            Assert.That(animation(), Is.EqualTo(1)); Assert.That(shader(), Is.EqualTo(1));
            cache.Invalidate(AnalysisDomain.Materials);
            Assert.That(animation(), Is.EqualTo(1)); Assert.That(shader(), Is.EqualTo(1));
            cache.Invalidate(AnalysisDomain.Hierarchy);
            Assert.That(animation(), Is.EqualTo(2)); Assert.That(shader(), Is.EqualTo(1));
            cache.Invalidate(AnalysisDomain.All);
            Assert.That(animation(), Is.EqualTo(3)); Assert.That(shader(), Is.EqualTo(2));
        }

        [Test]
        public void FailedOptimizationStepCannotLeaveAStaleFactInTheBuild()
        {
            var context = new BuildContext(root, null); var cache = context.GetState<OptimizationAnalysis>(); int reads = 0;
            Func<int> read = () => cache.Get("mesh", AnalysisDomain.Meshes, () => ++reads);
            Assert.That(read(), Is.EqualTo(1));
            Assert.Throws<InvalidOperationException>(() => OptimizationSession.Step(context, "failed mesh write", AnalysisDomain.Meshes,
                _ => throw new InvalidOperationException("test")));
            Assert.That(read(), Is.EqualTo(2));
        }

        [Test]
        public void ExternalBoundariesReleaseAnimationServicesAfterExceptionsAndInvalidateFacts()
        {
            root.AddComponent<VRCAvatarDescriptor>(); var context = new BuildContext(root, null);
            var cache = context.GetState<OptimizationAnalysis>(); int reads = 0;
            Func<int> read = () => cache.Get("source", AnalysisDomain.ShaderSources, () => ++reads);
            Assert.Throws<InvalidOperationException>(() => OptimizationSession.RunBoundary(context, "first", () =>
            {
                using var first = OptimizationSession.Acquire(context);
                using var second = OptimizationSession.Acquire(context);
                Assert.That(first.Services, Is.SameAs(second.Services));
                Assert.That(read(), Is.EqualTo(1));
                throw new InvalidOperationException("test");
            }));
            OptimizationSession.RunBoundary(context, "second", () =>
            {
                using var next = OptimizationSession.Acquire(context);
                Assert.That(next.Services, Is.Not.Null);
                Assert.That(read(), Is.EqualTo(2));
            });
            Assert.That(read(), Is.EqualTo(3));
        }
    }
}
