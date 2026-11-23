using System;
using System.Collections.Generic;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.ObjectOptimization;
using NUnit.Framework;
using UnityEngine;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        [Test]
        public void ObjectAnalysisSnapshotSharesReferencesOnlyUntilTheNextRewrite()
        {
            var settings = ObjectSettings();
            var snapshot = new ObjectAnalysisSnapshot(root, new Dictionary<object, VirtualAnimatorController>(), null,
                settings, Array.Empty<string>(), new ShaderPropertyUsage());
            var first = PolicyChild("First anchor").transform;
            var second = PolicyChild("Second anchor").transform;
            renderer.probeAnchor = first;
            Assert.That(snapshot.Dependencies(false).ReferenceOwners[first], Does.Contain(renderer));
            Assert.That(snapshot.Dependencies(true).ReferenceOwners[first], Does.Contain(renderer));
            snapshot.Rewrite(() => { renderer.probeAnchor = second; return 1; });
            Assert.That(snapshot.Dependencies(false).ReferenceOwners.ContainsKey(first), Is.False);
            Assert.That(snapshot.Dependencies(true).ReferenceOwners[second], Does.Contain(renderer));
        }

        [Test]
        public void ObjectAnalysisSnapshotsReuseNoOpsAndInvalidateOnMutationOrException()
        {
            var settings = ObjectSettings();
            var controllers = new Dictionary<object, VirtualAnimatorController>();
            var shaders = new ShaderPropertyUsage();
            var snapshot = new ObjectAnalysisSnapshot(root, controllers, null, settings, Array.Empty<string>(), shaders);
            var index = snapshot.Index;
            var transforms = snapshot.Dependencies(false);
            var objects = snapshot.Dependencies(true);
            var mutations = snapshot.Mutations;
            Assert.That(objects, Is.Not.SameAs(transforms));
            Assert.That(snapshot.Rewrite(() => 0), Is.Zero);
            Assert.That(snapshot.Index, Is.SameAs(index));
            Assert.That(snapshot.Dependencies(false), Is.SameAs(transforms));
            Assert.That(snapshot.Dependencies(true), Is.SameAs(objects));
            Assert.That(snapshot.Mutations, Is.SameAs(mutations));
            GameObject added = null;
            snapshot.Rewrite(() => { added = PolicyChild("Added output"); added.AddComponent<Light>(); return 1; });
            Assert.That(snapshot.Index, Is.Not.SameAs(index));
            Assert.That(snapshot.Mutations, Is.Not.SameAs(mutations));
            Assert.That(snapshot.Dependencies(true).IsLive(added.transform), Is.True);
            index = snapshot.Index;
            Assert.Throws<InvalidOperationException>(() => snapshot.Rewrite(() =>
            {
                UnityEngine.Object.DestroyImmediate(added);
                throw new InvalidOperationException("Failed after editing the hierarchy");
            }));
            Assert.That(snapshot.Index, Is.Not.SameAs(index));
            var expected = AvatarDependencyGraph.Create(root, snapshot.Index, settings, true, shaders);
            foreach (var component in expected.Components)
                Assert.That(snapshot.Dependencies(true).IsLive(component), Is.EqualTo(expected.IsLive(component)));
        }

        [Test]
        public void ObjectAnalysisSnapshotInvalidatesEvenWhenADeletionCallbackDeclines()
        {
            var settings = ObjectSettings();
            var controllers = new Dictionary<object, VirtualAnimatorController>();
            var snapshot = new ObjectAnalysisSnapshot(root, controllers, null, settings, Array.Empty<string>(), new ShaderPropertyUsage());
            var target = PolicyChild("Retained target");
            var index = snapshot.Index;
            settings.DestroyObject = _ => target.AddComponent<Light>();
            Assert.That(snapshot.Rewrite(() => settings.TryDestroy(target) ? 1 : 0), Is.Zero);
            Assert.That(snapshot.Index, Is.Not.SameAs(index));
            Assert.That(snapshot.Dependencies(true).IsLive(target.transform), Is.True);
        }

        [Test]
        public void ObjectCleanupSnapshotRebuildsAfterARealRewrite()
        {
            var settings = ObjectSettings();
            var controllers = new Dictionary<object, VirtualAnimatorController>();
            var shaders = new ShaderPropertyUsage();
            var snapshot = new ObjectAnalysisSnapshot(root, controllers, null, settings, Array.Empty<string>(), shaders);
            var unused = PolicyChild("Unused container");
            var index = snapshot.Index;
            Assert.That(UnusedObjectCollection.Apply(root, controllers, null, settings, Array.Empty<string>(),
                new List<string>(), shaders, snapshot), Is.GreaterThan(0));
            Assert.That(unused == null, Is.True);
            Assert.That(snapshot.Index, Is.Not.SameAs(index));
            index = snapshot.Index;
            Assert.That(UnusedObjectCollection.Apply(root, controllers, null, settings, Array.Empty<string>(),
                new List<string>(), shaders, snapshot), Is.Zero);
            Assert.That(snapshot.Index, Is.SameAs(index));
        }
    }
}
