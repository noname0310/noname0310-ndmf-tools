using System;
using System.Linq;
using Noname.AvatarTools.Build;
using NUnit.Framework;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        [Test]
        public void FailureCandidateUsesArgumentTypeInStackAndRetainsAmbiguousInstances()
        {
            var first = root.AddComponent<DependencyAuthoringFixture>();
            var child = Track(new GameObject("Another")); child.transform.SetParent(root.transform);
            var second = child.AddComponent<DependencyAuthoringFixture>();
            string stack = "  at Example.BuildPass.Process (UnityEngine.SkinnedMeshRenderer renderer, " + typeof(DependencyAuthoringFixture).FullName + " tuning) [0x0] in file.cs:42";
            var candidates = BuildFailureCandidates.Find(root, new[] { new AvatarBuildErrorTrace { StackTrace = stack } });
            Assert.That(candidates.Select(c => c.Component), Is.EquivalentTo(new Component[] { first, second }));
            Assert.That(candidates.All(c => c.MatchingInstances == 2 && c.Evidence.Contains("Process")), Is.True);
        }

        [Test]
        public void FailureCandidateIgnoresMessageTextRuntimeTypesAndAltoInfrastructure()
        {
            root.AddComponent<DependencyAuthoringFixture>(); root.AddComponent<ALTOBuildFilter>();
            var errors = new[] { new AvatarBuildErrorTrace { Message = typeof(DependencyAuthoringFixture).FullName,
                StackTrace = "Example.Pass.Run (UnityEngine.SkinnedMeshRenderer renderer, Noname.AvatarTools.ALTOBuildFilter filter)\n" +
                    "Please remove " + typeof(DependencyAuthoringFixture).FullName + " (not a frame)" } };
            Assert.That(BuildFailureCandidates.Find(root, errors), Is.Empty);
        }

        [Test]
        public void FailureCandidateMapsDuplicateNamesAndExactComponentIndexToFreshClone()
        {
            var first = Track(new GameObject("Same")); first.transform.SetParent(root.transform);
            var second = Track(new GameObject("Same")); second.transform.SetParent(root.transform);
            first.AddComponent<DependencyAuthoringFixture>(); second.AddComponent<DependencyAuthoringFixture>();
            var selected = second.AddComponent<DependencyAuthoringFixture>();
            var clone = Track(UnityEngine.Object.Instantiate(root));
            var mapped = BuildFailureCandidates.InClone(selected, root, clone);
            Assert.That(mapped, Is.SameAs(clone.transform.GetChild(1).GetComponents<DependencyAuthoringFixture>()[1]));
            Assert.That(mapped, Is.Not.SameAs(selected));
        }

        [Test]
        public void FailureCandidateAdditionIsExplicitAndRespectsScopeAndPlatform()
        {
            root.AddComponent<VRCAvatarDescriptor>();
            var filter = root.AddComponent<ALTOBuildFilter>(); filter.ApplyOnPlatforms = OptimizationPlatforms.All;
            var candidate = root.AddComponent<DependencyAuthoringFixture>();
            BuildFailureCandidates.Validate(filter, candidate);
            Assert.That(filter.ExcludedComponents, Is.Empty);
            BuildFailureCandidates.AddSelected(filter, candidate);
            Assert.That(filter.ExcludedComponents, Is.EqualTo(new Component[] { candidate }));
            Assert.Throws<InvalidOperationException>(() => BuildFailureCandidates.AddSelected(filter, candidate));
            filter.ExcludedComponents.Clear(); filter.ApplyOnPlatforms = OptimizationPlatforms.None;
            Assert.Throws<InvalidOperationException>(() => BuildFailureCandidates.Validate(filter, candidate));
        }

        [Test]
        public void FailureCandidateTrialRebuildsFreshCopyAndLeavesOriginalFilterUntouched()
        {
            root.AddComponent<VRCAvatarDescriptor>(); root.AddComponent<Animator>();
            var filter = root.AddComponent<ALTOBuildFilter>(); filter.ApplyOnPlatforms = OptimizationPlatforms.All;
            var fixture = root.AddComponent<BuildFailureFixture>();
            AvatarBuildAnalysisResult baseline = null, trial = null;
            bool previous = UnityEngine.TestTools.LogAssert.ignoreFailingMessages;
            try
            {
                UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;
                baseline = AvatarBuildAnalysis.Analyze(root);
                Assert.That(baseline.Success, Is.False);
                var candidates = BuildFailureCandidates.Find(root, baseline.ErrorTraces);
                Assert.That(candidates.Select(c => c.Component), Is.EqualTo(new Component[] { fixture }));
                UnityEngine.TestTools.LogAssert.ignoreFailingMessages = previous;
                trial = BuildFailureCandidates.Test(filter, fixture);
                Assert.That(trial.Success, Is.True, trial.Error);
                Assert.That(trial.Errors, Is.Empty);
                Assert.That(fixture != null, Is.True); Assert.That(filter.ExcludedComponents, Is.Empty);
                Assert.That(root.GetComponent<BuildFailureFixture>(), Is.SameAs(fixture));
            }
            finally
            {
                UnityEngine.TestTools.LogAssert.ignoreFailingMessages = previous;
                foreach (var result in new[] { baseline, trial })
                    if (result != null && System.IO.File.Exists(result.ReportPath)) System.IO.File.Delete(result.ReportPath);
            }
        }
    }
}
