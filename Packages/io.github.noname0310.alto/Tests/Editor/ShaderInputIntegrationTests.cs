using System.Linq;
using Noname.AvatarTools.Analysis;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        [Test]
        public void ShaderInputCertificatesReuseOnlyTheRelevantMaterialConstants()
        {
            RequireD3DAnalysis();
            var material = Track(new Material(MeshInputShader("Sliced"))); renderer.sharedMaterial = material;
            var query = new ShaderInputQuery(new ShaderInputQuery.Semantic("TEXCOORD", 3));
            var sources = new ShaderPropertyUsage();
            Assert.That(sources.MeshInputs.Issue(material, renderer, FrameIndex(), query, BuildTarget.StandaloneWindows64), Is.Null);
            int compiles = sources.MeshInputs.CompileCalls, preprocess = sources.MeshInputs.PreprocessCalls;
            Assert.That(compiles, Is.GreaterThan(0));
            Assert.That(preprocess, Is.LessThan(30), "Unrelated fragment variants must not multiply the input-use query.");
            Assert.That(sources.MeshInputs.UseSliceAnalyses, Is.LessThan(preprocess), "Identical tier programs reuse dependency summaries.");
            Assert.That(sources.MeshInputs.SpecializationCalls, Is.LessThan(preprocess), "Constant substitution is shared by identical tier programs and proof consumers.");
            material.SetFloat("_Unrelated", .8f);
            Assert.That(sources.MeshInputs.Issue(material, renderer, FrameIndex(), query, BuildTarget.StandaloneWindows64), Is.Null);
            Assert.That(sources.MeshInputs.CompileCalls, Is.EqualTo(compiles));
            Assert.That(sources.MeshInputs.PreprocessCalls, Is.EqualTo(preprocess));
            material.SetFloat("_Gate", 1);
            Assert.That(sources.MeshInputs.Issue(material, renderer, FrameIndex(), query, BuildTarget.StandaloneWindows64), Does.Contain("may depend"),
                "A changed gate must invalidate the absence certificate, even when the native program can be reused.");
        }

        [Test]
        public void ShaderInputCertificatesDoNotHideNewlyAnimatedConstants()
        {
            RequireD3DAnalysis();
            var pair = MergePair(); var material = Track(new Material(MeshInputShader("Sliced"))); pair.First.sharedMaterial = material;
            var query = new ShaderInputQuery(new ShaderInputQuery.Semantic("TEXCOORD", 3));
            var sources = new ShaderPropertyUsage();
            Assert.That(sources.MeshInputs.Issue(material, pair.First, FrameIndex(), query, BuildTarget.StandaloneWindows64), Is.Null);
            var clip = Clip();
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("First", typeof(SkinnedMeshRenderer), "material._Gate"), AnimationCurve.Linear(0, 0, 1, 1));
            Assert.That(sources.MeshInputs.Issue(material, pair.First, FrameIndex(clip), query, BuildTarget.StandaloneWindows64), Does.Contain("may depend"));
        }

        [TestCase("Always", true)] [TestCase("", true)] [TestCase("ForwardAdd", true)]
        [TestCase("ShadowCaster", true)] [TestCase("Deferred", true)] [TestCase("ALTO_Test_NotScheduled", false)]
        public void ShaderInputPassScheduleKeepsBuiltinAndUnknownPipelineCoverage(string mode, bool scheduled)
        {
            Assert.That(ShaderPassSchedule.MayExecute(mode, true), Is.EqualTo(scheduled));
            Assert.That(ShaderPassSchedule.MayExecute(mode, false), Is.True);
            Assert.That(ShaderPassSchedule.MayExecute(null, true), Is.True);
        }

        [Test]
        public void ShaderInputPassSelectionMatchesTheBuiltinCameraSchedule()
        {
            RequireD3DAnalysis();
            if (GraphicsSettings.currentRenderPipeline != null) Assert.Ignore("This fixture exercises the built-in renderer schedule.");
            var material = Track(new Material(MeshInputShader("Sliced")));
            var go = PolicyChild("Scheduled pass");
            go.AddComponent<MeshFilter>().sharedMesh = Track(new Mesh {
                vertices = new[] { new Vector3(-1, -1, 0), new Vector3(3, -1, 0), new Vector3(-1, 3, 0) }, triangles = new[] { 0, 1, 2 } });
            go.AddComponent<MeshRenderer>().sharedMaterial = material;
            var camera = PolicyChild("Camera").AddComponent<Camera>(); camera.scene = scene;
            camera.transform.localPosition = new Vector3(0, 0, -2); camera.orthographic = true; camera.orthographicSize = 1;
            camera.enabled = false; camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black;
            var target = RenderTexture.GetTemporary(16, 16, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            try
            {
                var pixels = DrawAssembly(camera, target);
                Assert.That(pixels.Any(p => p.r > 200), Is.True, "The ordinary pass must render.");
                Assert.That(pixels.Any(p => p.g > 200), Is.False, "A custom LightMode must not render through the normal built-in schedule.");
            }
            finally { RenderTexture.ReleaseTemporary(target); }
            var sources = new ShaderPropertyUsage();
            Assert.That(sources.GetPrograms(material.shader).Passes.Any(p => !ShaderPassSchedule.MayExecute(p.Pass)), Is.True);
            Assert.That(sources.MeshInputs.Issue(material.shader, false, BuildTarget.StandaloneWindows64), Is.Null);
            Assert.That(sources.Get(material.shader).Tokens.Contains("SV_VertexID"), Is.True, "Source discovery must retain the metadata pass.");
        }

        [Test]
        public void ShaderInputUsePassSelectionDoesNotImportUnreferencedSiblingPrograms()
        {
            RequireD3DAnalysis(); var sources = new ShaderPropertyUsage();
            var shader = MeshInputShader("UsePassSelected");
            Assert.That(sources.MeshInputs.Issue(shader, false, BuildTarget.StandaloneWindows64), Is.Null);
            Assert.That(sources.GetPrograms(shader).Passes.Any(p => p.Pass.Name == "INDEX"), Is.False);
            Assert.That(sources.MeshInputs.Issue(MeshInputShader("UsePassOwner"), false, BuildTarget.StandaloneWindows64), Does.Contain("may depend"));
        }
    }
}
