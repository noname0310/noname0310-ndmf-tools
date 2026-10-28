using System;
using System.Collections.Generic;
using System.Linq;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.Build;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private void MaterialFixture(int submeshes, int materials)
        {
            var mesh = Mesh("Morph");
            mesh.subMeshCount = submeshes;
            for (int i = 0; i < submeshes; i++) mesh.SetTriangles(new[] { 0, 1, 2 }, i);
            renderer.sharedMaterials = Enumerable.Range(0, materials)
                .Select(i => Track(new Material(Shader.Find("Unlit/Texture")) { name = "Slot " + i })).ToArray();
        }

        private AnimationClip SlotClip(int index, Material value, float time = 0)
        {
            var clip = Clip();
            AnimationUtility.SetObjectReferenceCurve(clip,
                EditorCurveBinding.PPtrCurve("", typeof(SkinnedMeshRenderer), MaterialFilterPass.SlotPrefix + index + "]"),
                new[] { new ObjectReferenceKeyframe { time = time, value = value } });
            return clip;
        }

        private VirtualAnimatorController VirtualSlotController(AnimationClip clip) =>
            new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(clip));

        private int RewriteSlots(VirtualAnimatorController controller, MaterialSlotPlan plan, params MaterialExclusion[] rules) =>
            MaterialFilterPass.RewriteAnimations(root, new Dictionary<object, VirtualAnimatorController>
                { [VRCAvatarDescriptor.AnimLayerType.FX] = controller },
                new Dictionary<Renderer, MaterialSlotPlan> { [renderer] = plan }, rules, new HashSet<Renderer> { renderer });

        [Test]
        public void ExtraPassRemovalKeepsMeshAndRemapsFollowingMaterialCurves()
        {
            MaterialFixture(1, 3);
            var source = renderer.sharedMesh;
            var materials = renderer.sharedMaterials;
            var rule = new MaterialExclusion { Renderer = renderer, Slot = 1 };
            var plan = MaterialSlotPlan.Create(renderer, new[] { rule });
            var clip = SlotClip(2, materials[2], 2);
            var controller = VirtualSlotController(clip);
            Assert.That(RewriteSlots(controller, plan, rule), Is.EqualTo(1));
            var outputClip = controller.AllReachableNodes().OfType<VirtualClip>().Single();
            Assert.That(outputClip.GetObjectCurveBindings().Single().propertyName, Is.EqualTo(MaterialFilterPass.SlotPrefix + "1]"));
            Assert.That(AnimationUtility.GetObjectReferenceCurveBindings(clip).Single().propertyName, Does.EndWith("[2]"));
            plan.Apply(new BuildContext(root, null));
            Assert.That(renderer.sharedMesh, Is.SameAs(source));
            Assert.That(renderer.sharedMaterials, Is.EqualTo(new[] { materials[0], materials[2] }));
            Assert.That(MotionAnalysis.EndTime(outputClip), Is.EqualTo(2));
        }

        [Test]
        public void PrimaryPassCannotBeRemovedWithoutExplicitGeometryRemoval()
        {
            MaterialFixture(2, 3);
            Assert.Throws<InvalidOperationException>(() => MaterialSlotPlan.Create(renderer,
                new[] { new MaterialExclusion { Renderer = renderer, Slot = 1 } }));
            Assert.That(renderer.sharedMesh.subMeshCount, Is.EqualTo(2));
        }

        [Test]
        public void RemovingLastSubmeshAlsoRemovesEveryOverlayDrawingIt()
        {
            MaterialFixture(2, 4);
            var source = renderer.sharedMesh;
            renderer.SetBlendShapeWeight(0, 37);
            var plan = MaterialSlotPlan.Create(renderer, new[]
                { new MaterialExclusion { Renderer = renderer, Slot = 1, Mode = MaterialRemovalMode.RemoveSubMeshGeometry } });
            plan.Apply(new BuildContext(root, null));
            Track(renderer.sharedMesh);
            Assert.That(plan.SlotMap, Is.EqualTo(new[] { 0, -1, -1, -1 }));
            Assert.That(renderer.sharedMesh.subMeshCount, Is.EqualTo(1));
            Assert.That(renderer.sharedMaterials.Length, Is.EqualTo(1));
            Assert.That(renderer.sharedMesh.GetBlendShapeName(0), Is.EqualTo("Morph"));
            Assert.That(renderer.GetBlendShapeWeight(0), Is.EqualTo(37));
            Assert.That(source.subMeshCount, Is.EqualTo(2));
        }

        [Test]
        public void RemovingMiddleSubmeshPreservesLastSubmeshOverlayMapping()
        {
            MaterialFixture(3, 5);
            var source = renderer.sharedMesh;
            var materials = renderer.sharedMaterials;
            var plan = MaterialSlotPlan.Create(renderer, new[]
                { new MaterialExclusion { Renderer = renderer, Slot = 1, Mode = MaterialRemovalMode.RemoveSubMeshGeometry } });
            plan.Apply(new BuildContext(root, null));
            Track(renderer.sharedMesh);
            Assert.That(plan.SlotMap, Is.EqualTo(new[] { 0, -1, 1, 2, 3 }));
            Assert.That(renderer.sharedMesh.subMeshCount, Is.EqualTo(2));
            Assert.That(renderer.sharedMesh.GetSubMesh(1).indexStart, Is.EqualTo(source.GetSubMesh(2).indexStart));
            Assert.That(renderer.sharedMaterials, Is.EqualTo(new[] { materials[0], materials[2], materials[3], materials[4] }));
        }

        [Test]
        public void MaterialIdentityRulesRejectAnimatedSwapsButExplicitSlotsWork()
        {
            MaterialFixture(1, 2);
            var materials = renderer.sharedMaterials;
            var rule = new MaterialExclusion { Renderer = renderer, Material = materials[1] };
            var plan = MaterialSlotPlan.Create(renderer, new[] { rule });
            var controller = VirtualSlotController(SlotClip(1, materials[0], 3));
            Assert.Throws<InvalidOperationException>(() => RewriteSlots(controller, plan, rule));
            rule.Material = null; rule.Slot = 1;
            Assert.That(RewriteSlots(controller, plan, rule), Is.EqualTo(1));
            var clip = controller.AllReachableNodes().OfType<VirtualClip>().Single();
            Assert.That(clip.GetObjectCurveBindings(), Is.Empty);
            Assert.That(MotionAnalysis.EndTime(clip), Is.EqualTo(3));
        }

        [Test]
        public void MaterialReferencedOnlyByAnimationIsReportedInsteadOfSilentlyIgnored()
        {
            MaterialFixture(1, 2);
            var other = Track(new Material(Shader.Find("Unlit/Texture")));
            var rule = new MaterialExclusion { Renderer = renderer, Material = other };
            var plan = MaterialSlotPlan.Create(renderer, new[] { rule });
            Assert.That(plan.HasChanges, Is.False);
            var controller = VirtualSlotController(SlotClip(1, other));
            Assert.Throws<InvalidOperationException>(() => RewriteSlots(controller, plan, rule));
        }

        [Test]
        public void MaterialFilterRejectsAnimatedMeshChanges()
        {
            MaterialFixture(1, 2);
            var rule = new MaterialExclusion { Renderer = renderer, Slot = 1 };
            var plan = MaterialSlotPlan.Create(renderer, new[] { rule });
            var clip = Clip();
            AnimationUtility.SetObjectReferenceCurve(clip, EditorCurveBinding.PPtrCurve("", typeof(SkinnedMeshRenderer), "m_Mesh"),
                new[] { new ObjectReferenceKeyframe { time = 0, value = renderer.sharedMesh } });
            Assert.Throws<InvalidOperationException>(() => RewriteSlots(VirtualSlotController(clip), plan, rule));
        }

        [Test]
        public void SharedSlotCurveCannotBeRewrittenDifferentlyForTwoAnimatorRoots()
        {
            MaterialFixture(1, 3);
            var a = PolicyChild("A"); var b = PolicyChild("B");
            var ra = a.AddComponent<SkinnedMeshRenderer>(); ra.sharedMesh = renderer.sharedMesh; ra.sharedMaterials = renderer.sharedMaterials;
            var rb = b.AddComponent<SkinnedMeshRenderer>(); rb.sharedMesh = renderer.sharedMesh; rb.sharedMaterials = renderer.sharedMaterials;
            var rule = new MaterialExclusion { Renderer = ra, Slot = 1 };
            var plan = MaterialSlotPlan.Create(ra, new[] { rule });
            var controller = VirtualSlotController(SlotClip(2, renderer.sharedMaterials[2]));
            Assert.Throws<InvalidOperationException>(() => MaterialFilterPass.RewriteAnimations(root,
                new Dictionary<object, VirtualAnimatorController> { [a.AddComponent<Animator>()] = controller, [b.AddComponent<Animator>()] = controller },
                new Dictionary<Renderer, MaterialSlotPlan> { [ra] = plan }, new[] { rule }, new HashSet<Renderer> { ra, rb }));
            Assert.That(controller.AllReachableNodes().OfType<VirtualClip>().Single().GetObjectCurveBindings().Single().propertyName,
                Does.EndWith("[2]"));
        }

        [Test]
        public void MaterialRulesSurviveEarlyMarkerRemoval()
        {
            MaterialFixture(1, 2);
            var filter = Filter();
            filter.MaterialExclusions.Add(new MaterialExclusion { Renderer = renderer, Slot = 1 });
            var context = new BuildContext(root, null);
            BuildFilterPass.RemoveEarly(context);
            Assert.That(root.GetComponent<ALTOBuildFilter>(), Is.Null);
            Assert.That(context.GetState<BuildFilterState>().Materials.Single().Renderer, Is.SameAs(renderer));
        }
    }
}
