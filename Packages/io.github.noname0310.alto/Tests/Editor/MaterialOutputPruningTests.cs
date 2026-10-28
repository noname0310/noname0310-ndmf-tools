using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.AnimatorOptimization;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDKBase;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private Material OutputMaterial(string suffix = "MaterialOutputUsage")
        {
            var shader = Shader.Find("Hidden/Noname/ALTO/" + suffix);
            Assert.That(shader, Is.Not.Null); Assert.That(ShaderUtil.ShaderHasError(shader), Is.False);
            return Track(new Material(shader));
        }
        private static EditorCurveBinding MaterialBinding(string property, string path = "") =>
            EditorCurveBinding.FloatCurve(path, typeof(SkinnedMeshRenderer), "material." + property);
        private (VirtualAnimatorController Controller, MaterialBindingUsage Usage, MaterialOutputOptimizationReport Report)
            PruneMaterialOutputs(AnimatorController source)
        {
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var entries = new Dictionary<object, VirtualAnimatorController> { [0] = controller };
            var usage = new MaterialBindingUsage(root, entries, null);
            var report = new MaterialOutputOptimizationReport();
            MaterialOutputPruning.Apply(new[] { controller }, usage, report);
            return (controller, usage, report);
        }

        [TestCase("MaterialOutputUsage")]
        [TestCase("MaterialOutputUsePass")]
        [TestCase("MaterialOutputFallback")]
        public void ShaderAbsenceProofIncludesHiddenUniformsMacrosVariantsAndDependencies(string suffix)
        {
            var proof = new ShaderPropertyUsage().Get(OutputMaterial(suffix).shader);
            Assert.That(proof.Uncertain, Is.Null);
            foreach (string name in new[] { "_Color", "_DeclaredUnused", "_HiddenUniform", "_MacroOnly", "_AlternateUniform" })
                Assert.That(proof.MayUse(name), Is.True, name);
            Assert.That(proof.MayUse("_RemovedLightingControl"), Is.False);
        }

        [Test]
        public void MacroAnalysisKeepsNestedPastingAndRejectsUnsupportedSyntax()
        {
            var proof = new ShaderPropertyUsage.Proof();
            proof.Tokens.UnionWith(new[] { "_", "Nested", "Only" });
            ShaderPropertyUsage.ScanPasting("#define PREFIX(x) _##x\n#define SUFFIX(x) x##Only", proof);
            Assert.That(proof.MayUse("_NestedOnly"), Is.True);
            Assert.That(proof.MayUse("NestedNested"), Is.False);
            ShaderPropertyUsage.ScanPasting("#define UNKNOWN(...) ,##__VA_ARGS__", proof);
            Assert.That(proof.Uncertain, Is.Not.Null);
            Assert.That(proof.MayUse("Anything"), Is.True);
        }

        [Test]
        public void PastedPredefinedNumericMacrosNeedNotOccurLiterallyInSource()
        {
            var proof = new ShaderPropertyUsage.Proof(); proof.Tokens.Add("_Generated");
            ShaderPropertyUsage.ScanPasting("#define NUMBERED(value) _Generated##value", proof);
            Assert.That(proof.MayUse("_Generated419"), Is.True);
            Assert.That(proof.MayUse("_Absent419"), Is.False);
        }

        [Test]
        public void MissingShaderRetainsEveryMaterialOutput()
        {
            Assert.That(new ShaderPropertyUsage().Get(null).MayUse("_RemovedLightingControl"), Is.True);
        }

        [Test]
        public void PlatformStrippedSubshadersDoNotProduceInspectionErrorsOrAnAbsenceProof()
        {
            var shader = OutputMaterial("MaterialOutputPlatform").shader;
            var data = ShaderUtil.GetShaderData(shader);
            var proof = new ShaderPropertyUsage().Get(shader);
            Assert.That(proof.MayUse("_StrippedBackendUniform"), Is.True);
            if (data.SerializedSubshaderCount > data.SubshaderCount)
            {
                Assert.That(proof.Uncertain, Is.Not.Null);
                Assert.That(proof.MayUse("_RemovedLightingControl"), Is.True);
            }
        }

        [Test]
        public void UnusedMaterialChannelsAreRemovedTogetherWithoutChangingLiveChannelsOrSources()
        {
            Mesh(); renderer.sharedMaterial = OutputMaterial();
            var clip = Clip();
            foreach (char channel in "rgba")
            {
                AnimationUtility.SetEditorCurve(clip, MaterialBinding("_Color." + channel), AnimationCurve.Linear(0, 0.2f, 2, 0.9f));
                AnimationUtility.SetEditorCurve(clip, MaterialBinding("_RemovedLightingControl." + channel), AnimationCurve.Linear(0, 0, 2, 1));
            }
            var source = Controller(clip);
            var before = EvaluateMaterialVector(source, "_Color");
            var result = PruneMaterialOutputs(source);
            Assert.That(result.Report.RemovedCurves, Is.EqualTo(4));
            Assert.That(AnimationUtility.GetCurveBindings(clip).Length, Is.EqualTo(8));
            var after = EvaluateMaterialVector(CommitAnimator(result.Controller, root), "_Color");
            for (int i = 0; i < 4; i++) Assert.That(after[i], Is.EqualTo(before[i]).Within(1e-5));
            Assert.That(renderer.sharedMaterial.GetColor("_Color"), Is.EqualTo(Color.white));
        }

        [Test]
        public void MaterialPruningPreservesClipDurationAndIsIdempotent()
        {
            Mesh(); renderer.sharedMaterial = OutputMaterial();
            var clip = Clip(); AnimationUtility.SetEditorCurve(clip, MaterialBinding("_RemovedLightingControl"), AnimationCurve.Linear(0, 0, 4, 1));
            var result = PruneMaterialOutputs(Controller(clip));
            var output = result.Controller.AllReachableNodes().OfType<VirtualClip>().Single();
            Assert.That(result.Report.RemovedCurves, Is.EqualTo(1));
            Assert.That(MotionAnalysis.EndTime(output), Is.EqualTo(4));
            var repeated = new MaterialOutputOptimizationReport();
            MaterialOutputPruning.Apply(new[] { result.Controller }, result.Usage, repeated);
            Assert.That(repeated.RemovedCurves, Is.Zero);
        }

        [TestCase(false)] [TestCase(true)]
        public void MaterialSwapsAndOtherSlotsKeepPossibleUniformConsumers(bool swap)
        {
            Mesh(); renderer.sharedMaterial = OutputMaterial("MaterialAnimationRegression");
            var hidden = OutputMaterial();
            var clip = StaticCurve(MaterialBinding("_HiddenUniform.x"), 1);
            if (swap) AnimationUtility.SetObjectReferenceCurve(clip,
                EditorCurveBinding.PPtrCurve("", typeof(SkinnedMeshRenderer), "m_Materials.Array.data[0]"),
                new[] { new ObjectReferenceKeyframe { time = 0, value = hidden } });
            else renderer.sharedMaterials = new[] { renderer.sharedMaterial, hidden };
            Assert.That(PruneMaterialOutputs(Controller(clip)).Report.RemovedCurves, Is.Zero);
        }

        [Test]
        public void SharedClipWithDifferentAnimatorRootsRequiresProofForEveryRenderer()
        {
            Mesh(); renderer.sharedMaterial = OutputMaterial("MaterialAnimationRegression");
            var child = Track(new GameObject("Child")); child.transform.SetParent(root.transform, false);
            child.AddComponent<SkinnedMeshRenderer>().sharedMaterial = OutputMaterial();
            var owner = child.AddComponent<Animator>();
            var source = Controller(StaticCurve(MaterialBinding("_HiddenUniform.x"), 1));
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var usage = new MaterialBindingUsage(root, new Dictionary<object, VirtualAnimatorController> { [0] = controller, [owner] = controller }, null);
            var clip = controller.AllReachableNodes().OfType<VirtualClip>().Single();
            Assert.That(usage.Unused(clip, MaterialBinding("_HiddenUniform.x")), Is.False);
        }

        [Test]
        public void SharedSwapWithOneMissingOwnerStillProtectsItsValidTarget()
        {
            Mesh(); renderer.sharedMaterial = OutputMaterial("MaterialAnimationRegression");
            var child = Track(new GameObject("NoRenderer")); child.transform.SetParent(root.transform, false);
            var owner = child.AddComponent<Animator>();
            var swap = Clip();
            AnimationUtility.SetObjectReferenceCurve(swap,
                EditorCurveBinding.PPtrCurve("", typeof(SkinnedMeshRenderer), "m_Materials.Array.data[0]"),
                new[] { new ObjectReferenceKeyframe { time = 0, value = OutputMaterial() } });
            var clone = new CloneContext(GenericPlatformAnimatorBindings.Instance);
            var swapController = clone.Clone(Controller(swap));
            var valueController = clone.Clone(Controller(StaticCurve(MaterialBinding("_HiddenUniform.x"), 1)));
            var usage = new MaterialBindingUsage(root, new Dictionary<object, VirtualAnimatorController> {
                [0] = valueController, [1] = swapController, [owner] = swapController
            }, null);
            var clip = valueController.AllReachableNodes().OfType<VirtualClip>().Single();
            Assert.That(usage.Unused(clip, MaterialBinding("_HiddenUniform.x")), Is.False);
        }

        [Test]
        public void MissingEarlyRendererAndNonMaterialBindingsAreRetained()
        {
            Mesh(); renderer.sharedMaterial = OutputMaterial();
            var clip = StaticCurve(MaterialBinding("_RemovedLightingControl", "GeneratedLater"), 1);
            AnimationUtility.SetEditorCurve(clip, Binding("Face"), AnimationCurve.Constant(0, 1, 50));
            Assert.That(PruneMaterialOutputs(Controller(clip)).Report.RemovedCurves, Is.Zero);
        }

        [Test]
        public void DynamicInputsDoNotKeepUnusedShaderPropertiesButAnimationEventsRemainProtected()
        {
            Mesh(); renderer.sharedMaterial = OutputMaterial();
            var clip = StaticCurve(MaterialBinding("_RemovedLightingControl"), 1);
            var source = TreeController(("Preserved", clip));
            Assert.That(PruneMaterialOutputs(source).Report.RemovedCurves, Is.EqualTo(1));
            AnimationUtility.SetAnimationEvents(clip, new[] { new AnimationEvent { functionName = "ObservedEvent" } });
            Assert.That(PruneMaterialOutputs(source).Report.RemovedCurves, Is.Zero);
        }

        [TestCase(false)] [TestCase(true)]
        public void PruningUnusedMaterialOutputsKeepsPreservedInputAndNativeVisibleOutputs(bool writeDefaults)
        {
            Mesh("a"); renderer.sharedMaterial = OutputMaterial();
            var clip = StaticCurve(MaterialBinding("_RemovedLightingControl"), 1);
            AnimationUtility.SetEditorCurve(clip, Binding("a"), AnimationCurve.Constant(0, 1, 100));
            foreach (char channel in "rgba")
                AnimationUtility.SetEditorCurve(clip, MaterialBinding("_Color." + channel), AnimationCurve.Linear(0, 0.2f, 1, 0.9f));
            var source = TreeController(("Preserved", clip)); source.layers[0].stateMachine.defaultState.writeDefaultValues = writeDefaults;
            FreezeExpressions(new VRCExpressionParameters.Parameter { name = "Preserved", valueType = VRCExpressionParameters.ValueType.Float });
            var result = PruneMaterialOutputs(source);
            ParameterLiveness.RemoveUnusedCurves(root, new[] { result.Controller }, new AnimatorOptimizationReport(), new[] { "Preserved" },
                (motion, binding) => !result.Usage.Unused(motion, binding));
            var colours = new List<Vector4>(); var block = new MaterialPropertyBlock();
            float Read()
            {
                renderer.GetPropertyBlock(block); colours.Add(block.GetVector("_Color"));
                return renderer.GetBlendShapeWeight(0);
            }
            var before = SampleFrozen(source, (p, i) => p.SetFloat("Preserved", i % 4 / 3f), 12, Read);
            var beforeColours = colours.ToArray(); colours.Clear(); renderer.SetPropertyBlock(null);
            var after = SampleFrozen(CommitAnimator(result.Controller, root), (p, i) => p.SetFloat("Preserved", i % 4 / 3f), 12, Read);
            Assert.That(result.Report.RemovedCurves, Is.EqualTo(1));
            Assert.That(result.Controller.Parameters.ContainsKey("Preserved"), Is.True);
            Assert.That(result.Controller.AllReachableNodes().OfType<VirtualBlendTree>().Any(t =>
                t.Children.Any(c => c.DirectBlendParameter == "Preserved")), Is.True);
            Assert.That(before.Max() - before.Min(), Is.GreaterThan(50));
            Assert.That(after, Is.EqualTo(before).Within(0.0001f));
            Assert.That(colours.Count, Is.EqualTo(beforeColours.Length));
            for (int i = 0; i < colours.Count; i++)
                Assert.That(Vector4.Distance(colours[i], beforeColours[i]), Is.LessThan(0.0001f));
            Assert.That(AnimationUtility.GetCurveBindings(clip).Length, Is.EqualTo(6));
            Assert.That(MotionAnalysis.EndTime(result.Controller.AllReachableNodes().OfType<VirtualClip>().Single()), Is.EqualTo(1));
        }

        [TestCase(false)] [TestCase(true)]
        public void ProvenUnusedMaterialOutputsReleaseInternalParameterComputations(bool observable)
        {
            Mesh(); renderer.sharedMaterial = OutputMaterial();
            var source = TreeController(("B", StaticCurve(ParameterBinding("A"), 1)), ("A", StaticCurve(ParameterBinding("B"), 1)),
                ("A", StaticCurve(MaterialBinding(observable ? "_HiddenUniform.x" : "_RemovedLightingControl"), 1)));
            var result = PruneMaterialOutputs(source);
            var report = new AnimatorOptimizationReport();
            ParameterLiveness.RemoveUnusedCurves(root, new[] { result.Controller }, report, null, (clip, binding) => !result.Usage.Unused(clip, binding));
            Assert.That(result.Report.RemovedCurves, Is.EqualTo(observable ? 0 : 1));
            Assert.That(report.ParameterCurves, Is.EqualTo(observable ? 0 : 2));
        }

        [Test]
        public void DeadDriverOperationsArePrunedButRandomPreservedAndNetworkDestinationsRemain()
        {
            var source = Controller(Clip());
            foreach (string name in new[] { "DeadSet", "DeadAdd", "DeadCopy", "Random", "Preserved", "Network" })
                source.AddParameter(name, AnimatorControllerParameterType.Float);
            var driver = Track(source.layers[0].stateMachine.defaultState.AddStateMachineBehaviour<VRCAvatarParameterDriver>());
            driver.parameters = new List<VRC_AvatarParameterDriver.Parameter> {
                new() { name = "DeadSet", type = VRC_AvatarParameterDriver.ChangeType.Set, value = 1 },
                new() { name = "DeadAdd", type = VRC_AvatarParameterDriver.ChangeType.Add, value = 1 },
                new() { name = "DeadCopy", source = "DeadSet", type = VRC_AvatarParameterDriver.ChangeType.Copy },
                new() { name = "Random", type = VRC_AvatarParameterDriver.ChangeType.Random },
                new() { name = "Preserved", type = VRC_AvatarParameterDriver.ChangeType.Set },
                new() { name = "Network", type = VRC_AvatarParameterDriver.ChangeType.Set }
            };
            var descriptor = root.AddComponent<VRCAvatarDescriptor>();
            descriptor.expressionParameters = Track(ScriptableObject.CreateInstance<VRCExpressionParameters>());
            descriptor.expressionParameters.parameters = new[] { new VRCExpressionParameters.Parameter { name = "Network", networkSynced = true } };
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var report = new AnimatorOptimizationReport();
            ParameterLiveness.RemoveUnusedCurves(root, new[] { controller }, report, new[] { "Preserved" }, removeDeadDriverOperations: true);
            Assert.That(report.DriverOperations, Is.EqualTo(3));
            Assert.That(driver.parameters.Count, Is.EqualTo(6), "The source behaviour must not be modified.");
            var kept = controller.AllReachableNodes().OfType<VirtualState>().SelectMany(s => s.Behaviours)
                .OfType<VRC_AvatarParameterDriver>().SelectMany(d => d.parameters).Select(p => p.name);
            Assert.That(kept, Is.EquivalentTo(new[] { "Random", "Preserved", "Network" }));
            var again = new AnimatorOptimizationReport();
            ParameterLiveness.RemoveUnusedCurves(root, new[] { controller }, again, new[] { "Preserved" }, removeDeadDriverOperations: true);
            Assert.That(again.DriverOperations, Is.Zero);
        }

        [Test]
        public void DriversFeedingReachableTransitionsRemainObservable()
        {
            var source = Controller(Clip(), Clip()); source.AddParameter("Condition", AnimatorControllerParameterType.Float);
            var states = source.layers[0].stateMachine.states;
            Edge(states[0].state, states[1].state, "Condition", 0, AnimatorConditionMode.Greater);
            Driver(states[0].state, "Condition", 1);
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var report = new AnimatorOptimizationReport();
            ParameterLiveness.RemoveUnusedCurves(root, new[] { controller }, report, removeDeadDriverOperations: true);
            Assert.That(report.DriverOperations, Is.Zero);
        }
    }
}
