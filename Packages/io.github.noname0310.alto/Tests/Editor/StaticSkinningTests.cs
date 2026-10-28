using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.Meshes;
using Noname.AvatarTools.ObjectOptimization;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        [TestCase(false, 1)] [TestCase(true, 1)] [TestCase(false, 5)] [TestCase(true, 5)]
        public void StaticSkinningPreservesNativeDeformationAcrossParentPoses(bool collapsed, int influences)
        {
            var parent = PolicyChild("Moving parent").transform;
            var bones = Enumerable.Range(0, influences).Select(i => PolicyChild("Helper " + i).transform).ToArray();
            foreach (var bone in bones)
            {
                bone.SetParent(parent, false); bone.localPosition = new Vector3(0.1f, 0.2f, -0.1f);
                bone.localRotation = Quaternion.Euler(12, 25, -7); bone.localScale = new Vector3(1.2f, 0.7f, 0.9f);
            }
            var source = AssemblyPart("Source", bones, Vector3.left); var mesh = source.sharedMesh;
            var originalPoses = mesh.bindposes; var originalBones = source.bones;
            Frame(mesh, "Morph", 50, Vector3.up * 0.1f); Frame(mesh, "Morph", 100, Vector3.right * 0.2f);
            source.SetBlendShapeWeight(0, 145);
            if (collapsed) bones[0].localScale = Vector3.zero;
            var plan = StaticSkinningPlan.Create(source, bone => bone != parent);
            Assert.That(plan.ReboundEntries, Is.EqualTo(influences)); Assert.That(plan.Bones, Is.All.EqualTo(parent));
            using var assembly = SkinnedMeshAssembler.Create(new[] { source }, false,
                skinning: new Dictionary<SkinnedMeshRenderer, StaticSkinningPlan> { [source] = plan });
            var output = AssemblyOutput(assembly);
            for (int pose = 0; pose < 4; pose++)
            {
                parent.localPosition = new Vector3(pose * 0.1f, 0, 0);
                parent.localRotation = Quaternion.Euler(pose * 20, pose * -15, pose * 12);
                parent.localScale = pose == 3 ? Vector3.zero : new Vector3(1 + pose * 0.1f, 1 - pose * 0.1f, 1);
                var before = AssemblyWorldVertices(source); var after = AssemblyWorldVertices(output);
                for (int vertex = 0; vertex < before.Length; vertex++)
                    Assert.That(Vector3.Distance(before[vertex], after[vertex]), Is.LessThan(0.00001f), "Pose " + pose + ", vertex " + vertex);
                using var left = new TemporaryBakedMesh(source); using var right = new TemporaryBakedMesh(output);
                for (int vertex = 0; vertex < left.Mesh.vertexCount; vertex++)
                {
                    Assert.That(Vector3.Distance(left.Mesh.normals[vertex], right.Mesh.normals[vertex]), Is.LessThan(0.00001f), "Normal");
                    Assert.That(Vector4.Distance(left.Mesh.tangents[vertex], right.Mesh.tangents[vertex]), Is.LessThan(0.00001f), "Tangent");
                }
            }
            Assert.That(source.bones, Is.EqualTo(originalBones)); Assert.That(mesh.bindposes, Is.EqualTo(originalPoses));
            Assert.That(source.GetBlendShapeWeight(0), Is.EqualTo(145)); Assert.That(source.rootBone, Is.EqualTo(root.transform));
        }

        private sealed class TemporaryBakedMesh : IDisposable
        {
            internal readonly Mesh Mesh = new();
            internal TemporaryBakedMesh(SkinnedMeshRenderer renderer) { renderer.BakeMesh(Mesh); }
            public void Dispose() => UnityEngine.Object.DestroyImmediate(Mesh);
        }

        [TestCase("Animation")] [TestCase("PhysBone")] [TestCase("Constraint")] [TestCase("Rigidbody")]
        [TestCase("RootMotion")] [TestCase("Descriptor")] [TestCase("ObjectKey")]
        public void StaticSkinningRetainsRuntimeTransformWriters(string writer)
        {
            var bone = PolicyChild("Bone").transform; var child = PolicyChild("Child").transform; child.SetParent(bone, false);
            var clip = Clip();
            if (writer == "Animation") AnimationUtility.SetEditorCurve(clip,
                EditorCurveBinding.FloatCurve("Bone", typeof(Transform), "m_LocalScale.x"), AnimationCurve.Constant(0, 1, 1));
            if (writer == "PhysBone") bone.gameObject.AddComponent<VRCPhysBone>();
            if (writer == "Constraint") bone.gameObject.AddComponent<PositionConstraint>();
            if (writer == "Rigidbody") bone.gameObject.AddComponent<Rigidbody>();
            if (writer == "RootMotion") bone.gameObject.AddComponent<Animator>().applyRootMotion = true;
            if (writer == "Descriptor")
            {
                var descriptor = root.AddComponent<VRCAvatarDescriptor>(); var eye = descriptor.customEyeLookSettings;
                eye.leftEye = bone; descriptor.customEyeLookSettings = eye;
            }
            if (writer == "ObjectKey") AnimationUtility.SetObjectReferenceCurve(clip,
                EditorCurveBinding.PPtrCurve("", typeof(SkinnedMeshRenderer), "m_RootBone"), new[] { new ObjectReferenceKeyframe { value = bone } });
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(clip));
            var index = AvatarAnimationIndex.Create(root, new Dictionary<object, VirtualAnimatorController> { [root] = controller }, null);
            var mutation = TransformMutationAnalysis.Create(root, index);
            Assert.That(mutation.Uncertain, Is.Null); Assert.That(mutation.Mutable, Does.Contain(bone));
            Assert.That(mutation.Mutable.Contains(child), Is.EqualTo(writer == "PhysBone" || writer == "RootMotion"));
        }

        [Test]
        public void StaticSkinningRejectsUnknownBehaviourSideEffects()
        {
            var source = Controller(Clip());
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var state = controller.Layers.First().StateMachine.DefaultState;
            state.Behaviours = state.Behaviours.Add(Track(ScriptableObject.CreateInstance<UnsupportedReachabilityBehaviour>()));
            var index = AvatarAnimationIndex.Create(root, new Dictionary<object, VirtualAnimatorController> { [root] = controller }, null);
            Assert.That(TransformMutationAnalysis.Create(root, index).Uncertain, Does.Contain("StateMachineBehaviour"));
        }

        [TestCase(false)] [TestCase(true)]
        public void StaticSkinningBuildPassRespectsPreservationAndCollectsUnusedHelpers(bool preserved)
        {
            var pair = MergePair(); var bone = pair.First.bones[0]; var source = pair.First.sharedMesh;
            var before = AssemblyWorldVertices(pair.First);
            var descriptor = root.AddComponent<VRCAvatarDescriptor>();
            descriptor.baseAnimationLayers = Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>();
            descriptor.specialAnimationLayers = Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>();
            var settings = root.AddComponent<ALTOTraceAndOptimizeMeshes>();
            settings.MergeCompatibleSkinnedMeshes = false; settings.MergeIdenticalMaterialSlots = false; settings.RemoveUnusedMeshData = false;
            var collection = root.AddComponent<ALTOTraceAndOptimizeObjects>();
            if (preserved) collection.PreserveComponents.Add(bone);
            var context = new BuildContext(root, null);
            ObjectOptimizationPass.Capture(context); MeshOptimizationPass.Capture(context); MeshOptimizationPass.Prepare(context); MeshOptimizationPass.Apply(context);
            foreach (var part in new[] { pair.First, pair.Second }) if (part.sharedMesh != source) Track(part.sharedMesh);
            OptimizationPipeline.AfterMeshes(context);
            Assert.That(bone != null, Is.EqualTo(preserved)); Assert.That(pair.First.bones[0], Is.EqualTo(preserved ? bone : root.transform));
            var after = AssemblyWorldVertices(pair.First);
            for (int vertex = 0; vertex < before.Length; vertex++) Assert.That(Vector3.Distance(before[vertex], after[vertex]), Is.LessThan(0.00001f));
            Assert.That(source.bindposes.Length, Is.EqualTo(1)); Assert.That(pair.First.sharedMesh == source, Is.EqualTo(preserved));
        }
    }
}
