using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.Build;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    [Serializable]
    public class ReferenceTraversalEntry
    {
        public UnityEngine.Object Target;
        public string Text;
    }

    public class ReferenceTraversalFixture : ScriptableObject
    {
        public ReferenceTraversalEntry[] Entries;
        [SerializeReference] public ReferenceTraversalEntry[] Managed;
        public ExposedReference<GameObject>[] Exposed;
        public UnityEngine.Object[] Objects;
        public string Text;
        public float[] Numbers;
        public Vector3[] Vectors;
        public AnimationCurve Curve;
        public Gradient Gradient;
    }

    public sealed partial class BlendShapeOptimizationTests
    {
        [Test]
        public void AnalysisTraversalPreservesNestedManagedAndExposedReferences()
        {
            var fixture = Track(ScriptableObject.CreateInstance<ReferenceTraversalFixture>());
            fixture.Entries = new[] { new ReferenceTraversalEntry { Target = root, Text = new string('x', 4096) } };
            fixture.Managed = new[] { null, new ReferenceTraversalEntry { Target = renderer } };
            fixture.Exposed = new[] { new ExposedReference<GameObject> { defaultValue = root } };
            fixture.Objects = new UnityEngine.Object[] { null, root, renderer };
            fixture.Numbers = new float[1000]; fixture.Vectors = new Vector3[100];
            fixture.Text = new string('y', 4096); fixture.Curve = AnimationCurve.Linear(0, 0, 1, 1); fixture.Gradient = new Gradient();
            using var serialized = new SerializedObject(fixture);
            var expected = new Dictionary<string, UnityEngine.Object>();
            using (var iterator = serialized.GetIterator())
                while (iterator.Next(true))
                    if (iterator.propertyType == SerializedPropertyType.ObjectReference)
                        expected.Add(iterator.propertyPath, iterator.objectReferenceValue);
            var actual = SerializedReferences.Properties(serialized).ToDictionary(p => p.propertyPath, p => p.objectReferenceValue);
            Assert.That(actual, Is.EquivalentTo(expected));
            Assert.That(actual.Values, Does.Contain(root)); Assert.That(actual.Values, Does.Contain(renderer));
            Assert.That(actual.Keys.Any(k => k.StartsWith("Managed.", StringComparison.Ordinal)), Is.True);
            Assert.That(actual.Keys.Any(k => k.StartsWith("Exposed.", StringComparison.Ordinal)), Is.True);
        }

        [Test]
        public void AnalysisTraversalBindingSnapshotKeepsRootAliasesAndAmbiguitySeparate()
        {
            var top = PolicyChild("Renamed").AddComponent<Light>();
            var owner = PolicyChild("Nested").AddComponent<Animator>();
            var nested = PolicyChild("Shared", owner.transform).AddComponent<Light>();
            var duplicate = PolicyChild("Shared", owner.transform).AddComponent<Light>();
            var clip = StaticCurve(EditorCurveBinding.FloatCurve("Shared", typeof(Light), "m_Intensity"), 2);
            var clones = new CloneContext(VRChatPlatformAnimatorBindings.Instance);
            var entries = new Dictionary<object, VirtualAnimatorController> {
                [VRCAvatarDescriptor.AnimLayerType.FX] = clones.Clone(Controller(clip)),
                [owner] = clones.Clone(Controller(clip))
            };
            var index = AvatarAnimationIndex.Create(root, entries, new Dictionary<string, string> { ["Shared"] = "Renamed" });
            var use = index.Bindings.Single();
            Assert.That(use.Targets, Is.EquivalentTo(new[] { top.transform, nested.transform, duplicate.transform }));
            Assert.That(use.Roots, Is.EquivalentTo(new[] { root.transform, owner.transform })); Assert.That(use.Complete, Is.False);
            Assert.That(index.ForObject(top).Single(), Is.SameAs(use));
            Assert.That(index.ForObject(nested).Single(), Is.SameAs(use));
        }

        [Test]
        public void AnalysisTraversalBindingObjectsRemainLiveAfterComponentReplacement()
        {
            var obj = PolicyChild("Lamp"); var original = obj.AddComponent<Light>();
            var clip = StaticCurve(EditorCurveBinding.FloatCurve("Lamp", typeof(Light), "m_Intensity"), 2);
            var index = FrameIndex(clip); var use = index.Bindings.Single();
            UnityEngine.Object.DestroyImmediate(original);
            var replacement = obj.AddComponent<Light>();
            Assert.That(use.Objects().Single(), Is.SameAs(replacement));
            obj.name = "Moved";
            Assert.That(FrameIndex(clip).Bindings.Single().Targets, Is.Empty);
        }

        [Test]
        public void AnalysisTraversalShaderFactsExpireAtSourceAndPluginBoundaries()
        {
            var facts = new OptimizationAnalysis(); var first = facts.Shaders;
            facts.Invalidate(AnalysisDomain.Animation | AnalysisDomain.Hierarchy | AnalysisDomain.Meshes | AnalysisDomain.Physics);
            Assert.That(facts.Shaders, Is.SameAs(first));
            facts.Invalidate(AnalysisDomain.ShaderSources);
            Assert.That(facts.Shaders, Is.Not.SameAs(first));
            var second = facts.Shaders; facts.Invalidate(AnalysisDomain.All);
            Assert.That(facts.Shaders, Is.Not.SameAs(second));
        }
    }
}
