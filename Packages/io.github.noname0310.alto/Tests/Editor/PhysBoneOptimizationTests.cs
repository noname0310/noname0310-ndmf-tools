using System.Linq;
using nadena.dev.ndmf;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.ObjectOptimization;
using Noname.AvatarTools.Physics;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using VRC.Dynamics;
using VRC.SDK3.Dynamics.PhysBone.Components;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private VRCPhysBoneCollider LocalCollider(string name, Transform target, Transform parent = null)
        {
            var collider = PolicyChild(name, parent).AddComponent<VRCPhysBoneCollider>();
            collider.rootTransform = target; collider.globalCollision = VRCPhysBoneBase.AdvancedBool.False;
            return collider;
        }

        private BuildContext OptimizePhysics()
        {
            var context = new BuildContext(root, null);
            PhysBoneOptimizationPass.Capture(context); PhysBoneOptimizationPass.Prepare(context); PhysBoneOptimizationPass.Apply(context);
            return context;
        }

        [Test]
        public void ColliderSharingPreservesEachSimulationListOrderAndMultiplicity()
        {
            MaterialDescriptor(); root.AddComponent<ALTOTraceAndOptimizePhysBones>();
            var target = PolicyChild("Target").transform;
            var a = LocalCollider("First collider", target); var b = LocalCollider("Second collider", target);
            var unrelated = LocalCollider("Different collider", target); unrelated.radius += 0.25f;
            var first = PhysBone("First simulation"); var second = PhysBone("Second simulation");
            first.colliders.Add(a); second.colliders.AddRange(new[] { unrelated, b, null, b });
            var secondOwner = b.gameObject;
            OptimizePhysics();
            Assert.That(a != null && b == null && unrelated != null, Is.True);
            Assert.That(first.colliders, Is.EqualTo(new[] { a }));
            Assert.That(second.colliders, Is.EqualTo(new[] { unrelated, a, null, a }));
            Assert.That(secondOwner != null, Is.True, "Hierarchy collection is a separate pass.");
        }

        [Test]
        public void ColliderSharingDoesNotCollapseTwoApplicationsInOneSimulation()
        {
            MaterialDescriptor(); root.AddComponent<ALTOTraceAndOptimizePhysBones>();
            var target = PolicyChild("Target").transform;
            var a = LocalCollider("First", target); var b = LocalCollider("Second", target); var c = LocalCollider("Third", target);
            var first = PhysBone("First simulation"); var second = PhysBone("Second simulation");
            first.colliders.AddRange(new[] { a, b }); second.colliders.AddRange(new[] { b, c });
            OptimizePhysics();
            Assert.That(a != null && b != null && c == null, Is.True);
            Assert.That(first.colliders, Is.EqualTo(new[] { a, b }));
            Assert.That(second.colliders, Is.EqualTo(new[] { b, a }));
        }

        [TestCase("Radius")] [TestCase("Rotation")] [TestCase("Root")] [TestCase("Global")]
        [TestCase("Disabled")] [TestCase("InactiveParent")] [TestCase("ExplicitPreserve")]
        [TestCase("PreservedConsumer")] [TestCase("ExternalReader")]
        public void ColliderSharingRetainsDistinctOrProtectedInterfaces(string difference)
        {
            MaterialDescriptor(); var settings = root.AddComponent<ALTOTraceAndOptimizePhysBones>();
            var target = PolicyChild("Target").transform; var container = PolicyChild("Container");
            var a = LocalCollider("First", target); var b = LocalCollider("Second", target, container.transform);
            var first = PhysBone("First simulation"); var second = PhysBone("Second simulation");
            first.colliders.Add(a); second.colliders.Add(b);
            if (difference == "Radius") b.radius += 0.001f;
            if (difference == "Rotation") b.rotation = Quaternion.Euler(1, 2, 3);
            if (difference == "Root") b.rootTransform = container.transform;
            if (difference == "Global") a.globalCollision = b.globalCollision = VRCPhysBoneBase.AdvancedBool.True;
            if (difference == "Disabled") b.enabled = false;
            if (difference == "InactiveParent") container.SetActive(false);
            if (difference == "ExplicitPreserve") settings.PreserveColliders.Add(b);
            if (difference == "PreservedConsumer") settings.PreservePhysBones.Add(second);
            if (difference == "ExternalReader") PolicyChild("Reader").AddComponent<DependencyAuthoringFixture>().Target = b;
            OptimizePhysics();
            Assert.That(a != null && b != null, Is.True);
            Assert.That(first.colliders.Single(), Is.SameAs(a)); Assert.That(second.colliders.Single(), Is.SameAs(b));
        }

        [TestCase("Owner")] [TestCase("Collider")] [TestCase("List")]
        public void ColliderSharingPreservesAnimatorWritesAndReferences(string mode)
        {
            var target = PolicyChild("Target").transform; var container = PolicyChild("Container");
            var a = LocalCollider("First", target); var b = LocalCollider("Second", target, container.transform);
            var first = PhysBone("First simulation"); var second = PhysBone("Second simulation");
            first.colliders.Add(a); second.colliders.Add(b);
            var clip = Clip();
            if (mode == "Owner") AnimationUtility.SetEditorCurve(clip,
                EditorCurveBinding.FloatCurve("Container", typeof(GameObject), "m_IsActive"), AnimationCurve.Constant(0, 1, 1));
            if (mode == "Collider") AnimationUtility.SetEditorCurve(clip,
                EditorCurveBinding.FloatCurve("Container/Second", typeof(VRCPhysBoneCollider), "radius"), AnimationCurve.Constant(0, 1, b.radius));
            if (mode == "List") AnimationUtility.SetObjectReferenceCurve(clip,
                EditorCurveBinding.PPtrCurve("Second simulation", typeof(VRCPhysBone), "colliders.Array.data[0]"),
                new[] { new ObjectReferenceKeyframe { value = b } });
            MaterialDescriptor(Controller(clip)); root.AddComponent<ALTOTraceAndOptimizePhysBones>(); OptimizePhysics();
            Assert.That(a != null && b != null, Is.True);
            Assert.That(second.colliders.Single(), Is.SameAs(b));
        }

        [TestCase(true)] [TestCase(false)]
        public void ColliderSharingProtectsComponentsAndConsumersOutsideScope(bool consumerOutside)
        {
            MaterialDescriptor(); var settings = root.AddComponent<ALTOTraceAndOptimizePhysBones>();
            var scope = PolicyChild("Scope").transform; settings.ScopeRoot = scope;
            var a = LocalCollider("First", scope, scope); var b = LocalCollider("Second", scope, consumerOutside ? scope : root.transform);
            var first = PhysBone("First simulation", scope); var second = PhysBone("Second simulation", consumerOutside ? root.transform : scope);
            first.colliders.Add(a); second.colliders.Add(b); OptimizePhysics();
            Assert.That(a != null && b != null, Is.True); Assert.That(second.colliders.Single(), Is.SameAs(b));
        }

        [Test]
        public void ColliderSharingUsesEffectiveRootWithoutConflatingIndependentRoots()
        {
            MaterialDescriptor(); root.AddComponent<ALTOTraceAndOptimizePhysBones>();
            var a = LocalCollider("First", null); var b = LocalCollider("Second", a.transform);
            var first = PhysBone("First simulation"); var second = PhysBone("Second simulation");
            first.colliders.Add(a); second.colliders.Add(b); OptimizePhysics();
            Assert.That(a != null && b == null, Is.True); Assert.That(second.colliders.Single(), Is.SameAs(a));
        }

        [Test]
        public void PhysicsProtectionFollowsExternalComponentMapping()
        {
            var first = PhysBone("First"); var second = PhysBone("Protected input");
            var state = new PhysBoneOptimizationState { Component = root.AddComponent<ALTOTraceAndOptimizePhysBones>(),
                Scope = new OptimizationBuildScope { Root = root.transform } };
            state.Inputs.Add((first, false)); state.Inputs.Add((second, true));
            state.Component.BuildInputs = new Component[] { first, first }; Object.DestroyImmediate(second);
            Assert.That(PhysBoneOptimizationPass.Protected(state, first, new ObjectOptimizationState()), Is.True);
        }

        [TestCase("Static")] [TestCase("TipAnimation")] [TestCase("AncestorScale")]
        [TestCase("SeparateOwnerScale")] [TestCase("AncestorRotation")]
        [TestCase("OwnSettings")] [TestCase("OverlappingPhysics")]
        [TestCase("Constraint")] [TestCase("Preserved")] [TestCase("ExternalReader")]
        public void IsAnimatedOptimizationRequiresNoExternalChainOrScaleWriters(string mode)
        {
            var parent = PolicyChild("Parent").transform; var bone = PhysBone("Simulation", parent); bone.isAnimated = true;
            var owner = PolicyChild("Owner");
            if (mode == "SeparateOwnerScale")
            {
                var target = bone.transform; Object.DestroyImmediate(bone);
                bone = owner.AddComponent<VRCPhysBone>(); bone.rootTransform = target; bone.isAnimated = true;
            }
            if (mode == "OverlappingPhysics") parent.gameObject.AddComponent<VRCPhysBone>().ignoreOtherPhysBones = false;
            if (mode == "Constraint") bone.GetRootTransform().GetChild(0).gameObject.AddComponent<UnityEngine.Animations.PositionConstraint>();
            if (mode == "ExternalReader") PolicyChild("Reader").AddComponent<DependencyAuthoringFixture>().Target = bone;
            var clip = Clip();
            if (mode == "TipAnimation") AnimationUtility.SetEditorCurve(clip,
                EditorCurveBinding.FloatCurve("Parent/Simulation/Tip", typeof(Transform), "localEulerAnglesRaw.x"), AnimationCurve.Constant(0, 1, 30));
            if (mode == "AncestorScale" || mode == "SeparateOwnerScale" || mode == "AncestorRotation")
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(mode == "SeparateOwnerScale" ? "Owner" : "Parent", typeof(Transform),
                    mode == "AncestorRotation" ? "localEulerAnglesRaw.y" : "localScale.x"), AnimationCurve.Linear(0, 0, 1, 1));
            if (mode == "OwnSettings") AnimationUtility.SetEditorCurve(clip,
                EditorCurveBinding.FloatCurve("Parent/Simulation", typeof(VRCPhysBone), "isAnimated"), AnimationCurve.Constant(0, 1, 1));
            MaterialDescriptor(Controller(clip)); var settings = root.AddComponent<ALTOTraceAndOptimizePhysBones>();
            settings.ShareEquivalentColliders = false;
            if (mode == "Preserved") settings.PreservePhysBones.Add(bone);
            OptimizePhysics();
            Assert.That(bone.isAnimated, Is.EqualTo(mode != "Static" && mode != "AncestorRotation"));
        }

        [Test]
        public void IsAnimatedOptimizationCanBeDisabledIndependentlyOfColliderSharing()
        {
            MaterialDescriptor(); var settings = root.AddComponent<ALTOTraceAndOptimizePhysBones>(); settings.OptimizeIsAnimated = false;
            var bone = PhysBone("Simulation"); bone.isAnimated = true; OptimizePhysics();
            Assert.That(bone.isAnimated, Is.True);
        }
    }
}
