using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using nadena.dev.ndmf;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.Meshes;
using Noname.AvatarTools.ObjectOptimization;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private const string HiddenConsumerProgram =
            "HLSLPROGRAM\n#pragma target 4.5\n#pragma vertex vert\n#pragma fragment frag\n" +
            "struct V { float4 p:POSITION; }; struct O { float4 p:SV_POSITION; };\n" +
            "O vert(V v) { O o; o.p=v.p; return o; }\n" +
            "float4 frag(O o):SV_Target { return float4(1,0,0,1); }\nENDHLSL\n";

        private static Color[] HiddenGeometryPixels(Mesh mesh, Material material)
        {
            var target = RenderTexture.GetTemporary(32, 32, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var previous = RenderTexture.active;
            var readback = new Texture2D(32, 32, TextureFormat.RGBA32, false, true);
            var commands = new CommandBuffer();
            try
            {
                commands.SetRenderTarget(target); commands.ClearRenderTarget(false, true, Color.clear);
                commands.DrawMesh(mesh, Matrix4x4.identity, material, 0, 0); Graphics.ExecuteCommandBuffer(commands);
                RenderTexture.active = target; readback.ReadPixels(new Rect(0, 0, 32, 32), 0, 0); readback.Apply();
                return readback.GetPixels();
            }
            finally { commands.Release(); RenderTexture.active = previous; RenderTexture.ReleaseTemporary(target); Object.DestroyImmediate(readback); }
        }

        [TestCase("None", 1)] [TestCase("Disabled", 0)] [TestCase("Preserved", 0)]
        [TestCase("ParticleInitial", 0)] [TestCase("ParticleAnimated", 0)] [TestCase("Collider", 0)]
        [TestCase("PrimitiveId", 0)] [TestCase("GeometryStage", 0)]
        public void HiddenGeometry_ConsumersAndShaderOutputsControlDeletion(string boundary, int expected)
        {
            string directory = "Assets/__ALTOHiddenConsumers_" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(directory); string path = directory + "/Probe.shader";
            try
            {
                string program = HiddenConsumerProgram;
                if (boundary == "PrimitiveId") program = program.Replace("frag(O o)", "frag(O o, uint id:SV_PrimitiveID)")
                    .Replace("float4(1,0,0,1)", "float4(id * .25,0,0,1)");
                if (boundary == "GeometryStage") program = program.Replace("#pragma fragment frag", "#pragma fragment frag\n#pragma geometry geom")
                    .Replace("float4 frag", "[maxvertexcount(3)] void geom(triangle O p[3], inout TriangleStream<O> stream) { " +
                        "O o; o.p=float4(-.5,-.5,0,1); stream.Append(o); o.p=float4(.5,-.5,0,1); stream.Append(o); " +
                        "o.p=float4(0,.5,0,1); stream.Append(o); }\nfloat4 frag");
                File.WriteAllText(path, "Shader \"Hidden/ALTO/HiddenConsumerProbe\" { SubShader { Pass { Cull Off ZWrite Off ZTest Always\n" + program + "} } Fallback Off }");
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                var material = Track(new Material(AssetDatabase.LoadAssetAtPath<Shader>(path)));
                Assert.That(ShaderUtil.ShaderHasError(material.shader), Is.False);
                renderer.sharedMaterial = material; var original = HiddenGeometryMesh();
                AnimationClip selector = null;
                if (boundary.StartsWith("Particle", StringComparison.Ordinal))
                {
                    var particle = PolicyChild("Emitter").AddComponent<ParticleSystem>();
                    var shape = particle.shape; shape.enabled = true; shape.shapeType = ParticleSystemShapeType.SkinnedMeshRenderer;
                    if (boundary == "ParticleInitial") shape.skinnedMeshRenderer = renderer;
                    else
                    {
                        selector = Clip();
                        AnimationUtility.SetObjectReferenceCurve(selector,
                            EditorCurveBinding.PPtrCurve("Emitter", typeof(ParticleSystem), "ShapeModule.m_SkinnedMeshRenderer"),
                            new[] { new ObjectReferenceKeyframe { time = 0, value = renderer } });
                    }
                }
                if (boundary == "Collider") PolicyChild("Mesh consumer").AddComponent<MeshCollider>().sharedMesh = original;
                var index = selector == null ? FrameIndex() : FrameIndex(selector);
                if (boundary == "ParticleAnimated")
                {
                    Assert.That(BlendShapeEffectAnalysis.FindFullVertexReaders(root).Contains(renderer), Is.False);
                    Assert.That(BlendShapeEffectAnalysis.FindFullVertexReaders(root, index).Contains(renderer), Is.True);
                }
                var scope = new OptimizationBuildScope { Root = root.transform };
                var settings = new MeshOptimizationState { Scope = scope, RemoveHiddenGeometry = boundary != "Disabled" };
                if (boundary == "Preserved") settings.Preserved.Add(ObjectRegistry.GetReference(renderer));
                var objects = new ObjectOptimizationState { Scope = scope }; var shaders = new ShaderPropertyUsage();
                var references = AvatarDependencyGraph.Create(root, index, objects, false, shaders);
                var messages = new List<string>();
                var before = boundary == "None" ? HiddenGeometryPixels(original, material) : null;
                int removed = PermanentlyHiddenGeometry.Apply(root, new[] { renderer }, settings, objects, index,
                    references, shaders, BuildTarget.StandaloneWindows64, copy => Track(copy), messages);
                Assert.That(removed, Is.EqualTo(expected), string.Join("\n", messages));
                Assert.That(original.triangles, Has.Length.EqualTo(6));
                if (before != null)
                {
                    Assert.That(before.Any(pixel => pixel.r > .5f), Is.True, "The native GPU fixture must draw visible geometry.");
                    Assert.That(HiddenGeometryPixels(renderer.sharedMesh, material), Is.EqualTo(before));
                }
            }
            finally { AssetDatabase.DeleteAsset(directory); }
        }
    }
}
