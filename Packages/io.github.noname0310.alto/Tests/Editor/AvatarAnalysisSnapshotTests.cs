using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.Build;
using NUnit.Framework;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        [Test]
        public void AvatarSnapshotSharesOnlyEquivalentQueryContexts()
        {
            var cache = new OptimizationAnalysis();
            var entries = new Dictionary<object, VirtualAnimatorController>();
            var paths = new Dictionary<string, string> { ["Before"] = "After" };
            var preserved = new HashSet<string> { "External" };
            var snapshot = cache.Avatar(root, entries, paths, preserved);
            Assert.That(cache.Avatar(root, new Dictionary<object, VirtualAnimatorController>(entries), new Dictionary<string, string>(paths), new[] { "External", "External" }), Is.SameAs(snapshot));
            Assert.That(cache.Avatar(root, entries, null, preserved), Is.Not.SameAs(snapshot));
            paths["Before"] = "Elsewhere";
            Assert.That(cache.Avatar(root, entries, paths, preserved), Is.Not.SameAs(snapshot));
            Assert.That(snapshot.Paths["Before"], Is.EqualTo("After"));
            preserved.Add("Another external input");
            Assert.That(cache.Avatar(root, entries, snapshot.Paths, preserved), Is.Not.SameAs(snapshot));
            Assert.That(snapshot.Preserved, Is.EquivalentTo(new[] { "External" }));
        }

        [Test]
        public void AvatarSnapshotDoesNotConfuseChildAndPlayableControllerOwners()
        {
            root.AddComponent<VRCAvatarDescriptor>();
            var child = PolicyChild("Private animator").AddComponent<Animator>();
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(Clip()));
            var cache = new OptimizationAnalysis();
            var entries = new Dictionary<object, VirtualAnimatorController> { [VRCAvatarDescriptor.AnimLayerType.FX] = controller };
            var snapshot = cache.Avatar(root, entries, null, null);
            entries.Clear(); entries.Add(child, controller);
            var other = cache.Avatar(root, entries, null, null);
            Assert.That(other, Is.Not.SameAs(snapshot));
            Assert.That(other.Index.ControllerEntries.Keys, Does.Contain(child));
            Assert.That(snapshot.Index.ControllerEntries.Keys, Does.Contain(VRCAvatarDescriptor.AnimLayerType.FX));
        }

        [Test]
        public void AvatarSnapshotSharesReachabilityWithItsIndexAndRefreshesNativeReferences()
        {
            root.AddComponent<VRCAvatarDescriptor>();
            var cache = new OptimizationAnalysis();
            var entries = new Dictionary<object, VirtualAnimatorController>();
            var first = PolicyChild("First").transform;
            var second = PolicyChild("Second").transform;
            renderer.probeAnchor = first;
            var snapshot = cache.Avatar(root, entries, null, null);
            Assert.That(snapshot.Index.Reachability, Is.SameAs(snapshot.Reachability));
            Assert.That(snapshot.References.Get(renderer).Any(r => r.Value == first), Is.True);
            var index = snapshot.Index;
            cache.Invalidate(AnalysisDomain.None);
            Assert.That(cache.Avatar(root, entries, null, null).Index, Is.SameAs(index));
            renderer.probeAnchor = second;
            cache.Invalidate(AnalysisDomain.Materials | AnalysisDomain.Hierarchy);
            var after = cache.Avatar(root, entries, null, null);
            Assert.That(after.Index, Is.Not.SameAs(index));
            Assert.That(after.References.Get(renderer).Any(r => r.Value == second), Is.True);
            Assert.That(after.References.Get(renderer).Any(r => r.Value == first), Is.False);
        }

        [TestCase("Mutation")] [TestCase("Exception")] [TestCase("DeclinedDeletion")]
        public void AvatarSnapshotObjectTransactionsInvalidateEverySharedConsumer(string boundary)
        {
            var cache = new OptimizationAnalysis();
            var entries = new Dictionary<object, VirtualAnimatorController>();
            var settings = ObjectSettings();
            var target = PolicyChild("Target");
            var shared = cache.Avatar(root, entries, null, null);
            var snapshot = new ObjectAnalysisSnapshot(root, entries, null, settings, null, cache.Shaders, shared);
            var index = snapshot.Index;
            var shaders = cache.Shaders;
            Assert.That(snapshot.Rewrite(() => 0), Is.Zero);
            Assert.That(cache.Avatar(root, entries, null, null).Index, Is.SameAs(index));
            if (boundary == "Exception")
                Assert.Throws<InvalidOperationException>(() => snapshot.Rewrite(() =>
                { target.AddComponent<Light>(); throw new InvalidOperationException("Changed output"); }));
            else if (boundary == "DeclinedDeletion")
            {
                settings.DestroyObject = _ => target.AddComponent<Light>();
                Assert.That(snapshot.Rewrite(() => settings.TryDestroy(target) ? 1 : 0), Is.Zero);
            }
            else snapshot.Rewrite(() => { target.AddComponent<Light>(); return 1; });
            Assert.That(cache.Avatar(root, entries, null, null).Index, Is.Not.SameAs(index));
            Assert.That(ReferenceEquals(cache.Shaders, shaders), Is.EqualTo(boundary == "Mutation"),
                "An ordinary avatar edit leaves shader source snapshots valid; external callbacks and failures do not.");
            Assert.That(snapshot.Dependencies(true).IsLive(target.transform), Is.True);
            // The lexical consumer may continue after its epoch was retired.
            // A second mutation must invalidate its newly captured facts as well.
            index = snapshot.Index;
            snapshot.Rewrite(() => { UnityEngine.Object.DestroyImmediate(target.GetComponent<Light>()); return 1; });
            Assert.That(snapshot.Index, Is.Not.SameAs(index));
            Assert.That(snapshot.Dependencies(true).IsLive(target.transform), Is.False);
        }
    }
}
