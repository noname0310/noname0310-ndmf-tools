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

    [Serializable]
    public class ReferenceFreeTraversalEntry
    {
        public string Name;
        public float Value;
        public List<int> Data;
        public object RuntimeCache;
    }

    [Serializable]
    public class ReferenceDerivedTraversalEntry : ReferenceFreeTraversalEntry
    {
        [SerializeField] private UnityEngine.Object target;
        public ReferenceDerivedTraversalEntry(UnityEngine.Object target) { this.target = target; }
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
        public ReferenceFreeTraversalEntry[] ScalarEntries;
        [SerializeReference] public ReferenceFreeTraversalEntry Polymorphic;
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
            fixture.ScalarEntries = Enumerable.Range(0, 100).Select(i => new ReferenceFreeTraversalEntry {
                Name = "Scalar " + i, Value = i, Data = new List<int> { i, i + 1 }, RuntimeCache = root
            }).ToArray();
            fixture.Polymorphic = new ReferenceDerivedTraversalEntry(renderer);
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
            Assert.That(actual.Keys.Any(k => k.StartsWith("Polymorphic.", StringComparison.Ordinal)), Is.True);
            Assert.That(SerializedReferenceSchema.ReferenceFreeFields(typeof(ReferenceTraversalFixture)), Does.Contain("ScalarEntries"));
            Assert.That(SerializedReferenceSchema.ReferenceFreeFields(typeof(ReferenceTraversalFixture)), Does.Not.Contain("Polymorphic"));
        }

        [Test]
        public void ReferenceTraversalSchemaDoesNotCacheInstanceContentsOrEmptyArrays()
        {
            var fixture = Track(ScriptableObject.CreateInstance<ReferenceTraversalFixture>());
            fixture.Entries = Array.Empty<ReferenceTraversalEntry>();
            using var serialized = new SerializedObject(fixture);
            var before = SerializedReferences.Properties(serialized).Select(p => p.objectReferenceValue).ToArray();
            Assert.That(before, Has.No.Member(root));
            fixture.Entries = new[] { new ReferenceTraversalEntry { Target = root } };
            fixture.Polymorphic = new ReferenceDerivedTraversalEntry(renderer);
            serialized.Update();
            var after = SerializedReferences.Properties(serialized).Select(p => p.objectReferenceValue).ToArray();
            Assert.That(after, Does.Contain(root));
            Assert.That(after, Does.Contain(renderer));
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

        [TestCase(false)] [TestCase(true)]
        public void DependencyTraversalRestoresSharedInputsBetweenNestedAndSiblingBranches(bool normalized)
        {
            var a = Clip(); a.name = "Traversal A";
            var b = Clip(); b.name = "Traversal B";
            var c = Clip(); c.name = "Traversal C";
            var innerController = TreeController(("SharedWeight", a), ("InnerOnly", b));
            var inner = innerController.layers[0].stateMachine.defaultState.motion;
            using (var serialized = new SerializedObject(inner))
            { serialized.FindProperty("m_NormalizedBlendValues").boolValue = normalized; serialized.ApplyModifiedPropertiesWithoutUndo(); }
            var source = TreeController(("SharedWeight", inner), ("SiblingOnly", c), ("Second", inner));
            source.AddParameter("InnerOnly", AnimatorControllerParameterType.Float);
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var graph = AnimatorParameterDependencies.Collect(root, new[] { controller });
            var names = new HashSet<string> { "SharedWeight", "InnerOnly", "SiblingOnly", "Second" };
            string[] Inputs(string name) => graph.MotionInputs(graph.Clips.Single(clip => clip.Name == name)).Where(names.Contains).ToArray();
            Assert.That(Inputs(a.name), Is.EquivalentTo(normalized
                ? new[] { "SharedWeight", "InnerOnly", "Second" } : new[] { "SharedWeight", "Second" }));
            Assert.That(Inputs(b.name), Is.EquivalentTo(new[] { "SharedWeight", "InnerOnly", "Second" }));
            Assert.That(Inputs(c.name), Is.EquivalentTo(new[] { "SiblingOnly" }), "Scratch state from a prior branch must not become a sibling dependency.");
        }
    }
}
