using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private static void RequireD3DAnalysis()
        {
            if (Application.platform != RuntimePlatform.WindowsEditor ||
                ShaderMeshInputs.Backends(PlayerSettings.GetGraphicsAPIs(BuildTarget.StandaloneWindows64))?.All(b => b == ShaderCompilerPlatform.D3D) != true)
                Assert.Ignore("This proof adapter requires Windows and a D3D build target.");
        }

        [TestCase(-.5f)] [TestCase(0f)] [TestCase(.02f)] [TestCase(.04045f)]
        [TestCase(.5f)] [TestCase(.73535698f)] [TestCase(1f)] [TestCase(2f)]
        public void CompiledGammaConstantsMatchNativeGpuUniforms(float value)
        {
            RequireD3DAnalysis();
            if (!SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBFloat)) Assert.Ignore("Float render targets are required.");
            var material = Track(new Material(MeshInputShader("Gamma"))); material.SetFloat("_Gate", value);
            renderer.sharedMaterial = material;
            var constants = FrameIndex().MaterialInputs.ConstantUniforms(renderer, material);
            bool known = PlayerSettings.colorSpace != ColorSpace.Linear;
            Assert.That(constants.ContainsKey("_Gate"), Is.EqualTo(known));
            var mesh = Track(new Mesh { vertices = new[] { new Vector3(-1, -1), new Vector3(3, -1), new Vector3(-1, 3) }, triangles = new[] { 0, 1, 2 } });
            var target = RenderTexture.GetTemporary(8, 8, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            var pixels = Track(new Texture2D(8, 8, TextureFormat.RGBAFloat, false, true));
            var previous = RenderTexture.active;
            try
            {
                RenderTexture.active = target; GL.Clear(false, true, new Color(-100, 0, 0, 0));
                Assert.That(material.SetPass(0), Is.True); Graphics.DrawMeshNow(mesh, Matrix4x4.identity);
                pixels.ReadPixels(new Rect(0, 0, 8, 8), 0, 0); pixels.Apply();
                float uploaded = pixels.GetPixel(4, 4).r;
                Assert.That(uploaded, Is.Not.EqualTo(-100), "The fixture must draw a pixel.");
                if (known) Assert.That(constants["_Gate"], Is.EqualTo(uploaded), "Specialization must use the uploaded float, including its rounding.");
                else if (value == -.5f || value == .5f)
                    Assert.That(uploaded, Is.Not.EqualTo(material.GetFloat("_Gate")), "The Gamma getter is not the shader uniform.");
            }
            finally { RenderTexture.active = previous; RenderTexture.ReleaseTemporary(target); }
        }

        [TestCase(-.5f, true)] [TestCase(0f, true)] [TestCase(1f, true)]
        [TestCase(.5f, true)] [TestCase(.73535698f, true)]
        [TestCase(.5f, false)] [TestCase(.73535698f, false)]
        public void CompiledGammaInputsRetainUnknownUploadsButKeepIndependentConstantProofs(float value, bool indexEffect)
        {
            RequireD3DAnalysis(); var material = Track(new Material(MeshInputShader("Gamma")));
            material.SetFloat("_Gate", value); material.SetFloat("_IndexEffect", indexEffect ? 1 : 0); renderer.sharedMaterial = material;
            var sources = new ShaderPropertyUsage();
            string issue = sources.MeshInputs.Issue(material, renderer, FrameIndex(), false, BuildTarget.StandaloneWindows64);
            bool live = indexEffect && (PlayerSettings.colorSpace == ColorSpace.Linear || value != 0 && value != 1 && value != .5f);
            Assert.That(issue == null, Is.EqualTo(!live), issue);
            Assert.That(sources.MeshInputs.CompileCalls, Is.GreaterThan(0));
        }

        [TestCase(0f)] [TestCase(1f)]
        public void CompiledIndexedSemanticsRetainLiveReadsButAllowProvenUnusedInputs(float value)
        {
            RequireD3DAnalysis(); var material = Track(new Material(MeshInputShader("Indexed")));
            material.SetFloat("_IndexEffect", value); renderer.sharedMaterial = material;
            var sources = new ShaderPropertyUsage();
            string issue = sources.MeshInputs.Issue(material, renderer, FrameIndex(), false, BuildTarget.StandaloneWindows64);
            Assert.That(issue == null, Is.EqualTo(value == 0), issue);
            Assert.That(sources.MeshInputs.CompileCalls, Is.GreaterThan(0), "The source fast path must not hide indexed semantics.");
        }

        [Test]
        public void CompiledShaderInputsDiscardUnusedSemanticDeclarations()
        {
            RequireD3DAnalysis(); var sources = new ShaderPropertyUsage();
            Assert.That(sources.MeshInputs.Issue(MeshInputShader("Dead"), false, BuildTarget.StandaloneWindows64), Is.Null);
            Assert.That(sources.MeshInputs.CompileCalls, Is.GreaterThan(0));
        }

        [TestCase("SV_VertexID", ShaderType.Vertex)]
        [TestCase("SV_PrimitiveID", ShaderType.Fragment)]
        [TestCase("BLENDINDICES", ShaderType.Vertex)]
        [TestCase("BLENDWEIGHT", ShaderType.Vertex)]
        public void CompiledShaderInputsDistinguishReadsFromDeclarations(string semantic, ShaderType stage)
        {
            RequireD3DAnalysis();
            string output = stage == ShaderType.Vertex ? "SV_POSITION" : "SV_Target";
            string source = "float4 main(uint id : " + semantic + ") : " + output + " { return float4(id,0,0,1); }";
            Assert.That(D3DShaderInputs.Compile(source, "main", stage, out var inputs, out string error), Is.True, error);
            Assert.That(inputs.Any(i => i.Semantic.Equals(semantic, StringComparison.OrdinalIgnoreCase) && i.ReadMask != 0), Is.True);
            source = "float4 main(uint id : " + semantic + ") : " + output + " { return float4(0,0,0,1); }";
            Assert.That(D3DShaderInputs.Compile(source, "main", stage, out inputs, out error), Is.True, error);
            Assert.That(inputs.Any(i => i.Semantic.Equals(semantic, StringComparison.OrdinalIgnoreCase) && i.ReadMask != 0), Is.False);
        }

        [Test]
        public void CompiledShaderInputsIncludeGeometryPrimitiveIds()
        {
            RequireD3DAnalysis();
            const string source = "struct V { float4 p : SV_POSITION; }; [maxvertexcount(3)] void main(triangle V v[3], uint id : SV_PrimitiveID, inout TriangleStream<V> output) { v[0].p.x += id; output.Append(v[0]); output.Append(v[1]); output.Append(v[2]); }";
            Assert.That(D3DShaderInputs.Compile(source, "main", ShaderType.Geometry, out var inputs, out string error), Is.True, error);
            Assert.That(inputs.Any(i => i.Semantic == "SV_PrimitiveID" && i.ReadMask != 0), Is.True);
            Assert.That(D3DShaderInputs.Reflect(new byte[32], ShaderType.Hull, out _, out error), Is.False);
            Assert.That(error, Does.Contain("coverage"));
        }

        [TestCase(false)] [TestCase(true)]
        public void CompiledMaterialInputsRequireAnUnanimatedConstant(bool animated)
        {
            RequireD3DAnalysis(); var pair = MergePair();
            var material = Track(new Material(MeshInputShader("Constant"))); pair.First.sharedMaterial = material;
            var clip = Clip();
            if (animated) AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("First", typeof(SkinnedMeshRenderer), "material._IndexEffect"), AnimationCurve.Linear(0, 0, 1, 1));
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(clip));
            var index = AvatarAnimationIndex.Create(root, new Dictionary<object, VirtualAnimatorController> { [VRCAvatarDescriptor.AnimLayerType.FX] = controller }, null);
            var sources = new ShaderPropertyUsage();
            string issue = sources.MeshInputs.Issue(material, pair.First, index, false, BuildTarget.StandaloneWindows64);
            Assert.That(issue == null, Is.EqualTo(!animated), issue);
            int calls = sources.MeshInputs.CompileCalls;
            Assert.That(sources.MeshInputs.Issue(material, pair.First, index, false, BuildTarget.StandaloneWindows64), Is.EqualTo(issue));
            Assert.That(sources.MeshInputs.CompileCalls, Is.EqualTo(calls));
        }

        [Test]
        public void CompiledMaterialProofCacheIncludesConstantValues()
        {
            RequireD3DAnalysis(); var pair = MergePair(); var material = Track(new Material(MeshInputShader("Constant")));
            pair.First.sharedMaterial = material; var sources = new ShaderPropertyUsage();
            foreach (float value in new[] { 0f, 1f, 0f })
            {
                material.SetFloat("_IndexEffect", value);
                var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(Clip()));
                var index = AvatarAnimationIndex.Create(root, new Dictionary<object, VirtualAnimatorController> { [VRCAvatarDescriptor.AnimLayerType.FX] = controller }, null);
                Assert.That(sources.MeshInputs.Issue(material, pair.First, index, false, BuildTarget.StandaloneWindows64) == null, Is.EqualTo(value == 0));
            }
        }

        [TestCase("float _Flag;", true)] [TestCase("uniform float _Flag;", true)]
        [TestCase("cbuffer Params { float _Flag; };", false)]
        [TestCase("float _Flag : register(c0);", false)]
        [TestCase("float _Flag[2];", false)] [TestCase("float _Flag = 1;", false)]
        [TestCase("float _Flag; void f() { _Flag = 1; }", false)]
        public void ShaderConstantSpecializationKeepsUnsupportedDeclarations(string source, bool replaced)
        {
            string result = ShaderConstantInputs.Specialize(source, new Dictionary<string, float> { ["_Flag"] = 0 }, out int count);
            Assert.That(count, Is.EqualTo(replaced ? 1 : 0));
            if (replaced) Assert.That(result, Does.Contain("static const float _Flag"));
        }

        [Test]
        public void ShaderConstantSpecializationDoesNotRewriteLocalShadowing()
        {
            const string code = "float _Flag; float helper(float _Flag) { return _Flag; }";
            string result = ShaderConstantInputs.Specialize(code, new Dictionary<string, float> { ["_Flag"] = 0 }, out int count);
            Assert.That(count, Is.EqualTo(1)); Assert.That(result, Does.Contain("helper(float _Flag)"));
        }

        [TestCase(0f, true)] [TestCase(1f, false)] [TestCase(-1f, false)]
        public void ShaderIntegerSpecializationDoesNotGuessUniformUploadConversions(float value, bool replaced)
        {
            ShaderConstantInputs.Specialize("uint _Flag;", new Dictionary<string, float> { ["_Flag"] = value }, out int count);
            Assert.That(count != 0, Is.EqualTo(replaced));
        }

        [Test]
        public void ShaderConstantsExcludeDuplicatePropertyDeclarations()
        {
            var pair = MergePair(); var material = Track(new Material(MeshInputShader("Constant")));
            pair.First.sharedMaterial = material;
            Assert.That(Enumerable.Range(0, material.shader.GetPropertyCount()).Count(i => material.shader.GetPropertyName(i) == "_RepeatedValue"), Is.EqualTo(2));
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(Clip()));
            var index = AvatarAnimationIndex.Create(root, new Dictionary<object, VirtualAnimatorController> { [VRCAvatarDescriptor.AnimLayerType.FX] = controller }, null);
            Assert.That(index.MaterialInputs.ConstantUniforms(pair.First, material).ContainsKey("_RepeatedValue"), Is.False);
        }

        [Test]
        public void ShaderIntegerPropertiesRequireACompatibleConstantRepresentation()
        {
            var pair = MergePair(); var material = Track(new Material(MeshInputShader("Constant")));
            pair.First.sharedMaterial = material;
            foreach (int value in new[] { 0, 1, -1 })
            {
                material.SetInteger("_IntegerValue", value);
                var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(Clip()));
                var index = AvatarAnimationIndex.Create(root, new Dictionary<object, VirtualAnimatorController> { [VRCAvatarDescriptor.AnimLayerType.FX] = controller }, null);
                Assert.That(index.MaterialInputs.ConstantUniforms(pair.First, material).ContainsKey("_IntegerValue"), Is.EqualTo(value == 0));
            }
        }

        [Test]
        public void CompiledShaderProofRejectsMalformedProgramsAndBytecode()
        {
            RequireD3DAnalysis();
            Assert.That(D3DShaderInputs.Compile("not hlsl", "main", ShaderType.Vertex, out _, out _), Is.False);
            Assert.That(D3DShaderInputs.Reflect(Array.Empty<byte>(), ShaderType.Vertex, out _, out _), Is.False);
        }
    }
}
