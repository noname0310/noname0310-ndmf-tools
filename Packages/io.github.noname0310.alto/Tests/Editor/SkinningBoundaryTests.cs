using System.Linq;
using nadena.dev.ndmf;
using Noname.AvatarTools.Meshes;
using NUnit.Framework;
using UnityEngine;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        [TestCase("None")] [TestCase("Scale")] [TestCase("BindPose")]
        public void SkinningBoundariesRetainSharedInputsOnlyWhenInversionIsUnsafe(string singular)
        {
            var parent = PolicyChild("Parent").transform;
            var first = PolicyChild("First", parent).transform; var second = PolicyChild("Second", parent).transform;
            var a = AssemblyPart("A", new[] { first }, Vector3.left);
            var b = AssemblyPart("B", new[] { second }, Vector3.right);
            if (singular == "Scale") first.localScale = Vector3.zero;
            if (singular == "BindPose") a.sharedMesh.bindposes = new[] { Matrix4x4.Scale(new Vector3(0, 1, 1)) };
            var vertices = a.sharedMesh.vertices; var bindposes = a.sharedMesh.bindposes;
            var result = SkinningBoundaryDependencies.Collect(root.transform, new[] { a, b });
            Assert.That(result, Is.EquivalentTo(singular == "None" ? new Transform[0] : new[] { parent, first, second }));
            Assert.That(a.sharedMesh.vertices, Is.EqualTo(vertices)); Assert.That(a.sharedMesh.bindposes, Is.EqualTo(bindposes));
            Assert.That(first.localScale, Is.EqualTo(singular == "Scale" ? Vector3.zero : Vector3.one));
        }

        [Test]
        public void UnusedSingularBoneEntriesDoNotRetainDeadHierarchy()
        {
            var used = PolicyChild("Used").transform; var unused = PolicyChild("Unused").transform;
            var input = AssemblyPart("Input", new[] { used }, Vector3.zero);
            input.bones = new[] { used, unused }; input.sharedMesh.bindposes = new[] { Matrix4x4.identity, Matrix4x4.zero };
            unused.localScale = Vector3.zero;
            Assert.That(SkinningBoundaryDependencies.Collect(root.transform, new[] { input }), Is.Empty);
        }

        [TestCase(false)] [TestCase(true)]
        public void SkinningBoundaryCaptureHonorsTheOwnedScopeAndStaticSkinningOption(bool rebind)
        {
            MaterialDescriptor(); var scope = PolicyChild("Scope");
            var bone = PolicyChild("Bone").transform;
            var input = AssemblyPart("Input", new[] { bone }, Vector3.zero); input.transform.SetParent(scope.transform);
            bone.localScale = Vector3.zero;
            var outside = PolicyChild("Outside bone").transform;
            AssemblyPart("Outside", new[] { outside }, Vector3.up);
            var settings = scope.AddComponent<ALTOTraceAndOptimizeMeshes>(); settings.RebindStaticBones = rebind;
            var context = new BuildContext(root, null); MeshOptimizationPass.Capture(context); MeshOptimizationPass.Prepare(context);
            Assert.That(settings.BuildSkinningDependencies.Contains(bone), Is.EqualTo(rebind));
            Assert.That(settings.BuildSkinningDependencies.Contains(outside), Is.False);
        }
    }
}
