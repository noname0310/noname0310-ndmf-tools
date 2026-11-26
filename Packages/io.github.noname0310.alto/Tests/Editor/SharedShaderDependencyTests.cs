using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.Materials;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public void SharedShaderConstants_UncalledHelpersDoNotTriggerRuntimeValidation(bool conditionalCaller)
        {
            RequireRuntimeShaderTarget();
            string directory = "Assets/__ALTO_EntryUse_" + Guid.NewGuid().ToString("N"); Directory.CreateDirectory(directory);
            Shader replacement = null;
            try
            {
                string program = "Pass { CGPROGRAM\n#pragma vertex vert_img\n#pragma fragment frag\n" +
                    "#pragma multi_compile_local _ ALTO_ENABLE_HELPER\n#include \"UnityCG.cginc\"\nfloat _Shared;\n" +
                    "float unused() { return _Shared; }\nfloat4 frag(v2f_img input):SV_Target {\n" +
                    (conditionalCaller ? "#if ALTO_ENABLE_HELPER\nreturn unused();\n#else\nreturn 1;\n#endif\n" : "return 1;\n") +
                    "}\nENDCG\n}";
                var original = ImportDependencyShader(directory, "Owned", DependencyShaderSource("ALTO/EntryUse/" + Guid.NewGuid().ToString("N"), program));
                var statistics = new SharedShaderConstantSource.Statistics();
                bool result = SharedShaderConstantSource.TryCreate(original, new System.Collections.Generic.Dictionary<string, float> { ["_Shared"] = .25f },
                    EditorUserBuildSettings.activeBuildTarget, new ShaderPropertyUsage(), out replacement, out _, out _, out string reason, statistics);
                Assert.That(result, Is.EqualTo(conditionalCaller), reason + "; " + statistics.EntryUseDetail);
                if (!conditionalCaller)
                {
                    Assert.That(reason, Does.Contain("unreachable from every shader entry"));
                    Assert.That(statistics.NativeCompiles, Is.Zero);
                }
                else Assert.That(statistics.ValidatedPrograms, Is.Positive);
            }
            finally
            {
                if (replacement != null) UnityEngine.Object.DestroyImmediate(replacement);
                AssetDatabase.DeleteAsset(directory);
            }
        }

        private static string DependencyProgram(string name, bool red, string keywords = "#pragma multi_compile_local _ ALTO_DEPENDENCY_ALTERNATE\n")
            => "Pass { Name \"" + name + "\" CGPROGRAM\n#pragma vertex vert_img\n#pragma fragment frag\n" + keywords +
                "#include \"UnityCG.cginc\"\nfloat _Shared; float _Different;\n" +
                "float4 frag(v2f_img input):SV_Target { float value=_Shared;\n#if defined(ALTO_DEPENDENCY_ALTERNATE)\nvalue=1-value;\n#endif\n" +
                (red ? "return float4(value,0,_Different,1);" : "return float4(0,value,_Different,1);") + "}\nENDCG\n}";

        private static string DependencyShaderSource(string name, string passes, string fallback = "Off", int lod = 0)
            => "Shader \"" + name + "\" { Properties { _Shared (\"Shared\",Float)=0.25 _Different (\"Different\",Float)=0.2 } " +
                "SubShader { LOD " + lod + " Cull Off ZWrite Off ZTest Always\n" + passes + "\n} Fallback " + fallback + " }";

        private static Shader ImportDependencyShader(string directory, string file, string source)
        {
            string path = directory + "/" + file + ".shader"; File.WriteAllText(path, source);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
            Assert.That(shader, Is.Not.Null); Assert.That(ShaderUtil.ShaderHasError(shader), Is.False); return shader;
        }

        private Color SharedShaderCameraPixel(Material material)
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) Assert.Ignore("A graphics device is required.");
            var quad = PolicyChild("Shader fallback quad");
            var mesh = Track(new Mesh
            {
                vertices = new[] { new Vector3(-1, -1, 0), new Vector3(1, -1, 0), new Vector3(1, 1, 0), new Vector3(-1, 1, 0) },
                uv = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up },
                triangles = new[] { 0, 1, 2, 0, 2, 3 }
            });
            quad.AddComponent<MeshFilter>().sharedMesh = mesh;
            quad.AddComponent<MeshRenderer>().sharedMaterial = material;
            var cameraObject = PolicyChild("Shader fallback camera");
            var camera = cameraObject.AddComponent<Camera>(); camera.scene = scene; camera.enabled = false;
            camera.transform.localPosition = new Vector3(0, 0, -2); camera.orthographic = true; camera.orthographicSize = 1;
            camera.aspect = 1; camera.nearClipPlane = .1f; camera.farClipPlane = 4; camera.renderingPath = RenderingPath.Forward;
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.clear;
            var target = RenderTexture.GetTemporary(4, 4, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            var previous = RenderTexture.active; var readback = new Texture2D(4, 4, TextureFormat.RGBAFloat, false, true);
            try
            {
                // A renderer draw selects Unity's active subshader. Graphics.Blit
                // with an explicit pass index can still draw the original pass.
                camera.targetTexture = target; camera.Render(); RenderTexture.active = target;
                readback.ReadPixels(new Rect(0, 0, 4, 4), 0, 0); readback.Apply(); return readback.GetPixel(1, 1);
            }
            finally
            {
                camera.targetTexture = null; RenderTexture.active = previous; RenderTexture.ReleaseTemporary(target);
                UnityEngine.Object.DestroyImmediate(readback); UnityEngine.Object.DestroyImmediate(cameraObject);
                UnityEngine.Object.DestroyImmediate(quad);
            }
        }

        [TestCase("Example/Unlit/Transparent")]
        [TestCase("Example/VertexLit/Toon/Outline/Fade/Cutout/Particle/Sprite/MatCap")]
        public void SharedShaderConstants_GeneratedNamePreservesSafetyFallbackTokens(string originalName)
        {
            RequireRuntimeShaderTarget();
            string directory = "Assets/__ALTO_ShaderSafetyFallback_" + Guid.NewGuid().ToString("N"); Directory.CreateDirectory(directory);
            try
            {
                var original = ImportDependencyShader(directory, "Owned", DependencyShaderSource(originalName + "/" + Guid.NewGuid().ToString("N"),
                    DependencyProgram("OWNED", true, "")));
                var material = Track(new Material(original));
                Assert.That(material.GetTag("VRCFallback", false, ""), Is.Empty);
                Color expected = SharedShaderPixel(material, 0);
                var constants = new Dictionary<string, float> { ["_Shared"] = .25f };
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    var statistics = new SharedShaderConstantSource.Statistics();
                    Assert.That(SharedShaderConstantSource.TryCreate(original, constants, SharedShaderTarget, new ShaderPropertyUsage(),
                        out var shader, out _, out _, out string reason, statistics,
                        new Dictionary<string, float[]> { ["_Different"] = new[] { material.GetFloat("_Different") } }), Is.True, reason); Track(shader);
                    Assert.That(shader.name, Does.EndWith("/" + original.name));
                    // The client contract matches these names case-sensitively
                    // when no VRCFallback tag exists. This checks that contract;
                    // it does not execute the VRChat client's fallback renderer.
                    foreach (string token in new[] { "Unlit", "VertexLit", "Toon", "Outline", "Transparent", "Fade", "Cutout", "Particle", "Sprite", "MatCap" })
                        Assert.That(shader.name.IndexOf(token, StringComparison.Ordinal) >= 0,
                            Is.EqualTo(original.name.IndexOf(token, StringComparison.Ordinal) >= 0), token);
                    var output = Track(new Material(material) { shader = shader });
                    Assert.That(output.GetTag("VRCFallback", false, ""), Is.Empty);
                    Assert.That(Vector4.Distance(SharedShaderPixel(output, 0), expected), Is.LessThan(.00001f));
                    Assert.That(statistics.CacheHits, Is.EqualTo(attempt));
                }
            }
            finally { AssetDatabase.DeleteAsset(directory); }
        }

        [Test]
        public void SharedShaderConstants_ExactInternalSafetyFallbackNamesRetainOriginal()
        {
            RequireRuntimeShaderTarget();
            // Reuse the existing native shader without importing a second asset
            // under a globally recognized built-in name.
            var original = Shader.Find("Standard"); Assert.That(original, Is.Not.Null);
            var statistics = new SharedShaderConstantSource.Statistics();
            Assert.That(SharedShaderConstantSource.TryCreate(original, new Dictionary<string, float> { ["_Glossiness"] = .5f },
                SharedShaderTarget, new ShaderPropertyUsage(), out var output, out _, out _, out string reason, statistics), Is.False);
            Assert.That(output, Is.Null); Assert.That(reason, Does.Contain("exact-name VRChat Safety Fallback contract"));
            Assert.That(original.name, Is.EqualTo("Standard"));
            Assert.That(statistics.ImportMilliseconds, Is.Zero); Assert.That(statistics.ValidatedPrograms, Is.Zero);
        }

        [Test]
        public void SharedShaderConstants_UsePassRetainsDependencyUniformsAndOneForOneOwnership()
        {
            RequireRuntimeShaderTarget();
            string directory = "Assets/__ALTO_ShaderDependency_" + Guid.NewGuid().ToString("N"); Directory.CreateDirectory(directory);
            try
            {
                string name = "Hidden/ALTO/Dependency/" + Guid.NewGuid().ToString("N");
                var dependency = ImportDependencyShader(directory, "Dependency", DependencyShaderSource(name + "/Dependency", DependencyProgram("IMPORTED", true)));
                var original = ImportDependencyShader(directory, "Owned", DependencyShaderSource(name + "/Owned",
                    DependencyProgram("OWNED", false) + "\nUsePass \"" + dependency.name + "/IMPORTED\""));
                var material = Track(new Material(original)); renderer.sharedMaterial = material;
                var other = PolicyChild("Dependency user").AddComponent<MeshRenderer>();
                var retained = Track(new Material(dependency)); retained.SetFloat("_Shared", .8f); other.sharedMaterial = retained;
                var diagnostics = new List<string>();
                Assert.That(SpecializeSharedShaders(SharedShaderControllers(), diagnostics), Is.EqualTo(2), string.Join("\n", diagnostics));
                Assert.That(other.sharedMaterial, Is.Not.SameAs(retained));
                Assert.That(Vector4.Distance(SharedShaderPixel(retained, 0), SharedShaderPixel(other.sharedMaterial, 0)), Is.LessThan(.00001f));
                Assert.That(new[] { renderer.sharedMaterial.shader, other.sharedMaterial.shader }.Distinct().Count(), Is.EqualTo(2));
                foreach (bool alternate in new[] { false, true })
                {
                    if (alternate) { material.EnableKeyword("ALTO_DEPENDENCY_ALTERNATE"); renderer.sharedMaterial.EnableKeyword("ALTO_DEPENDENCY_ALTERNATE"); }
                    else { material.DisableKeyword("ALTO_DEPENDENCY_ALTERNATE"); renderer.sharedMaterial.DisableKeyword("ALTO_DEPENDENCY_ALTERNATE"); }
                    for (int pass = 0; pass < 2; pass++)
                        Assert.That(Vector4.Distance(SharedShaderPixel(material, pass), SharedShaderPixel(renderer.sharedMaterial, pass)), Is.LessThan(.00001f));
                }
                var data = ShaderUtil.GetShaderData(renderer.sharedMaterial.shader);
                for (int pass = 0; pass < 2; pass++)
                {
                    var program = data.GetSubshader(0).GetPass(pass).PreprocessVariant(ShaderType.Fragment, Array.Empty<string>(),
                        ShaderCompilerPlatform.D3D, SharedShaderTarget, GraphicsTier.Tier1, false);
                    Assert.That(D3DShaderInputs.CompileUncached(program.PreprocessedCode, "frag", ShaderType.Fragment, out var uniforms, out string issue), Is.True, issue);
                    Assert.That(uniforms.Any(u => ShaderInputQuery.Uniform("_Shared").MatchesCompiled(u) && u.ReadMask != 0), Is.EqualTo(pass == 1));
                }
            }
            finally { AssetDatabase.DeleteAsset(directory); }
        }

        [TestCase(false)] [TestCase(true)]
        public void SharedShaderConstants_GeneratedShaderRetainsOnlyItsActualDependencies(bool dependsOnCandidate)
        {
            RequireRuntimeShaderTarget();
            string directory = "Assets/__ALTO_NativeShaderOwnership_" + Guid.NewGuid().ToString("N"); Directory.CreateDirectory(directory);
            try
            {
                var original = ImportDependencyShader(directory, "Owned", DependencyShaderSource(
                    "Hidden/ALTO/NativeOwnership/" + Guid.NewGuid().ToString("N"), DependencyProgram("OWNED", true, "")));
                var material = Track(new Material(original)); renderer.sharedMaterial = material;
                string passes = dependsOnCandidate ? "UsePass \"" + original.name + "/OWNED\"" : DependencyProgram("GENERATED", false, "");
                var generated = Track(ShaderUtil.CreateShaderAsset(DependencyShaderSource(
                    "Hidden/ALTO/NativeGenerated/" + Guid.NewGuid().ToString("N"), passes), true));
                Assert.That(ShaderUtil.ShaderHasError(generated), Is.False);
                Assert.That(AssetDatabase.GetAssetPath(generated), Is.Empty);
                var dependencies = ShaderPropertyUsage.CollectNativeShaderDependencies(generated);
                Assert.That(dependencies.Contains(original), Is.EqualTo(dependsOnCandidate));
                var other = PolicyChild("Generated shader consumer").AddComponent<MeshRenderer>();
                var retained = Track(new Material(generated)); other.sharedMaterial = retained;
                var before = SharedShaderPixel(material, 0); var messages = new List<string>();
                Assert.That(SpecializeSharedShaders(SharedShaderControllers(), messages), Is.EqualTo(dependsOnCandidate ? 0 : 1),
                    string.Join("\n", messages));
                Assert.That(other.sharedMaterial, Is.SameAs(retained));
                Assert.That(other.sharedMaterial.shader, Is.SameAs(generated));
                Assert.That(renderer.sharedMaterial.shader == original, Is.EqualTo(dependsOnCandidate));
                Assert.That(Vector4.Distance(SharedShaderPixel(renderer.sharedMaterial, 0), before), Is.LessThan(1e-6));
                Assert.That(new[] { renderer.sharedMaterial.shader, other.sharedMaterial.shader }.Distinct().Count(), Is.EqualTo(2));
            }
            finally { AssetDatabase.DeleteAsset(directory); }
        }

        [UnityTest]
        public IEnumerator SharedShaderConstants_FallbackSelectionAndMaximumLodSurviveColdAndCachedReplacement_Async()
        {
            RequireRuntimeShaderTarget();
            string directory = "Assets/__ALTO_ShaderFallback_" + Guid.NewGuid().ToString("N"); Directory.CreateDirectory(directory);
            try
            {
                string name = "Hidden/ALTO/Fallback/" + Guid.NewGuid().ToString("N");
                var dependency = ImportDependencyShader(directory, "Fallback", DependencyShaderSource(name + "/Fallback", DependencyProgram("LOW", true), lod: 100));
                var original = ImportDependencyShader(directory, "Owned", DependencyShaderSource(name + "/Owned", DependencyProgram("HIGH", false),
                    "\"" + dependency.name + "\"", 200));
                Assert.That(ShaderUtil.GetShaderData(original).GetSerializedSubshader(0).LevelOfDetail, Is.EqualTo(200));
                Assert.That(ShaderUtil.GetShaderData(dependency).GetSerializedSubshader(0).LevelOfDetail, Is.EqualTo(100));
                yield return null;
                var material = Track(new Material(original));
                Color high = SharedShaderCameraPixel(material);
                Assert.That(high.g, Is.EqualTo(.25f).Within(.00001f), "The source high-LOD draw must execute: " + high);
                var fallback = Track(new Material(dependency));
                Color low = SharedShaderCameraPixel(fallback);
                Assert.That(low.r, Is.EqualTo(.25f).Within(.00001f), "The dependency draw must execute: " + low);
                original.maximumLOD = 100;
                // Shader selection can be cached by the editor render loop. Give
                // Unity a frame to apply the changed shader setting before drawing.
                yield return null;
                Assert.That(original.maximumLOD, Is.EqualTo(100));
                var selected = ShaderUtil.GetShaderData(original);
                Assert.That(selected.ActiveSubshader.LevelOfDetail, Is.EqualTo(100),
                    "The source must select its fallback before testing replacement; active index=" + selected.ActiveSubshaderIndex);
                Color expected = SharedShaderCameraPixel(material);
                Assert.That(Vector4.Distance(expected, low), Is.LessThan(.00001f),
                    "The source draw must select its fallback after maximumLOD changes: " + expected);
                var constants = new Dictionary<string, float> { ["_Shared"] = .25f };
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    var statistics = new SharedShaderConstantSource.Statistics();
                    Assert.That(SharedShaderConstantSource.TryCreate(original, constants, SharedShaderTarget, new ShaderPropertyUsage(),
                        out var shader, out _, out _, out string reason, statistics,
                        new Dictionary<string, float[]> { ["_Different"] = new[] { material.GetFloat("_Different") } }), Is.True, reason); Track(shader);
                    Assert.That(shader.maximumLOD, Is.EqualTo(100));
                    yield return null;
                    Assert.That(ShaderUtil.GetShaderData(shader).ActiveSubshader.LevelOfDetail, Is.EqualTo(100));
                    var output = Track(new Material(material) { shader = shader });
                    Assert.That(Vector4.Distance(SharedShaderCameraPixel(output), expected), Is.LessThan(.00001f));
                    if (attempt == 1) Assert.That(statistics.CacheHits, Is.EqualTo(1));
                }
            }
            finally { AssetDatabase.DeleteAsset(directory); }
        }

        [TestCase(false)] [TestCase(true)]
        public void SharedShaderConstants_DirectAndTransitiveBackEdgesRetainTheOriginalShader(bool transitive)
        {
            RequireRuntimeShaderTarget();
            string directory = "Assets/__ALTO_ShaderCycle_" + Guid.NewGuid().ToString("N"); Directory.CreateDirectory(directory);
            try
            {
                string name = "Hidden/ALTO/Cycle/" + Guid.NewGuid().ToString("N");
                var dependency = ImportDependencyShader(directory, "Dependency", DependencyShaderSource(name + "/Dependency", DependencyProgram("IMPORTED", true)));
                var original = ImportDependencyShader(directory, "Owned", DependencyShaderSource(name + "/Owned", DependencyProgram("OWNED", false),
                    transitive ? "\"" + dependency.name + "\"" : "Off"));
                // A changed source dependency must be rejected before candidate
                // import, even while Unity still has the previous native program.
                File.WriteAllText(directory + "/" + (transitive ? "Dependency" : "Owned") + ".shader",
                    DependencyShaderSource(transitive ? dependency.name : original.name, DependencyProgram("UNCHANGED", true), "\"" + original.name + "\""));
                var statistics = new SharedShaderConstantSource.Statistics();
                Assert.That(SharedShaderConstantSource.TryCreate(original, new Dictionary<string, float> { ["_Shared"] = .25f },
                    SharedShaderTarget, new ShaderPropertyUsage(), out var output, out _, out _, out string reason, statistics), Is.False);
                Assert.That(output, Is.Null); Assert.That(reason, Does.Contain("retain the original"));
                Assert.That(statistics.ImportMilliseconds, Is.Zero); Assert.That(statistics.ValidatedPrograms, Is.Zero);
            }
            finally { AssetDatabase.DeleteAsset(directory); }
        }

        [TestCase(15)] [TestCase(16)]
        public void SharedShaderConstants_PreflightBudgetsRetainBeforeCandidateImportOrCompilation(int keywords)
        {
            RequireRuntimeShaderTarget();
            string directory = "Assets/__ALTO_ShaderPreflight_" + Guid.NewGuid().ToString("N"); Directory.CreateDirectory(directory);
            try
            {
                string declarations = string.Join("\n", Enumerable.Range(0, keywords).Select(i => "#pragma multi_compile_local _ ALTO_PREFLIGHT_" + i)) + "\n";
                var shader = ImportDependencyShader(directory, "Budget", DependencyShaderSource("Hidden/ALTO/Preflight/" + Guid.NewGuid().ToString("N"),
                    DependencyProgram("OWNED", false, declarations)));
                var statistics = new SharedShaderConstantSource.Statistics();
                Assert.That(SharedShaderConstantSource.TryCreate(shader, new Dictionary<string, float> { ["_Shared"] = .25f },
                    SharedShaderTarget, new ShaderPropertyUsage(), out var output, out _, out _, out string reason, statistics), Is.False);
                Assert.That(reason, Does.Contain("limit").Or.Contain("budget").Or.Contain("bound")); Assert.That(output, Is.Null);
                Assert.That(statistics.PreflightMilliseconds, Is.GreaterThan(0));
                Assert.That(statistics.SourceMilliseconds, Is.Zero); Assert.That(statistics.ImportMilliseconds, Is.Zero);
                Assert.That(statistics.ValidatedPrograms, Is.Zero); Assert.That(statistics.PreprocessMilliseconds, Is.Zero);
                Assert.That(statistics.CompileMilliseconds, Is.Zero); TestContext.WriteLine(statistics.Detail);
            }
            finally { AssetDatabase.DeleteAsset(directory); }
        }

        [Test]
        public void SharedShaderConstants_PureUsePassWrappersShareOneSpecializedDependency()
        {
            RequireRuntimeShaderTarget();
            string directory = "Assets/__ALTO_ShaderSharedPass_" + Guid.NewGuid().ToString("N"); Directory.CreateDirectory(directory);
            try
            {
                string name = "Hidden/ALTO/SharedPass/" + Guid.NewGuid().ToString("N");
                var dependency = ImportDependencyShader(directory, "Dependency", DependencyShaderSource(name + "/Dependency", DependencyProgram("IMPORTED", true)));
                var first = ImportDependencyShader(directory, "First", DependencyShaderSource(name + "/First", "UsePass \"" + dependency.name + "/IMPORTED\""));
                var second = ImportDependencyShader(directory, "Second", DependencyShaderSource(name + "/Second", "UsePass \"" + dependency.name + "/IMPORTED\""));
                var material = Track(new Material(first)); renderer.sharedMaterial = material;
                var other = PolicyChild("Other wrapper").AddComponent<MeshRenderer>();
                var secondMaterial = Track(new Material(second)); secondMaterial.SetFloat("_Different", .75f); other.sharedMaterial = secondMaterial;
                var messages = new List<string>();
                Assert.That(SpecializeSharedShaders(SharedShaderControllers(), messages), Is.EqualTo(2), string.Join("\n", messages));
                var firstDependencies = ShaderPropertyUsage.CollectNativeShaderDependencies(renderer.sharedMaterial.shader);
                var secondDependencies = ShaderPropertyUsage.CollectNativeShaderDependencies(other.sharedMaterial.shader);
                Assert.That(firstDependencies.Union(secondDependencies).Count(), Is.LessThanOrEqualTo(3),
                    "Native UsePass may copy the proved program into its caller. It must never retain additional shaders.");
                Assert.That(firstDependencies.Contains(dependency) || secondDependencies.Contains(dependency), Is.False);
                foreach (var shader in new[] { renderer.sharedMaterial.shader, other.sharedMaterial.shader })
                {
                    var pass = ShaderUtil.GetShaderData(shader).GetSerializedSubshader(0).GetPass(0);
                    var program = pass.PreprocessVariant(ShaderType.Fragment, Array.Empty<string>(), ShaderCompilerPlatform.D3D,
                        SharedShaderTarget, GraphicsTier.Tier1, false);
                    Assert.That(D3DShaderInputs.CompileUncached(program.PreprocessedCode, "frag", ShaderType.Fragment, out var inputs, out string reason), Is.True, reason);
                    Assert.That(inputs.Any(i => ShaderInputQuery.Uniform("_Shared").MatchesCompiled(i) && i.ReadMask != 0), Is.False);
                    Assert.That(inputs.Any(i => ShaderInputQuery.Uniform("_Different").MatchesCompiled(i) && i.ReadMask != 0), Is.True);
                }
                foreach (bool alternate in new[] { false, true })
                foreach (var pair in new[] { (material, renderer.sharedMaterial), (secondMaterial, other.sharedMaterial) })
                {
                    foreach (var m in new[] { pair.Item1, pair.Item2 })
                        if (alternate) m.EnableKeyword("ALTO_DEPENDENCY_ALTERNATE"); else m.DisableKeyword("ALTO_DEPENDENCY_ALTERNATE");
                    Assert.That(Vector4.Distance(SharedShaderPixel(pair.Item1, 0), SharedShaderPixel(pair.Item2, 0)), Is.LessThan(.00001f));
                }
            }
            finally { AssetDatabase.DeleteAsset(directory); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SharedShaderConstants_UnchangedDependencyWrappersSkipNativePlanning(bool fallback)
        {
            RequireRuntimeShaderTarget();
            string directory = "Assets/__ALTO_UnchangedWrapper_" + Guid.NewGuid().ToString("N"); Directory.CreateDirectory(directory);
            try
            {
                string name = "ALTO/UnchangedWrapper/" + Guid.NewGuid().ToString("N");
                var dependency = ImportDependencyShader(directory, "Dependency",
                    DependencyShaderSource(name + "/Dependency", DependencyProgram("IMPORTED", true)));
                var wrapper = ImportDependencyShader(directory, "Wrapper", DependencyShaderSource(name + "/Wrapper",
                    fallback ? "Pass {}" : "UsePass \"" + dependency.name + "/IMPORTED\"",
                    fallback ? "\"" + dependency.name + "\"" : "Off"));
                var statistics = new SharedShaderConstantSource.Statistics();
                Assert.That(SharedShaderConstantSource.TryCreate(wrapper, new Dictionary<string, float> { ["_Shared"] = .25f },
                    SharedShaderTarget, new ShaderPropertyUsage(), out var output, out _, out _, out string reason, statistics), Is.False);
                Assert.That(reason, Does.Contain("no supported standalone uniform declaration"));
                Assert.That(output, Is.Null);
                Assert.That(statistics.KeyMilliseconds, Is.Zero);
                Assert.That(statistics.PreflightMilliseconds, Is.Zero);
                Assert.That(statistics.PlannedPrograms, Is.Zero);
                Assert.That(statistics.NativeCompiles, Is.Zero);
                Assert.That(statistics.ImportMilliseconds, Is.Zero);
            }
            finally { AssetDatabase.DeleteAsset(directory); }
        }

        [Test]
        public void SharedShaderConstants_UnsupportedStageIsCheckedBeforeExpandingOtherStageVariants()
        {
            RequireRuntimeShaderTarget();
            string directory = "Assets/__ALTO_ShaderStageOrder_" + Guid.NewGuid().ToString("N"); Directory.CreateDirectory(directory);
            try
            {
                string keywords = string.Join("\n", Enumerable.Range(0, 6).Select(i => "#pragma multi_compile_local_vertex _ ALTO_STAGE_ORDER_" + i));
                string source = "Shader \"Hidden/ALTO/StageOrder/" + Guid.NewGuid().ToString("N") + "\" {\n" +
                    "Properties { _Shared(\"Shared\",Float)=0.25 }\nSubShader { Pass {\nHLSLPROGRAM\n" +
                    "#pragma target 4.5\n#pragma vertex vert\n#pragma fragment frag\n" + keywords + "\n" +
                    "float _Shared; cbuffer First { float Other; }; cbuffer Second { float Another; };\n" +
                    "float4 vert(float4 p:POSITION):SV_POSITION {return p;}\n" +
                    "float4 frag():SV_Target {return float4(_Shared,Other,Another,1);}\nENDHLSL\n} } }";
                var shader = ImportDependencyShader(directory, "Probe", source);
                var statistics = new SharedShaderConstantSource.Statistics();
                Assert.That(SharedShaderConstantSource.TryCreate(shader, new Dictionary<string, float> { ["_Shared"] = .25f },
                    SharedShaderTarget, new ShaderPropertyUsage(), out var output, out _, out _, out string reason, statistics), Is.False);
                Assert.That(output, Is.Null);
                Assert.That(reason, Does.Contain("no unique metadata mapping"));
                Assert.That(statistics.PlannedPrograms, Is.GreaterThan(64));
                Assert.That(statistics.NativeCompiles, Is.LessThanOrEqualTo(6),
                    "The unchanged vertex stage cannot consume its full keyword universe before the fragment counterexample is examined.");
                var repeated = new SharedShaderConstantSource.Statistics();
                Assert.That(SharedShaderConstantSource.TryCreate(shader, new Dictionary<string, float> { ["_Shared"] = .25f },
                    SharedShaderTarget, new ShaderPropertyUsage(), out var repeatedOutput, out _, out _, out string repeatedReason, repeated), Is.False);
                Assert.That(repeatedOutput, Is.Null); Assert.That(repeatedReason, Is.EqualTo(reason));
                Assert.That(repeated.RetainedCacheHits, Is.EqualTo(1)); Assert.That(repeated.NativeCompiles, Is.Zero);
                Assert.That(repeated.ImportMilliseconds, Is.Zero);
                var changedValue = new SharedShaderConstantSource.Statistics();
                Assert.That(SharedShaderConstantSource.TryCreate(shader, new Dictionary<string, float> { ["_Shared"] = .5f },
                    SharedShaderTarget, new ShaderPropertyUsage(), out _, out _, out _, out _, changedValue), Is.False);
                Assert.That(changedValue.RetainedCacheHits, Is.Zero); Assert.That(changedValue.NativeCompiles, Is.GreaterThan(0));
                File.AppendAllText(directory + "/Probe.shader", "\n// New source revision.\n");
                AssetDatabase.ImportAsset(directory + "/Probe.shader", ImportAssetOptions.ForceSynchronousImport);
                var changedSource = new SharedShaderConstantSource.Statistics();
                Assert.That(SharedShaderConstantSource.TryCreate(shader, new Dictionary<string, float> { ["_Shared"] = .25f },
                    SharedShaderTarget, new ShaderPropertyUsage(), out _, out _, out _, out _, changedSource), Is.False);
                Assert.That(changedSource.RetainedCacheHits, Is.Zero); Assert.That(changedSource.NativeCompiles, Is.GreaterThan(0));
            }
            finally { AssetDatabase.DeleteAsset(directory); }
        }

        [Test]
        public void SharedConstantCertificateAcceptsOnlyAuthorizedDeclarationSubsets()
        {
            var plan = ShaderConstantInputs.Prepare("float _A; float _B; float4 f() { return _A + _B; }");
            var facts = new Dictionary<string, float> { ["_A"] = .25f, ["_B"] = .5f };
            string subset = plan.Apply(new Dictionary<string, float> { ["_B"] = .5f }, out _);
            Assert.That(plan.MatchesSpecialization(subset, facts, out int count), Is.True); Assert.That(count, Is.EqualTo(1));
            Assert.That(plan.MatchesSpecialization(subset.Replace("return _A + _B", "return _A - _B"), facts, out _), Is.False);
            Assert.That(plan.MatchesSpecialization(plan.Apply(new Dictionary<string, float> { ["_B"] = .75f }, out _), facts, out _), Is.False);
            Assert.That(plan.MatchesSpecialization(plan.Source, facts, out count), Is.True); Assert.That(count, Is.Zero);
        }

        [Test]
        public void SharedConstantCertificateIgnoresDiagnosticsButRetainsMacroWritesAndCodePragmas()
        {
            var plan = ShaderConstantInputs.Prepare("#pragma warning(disable: 1234)\nfloat _A;\n#define SET_VALUE _A = 2;\nfloat _B;\n");
            Assert.That(plan.UniformNames, Does.Not.Contain("_A"));
            Assert.That(plan.UniformNames, Does.Contain("_B"));
            string source = "#pragma warning(disable: 1234)\nfloat _A;\nfloat4 f() { return _A; }";
            plan = ShaderConstantInputs.Prepare(source);
            var facts = new Dictionary<string, float> { ["_A"] = .25f };
            string candidate = plan.Apply(facts, out _).Replace("#pragma warning(disable: 1234)", "");
            Assert.That(plan.MatchesSpecialization(candidate, facts, out _), Is.True);
            Assert.That(plan.MatchesSpecialization("#pragma pack_matrix(row_major)\n" + candidate, facts, out _), Is.False);
        }

        [Test]
        public void NativeVariantProjectionRetainsUnlistedEngineChoicesAndAllPassAlternatives()
        {
            var entries = new[] { new[] { "LIGHT_A", "FRAGMENT" }, new[] { "LIGHT_B" } };
            var represented = entries.SelectMany(e => e).ToHashSet(StringComparer.Ordinal);
            var projected = NativeShaderVariants.Project(entries, represented, new[] { "LIGHT_A", "LIGHT_B", "STEREO" }, 8);
            Assert.That(projected.Select(v => string.Join(";", v)), Is.EquivalentTo(new[] { "LIGHT_A", "LIGHT_B", "LIGHT_A;STEREO", "LIGHT_B;STEREO" }));
            Assert.Throws<InvalidOperationException>(() => NativeShaderVariants.Project(entries, represented, new[] { "LIGHT_A", "LIGHT_B", "STEREO" }, 2));
        }
    }
}
