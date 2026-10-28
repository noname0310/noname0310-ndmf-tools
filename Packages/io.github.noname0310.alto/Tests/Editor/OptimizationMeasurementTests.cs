using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using nadena.dev.ndmf;
using Noname.AvatarTools.AnimatorOptimization;
using Noname.AvatarTools.Build;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using VRC.SDK3.Avatars.Components;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        [TestCase("None", false)]
        [TestCase("BlendShapes", true)]
        [TestCase("Animator", true)]
        [TestCase("DisabledBlendShapes", false)]
        [TestCase("DisabledAnimator", false)]
        public void StandaloneOptimizerMeasurementsRequireAnEnabledConfiguration(string component, bool measured)
        {
            root.AddComponent<VRCAvatarDescriptor>();
            if (component.EndsWith("BlendShapes", StringComparison.Ordinal))
                root.AddComponent<ALTOTraceAndOptimizeBlendShapes>().enabled = measured;
            if (component.EndsWith("Animator", StringComparison.Ordinal))
                root.AddComponent<ALTOTraceAndOptimizeAnimator>().enabled = measured;
            var context = new BuildContext(root, null);
            ALTOPlugin.Capture(context);
            AnimatorOptimizationPass.Capture(context);
            OptimizationSession.Step(context, "configuration fixture", AnalysisDomain.None, _ => { });
            var report = context.GetState<OptimizationBuildReport>();
            Assert.That(report.Input != null, Is.EqualTo(measured));
            Assert.That(report.Measurements.Count, Is.EqualTo(measured ? 1 : 0));
            Assert.That(report.Timeline != null, Is.EqualTo(measured));
            if (measured)
                Assert.That(report.Measurements.Single().Completed && report.Measurements.Single().Succeeded, Is.True);
        }

        [Test]
        public void FailedNestedMeasurementsKeepInclusiveTimingAndReleaseTheirParent()
        {
            var context = new BuildContext(root, null);
            var report = context.GetState<OptimizationBuildReport>(); report.Initialize(root, "PC");
            Assert.Throws<InvalidOperationException>(() => OptimizationSession.RunBoundary(context, "first", () =>
            {
                OptimizationSession.Step(context, "successful child", AnalysisDomain.None, _ => { });
                OptimizationSession.Step(context, "failed child", AnalysisDomain.Meshes,
                    _ => throw new InvalidOperationException("fixture failure"));
            }));
            OptimizationSession.RunBoundary(context, "next", () => { });

            var samples = report.Measurements;
            Assert.That(samples.Select(s => s.ParentIndex), Is.EqualTo(new[] { -1, 0, 0, -1 }));
            Assert.That(samples.All(s => s.Completed), Is.True);
            Assert.That(samples.Select(s => s.Succeeded), Is.EqualTo(new[] { false, true, false, true }));
            Assert.That(report.Timeline.Current, Is.EqualTo(-1));
            foreach (var sample in samples)
            {
                Assert.That(sample.Milliseconds, Is.GreaterThanOrEqualTo(0));
                Assert.That(sample.Before, Is.Not.Null);
                Assert.That(sample.After, Is.Not.Null);
                if (sample.ParentIndex < 0) continue;
                var parent = samples[sample.ParentIndex];
                Assert.That(sample.StartMilliseconds, Is.GreaterThanOrEqualTo(parent.StartMilliseconds));
                Assert.That(sample.StartMilliseconds + sample.Milliseconds,
                    Is.LessThanOrEqualTo(parent.StartMilliseconds + parent.Milliseconds + 0.000001));
            }
            Assert.That(samples[3].StartMilliseconds, Is.GreaterThanOrEqualTo(samples[0].StartMilliseconds + samples[0].Milliseconds));
        }

        [Test]
        public void FailedAnalysisSerializesMeasurementsPublishedBeforeTheFailure()
        {
            root.AddComponent<VRCAvatarDescriptor>();
            var scene = root.scene;
            int originalRoots = scene.rootCount;
            LogAssert.Expect(LogType.Exception, new Regex("InvalidOperationException: Measured analysis failure"));
            var result = AvatarBuildAnalysis.Analyze(root, clone =>
            {
                var context = new BuildContext(clone, null);
                context.GetState<OptimizationBuildReport>().Initialize(clone, "PC");
                OptimizationSession.RunBoundary(context, "failed preparation", () =>
                    OptimizationSession.Step(context, "failed operation", AnalysisDomain.Meshes,
                        _ => throw new InvalidOperationException("Measured analysis failure")));
            });
            try
            {
                var saved = JsonUtility.FromJson<AvatarBuildAnalysisResult>(File.ReadAllText(result.ReportPath));
                Assert.That(saved.Success, Is.False);
                Assert.That(saved.Error, Does.Contain("Measured analysis failure"));
                Assert.That(saved.Measurements.Select(s => s.Name), Is.EqualTo(new[] { "failed preparation", "failed operation" }));
                Assert.That(saved.Measurements.Select(s => s.ParentIndex), Is.EqualTo(new[] { -1, 0 }));
                Assert.That(saved.Measurements.All(s => s.Completed && !s.Succeeded && s.Before != null && s.After != null), Is.True);
                Assert.That(saved.AfterCleanup, Is.Not.Null);
                Assert.That(saved.MeasurementNotes, Does.Contain("entire Editor"));
                Assert.That(scene.rootCount, Is.EqualTo(originalRoots));
                Assert.That(root != null, Is.True);
            }
            finally { if (File.Exists(result.ReportPath)) File.Delete(result.ReportPath); }
        }
    }
}
