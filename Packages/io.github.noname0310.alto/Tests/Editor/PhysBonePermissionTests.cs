using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Build;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using VRC.Dynamics;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        [TestCase(0, true, true, -1, false)]
        [TestCase(1, false, false, 0, true)]
        [TestCase(2, false, false, 0, false)]
        [TestCase(2, true, false, 0, true)]
        [TestCase(2, false, true, 0, true)]
        [TestCase(2, false, false, 1, true)]
        [TestCase(2, false, false, 1 << 30, true)]
        [TestCase(99, false, false, 0, true)]
        public void GlobalColliderLivenessUsesEffectivePermissions(int mode, bool self, bool others, int flags, bool keep)
        {
            var collider = PermissionCollider(mode, self, others, flags);
            CollectObjects(ObjectSettings());
            Assert.That(collider != null, Is.EqualTo(keep));
        }

        [TestCase(0, true, true, -1, false)]
        [TestCase(1, false, false, 0, true)]
        [TestCase(2, false, false, 0, false)]
        [TestCase(2, true, false, 0, true)]
        [TestCase(2, false, true, 0, true)]
        [TestCase(2, false, false, 1, true)]
        [TestCase(2, false, false, 1 << 30, true)]
        [TestCase(99, false, false, 0, true)]
        public void OrphanColliderRemovalUsesEffectivePermissions(int mode, bool self, bool others, int flags, bool keep)
        {
            PhysFilter();
            var collider = PermissionCollider(mode, self, others, flags);
            var context = new BuildContext(root, null); PhysBoneFilterPass.Capture(context);
            ApplyPhysRemoval(context);
            Assert.That(collider != null, Is.EqualTo(keep));
        }

        private VRCPhysBoneCollider PermissionCollider(int mode, bool self = false, bool others = false, int flags = 0)
        {
            var collider = PolicyChild("Permission collider").AddComponent<VRCPhysBoneCollider>();
            collider.globalCollision = (VRCPhysBoneBase.AdvancedBool)mode;
            collider.globalCollisionAllowSelf = self;
            collider.globalCollisionAllowOthers = others;
            collider.globalCollisionFlags = (DynamicsUsageFlags)flags;
            return collider;
        }

        [TestCase("globalCollisionAllowSelf")]
        [TestCase("globalCollisionAllowOthers")]
        [TestCase("globalCollisionFlags")]
        public void AnimatedGlobalPermissionsRetainAnOtherwiseIneffectiveCollider(string property)
        {
            var collider = PermissionCollider(2);
            var binding = EditorCurveBinding.FloatCurve(collider.name, typeof(VRCPhysBoneCollider), property);
            var clip = StaticCurve(binding, 1);
            MaterialDescriptor(Controller(clip));
            PhysFilter();
            var context = new BuildContext(root, null); PhysBoneFilterPass.Capture(context);
            PhysBoneFilterPass.Apply(context);
            Assert.That(collider != null, Is.True, "The filter must retain animated permissions.");
            CollectObjects(ObjectSettings(), new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(clip)));
            Assert.That(collider != null, Is.True, "Whole-avatar liveness must retain animated permissions.");
        }

        [Test]
        public void IneffectiveGlobalPermissionsDoNotInvalidateExplicitLocalConsumers()
        {
            var collider = PermissionCollider(2);
            var bone = PhysBone("Local consumer"); bone.colliders.Add(collider);
            PhysFilter();
            var context = new BuildContext(root, null); PhysBoneFilterPass.Capture(context);
            ApplyPhysRemoval(context);
            Assert.That(collider != null, Is.True);
            CollectObjects(ObjectSettings());
            Assert.That(collider != null && bone != null, Is.True);
        }

        [TestCase(0)] [TestCase(2)]
        public void LocalColliderSharingIgnoresIneffectiveGlobalPermissionStorage(int mode)
        {
            MaterialDescriptor(); root.AddComponent<ALTOTraceAndOptimizePhysBones>();
            var target = PolicyChild("Target").transform;
            var first = LocalCollider("First", target);
            first.globalCollisionAllowSelf = first.globalCollisionAllowOthers = true;
            first.globalCollisionFlags = (DynamicsUsageFlags)(-1);
            var second = PermissionCollider(mode); second.rootTransform = target;
            var a = PhysBone("Consumer A"); var b = PhysBone("Consumer B");
            a.colliders.Add(first); b.colliders.Add(second);
            OptimizePhysics();
            Assert.That(first != null && second == null, Is.True);
            Assert.That(a.colliders.Single(), Is.SameAs(b.colliders.Single()));
        }

        [TestCase(false)] [TestCase(true)]
        public void SimulationMergingComparesEffectivePermissionSettings(bool collisionAllowed)
        {
            MaterialDescriptor(); SimulationSettings();
            var a = MergeableSimulation("A"); var b = MergeableSimulation("B");
            a.allowCollision = collisionAllowed ? VRCPhysBoneBase.AdvancedBool.True : VRCPhysBoneBase.AdvancedBool.False;
            a.collisionFilter = new VRCPhysBoneBase.PermissionFilter(!collisionAllowed, !collisionAllowed, (DynamicsUsageFlags)(collisionAllowed ? 0 : -1));
            a.grabFilter = a.poseFilter = new VRCPhysBoneBase.PermissionFilter(true, true, (DynamicsUsageFlags)(-1));
            b.allowCollision = b.allowGrabbing = b.allowPosing = VRCPhysBoneBase.AdvancedBool.Other;
            b.collisionFilter = new VRCPhysBoneBase.PermissionFilter(collisionAllowed, collisionAllowed, (DynamicsUsageFlags)(collisionAllowed ? -1 : 0));
            b.grabFilter = b.poseFilter = new VRCPhysBoneBase.PermissionFilter(false, false, 0);
            OptimizePhysics();
            Assert.That(root.GetComponentsInChildren<VRCPhysBone>(true).Length, Is.EqualTo(1));
        }

        [TestCase("GrabSelf")] [TestCase("PoseOthers")] [TestCase("GrabWorld")]
        [TestCase("CollisionSelf")] [TestCase("CollisionWorld")] [TestCase("Unknown")]
        public void SimulationMergingRetainsObservableOrUnknownPermissionDifferences(string mode)
        {
            MaterialDescriptor(); SimulationSettings();
            var a = MergeableSimulation("A"); var b = MergeableSimulation("B");
            a.allowCollision = b.allowCollision = VRCPhysBoneBase.AdvancedBool.Other;
            a.collisionFilter = new VRCPhysBoneBase.PermissionFilter(false, false, 0);
            b.collisionFilter = new VRCPhysBoneBase.PermissionFilter(false, false, 0);
            b.allowGrabbing = b.allowPosing = VRCPhysBoneBase.AdvancedBool.Other;
            b.grabFilter = new VRCPhysBoneBase.PermissionFilter(mode == "GrabSelf", false, (DynamicsUsageFlags)(mode == "GrabWorld" ? 1 : 0));
            b.poseFilter = new VRCPhysBoneBase.PermissionFilter(false, mode == "PoseOthers", 0);
            if (mode == "CollisionSelf") b.collisionFilter.allowSelf = true;
            if (mode == "CollisionWorld") b.collisionFilter.contentTypes = (DynamicsUsageFlags)1;
            if (mode == "Unknown") a.allowCollision = b.allowCollision = (VRCPhysBoneBase.AdvancedBool)99;
            OptimizePhysics();
            Assert.That(a != null && b != null, Is.True);
        }

        [Test]
        public void PhysBoneComponentResetAnimationPreventsSimulationMerging()
        {
            var a = MergeableSimulation("A"); var b = MergeableSimulation("B");
            var binding = EditorCurveBinding.FloatCurve("B", typeof(VRCPhysBone), "m_Enabled");
            var clip = StaticCurve(binding, 0);
            MaterialDescriptor(Controller(clip)); SimulationSettings();
            OptimizePhysics();
            Assert.That(a != null && b != null, Is.True);
            Assert.That(AnimationUtility.GetEditorCurve(clip, binding), Is.Not.Null);
        }

        [Test]
        public void ObservableRaycastPreservesItsSeparateOriginWithoutTreatingItAsAnOutput()
        {
            var origin = PolicyChild("Ray origin");
            var raycast = PolicyChild("Raycast").AddComponent<VRCRaycast>();
            raycast.RootTransform = origin.transform; raycast.Parameter = "Ray";
            var settings = ObjectSettings();
            CollectObjects(settings);
            Assert.That(raycast != null && origin != null, Is.True);
        }
    }
}
