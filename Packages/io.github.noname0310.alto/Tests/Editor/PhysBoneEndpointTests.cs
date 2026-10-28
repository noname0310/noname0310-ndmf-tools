using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Dynamics.PhysBone.Components;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private static Dictionary<Transform, (Transform Parent, Vector3 Position, Quaternion Rotation, Vector3 Scale, float BoneRatio, float TransformRatio)>
            PhysicsRestState(VRCPhysBone physics)
        {
            physics.InitTransforms(true);
            return physics.bones.ToDictionary(b => b.transform, b =>
                (b.parentIndex < 0 ? null : physics.bones[b.parentIndex].transform,
                    b.restPosition, b.restRotation, b.restScale, physics.CalcBoneRatio(b.boneChainIndex), physics.CalcTransformRatio(b.boneChainIndex)));
        }

        [TestCase(1)] [TestCase(3)]
        public void UnusedEndpointsKeepTheSdkRestPoseAndCurveCoordinatesOfRemainingBones(int branchCount)
        {
            MaterialDescriptor(); root.AddComponent<ALTOTraceAndOptimizePhysBones>();
            var physics = PolicyChild("Physics").AddComponent<VRCPhysBone>();
            physics.transform.localScale = new Vector3(0.7f, 1.3f, 0.8f);
            physics.transform.localRotation = Quaternion.Euler(13, 23, 31);
            var leaves = new List<Transform>();
            for (int i = 0; i < branchCount; i++)
            {
                var branch = PolicyChild("Branch " + i, physics.transform).transform;
                branch.localPosition = Vector3.right * i + Vector3.up;
                var middle = PolicyChild("Middle", branch).transform; middle.localPosition = Vector3.up;
                var leaf = PolicyChild("Tip", middle).transform; leaf.localPosition = new Vector3(0.1f, 0.7f, 0.2f); leaves.Add(leaf);
            }
            var before = PhysicsRestState(physics); int originalLength = physics.maxBoneChainIndex;
            var leafPositions = leaves.Select(t => t.position).ToArray(); var parents = leaves.Select(t => t.parent).ToArray();
            OptimizePhysics();
            Assert.That(leaves.All(t => t == null), Is.True);
            Assert.That(physics.endpointPosition, Is.EqualTo(new Vector3(0.1f, 0.7f, 0.2f)));
            var after = PhysicsRestState(physics);
            Assert.That(after.Count, Is.EqualTo(before.Count - leaves.Count));
            Assert.That(physics.maxBoneChainIndex + 1, Is.EqualTo(originalLength));
            foreach (var pair in after) Assert.That(pair.Value, Is.EqualTo(before[pair.Key]), pair.Key.name);
            for (int i = 0; i < parents.Length; i++)
                Assert.That(Vector3.Distance(parents[i].TransformPoint(physics.endpointPosition), leafPositions[i]), Is.LessThan(0.000001f));
        }

        [TestCase("Skinning")] [TestCase("ExternalReader")] [TestCase("AnimatedTransform")] [TestCase("AnimatedActive")]
        [TestCase("ExtraComponent")] [TestCase("Rotation")] [TestCase("Scale")] [TestCase("Stretch")]
        [TestCase("ExistingEndpoint")] [TestCase("IgnoredBranch")] [TestCase("OverlappingPhysics")]
        [TestCase("Fork")] [TestCase("DifferentOffsets")] [TestCase("Preserved")]
        public void EndpointReplacementKeepsUnsupportedOrObservableLeaves(string mode)
        {
            var physics = PhysBone("Physics"); var leaf = physics.transform.GetChild(0);
            if (mode == "Skinning") renderer.bones = new[] { leaf };
            if (mode == "ExternalReader") PolicyChild("Reader").AddComponent<DependencyAuthoringFixture>().Target = leaf;
            if (mode == "ExtraComponent") leaf.gameObject.AddComponent<Camera>();
            if (mode == "Rotation") leaf.localRotation = Quaternion.Euler(1, 2, 3);
            if (mode == "Scale") leaf.localScale = Vector3.one * 2;
            if (mode == "Stretch") physics.maxStretch = 0.5f;
            if (mode == "ExistingEndpoint") physics.endpointPosition = Vector3.up;
            if (mode == "IgnoredBranch") physics.ignoreTransforms.Add(PolicyChild("Ignored", physics.transform).transform);
            if (mode == "OverlappingPhysics") root.AddComponent<VRCPhysBone>().ignoreOtherPhysBones = false;
            if (mode == "Fork") PolicyChild("Other tip", physics.transform).transform.localPosition = Vector3.up;
            if (mode == "DifferentOffsets")
            {
                var parent = PolicyChild("Other branch", physics.transform).transform;
                PolicyChild("Other tip", parent).transform.localPosition = Vector3.up * 2;
            }
            var clip = Clip();
            if (mode == "AnimatedTransform") AnimationUtility.SetEditorCurve(clip,
                EditorCurveBinding.FloatCurve("Physics/Tip", typeof(Transform), "m_LocalPosition.x"), AnimationCurve.Linear(0, 0, 1, 1));
            if (mode == "AnimatedActive") AnimationUtility.SetEditorCurve(clip,
                EditorCurveBinding.FloatCurve("Physics/Tip", typeof(GameObject), "m_IsActive"), AnimationCurve.Constant(0, 1, 0));
            MaterialDescriptor(Controller(clip)); var settings = root.AddComponent<ALTOTraceAndOptimizePhysBones>();
            if (mode == "Preserved") settings.PreservePhysBones.Add(physics);
            OptimizePhysics();
            Assert.That(leaf != null, Is.True);
        }

        [Test]
        public void EndpointReplacementDoesNotRemoveLeavesOutsideTheComponentScope()
        {
            MaterialDescriptor(); var physics = PhysBone("Physics"); var leaf = physics.transform.GetChild(0);
            var scope = PolicyChild("Scope"); var component = scope.AddComponent<ALTOTraceAndOptimizePhysBones>();
            var localPhysics = scope.AddComponent<VRCPhysBone>(); localPhysics.rootTransform = physics.transform;
            UnityEngine.Object.DestroyImmediate(physics);
            OptimizePhysics();
            Assert.That(leaf != null && localPhysics.endpointPosition == Vector3.zero, Is.True);
        }
    }
}
