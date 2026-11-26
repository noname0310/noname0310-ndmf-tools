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
using UnityEditor.Rendering;
using UnityEngine;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private Mesh HiddenGeometryMesh()
        {
            var mesh = Mesh();
            mesh.vertices = new[] { Vector3.zero, Vector3.zero, Vector3.zero, Vector3.right, Vector3.up, Vector3.forward };
            mesh.normals = Enumerable.Repeat(Vector3.forward, 6).ToArray();
            mesh.tangents = Enumerable.Repeat(new Vector4(1, 0, 0, 1), 6).ToArray();
            mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.zero, Vector2.right, Vector2.up };
            mesh.triangles = new[] { 0, 1, 2, 3, 4, 5 };
            return mesh;
        }

        [TestCase("Identical", true)] [TestCase("Morph", false)] [TestCase("Skin", false)]
        [TestCase("VertexId", false)] [TestCase("NormalDisplacement", false)] [TestCase("UvDisplacement", false)]
        [TestCase("NaN", false)] [TestCase("TinyDifference", false)]
        public void HiddenTriangleProof_RequiresEquivalentRemainingDeformation(string kind, bool expected)
        {
            var mesh = HiddenGeometryMesh(); var inputs = new ShaderPositionInputs(); inputs.Reads.Add(("POSITION", 0), 15);
            if (kind == "Morph")
            { var delta = new Vector3[6]; delta[1] = Vector3.right; mesh.AddBlendShapeFrame("Later", 100, delta, new Vector3[6], new Vector3[6]); }
            if (kind == "Skin") mesh.boneWeights = new[] { new BoneWeight { boneIndex0 = 0, weight0 = 1 },
                new BoneWeight { boneIndex0 = 1, weight0 = 1 }, new BoneWeight { boneIndex0 = 0, weight0 = 1 },
                new BoneWeight { boneIndex0 = 0, weight0 = 1 }, new BoneWeight { boneIndex0 = 0, weight0 = 1 }, new BoneWeight { boneIndex0 = 0, weight0 = 1 } };
            if (kind == "VertexId") inputs.Reads.Add(("SV_VERTEXID", 0), 1);
            if (kind == "NormalDisplacement")
            { var normals = mesh.normals; normals[1] = Vector3.right; mesh.normals = normals; inputs.Reads.Add(("NORMAL", 0), 7); }
            if (kind == "UvDisplacement") inputs.Reads.Add(("TEXCOORD", 0), 3);
            if (kind == "NaN" || kind == "TinyDifference")
            { var p = mesh.vertices; p[1].x = kind == "NaN" ? float.NaN : float.Epsilon; mesh.vertices = p; }
            var proof = new HiddenTriangleProof(mesh);
            Assert.That(proof.Equivalent(0, 0, inputs), Is.EqualTo(expected));
            Assert.That(proof.Equivalent(0, 1, inputs), Is.False);
        }

        [TestCase(false)] [TestCase(true)]
        public void HiddenGeometry_AllPassProofPreservesVisibleTrianglesAndVertexIds(bool shadowDisplacement)
        {
            string directory = "Assets/__ALTOHiddenGeometry_" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(directory);
            string path = directory + "/Probe.shader";
            try
            {
                string pass = "HLSLPROGRAM\n#pragma target 4.5\n#pragma vertex vert\n#pragma fragment frag\n" +
                    "struct V { float4 p:POSITION; float3 n:NORMAL; float2 uv:TEXCOORD0; };\n" +
                    "struct O {float4 p:SV_POSITION;float2 uv:TEXCOORD0;};\n" +
                    "O vert(V v){O o;o.p=v.p; o.uv=v.uv;return o;}\nfloat4 frag(O o):SV_Target{return float4(o.uv,0,1);}\nENDHLSL\n";
                File.WriteAllText(path, "Shader \"Hidden/ALTO/HiddenGeometryProbe\" { SubShader { Pass {\n" + pass + "}\nPass {Tags{\"LightMode\"=\"ShadowCaster\"}\n" +
                    (shadowDisplacement ? pass.Replace("o.p=v.p;", "o.p=v.p+float4(v.uv,0,0);") : pass) + "} } Fallback Off }");
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                var material = Track(new Material(AssetDatabase.LoadAssetAtPath<Shader>(path)));
                var mesh = HiddenGeometryMesh(); renderer.sharedMaterial = material;
                var index = FrameIndex(); var scope = new OptimizationBuildScope { Root = root.transform };
                var settings = new MeshOptimizationState { Scope = scope, RemoveHiddenGeometry = true };
                var objectSettings = new ObjectOptimizationState { Scope = scope };
                var sources = new ShaderPropertyUsage();
                var references = AvatarDependencyGraph.Create(root, index, objectSettings, false, sources);
                var messages = new List<string>();
                int removed = PermanentlyHiddenGeometry.Apply(root, new[] { renderer }, settings, objectSettings, index,
                    references, sources, BuildTarget.StandaloneWindows64, copy => Track(copy), messages);
                Assert.That(removed, Is.EqualTo(shadowDisplacement ? 0 : 1), string.Join("\n", messages));
                Assert.That(renderer.sharedMesh.vertexCount, Is.EqualTo(6));
                Assert.That(renderer.sharedMesh.vertices, Is.EqualTo(mesh.vertices));
                Assert.That(renderer.sharedMesh.triangles, Is.EqualTo(shadowDisplacement ? mesh.triangles : new[] { 3, 4, 5 }));
                Assert.That(mesh.triangles, Has.Length.EqualTo(6));
                int calls = sources.MeshInputs.PreprocessCalls;
                Assert.That(sources.MeshInputs.PositionInputs(material, renderer, index, BuildTarget.StandaloneWindows64).Issue, Is.Null);
                Assert.That(sources.MeshInputs.PreprocessCalls, Is.EqualTo(calls), "Snapshot queries reuse output certificates");
            }
            finally { AssetDatabase.DeleteAsset(directory); }
        }
    }
}
