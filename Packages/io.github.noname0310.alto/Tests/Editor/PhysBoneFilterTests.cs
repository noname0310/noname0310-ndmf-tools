using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Build;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using VRC.Dynamics;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private ALTOPhysBoneFilter PhysFilter()
        {
            if (root.GetComponent<VRCAvatarDescriptor>() == null) root.AddComponent<VRCAvatarDescriptor>();
            var filter = root.AddComponent<ALTOPhysBoneFilter>(); filter.ApplyOnPlatforms = OptimizationPlatforms.All;
            filter.FilterMode = PhysBoneFilterMode.RemoveListed;
            return filter;
        }

        private VRCPhysBone PhysBone(string name, Transform parent = null)
        {
            var bone = PolicyChild(name, parent).AddComponent<VRCPhysBone>();
            PolicyChild("Tip", bone.transform).transform.localPosition = Vector3.up;
            return bone;
        }

        private void ApplyPhysRemoval(BuildContext context, params VRCPhysBone[] remove) =>
            PhysBoneFilterPass.ApplyRemoval(context, context.GetState<PhysBoneFilterState>(), root.GetComponentsInChildren<VRCPhysBone>(true),
                new HashSet<VRCPhysBone>(remove), new HashSet<string>(), new HashSet<ObjectReference>());

        [Test]
        public void NewPhysBoneFilterDefaultsToAndroidAndEmptyKeepList()
        {
            var filter = root.AddComponent<ALTOPhysBoneFilter>();
            Assert.That(filter.ApplyOnPlatforms, Is.EqualTo(OptimizationPlatforms.Android));
            Assert.That(filter.FilterMode, Is.EqualTo(PhysBoneFilterMode.KeepListed));
            Assert.That(filter.PhysBones, Is.Empty);
        }

        [Test]
        public void KeepListedFiltersPhysBonesCreatedAfterCaptureWithoutRemovingTheirBones()
        {
            var filter = PhysFilter(); filter.FilterMode = PhysBoneFilterMode.KeepListed;
            var kept = PhysBone("Hair"); filter.PhysBones.Add(kept);
            var context = new BuildContext(root, null); PhysBoneFilterPass.Capture(context);
            var generated = PhysBone("Generated secondary motion");
            var transform = generated.transform; var child = transform.GetChild(0);
            renderer.bones = new[] { transform, child };
            PhysBoneFilterPass.Apply(context);
            Assert.That(kept != null && generated == null, Is.True);
            Assert.That(transform != null && child != null, Is.True);
            Assert.That(child.localPosition, Is.EqualTo(Vector3.up));
            Assert.That(renderer.bones, Is.EqualTo(new[] { transform, child }));
        }

        [Test]
        public void BoneRootSelectionTracksReparentingAndLateGeneratedComponents()
        {
            var filter = PhysFilter(); filter.FilterMode = PhysBoneFilterMode.KeepListed;
            var hair = PolicyChild("Hair"); var strand = PolicyChild("Strand", hair.transform);
            filter.BoneRoots.Add(new PhysBoneRootSelection { Root = hair.transform });
            var context = new BuildContext(root, null); PhysBoneFilterPass.Capture(context);
            strand.transform.SetParent(root.transform, false);
            var lateBone = PolicyChild("Late bone", strand.transform);
            var generated = PhysBone("Generated outside original container"); generated.rootTransform = lateBone.transform;
            var unrelated = PhysBone("Unselected generated PhysBone");
            PhysBoneFilterPass.Apply(context);
            Assert.That(generated != null && unrelated == null, Is.True);
        }

        [Test]
        public void BoneRootSelectionKeepsExistingPhysBonesRetargetedByArmatureMerge()
        {
            var filter = PhysFilter(); filter.FilterMode = PhysBoneFilterMode.KeepListed;
            var selected = PhysBone("Selected hair"); filter.BoneRoots.Add(new PhysBoneRootSelection { Root = selected.transform });
            var context = new BuildContext(root, null); PhysBoneFilterPass.Capture(context);
            selected.rootTransform = PolicyChild("Merged bone destination").transform;
            PhysBoneFilterPass.Apply(context);
            Assert.That(selected != null, Is.True);
        }

        [Test]
        public void DirectPhysBoneSelectionFollowsRegisteredReplacement()
        {
            using var registry = new ObjectRegistryScope(new ObjectRegistry(root.transform));
            var filter = PhysFilter(); filter.FilterMode = PhysBoneFilterMode.KeepListed;
            var source = PhysBone("Original"); filter.PhysBones.Add(source);
            var context = new BuildContext(root, null); PhysBoneFilterPass.Capture(context);
            var replacement = PhysBone("Replacement"); ObjectRegistry.RegisterReplacedObject(source, replacement);
            Object.DestroyImmediate(source);
            PhysBoneFilterPass.Apply(context);
            Assert.That(replacement != null, Is.True);
        }

        [Test]
        public void OrphanCleanupKeepsSharedOutsideScopeGlobalAndExplicitColliders()
        {
            var filter = PhysFilter(); var scope = PolicyChild("Scope"); filter.ScopeRoot = scope.transform;
            var removed = PhysBone("Removed", scope.transform); filter.PhysBones.Add(removed);
            var outside = PhysBone("Outside");
            var unused = PolicyChild("Unused", scope.transform).AddComponent<VRCPhysBoneCollider>();
            var shared = PolicyChild("Shared", scope.transform).AddComponent<VRCPhysBoneCollider>();
            var global = PolicyChild("Global", scope.transform).AddComponent<VRCPhysBoneCollider>();
            global.globalCollision = VRCPhysBoneBase.AdvancedBool.True;
            var preserved = PolicyChild("Preserved", scope.transform).AddComponent<VRCPhysBoneCollider>();
            filter.PreserveColliders.Add(preserved);
            outside.colliders.Add(shared); removed.colliders.Add(unused); removed.colliders.Add(shared);
            var context = new BuildContext(root, null); PhysBoneFilterPass.Capture(context);
            ApplyPhysRemoval(context, removed);
            Assert.That(removed == null && unused == null, Is.True);
            Assert.That(outside != null && shared != null && global != null && preserved != null, Is.True);
        }

        [Test]
        public void UnknownColliderConsumerKeepsColliderAndPhysBoneConsumerRejectsRemovalAtomically()
        {
            var filter = PhysFilter(); var removed = PhysBone("Selected"); filter.PhysBones.Add(removed);
            var collider = PolicyChild("Collider").AddComponent<VRCPhysBoneCollider>();
            var consumer = PolicyChild("Consumer").AddComponent<ParameterPolicySourceFixture>();
            consumer.ReferencedObject = removed;
            var context = new BuildContext(root, null); PhysBoneFilterPass.Capture(context);
            Assert.Throws<InvalidOperationException>(() => ApplyPhysRemoval(context, removed));
            Assert.That(removed != null && collider != null, Is.True);
            consumer.ReferencedObject = collider;
            ApplyPhysRemoval(context, removed);
            Assert.That(removed == null && collider != null, Is.True);
        }

        [Test]
        public void NestedRemovalRetainsIgnoreOtherPhysBonesBoundary()
        {
            var filter = PhysFilter(); var outer = PhysBone("Outer"); outer.ignoreOtherPhysBones = true;
            var inner = PhysBone("Inner", outer.transform); filter.PhysBones.Add(inner); inner.enabled = false;
            var context = new BuildContext(root, null); PhysBoneFilterPass.Capture(context);
            var bone = inner.transform;
            ApplyPhysRemoval(context, inner);
            Assert.That(inner == null && bone != null, Is.True);
            Assert.That(outer.ignoreTransforms, Does.Contain(bone));
        }

        [Test]
        public void ExplicitlyOverlappingPhysBonesKeepTheirOriginalIgnoreSetting()
        {
            var filter = PhysFilter(); var outer = PhysBone("Outer"); outer.ignoreOtherPhysBones = false;
            var inner = PhysBone("Inner", outer.transform); filter.PhysBones.Add(inner);
            var context = new BuildContext(root, null); PhysBoneFilterPass.Capture(context);
            ApplyPhysRemoval(context, inner);
            Assert.That(outer.ignoreTransforms, Is.Empty);
        }

        [Test]
        public void AnimatedNestedBoundaryIsRejectedInsteadOfChangingRemainingPhysics()
        {
            var outer = PhysBone("Outer"); var inner = PhysBone("Inner", outer.transform);
            var clip = Clip();
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Outer", typeof(VRCPhysBone), "ignoreOtherPhysBones"),
                AnimationCurve.Linear(0, 0, 1, 1));
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(clip));
            Assert.Throws<InvalidOperationException>(() => PhysBoneFilterPass.ValidateAnimatedHierarchy(root,
                new Dictionary<object, VirtualAnimatorController> { [VRCAvatarDescriptor.AnimLayerType.FX] = controller }, new HashSet<VRCPhysBone> { inner }));
            Assert.That(inner != null && outer.ignoreTransforms.Count == 0, Is.True);
        }

        [Test]
        public void ExistingIgnoredAncestorsDoNotAcquireRedundantChildReferences()
        {
            var outer = PhysBone("Outer"); outer.ignoreOtherPhysBones = true;
            var ignored = PolicyChild("Already ignored", outer.transform); outer.ignoreTransforms.Add(ignored.transform);
            var inner = PhysBone("Removed", ignored.transform);
            PhysBoneFilterPass.PreserveNestedBoundaries(new[] { outer, inner }, new HashSet<VRCPhysBone> { inner });
            Assert.That(outer.ignoreTransforms, Is.EqualTo(new[] { ignored.transform }));
        }

        [Test]
        public void PreservedPhysBoneOutputRejectsRemoval()
        {
            var filter = PhysFilter(); var bone = PhysBone("Preserved"); bone.parameter = "Motion"; filter.PhysBones.Add(bone);
            var context = new BuildContext(root, null); PhysBoneFilterPass.Capture(context);
            Assert.Throws<InvalidOperationException>(() => PhysBoneFilterPass.ApplyRemoval(context, context.GetState<PhysBoneFilterState>(),
                new[] { bone }, new HashSet<VRCPhysBone> { bone }, new HashSet<string> { "Motion_IsGrabbed" }, new HashSet<ObjectReference>()));
            Assert.That(bone != null, Is.True);
        }

        [Test]
        public void AnimatedGlobalAndObjectReferenceCollidersAreNotOrphans()
        {
            var filter = PhysFilter();
            var global = PolicyChild("AnimatedGlobal").AddComponent<VRCPhysBoneCollider>();
            var referenced = PolicyChild("AnimatedReference").AddComponent<VRCPhysBoneCollider>();
            var unused = PolicyChild("Unused").AddComponent<VRCPhysBoneCollider>();
            var bone = PhysBone("Consumer");
            var clip = Clip();
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("AnimatedGlobal", typeof(VRCPhysBoneCollider), "globalCollision"), AnimationCurve.Constant(0, 1, 1));
            AnimationUtility.SetObjectReferenceCurve(clip, EditorCurveBinding.PPtrCurve("Consumer", typeof(VRCPhysBone), "colliders.Array.data[0]"),
                new[] { new ObjectReferenceKeyframe { value = referenced } });
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(clip));
            var context = new BuildContext(root, null); PhysBoneFilterPass.Capture(context);
            var references = PhysBoneFilterPass.AnimatedColliderReferences(root,
                new Dictionary<object, VirtualAnimatorController> { [VRCAvatarDescriptor.AnimLayerType.FX] = controller });
            PhysBoneFilterPass.ApplyRemoval(context, context.GetState<PhysBoneFilterState>(), new[] { bone }, new(), new HashSet<string>(), references);
            Assert.That(global != null && referenced != null && unused == null, Is.True);
        }

        [Test]
        public void PhysBoneScopeAndForeignTargetsAreValidatedBeforeRemoval()
        {
            var filter = PhysFilter(); var bone = PhysBone("Bone");
            filter.ScopeRoot = PolicyChild("Scope").transform; filter.PhysBones.Add(bone);
            Assert.Throws<InvalidOperationException>(() => PhysBoneFilterPass.Capture(new BuildContext(root, null)));
            filter.ScopeRoot = null;
            var context = new BuildContext(root, null); PhysBoneFilterPass.Capture(context);
            bone.rootTransform = Track(new GameObject("Foreign")).transform;
            Assert.Throws<InvalidOperationException>(() => ApplyPhysRemoval(context, bone));
            Assert.That(bone != null, Is.True);
        }

        [Test]
        public void PhysBoneBudgetsUseFourIndependentFinalSdkCounts()
        {
            var filter = PhysFilter(); var bone = PhysBone("A"); PhysBone("B");
            bone.colliders.Add(PolicyChild("Collider").AddComponent<VRCPhysBoneCollider>());
            filter.RemoveUnreferencedColliders = false;
            filter.Budget.Components = 0; filter.Budget.Colliders = 0; filter.Budget.OnExceeded = BudgetExceededAction.Error;
            var context = new BuildContext(root, null); PhysBoneFilterPass.Capture(context);
            context.GetState<PhysBoneFilterState>().Applied = true;
            var exception = Assert.Throws<InvalidOperationException>(() => PhysBoneFilterPass.CheckBudget(context));
            Assert.That(exception.Message, Does.Contain("Components 2 > 0").And.Contain("Colliders 1 > 0"));
            var counts = context.GetState<OptimizationBuildReport>().Physics;
            Assert.That(counts.Transforms, Is.GreaterThan(0));
            var exact = new PhysBoneBudget { Components = counts.Components, Transforms = counts.Transforms,
                Colliders = counts.Colliders, CollisionChecks = counts.CollisionChecks };
            Assert.That(counts.Exceeded(exact), Is.Empty);
        }
    }
}
