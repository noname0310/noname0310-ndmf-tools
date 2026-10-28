using System.Linq;
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
        private ALTOTraceAndOptimizePhysBones SimulationSettings()
        {
            var settings = root.AddComponent<ALTOTraceAndOptimizePhysBones>();
            settings.ShareEquivalentColliders = settings.OptimizeIsAnimated = settings.ReplaceUnusedEndBones = false;
            return settings;
        }

        private VRCPhysBone MergeableSimulation(string name, Transform parent = null)
        {
            var bone = PhysBone(name, parent);
            bone.multiChildType = VRCPhysBoneBase.MultiChildType.Ignore;
            bone.allowGrabbing = bone.allowPosing = VRCPhysBoneBase.AdvancedBool.False;
            return bone;
        }

        [TestCase(false)] [TestCase(true)]
        public void SimulationMergingRetainsBoneObjectsWorldTransformsAndSdkRestData(bool collapsedParent)
        {
            MaterialDescriptor(); SimulationSettings();
            var parent = PolicyChild("Parent").transform; parent.localPosition = Vector3.right;
            parent.localRotation = Quaternion.Euler(13, 27, 42); parent.localScale = new Vector3(collapsedParent ? 0 : 0.7f, 1.2f, 0.9f);
            var a = MergeableSimulation("A", parent); var b = MergeableSimulation("B", parent);
            var before = PhysicsRestState(a).Concat(PhysicsRestState(b)).ToDictionary(p => p.Key, p => p.Value);
            var transforms = before.Keys.ToArray(); renderer.bones = transforms;
            var matrices = transforms.Select(t => t.localToWorldMatrix).ToArray();
            OptimizePhysics();
            var remaining = root.GetComponentsInChildren<VRCPhysBone>(true);
            Assert.That(remaining.Length, Is.EqualTo(1)); Assert.That(a == null && b == null, Is.True);
            var merged = remaining[0]; Assert.That(merged.GetRootTransform(), Is.SameAs(merged.transform));
            Assert.That(merged.transform.parent, Is.SameAs(parent)); Assert.That(merged.multiChildType, Is.EqualTo(VRCPhysBoneBase.MultiChildType.Ignore));
            Assert.That(renderer.bones, Is.EqualTo(transforms));
            var after = PhysicsRestState(merged);
            Assert.That(after.Keys.Except(before.Keys).Single(), Is.SameAs(merged.transform));
            for (int i = 0; i < transforms.Length; i++)
            {
                var target = transforms[i]; var old = before[target]; var current = after[target];
                Assert.That(target.localToWorldMatrix, Is.EqualTo(matrices[i]));
                Assert.That(current.Position, Is.EqualTo(old.Position)); Assert.That(current.Rotation, Is.EqualTo(old.Rotation));
                Assert.That(current.Scale, Is.EqualTo(old.Scale));
                Assert.That(current.Parent, Is.SameAs(old.Parent != null ? old.Parent : merged.transform));
            }
        }

        [TestCase("Grabbing")] [TestCase("Posing")] [TestCase("Parameter")]
        [TestCase("Curve")] [TestCase("Radius")] [TestCase("Stretch")]
        [TestCase("MultiChild")] [TestCase("Position")] [TestCase("Rotation")] [TestCase("Scale")]
        [TestCase("Animated")] [TestCase("ExternalReader")] [TestCase("Preserved")]
        [TestCase("Overlap")] [TestCase("Toggle")] [TestCase("Disabled")]
        [TestCase("SeparateOwner")] [TestCase("Layer")]
        public void SimulationMergingRetainsDifferentOrObservableSimulationSemantics(string mode)
        {
            var a = MergeableSimulation("A"); var b = MergeableSimulation("B");
            if (mode == "Grabbing") a.allowGrabbing = b.allowGrabbing = VRCPhysBoneBase.AdvancedBool.True;
            if (mode == "Posing") a.allowPosing = b.allowPosing = VRCPhysBoneBase.AdvancedBool.True;
            if (mode == "Parameter") a.parameter = b.parameter = "Motion";
            if (mode == "Curve") { a.pullCurve = AnimationCurve.Linear(0, 0, 1, 1); b.pullCurve = AnimationCurve.Linear(0, 0, 1, 1); }
            if (mode == "Radius") b.radius += 0.01f;
            if (mode == "Stretch") a.maxStretch = b.maxStretch = 0.1f;
            if (mode == "MultiChild") a.multiChildType = b.multiChildType = VRCPhysBoneBase.MultiChildType.Average;
            if (mode == "Position") b.transform.localPosition = Vector3.right;
            if (mode == "Rotation") b.transform.localRotation = Quaternion.Euler(0, 10, 0);
            if (mode == "Scale") b.transform.localScale = Vector3.one * 2;
            if (mode == "ExternalReader") PolicyChild("Reader").AddComponent<DependencyAuthoringFixture>().Target = b;
            if (mode == "Overlap") root.AddComponent<VRCPhysBone>().ignoreOtherPhysBones = false;
            if (mode == "Disabled") b.enabled = false;
            if (mode == "SeparateOwner") b.rootTransform = PolicyChild("Separate chain").transform;
            if (mode == "Layer") b.gameObject.layer = 2;
            var clip = Clip();
            if (mode == "Animated") AnimationUtility.SetEditorCurve(clip,
                EditorCurveBinding.FloatCurve("B", typeof(VRCPhysBone), "radius"), AnimationCurve.Constant(0, 1, b.radius));
            if (mode == "Toggle") AnimationUtility.SetEditorCurve(clip,
                EditorCurveBinding.FloatCurve("B", typeof(GameObject), "m_IsActive"), AnimationCurve.Constant(0, 1, 1));
            MaterialDescriptor(Controller(clip)); var settings = SimulationSettings();
            if (mode == "Preserved") settings.PreservePhysBones.Add(b);
            OptimizePhysics();
            Assert.That(a != null && b != null, Is.True);
        }

        [Test]
        public void SimulationMergingPartitionsLargeGroupsWithoutChangingOriginalBoneCount()
        {
            MaterialDescriptor(); SimulationSettings();
            for (int i = 0; i < 65; i++) MergeableSimulation("Simulation " + i);
            OptimizePhysics();
            var merged = root.GetComponentsInChildren<VRCPhysBone>(true);
            Assert.That(merged.Length, Is.EqualTo(2));
            foreach (var bone in merged) { bone.InitTransforms(true); Assert.That(bone.bones.Count, Is.LessThanOrEqualTo(128)); }
            Assert.That(merged.Sum(b => b.bones.Count - 1), Is.EqualTo(130));
        }

        [Test]
        public void SimulationMergingRequiresBothTheComponentsAndNewOwnerInsideScope()
        {
            MaterialDescriptor(); var a = MergeableSimulation("A"); var b = MergeableSimulation("B");
            var settings = SimulationSettings(); settings.ScopeRoot = a.transform;
            OptimizePhysics(); Assert.That(a != null && b != null, Is.True);
        }
    }
}
