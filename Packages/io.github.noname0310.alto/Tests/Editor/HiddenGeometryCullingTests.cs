using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.Meshes;
using Noname.AvatarTools.ObjectOptimization;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        [TestCase(AnimatorCullingMode.AlwaysAnimate, 2, false)]
        [TestCase(AnimatorCullingMode.CullUpdateTransforms, 1, false)]
        [TestCase(AnimatorCullingMode.CullCompletely, 1, false)]
        [TestCase(AnimatorCullingMode.AlwaysAnimate, 2, true)]
        [TestCase(AnimatorCullingMode.CullCompletely, 0, true)]
        public void HiddenGeometryCulling_RetainsLastPrimitiveForNativeAnimatorVisibility(AnimatorCullingMode culling, int expected, bool splitSlots)
        {
            string directory = "Assets/__ALTOHiddenCulling_" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(directory);
            string path = directory + "/Probe.shader";
            try
            {
                File.WriteAllText(path, "Shader \"Hidden/ALTO/HiddenCullingProbe\" { SubShader { Pass {\n" +
                    "HLSLPROGRAM\n#pragma target 4.5\n#pragma vertex vert\n#pragma fragment frag\n" +
                    "float4 vert(float4 p:POSITION):SV_POSITION{return p;}\n" +
                    "float4 frag():SV_Target{return 1;}\nENDHLSL\n} } Fallback Off }");
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                renderer.sharedMaterial = Track(new Material(AssetDatabase.LoadAssetAtPath<Shader>(path)));
                var mesh = HiddenGeometryMesh(); mesh.vertices = Enumerable.Repeat(Vector3.zero, 6).ToArray();
                if (splitSlots)
                {
                    mesh.subMeshCount = 2;
                    mesh.SetIndices(new[] { 0, 1, 2 }, MeshTopology.Triangles, 0);
                    mesh.SetIndices(new[] { 3, 4, 5 }, MeshTopology.Triangles, 1);
                    renderer.sharedMaterials = new[] { renderer.sharedMaterial, renderer.sharedMaterial };
                }
                mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 2); renderer.localBounds = mesh.bounds;
                root.AddComponent<Animator>().cullingMode = culling;
                var index = FrameIndex(); var scope = new OptimizationBuildScope { Root = root.transform };
                var settings = new MeshOptimizationState { Scope = scope, RemoveHiddenGeometry = true };
                var objects = new ObjectOptimizationState { Scope = scope };
                var shaders = new ShaderPropertyUsage();
                var references = AvatarDependencyGraph.Create(root, index, objects, false, shaders);
                var messages = new List<string>();
                Assert.That(PermanentlyHiddenGeometry.Apply(root, new[] { renderer }, settings, objects, index,
                    references, shaders, BuildTarget.StandaloneWindows64, copy => Track(copy), messages), Is.EqualTo(expected),
                    string.Join("\n", messages));
                Assert.That(Enumerable.Range(0, mesh.subMeshCount).Sum(slot => (long)renderer.sharedMesh.GetIndexCount(slot)),
                    Is.EqualTo((2 - expected) * 3));
                Assert.That(renderer.sharedMesh.vertexCount, Is.EqualTo(mesh.vertexCount));
                Assert.That(renderer.sharedMesh.bounds, Is.EqualTo(mesh.bounds));
                Assert.That(renderer.localBounds, Is.EqualTo(mesh.bounds));
                Assert.That(Enumerable.Range(0, mesh.subMeshCount).Sum(slot => (long)mesh.GetIndexCount(slot)),
                    Is.EqualTo(6), "The source mesh remains untouched.");
                if (culling != AnimatorCullingMode.AlwaysAnimate)
                    Assert.That(PermanentlyHiddenGeometry.Apply(root, new[] { renderer }, settings, objects, FrameIndex(),
                        references, shaders, BuildTarget.StandaloneWindows64, copy => Track(copy), messages), Is.Zero,
                        "A second pass must retain the final culling primitive.");
            }
            finally { AssetDatabase.DeleteAsset(directory); }
        }
    }
}
