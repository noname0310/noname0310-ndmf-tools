using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.AnimatorOptimization;
using Noname.AvatarTools.Build;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private AvatarAnimationIndex ConstantOutputIndex(AnimatorController source, out VirtualAnimatorController controller)
        {
            controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            return AvatarAnimationIndex.Create(root,
                new Dictionary<object, VirtualAnimatorController> { [VRCAvatarDescriptor.AnimLayerType.FX] = controller }, null);
        }

        [TestCase(false, false)] [TestCase(false, true)] [TestCase(true, false)] [TestCase(true, true)]
        public void ConstantMaterialSlots_BakeMatchesNativeAndRetainsOtherSlots(bool writeDefaults, bool nullOutput)
        {
            Mesh("Shape");
            var initial = Track(new Material(Shader.Find("Hidden/InternalErrorShader")));
            var replacement = nullOutput ? null : Track(new Material(initial));
            var untouched = Track(new Material(initial)); renderer.sharedMaterials = new[] { initial, untouched };
            var clip = ConstantMaterialClip(replacement);
            AnimationUtility.SetEditorCurve(clip, Binding("Shape"), AnimationCurve.Linear(0, 0, .3f, 100));
            var source = Controller(clip); source.layers[0].stateMachine.defaultState.writeDefaultValues = writeDefaults;
            string originalClip = EditorJsonUtility.ToJson(clip);
            var expected = SampleFrozen(source, (p, i) => { }, 30, () =>
            {
                Assert.That(renderer.sharedMaterials[0], Is.SameAs(replacement));
                Assert.That(renderer.sharedMaterials[1], Is.SameAs(untouched));
                return renderer.GetBlendShapeWeight(0);
            });
            renderer.sharedMaterials = new[] { initial, untouched };
            var index = ConstantOutputIndex(source, out var controller); var report = new AnimatorOptimizationReport();
            Assert.That(ConstantMaterialSlots.Apply(index, _ => true, null, report), Is.EqualTo(1));
            Assert.That(report.InitialOutputs, Is.EqualTo(1));
            Assert.That(controller.AllReachableNodes().OfType<VirtualClip>().SelectMany(c => c.GetObjectCurveBindings()), Is.Empty);
            var actual = SampleFrozen(CommitAnimator(controller, root), (p, i) => { }, 30, () =>
            {
                Assert.That(renderer.sharedMaterials[0], Is.SameAs(replacement));
                Assert.That(renderer.sharedMaterials[1], Is.SameAs(untouched));
                return renderer.GetBlendShapeWeight(0);
            });
            Assert.That(actual, Is.EqualTo(expected).Within(.0001f));
            Assert.That(EditorJsonUtility.ToJson(clip), Is.EqualTo(originalClip));
            var next = AvatarAnimationIndex.Create(root,
                new Dictionary<object, VirtualAnimatorController> { [VRCAvatarDescriptor.AnimLayerType.FX] = controller }, null);
            Assert.That(ConstantMaterialSlots.Apply(next, _ => true, null, report), Is.Zero, "A second pass must do no work");
        }

        [TestCase("NoPermission")] [TestCase("PreservedRenderer")] [TestCase("PropertyBlock")]
        [TestCase("DynamicSlot")] [TestCase("MissingUpperBinding")] [TestCase("NestedAnimator")]
        [TestCase("AliasedBinding")] [TestCase("Mask")]
        public void ConstantMaterialSlots_RetainsUnprovenOrExcludedWrites(string reason)
        {
            var initial = Track(new Material(Shader.Find("Hidden/InternalErrorShader")));
            var replacement = Track(new Material(initial)); renderer.sharedMaterial = initial;
            var clip = ConstantMaterialClip(replacement);
            var binding = AnimationUtility.GetObjectReferenceCurveBindings(clip).Single();
            if (reason == "DynamicSlot") AnimationUtility.SetObjectReferenceCurve(clip, binding,
                new[] { new ObjectReferenceKeyframe { time = 0, value = initial }, new ObjectReferenceKeyframe { time = .3f, value = replacement } });
            if (reason == "AliasedBinding")
            {
                var alias = EditorCurveBinding.PPtrCurve("", typeof(Renderer), binding.propertyName);
                AnimationUtility.SetObjectReferenceCurve(clip, alias, AnimationUtility.GetObjectReferenceCurve(clip, binding));
            }
            var source = reason == "MissingUpperBinding" ? Controller(clip, Clip()) : Controller(clip);
            if (reason == "MissingUpperBinding")
            {
                var states = source.layers[0].stateMachine.states;
                var edge = Edge(states[0].state, states[1].state); edge.hasExitTime = true; edge.exitTime = 1;
            }
            if (reason == "Mask")
            { var layers = source.layers; layers[0].avatarMask = Track(new AvatarMask()); source.layers = layers; }
            if (reason == "PropertyBlock")
            { var block = new MaterialPropertyBlock(); block.SetFloat("_External", 1); renderer.SetPropertyBlock(block); }
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var entries = new Dictionary<object, VirtualAnimatorController> { [VRCAvatarDescriptor.AnimLayerType.FX] = controller };
            if (reason == "NestedAnimator") entries[PolicyChild("Nested").AddComponent<Animator>()] = controller;
            var index = AvatarAnimationIndex.Create(root, entries, null);
            Assert.That(ConstantMaterialSlots.Apply(index, _ => reason != "NoPermission", _ => reason == "PreservedRenderer",
                new AnimatorOptimizationReport()), Is.Zero, reason);
            Assert.That(renderer.sharedMaterial, Is.SameAs(initial));
            Assert.That(controller.AllReachableNodes().OfType<VirtualClip>().SelectMany(c => c.GetObjectCurveBindings()), Is.Not.Empty);
        }

        [Test]
        public void ConstantMaterialSlots_EquivalentInitialReferenceNeedsNoRelaxation()
        {
            var material = Track(new Material(Shader.Find("Hidden/InternalErrorShader"))); renderer.sharedMaterial = material;
            var index = ConstantOutputIndex(Controller(ConstantMaterialClip(material)), out _);
            var report = new AnimatorOptimizationReport();
            Assert.That(ConstantMaterialSlots.Apply(index, _ => false, null, report), Is.EqualTo(1));
            Assert.That(report.InitialOutputs, Is.Zero);
        }

        [TestCase(false)] [TestCase(true)]
        public void ConstantMaterialSlots_OptionControlsBaking(bool enabled)
        {
            MaterialDescriptor();
            var initial = Track(new Material(Shader.Find("Hidden/InternalErrorShader")));
            var replacement = Track(new Material(initial)); renderer.sharedMaterial = initial;
            ConstantOutputIndex(Controller(ConstantMaterialClip(replacement)), out var controller);
            var entries = new Dictionary<object, VirtualAnimatorController> { [VRCAvatarDescriptor.AnimLayerType.FX] = controller };
            root.AddComponent<ALTOOptimizationPolicy>().AllowInitialValueChanges = true;
            var context = new BuildContext(root, null); OptimizationPolicy.Capture(context);
            var report = new AnimatorOptimizationReport();
            AnimatorOptimizationPass.BakeMaterialOutputs(root, entries, null, Array.Empty<string>(),
                new AnimatorOptimizationSettings { MaterialSlots = enabled }, report, context.GetState<OptimizationPolicy>(),
                null, null, material => Track(material));
            Assert.That(renderer.sharedMaterial, Is.SameAs(enabled ? replacement : initial));
            Assert.That(report.MaterialSlotCurves, Is.EqualTo(enabled ? 1 : 0));
            Assert.That(controller.AllReachableNodes().OfType<VirtualClip>().SelectMany(c => c.GetObjectCurveBindings()).Count(),
                Is.EqualTo(enabled ? 0 : 1));
        }

        [Test]
        public void ConstantMaterialSlots_FixedPointRefreshesDownstreamNativeShaderProof()
        {
            RequireD3DAnalysis();
            var initial = Track(new Material(MeshInputShader("SignedZero"))); initial.SetFloat("_Zero", 1);
            var replacement = Track(new Material(initial)); replacement.SetFloat("_Zero", 0); renderer.sharedMaterial = initial;
            MaterialDescriptor(Controller(ConstantMaterialClip(replacement)));
            root.AddComponent<ALTOOptimizationPolicy>().AllowInitialValueChanges = true;
            var context = new BuildContext(root, null); OptimizationPolicy.Capture(context);
            context.GetState<AnimatorOptimizationState>().Settings = new AnimatorOptimizationSettings { MaterialSlots = true };
            using var lease = OptimizationSession.Acquire(context);
            var services = lease.Services;
            var entries = services.ControllerContext.Controllers.ToDictionary(e => e.Key, e => e.Value);
            var cache = context.GetState<OptimizationAnalysis>(); var sources = cache.Shaders;
            AvatarAnimationIndex Read() => cache.Get("material-slot downstream proof",
                AnalysisDomain.Animation | AnalysisDomain.Materials | AnalysisDomain.Hierarchy,
                () => AvatarAnimationIndex.Create(root, entries, services.ObjectPathRemapper.GetVirtualToRealPathMap()));
            var before = Read();
            Assert.That(before.MaterialInputs.ConstantUniforms(renderer, replacement).ContainsKey("_Zero"), Is.False);
            Assert.That(sources.MeshInputs.Issue(replacement, renderer, before, ShaderInputQuery.VertexId,
                BuildTarget.StandaloneWindows64), Is.Not.Null);
            Assert.That(OptimizationFixedPoint.Apply(context), Is.GreaterThan(0));
            Assert.That(renderer.sharedMaterial, Is.SameAs(replacement));
            var after = Read(); Assert.That(after, Is.Not.SameAs(before));
            Assert.That(cache.Shaders, Is.SameAs(sources), "Immutable shader artifacts can remain shared across material/animation rewrites.");
            Assert.That(after.MaterialInputs.ConstantUniforms(renderer, replacement)["_Zero"], Is.Zero);
            Assert.That(sources.MeshInputs.Issue(replacement, renderer, after, ShaderInputQuery.VertexId,
                BuildTarget.StandaloneWindows64), Is.Null, "The removed swap must no longer hide the immutable material input.");
        }

        [TestCase(0f, 60f)] [TestCase(.3f, 60f)] [TestCase(.125f, 24f)] [TestCase(.123f, 30f)] [TestCase(.47f, 120f)]
        public void ConstantMaterialSlots_EmptyClipRetainsNativeDuration(float duration, float frameRate)
        {
            Mesh("Shape"); var material = Track(new Material(Shader.Find("Hidden/InternalErrorShader"))); renderer.sharedMaterial = material;
            var clip = ConstantMaterialClip(material);
            clip.frameRate = frameRate;
            var binding = AnimationUtility.GetObjectReferenceCurveBindings(clip).Single();
            AnimationUtility.SetObjectReferenceCurve(clip, binding, new[] { new ObjectReferenceKeyframe { time = duration, value = material } });
            var index = ConstantOutputIndex(Controller(clip), out var controller);
            ConstantMaterialSlots.Apply(index, _ => false, null, new AnimatorOptimizationReport());
            var built = CommitAnimator(controller, root).animationClips.Single();
            Assert.That(built.length, Is.EqualTo(clip.length).Within(.000001f));
        }
    }
}
