using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.Meshes;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.Contact.Components;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        [TestCase((int)AnalysisDomain.Materials)]
        [TestCase((int)AnalysisDomain.Meshes)]
        [TestCase((int)AnalysisDomain.ShaderSources)]
        public void IncrementalAnalysisRetainsParameterProofsButExpiresOutputDefaults(int mutationValue)
        {
            var mutation = (AnalysisDomain)mutationValue;
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(Clip()));
            var entries = new Dictionary<object, VirtualAnimatorController> { [VRCAvatarDescriptor.AnimLayerType.FX] = controller };
            var facts = new OptimizationAnalysis();
            var snapshot = facts.Avatar(root, entries, null, Array.Empty<string>());
            var index = snapshot.Index; var reachability = snapshot.Reachability;
            var constants = index.ConstantOutputs; var materials = index.MaterialInputs;
            facts.Invalidate(mutation);
            var after = facts.Avatar(root, entries, null, Array.Empty<string>());
            Assert.That(after.Index, Is.SameAs(index));
            Assert.That(after.Reachability, Is.SameAs(reachability));
            Assert.That(after.Index.ConstantOutputs, Is.Not.SameAs(constants));
            Assert.That(after.Index.MaterialInputs, Is.Not.SameAs(materials));
        }

        [Test]
        public void IncrementalAnalysisRefreshesBothAddedAndRemovedSensorInputs()
        {
            root.AddComponent<VRCAvatarDescriptor>();
            var source = Controller(Clip()); source.AddParameter("Sensor", AnimatorControllerParameterType.Float);
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var entries = new Dictionary<object, VirtualAnimatorController> { [VRCAvatarDescriptor.AnimLayerType.FX] = controller };
            var facts = new OptimizationAnalysis();
            AvatarAnalysisSnapshot Read() => facts.Avatar(root, entries, null, Array.Empty<string>());
            Assert.That(Read().Reachability.Values("Sensor").TryGetConstant(out _), Is.True);
            var receiver = PolicyChild("Sensor").AddComponent<VRCContactReceiver>(); receiver.parameter = "Sensor";
            facts.Invalidate(AnalysisDomain.Physics);
            Assert.That(Read().Reachability.Values("Sensor").TryGetConstant(out _), Is.False);
            UnityEngine.Object.DestroyImmediate(receiver);
            facts.Invalidate(AnalysisDomain.Physics);
            Assert.That(Read().Reachability.Values("Sensor").TryGetConstant(out _), Is.True);
        }

        [Test]
        public void IncrementalAnalysisRefreshesBindingPathsAndProtectedInputSelection()
        {
            var light = PolicyChild("Lamp").AddComponent<Light>();
            var source = Controller(StaticCurve(EditorCurveBinding.FloatCurve("Lamp", typeof(Light), "m_Intensity"), 1));
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var entries = new Dictionary<object, VirtualAnimatorController> { [VRCAvatarDescriptor.AnimLayerType.FX] = controller };
            var facts = new OptimizationAnalysis();
            var before = facts.Avatar(root, entries, null, Array.Empty<string>());
            Assert.That(before.Index.ForObject(light).Count, Is.EqualTo(1));
            light.name = "Renamed";
            facts.Invalidate(AnalysisDomain.Hierarchy);
            Assert.That(facts.Avatar(root, entries, null, Array.Empty<string>()).Index.ForObject(light), Is.Empty);
            Assert.That(facts.Avatar(root, entries, null, new[] { "External" }), Is.Not.SameAs(before));
            var current = facts.Avatar(root, entries, null, Array.Empty<string>());
            facts.Invalidate(AnalysisDomain.All);
            Assert.That(facts.Avatar(root, entries, null, Array.Empty<string>()), Is.Not.SameAs(current));
        }

        [TestCase(false)] [TestCase(true)]
        public void IncrementalMeshMutationRefreshesNativeOutputLivenessInBothDirections(bool preserve)
        {
            var present = Mesh("NativeSmile"); NativeFixture();
            var source = Controller(Clip()); source.AddParameter("Weight", AnimatorControllerParameterType.Float);
            var control = Track(source.layers[0].stateMachine.defaultState.AddStateMachineBehaviour<VRCAnimatorFaceTrackingBlendshapesSet>());
            control.blendSets ??= new();
            control.blendSets.Add(new() { type = VRC.SDKBase.VRC_AnimatorFaceTrackingBlendshapesSet.SourceType.Parameter,
                sourceParameter = "Weight", blendshapesSetName = "Face" });
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var entries = new Dictionary<object, VirtualAnimatorController> { [VRCAvatarDescriptor.AnimLayerType.FX] = controller };
            var preserved = preserve ? new[] { "Weight" } : Array.Empty<string>();
            var facts = new OptimizationAnalysis();
            var snapshot = facts.Avatar(root, entries, null, preserved);
            var index = snapshot.Index; var reachability = snapshot.Reachability;
            Assert.That(snapshot.Dependencies(true).ExpandObservable(), Does.Contain("Weight"));
            Assert.That(snapshot.Dependencies(false).ExpandObservable(), Does.Contain("Weight"));
            renderer.sharedMesh = Mesh();
            facts.Invalidate(AnalysisDomain.Meshes);
            Assert.That(snapshot.Dependencies(true).ExpandObservable(), Does.Not.Contain("Weight"));
            Assert.That(snapshot.Dependencies(false).ExpandObservable(), Does.Not.Contain("Weight"));
            Assert.That(snapshot.Index, preserve ? Is.Not.SameAs(index) : Is.SameAs(index));
            Assert.That(snapshot.Reachability, Is.SameAs(reachability));
            renderer.sharedMesh = present;
            facts.Invalidate(AnalysisDomain.Meshes);
            Assert.That(snapshot.Dependencies(true).ExpandObservable(), Does.Contain("Weight"));
            Assert.That(snapshot.Dependencies(false).ExpandObservable(), Does.Contain("Weight"));
            var rebuilt = new OptimizationAnalysis().Avatar(root, entries, null, preserved);
            Assert.That(snapshot.Dependencies(true).ExpandObservable(), Is.EquivalentTo(rebuilt.Dependencies(true).ExpandObservable()));
        }

        [Test]
        public void IncrementalReferenceOwnersExpireIndependentlyAndKeepNullSlotsObservable()
        {
            var first = Track(ScriptableObject.CreateInstance<ReferenceTraversalFixture>());
            var second = Track(ScriptableObject.CreateInstance<ReferenceTraversalFixture>());
            var snapshot = new SerializedReferenceSnapshot();
            var initial = snapshot.Get(first); var other = snapshot.Get(second);
            first.Objects = new UnityEngine.Object[] { renderer };
            snapshot.Invalidate(first);
            Assert.That(snapshot.Get(first).Any(p => p.Value == renderer), Is.True);
            Assert.That(snapshot.Get(second), Is.SameAs(other));
            first.Objects = Array.Empty<UnityEngine.Object>();
            snapshot.Invalidate(AnalysisDomain.Hierarchy);
            Assert.That(snapshot.Get(first), Is.Empty);
            Assert.That(SerializedReferenceSchema.ManagedFieldsAreReferenceFree(typeof(ReferenceTraversalFixture)), Is.False);
        }

        [Test]
        public void IncrementalMaterialDefaultsRecheckAPreviouslyUnobservedUniform()
        {
            RequireD3DAnalysis(); Mesh();
            var material = UniformProofMaterial(renderer);
            var facts = new OptimizationAnalysis();
            var snapshot = facts.Avatar(root, new Dictionary<object, VirtualAnimatorController>(), null, Array.Empty<string>());
            var index = snapshot.Index;
            var changing = new HashSet<string> { "_Target" };
            Assert.That(facts.Shaders.MayRead(material, renderer, index, "_Target", changing, BuildTarget.StandaloneWindows64), Is.False);
            material.SetFloat("_Gate", 1);
            facts.Invalidate(AnalysisDomain.Materials);
            Assert.That(snapshot.Index, Is.SameAs(index));
            Assert.That(facts.Shaders.MayRead(material, renderer, snapshot.Index, "_Target", changing, BuildTarget.StandaloneWindows64), Is.True);
            material.SetFloat("_Gate", 0);
            facts.Invalidate(AnalysisDomain.Materials);
            Assert.That(facts.Shaders.MayRead(material, renderer, snapshot.Index, "_Target", changing, BuildTarget.StandaloneWindows64), Is.False);
        }

        [Test]
        public void IncrementalReferenceSchemaMatchesTheNativeTraversalForParameterDrivers()
        {
            var driver = Track(ScriptableObject.CreateInstance<VRCAvatarParameterDriver>());
            using var serialized = new SerializedObject(driver);
            var expected = SerializedReferences.Properties(serialized)
                .Where(p => p.propertyPath != "m_GameObject" && p.propertyPath != "m_Script" && p.objectReferenceValue != null)
                .Select(p => (p.propertyPath, p.objectReferenceValue)).ToArray();
            Assert.That(SerializedReferenceSchema.ManagedFieldsAreReferenceFree(driver.GetType()), Is.True);
            Assert.That(SerializedReferenceSnapshot.Read(driver).Select(p => (p.Path, p.Value)), Is.EquivalentTo(expected));
        }

        [Test]
        public void IncrementalMutationOwnersRemainPreciseUntilAnUnknownWriteOrFailure()
        {
            var first = Track(ScriptableObject.CreateInstance<ReferenceTraversalFixture>());
            var second = Track(ScriptableObject.CreateInstance<ReferenceTraversalFixture>());
            var facts = new OptimizationAnalysis();
            var snapshot = facts.Avatar(root, new Dictionary<object, VirtualAnimatorController>(), null, Array.Empty<string>());
            var references = snapshot.References;
            references.Get(first); var untouched = references.Get(second);
            first.Objects = new UnityEngine.Object[] { renderer };
            facts.Invalidate(new OptimizationChanges(1, AnalysisDomain.Materials, new[] { first }));
            Assert.That(snapshot.References, Is.SameAs(references));
            Assert.That(references.Get(first).Any(p => p.Value == renderer), Is.True);
            Assert.That(references.Get(second), Is.SameAs(untouched));
            var known = new OptimizationChanges(1, AnalysisDomain.Materials, new[] { first });
            Assert.That((known + default(OptimizationChanges)).ReferenceOwners, Is.EquivalentTo(new[] { first }));
            Assert.That((known + new OptimizationChanges(1, AnalysisDomain.Animation)).ReferenceOwners, Is.Null);
            var failure = OptimizationWorklist.Stage.WithChanges("unknown failure", AnalysisDomain.All, AnalysisDomain.All,
                () => { second.Objects = new UnityEngine.Object[] { renderer }; throw new InvalidOperationException(); });
            Assert.Throws<InvalidOperationException>(() => OptimizationWorklist.Run(new[] { failure }, invalidateChanges: facts.Invalidate));
            Assert.That(snapshot.References.Get(second).Any(p => p.Value == renderer), Is.True);
        }

        [Test]
        public void IncrementalAnalysisMutationSequencesMatchFreshReconstruction()
        {
            root.AddComponent<VRCAvatarDescriptor>();
            var source = Controller(StaticCurve(EditorCurveBinding.FloatCurve("Lamp", typeof(Light), "m_Intensity"), 1));
            source.AddParameter("Input", AnimatorControllerParameterType.Float);
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var entries = new Dictionary<object, VirtualAnimatorController> { [VRCAvatarDescriptor.AnimLayerType.FX] = controller };
            var facts = new OptimizationAnalysis();
            var light = PolicyChild("Lamp").AddComponent<Light>();
            VRCContactReceiver contact = null;
            var random = new System.Random(73281);
            for (int step = 0; step < 80; step++)
            {
                var before = facts.Avatar(root, entries, null, Array.Empty<string>());
                _ = before.Index; _ = before.Reachability;
                AnalysisDomain changed;
                switch (random.Next(5))
                {
                    case 0:
                        light.name = light.name == "Lamp" ? "Hidden" : "Lamp";
                        changed = AnalysisDomain.Hierarchy; break;
                    case 1:
                        if (contact == null) { contact = PolicyChild("Contact").AddComponent<VRCContactReceiver>(); contact.parameter = "Input"; }
                        else { UnityEngine.Object.DestroyImmediate(contact); contact = null; }
                        changed = AnalysisDomain.Physics; break;
                    case 2: changed = AnalysisDomain.Materials; break;
                    case 3: changed = AnalysisDomain.Meshes; break;
                    default: changed = AnalysisDomain.All; break;
                }
                facts.Invalidate(changed);
                var actual = facts.Avatar(root, entries, null, Array.Empty<string>());
                var expected = new OptimizationAnalysis().Avatar(root, entries, null, Array.Empty<string>());
                Assert.That(actual.Index.ForObject(light).Count, Is.EqualTo(expected.Index.ForObject(light).Count), "Step " + step);
                Assert.That(actual.Index.Bindings.SelectMany(b => b.Targets), Is.EquivalentTo(expected.Index.Bindings.SelectMany(b => b.Targets)), "Step " + step);
                Assert.That(actual.Reachability.Values("Input").Same(expected.Reachability.Values("Input")), Is.True, "Step " + step);
            }
        }

        [Test]
        public void IncrementalShaderDiscoverySharesNativeInventoryWithoutSharingPassSelection()
        {
            RequireD3DAnalysis();
            var previous = AnalysisCost.Observer;
            int captured = 0;
            AnalysisCost.Observer = (name, milliseconds) => { if (name == "Shader discovery/native inventory") captured++; };
            try
            {
                var sources = new ShaderPropertyUsage();
                var imported = sources.GetPrograms(MeshInputShader("UsePassSelected"));
                int first = captured;
                var owner = sources.GetPrograms(MeshInputShader("UsePassOwner"));
                Assert.That(first, Is.GreaterThan(0));
                Assert.That(captured, Is.EqualTo(first), "The shared native shader is inventoried once per source epoch.");
                Assert.That(owner.Passes.Any(p => p.Pass.Name == "INDEX"), Is.True);
                Assert.That(imported.Passes.Any(p => p.Pass.Name == "INDEX"), Is.False);
                Assert.That(sources.MeshInputs.Issue(MeshInputShader("UsePassSelected"), false, BuildTarget.StandaloneWindows64), Is.Null);
                Assert.That(sources.MeshInputs.Issue(MeshInputShader("UsePassOwner"), false, BuildTarget.StandaloneWindows64), Does.Contain("may depend"));
            }
            finally { AnalysisCost.Observer = previous; }
        }

        [Test]
        public void IncrementalShaderImportLayoutsFollowExactSourceAcrossSnapshots()
        {
            const string source = "Shader \"Probe\" { SubShader { UsePass \"Owner/FIRST\" } }";
            var artifacts = new ShaderAnalysisArtifacts();
            var first = artifacts.NativeImports(source);
            Assert.That(first[0][0], Is.EqualTo("Owner/FIRST"));
            Assert.That(artifacts.NativeImports(new string(source.ToCharArray())), Is.SameAs(first));
            Assert.That(artifacts.NativeImports(source.Replace("FIRST", "SECOND"))[0][0], Is.EqualTo("Owner/SECOND"));
            Assert.That(artifacts.NativeImports(source.Replace("UsePass", "#UsePass")), Is.Null);
            Assert.That(artifacts.NativeImports(source), Is.SameAs(first));
        }

        [TestCase(false)] [TestCase(true)]
        public void IncrementalMeshPlanningMatchesUncachedGroupingAndRetainedWriters(bool allowDefaults)
        {
            var pair = MergePair(); pair.Second.sharedMaterial = Track(new Material(pair.First.sharedMaterial));
            pair.First.sharedMaterial.SetFloat("_UvProbe", .2f); pair.Second.sharedMaterial.SetFloat("_UvProbe", .8f);
            var source = TreeController(("GestureLeftWeight", SharedMaterialCurve(pair.First, pair.Second)));
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var candidates = MergeCandidates(new[] { pair.First, pair.Second }, controller);
            var policy = new MeshMergePolicy { AllowAnimatedMaterialDefaultChanges = allowDefaults };
            bool previous = MeshMergePlanning.Reuse;
            try
            {
                MeshMergePlanning.Reuse = false;
                var expected = MeshMergeAnalysis.Group(candidates, true, policy);
                MeshMergePlanning.Reuse = true;
                var planning = new MeshMergePlanning(policy);
                var actual = MeshMergeAnalysis.Group(candidates, true, policy, planning);
                Assert.That(actual.Select(g => g.Select(c => c.Renderer).ToArray()).ToArray(),
                    Is.EqualTo(expected.Select(g => g.Select(c => c.Renderer).ToArray()).ToArray()));
                foreach (var group in actual)
                {
                    var fresh = MeshMaterialAnimationPlan.Create(group, policy);
                    var reused = planning.Plan(group);
                    Assert.That(reused.Issue, Is.EqualTo(fresh.Issue));
                    Assert.That(reused.Retained, Is.EquivalentTo(fresh.Retained));
                    Assert.That(reused.DefaultChanges, Is.EqualTo(fresh.DefaultChanges));
                    Assert.That(planning.Plan(group), Is.SameAs(reused));
                    planning.Clear();
                    Assert.That(planning.Plan(group), Is.Not.SameAs(reused));
                }
            }
            finally { MeshMergePlanning.Reuse = previous; }
        }

        [Test]
        public void IncrementalMeshPlanningRechecksReadersWhenAnotherMemberIntroducesAWritingUniform()
        {
            RequireD3DAnalysis();
            var pair = MergePair();
            var third = UnityEngine.Object.Instantiate(pair.Second.gameObject, root.transform).GetComponent<SkinnedMeshRenderer>();
            third.name = "Third";
            UniformProofMaterial(pair.First, 1); UniformProofMaterial(pair.Second); UniformProofMaterial(third);
            var clip = Clip();
            AnimationUtility.SetEditorCurve(clip, UniformBinding("First", "_Target"), AnimationCurve.Linear(0, 0, 1, 1));
            AnimationUtility.SetEditorCurve(clip, UniformBinding("Third", "_Gate"), AnimationCurve.Linear(0, 0, 1, 1));
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(clip));
            var candidates = MergeCandidates(new[] { pair.First, pair.Second, third }, controller);
            var policy = new MeshMergePolicy { Target = BuildTarget.StandaloneWindows64 };
            var planning = new MeshMergePlanning(policy);
            var pairPlan = planning.Plan(candidates.Take(2).ToArray());
            Assert.That(pairPlan.Issue, Is.Null);
            var groupPlan = planning.Plan(candidates);
            Assert.That(groupPlan.Issue, Is.EqualTo(MeshMaterialAnimationPlan.Create(candidates, policy).Issue));
            Assert.That(groupPlan.Issue, Does.Contain("interfering"));
            Assert.That(planning.Plan(candidates.Take(2).ToArray()), Is.SameAs(pairPlan));
        }
    }
}
