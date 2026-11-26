using System;
using System.Collections.Generic;
using System.Linq;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.AnimatorOptimization;
using Noname.AvatarTools.Meshes;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;
using VRC.Dynamics;
using VRC.SDK3.Dynamics.Contact.Components;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private VRCContactReceiver AnimatedMaterialContact(bool enabled, params Renderer[] targets)
        {
            var receiver = PolicyChild("Contact").AddComponent<VRCContactReceiver>();
            receiver.parameter = "Touch"; receiver.writeToMaterials = enabled; receiver.shaderTargetRenderers = targets;
            return receiver;
        }

        private Material ContactWriterMaterial()
        {
            var material = Track(new Material(Shader.Find("Hidden/Noname/ALTO/ContactWriterConstants")));
            renderer.sharedMaterial = material; return material;
        }

        [TestCase(false, false)] [TestCase(false, true)]
        [TestCase(true, false)] [TestCase(true, true)]
        public void ContactMaterialWriters_UnadvertisedEnableBindingKeepsNativeInitialConfiguration(bool initial, bool output)
        {
            Mesh("Timing"); var receiver = AnimatedMaterialContact(initial, renderer); var material = ContactWriterMaterial();
            var other = PolicyChild("Unrelated").AddComponent<MeshRenderer>();
            var binding = EditorCurveBinding.FloatCurve("Contact", typeof(VRCContactReceiver), "writeToMaterials");
            var clip = StaticCurve(binding, output ? 1 : 0); var source = Controller(clip);
            var values = SampleFrozen(source, (p, i) => { }, 4, () => receiver.writeToMaterials ? 1 : 0);
            receiver.writeToMaterials = initial;
            TestContext.WriteLine("Native writeToMaterials outputs: " + string.Join(",", values));
            Assert.That(values, Is.All.EqualTo(initial ? 1 : 0));
            var index = ConstantOutputIndex(source, out _);
            Assert.That(new NativeAnimationBindings(root).Supports(receiver, index.ForObject(receiver), out _), Is.False);
            Assert.That(ContactOutputs.Writers(root, renderer, index).Contains(receiver), Is.EqualTo(initial));
            Assert.That(ContactOutputs.Writers(root, other, index), Is.Empty, "Known targets must not disable unrelated renderers.");
            var constants = index.MaterialInputs.ConstantUniforms(renderer, material);
            Assert.That(constants.ContainsKey(receiver.ShaderPropertyNameData), Is.EqualTo(!initial));
            Assert.That(constants.ContainsKey("_Unrelated"), Is.True);
        }

        [TestCase(false, false)] [TestCase(false, true)] [TestCase(true, false)] [TestCase(true, true)]
        public void ContactMaterialWriters_NotKeyableEnableRemainsFixedForDiscreteAndBaseBindings(bool discrete, bool baseType)
        {
            Mesh("Timing"); var receiver = AnimatedMaterialContact(false, renderer);
            var type = baseType ? typeof(ContactReceiver) : typeof(VRCContactReceiver);
            var binding = discrete ? EditorCurveBinding.DiscreteCurve("Contact", type, "writeToMaterials") :
                EditorCurveBinding.FloatCurve("Contact", type, "writeToMaterials");
            var source = Controller(StaticCurve(binding, 1));
            var values = SampleFrozen(source, (p, i) => { }, 4, () => receiver.writeToMaterials ? 1 : 0);
            Assert.That(typeof(ContactReceiver).GetField("writeToMaterials").GetCustomAttributes(false)
                .Any(a => a.GetType().FullName == "UnityEngine.Animations.NotKeyableAttribute"), Is.True);
            Assert.That(values, Is.All.EqualTo(0));
            var index = ConstantOutputIndex(source, out _);
            Assert.That(new NativeAnimationBindings(root).Supports(receiver, index.ForObject(receiver), out _), Is.False);
            Assert.That(ContactOutputs.Writers(root, renderer, index), Is.Empty);
        }

        [TestCase(false)] [TestCase(true)]
        public void ContactMaterialWriters_UnadvertisedTargetBindingKeepsNativeInitialRenderers(bool baseType)
        {
            Mesh("Timing"); var initial = PolicyChild("Initial").AddComponent<MeshRenderer>();
            var unrelated = PolicyChild("Unrelated").AddComponent<MeshRenderer>();
            var receiver = AnimatedMaterialContact(true, initial);
            var binding = EditorCurveBinding.PPtrCurve("Contact", baseType ? typeof(ContactReceiver) : typeof(VRCContactReceiver),
                "shaderTargetRenderers.Array.data[0]");
            var clip = Clip(); AnimationUtility.SetObjectReferenceCurve(clip, binding,
                new[] { new ObjectReferenceKeyframe { time = 0, value = renderer } });
            var source = Controller(clip); var values = SampleFrozen(source, (p, i) => { }, 4,
                () => receiver.shaderTargetRenderers[0] == renderer ? 1 : 0);
            receiver.shaderTargetRenderers = new Renderer[] { initial };
            var index = ConstantOutputIndex(source, out _);
            bool supported = new NativeAnimationBindings(root).Supports(receiver, index.ForObject(receiver), out string issue);
            TestContext.WriteLine("Native target binding advertised=" + supported + "; samples=" + string.Join(",", values) + "; " + issue);
            Assert.That(supported, Is.False); Assert.That(values, Is.All.EqualTo(0));
            Assert.That(ContactOutputs.Writers(root, initial, index), Does.Contain(receiver));
            Assert.That(ContactOutputs.Writers(root, renderer, index), Is.Empty);
            Assert.That(ContactOutputs.Writers(root, unrelated, index), Is.Empty);
        }

        [TestCase("UnresolvedReceiver")] [TestCase("UnknownTargetBinding")]
        public void ContactMaterialWriters_UnresolvedConfigurationProtectsEveryRenderer(string reason)
        {
            var receiver = AnimatedMaterialContact(reason != "UnresolvedReceiver"); var clip = Clip();
            if (reason == "UnresolvedReceiver") AnimationUtility.SetEditorCurve(clip,
                EditorCurveBinding.FloatCurve("MissingContact", typeof(VRCContactReceiver), "writeToMaterials"), AnimationCurve.Constant(0, 1, 1));
            else AnimationUtility.SetObjectReferenceCurve(clip,
                EditorCurveBinding.PPtrCurve("Contact", typeof(VRCContactReceiver), "shaderTargetRenderers"),
                new[] { new ObjectReferenceKeyframe { time = 0, value = renderer } });
            var index = ConstantOutputIndex(Controller(clip), out _);
            Assert.That(ContactOutputs.Writers(root, renderer, index), Does.Contain(receiver));
        }

        [Test]
        public void ContactMaterialWriters_AnimatedTargetPastSdkLimitDoesNotBecomeActive()
        {
            var initial = PolicyChild("Initial").AddComponent<MeshRenderer>();
            var receiver = AnimatedMaterialContact(true, Enumerable.Repeat<Renderer>(initial, ContactReceiver.SHADER_MAX_UPDATED_RENDERERS + 1).ToArray());
            var clip = Clip(); AnimationUtility.SetObjectReferenceCurve(clip,
                EditorCurveBinding.PPtrCurve("Contact", typeof(VRCContactReceiver),
                    "shaderTargetRenderers.Array.data[" + ContactReceiver.SHADER_MAX_UPDATED_RENDERERS + "]"),
                new[] { new ObjectReferenceKeyframe { time = 0, value = renderer } });
            var index = ConstantOutputIndex(Controller(clip), out _);
            Assert.That(ContactOutputs.Writers(root, renderer, index), Is.Empty);
            Assert.That(ContactOutputs.Writers(root, initial, index), Does.Contain(receiver));
        }

        [Test]
        public void ContactMaterialWriters_MaterialBakingRetainsOnlyThePotentialNativeOutput()
        {
            var receiver = AnimatedMaterialContact(true, renderer); ContactWriterMaterial();
            var contact = EditorCurveBinding.FloatCurve("", typeof(SkinnedMeshRenderer), "material." + receiver.ShaderPropertyNameData);
            var unrelated = EditorCurveBinding.FloatCurve("", typeof(SkinnedMeshRenderer), "material._Unrelated");
            var clip = StaticCurve(contact, .6f); AnimationUtility.SetEditorCurve(clip, unrelated, AnimationCurve.Constant(0, 1, .7f));
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Contact", typeof(VRCContactReceiver), "writeToMaterials"),
                AnimationCurve.Constant(0, 1, 0));
            var entries = SharedShaderControllers(clip); var report = new AnimatorOptimizationReport();
            MaterialConstants.Optimize(root, entries, null, m => Track(m), report, allowInitialChanges: _ => true);
            var output = entries.Values.Single().AllReachableNodes().OfType<nadena.dev.ndmf.animator.VirtualClip>().Single();
            Assert.That(output.GetFloatCurve(contact), Is.Not.Null);
            Assert.That(output.GetFloatCurve(unrelated), Is.Null);
            Assert.That(report.MaterialCurves, Is.EqualTo(1));
        }

        [TestCase(false)] [TestCase(true)]
        public void ContactMaterialWriters_ShaderSpecializationKeepsNativeInitialUniform(bool enabled)
        {
            RequireRuntimeShaderTarget();
            var receiver = AnimatedMaterialContact(enabled, renderer); ContactWriterMaterial(); var clip = Clip();
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Contact", typeof(VRCContactReceiver), "writeToMaterials"),
                AnimationCurve.Constant(0, 1, enabled ? 0 : 1));
            var diagnostics = new List<string>();
            Assert.That(SpecializeSharedShaders(SharedShaderControllers(clip), diagnostics), Is.EqualTo(1), string.Join("\n", diagnostics));
            var pass = ShaderUtil.GetShaderData(renderer.sharedMaterial.shader).GetSubshader(0).GetPass(0);
            var program = pass.PreprocessVariant(ShaderType.Fragment, Array.Empty<string>(), ShaderCompilerPlatform.D3D,
                SharedShaderTarget, GraphicsTier.Tier1, false);
            Assert.That(D3DShaderInputs.CompileUncached(program.PreprocessedCode, "frag", ShaderType.Fragment, out var uniforms, out string issue), Is.True, issue);
            Assert.That(uniforms.Any(u => ShaderInputQuery.Uniform(receiver.ShaderPropertyNameData).MatchesCompiled(u) && u.ReadMask != 0), Is.EqualTo(enabled));
            Assert.That(uniforms.Any(u => ShaderInputQuery.Uniform("_Unrelated").MatchesCompiled(u) && u.ReadMask != 0), Is.False);
        }

        [TestCase(false)] [TestCase(true)]
        public void ContactMaterialWriters_PossibleSetsDoNotProveMergeCoverage(bool unknownBinding)
        {
            var pair = MergePair(); var receiver = AnimatedMaterialContact(true, pair.First, pair.Second);
            var clip = Clip(); AnimationUtility.SetObjectReferenceCurve(clip,
                EditorCurveBinding.PPtrCurve("Contact", typeof(VRCContactReceiver),
                    unknownBinding ? "shaderTargetRenderers" : "shaderTargetRenderers.Array.data[0]"),
                new[] { new ObjectReferenceKeyframe { time = 0, value = pair.Second } });
            var controller = SharedShaderControllers(clip).Values.Single();
            var candidates = MergeCandidates(new[] { pair.First, pair.Second }, controller, assertEligible: false);
            if (unknownBinding) Assert.That(candidates, Is.All.Null,
                "Equal potential writer sets do not establish simultaneous target coverage.");
            else Assert.That(MeshMergeAnalysis.Group(candidates, true).Count, Is.EqualTo(1),
                "The native Animator ignores this unadvertised SDK field, so fixed target coverage is retained.");
        }
    }
}
