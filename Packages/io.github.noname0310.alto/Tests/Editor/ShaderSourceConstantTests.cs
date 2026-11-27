using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.Materials;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        [Test]
        public void ShaderImportedAliasesMustResolveToAnActualDependency()
        {
            var standard = Shader.Find("Standard"); var vertexLit = Shader.Find("VertexLit");
            Assert.That(standard, Is.Not.Null); Assert.That(vertexLit, Is.Not.Null);
            var dependencies = ShaderPropertyUsage.CollectNativeShaderDependencies(standard);
            Assert.That(ShaderPropertyUsage.ResolveImportedDependency("VertexLit", dependencies, out bool ambiguous), Is.SameAs(vertexLit));
            Assert.That(ambiguous, Is.False);
            Assert.That(ShaderPropertyUsage.ResolveImportedDependency("VertexLit", new[] { standard }, out _), Is.Null);
            Assert.That(new ShaderPropertyUsage().GetPrograms(standard).Uncertain, Is.Null);
        }

        [Test]
        public void ShaderImportedAliasesDoNotHideAmbiguousActualNames()
        {
            string name = "Hidden/ALTO/Ambiguous/" + Guid.NewGuid().ToString("N");
            string source = "Shader \"" + name + "\" { SubShader { Pass {} } }";
            var first = Track(ShaderUtil.CreateShaderAsset(source)); var second = Track(ShaderUtil.CreateShaderAsset(source));
            Assert.That(ShaderPropertyUsage.ResolveImportedDependency(name, new[] { first, second }, out bool ambiguous), Is.Null);
            Assert.That(ambiguous, Is.True);
        }

        [TestCase(false)] [TestCase(true)]
        public void ShaderEngineIncludesCompileAfterSurfaceSourceSpecialization(bool explicitEnginePath)
        {
            RequireRuntimeShaderTarget();
            string directory = "Assets/ALTO_SurfaceConstants_" + Guid.NewGuid().ToString("N");
            try
            {
                System.IO.Directory.CreateDirectory(directory);
                string engine = System.IO.Path.Combine(EditorApplication.applicationContentsPath, "CGIncludes", "UnityCG.cginc").Replace('\\', '/');
                string include = explicitEnginePath ? engine : "UnityCG.cginc";
                string source = "Shader \"Hidden/ALTO/SurfaceConstants/" + Guid.NewGuid().ToString("N") +
                    "\" { Properties { _A(\"A\",Float)=0 _B(\"B\",Float)=0 } SubShader { CGPROGRAM\n" +
                    "#pragma surface surf Standard\n#include \"" + include + "\"\n" +
                    "half _A, _B; struct Input { float2 uv_MainTex; }; void surf(Input i,inout SurfaceOutputStandard o){o.Albedo=_A; o.Metallic=_B;}\nENDCG } }";
                source = ShaderSourceExpansion.Expand(source, directory, new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0);
                var original = ImportDependencyShader(directory, "Surface", source);
                Assert.That(SharedShaderConstantSource.TryCreate(original, new Dictionary<string, float> { ["_A"] = .25f },
                    SharedShaderTarget, new ShaderPropertyUsage(), out var generated, out string text, out int count, out string issue,
                    allowCompilerConstantFolding: true), Is.True, issue);
                Track(generated);
                Assert.That(ShaderUtil.ShaderHasError(generated), Is.False);
                Assert.That(count, Is.EqualTo(1));
                Assert.That(text, Does.Contain("#include \"UnityCG.cginc\"").And.Not.Contain(engine));
                Assert.That(text, Does.Contain("static const half _A").And.Contain("half _B;"));
            }
            finally { AssetDatabase.DeleteAsset(directory); }
        }

        [TestCase("_Value += 1;")]
        [TestCase("_Value %= 1;")]
        [TestCase("_Value.x = 1;")]
        [TestCase("++_Value;")]
        [TestCase("_Value.x++;")]
        [TestCase("Unknown(_Value);")]
        [TestCase("sincos(0, _Value, other);")]
        [TestCase("#define WRITE _Value = 1\nWRITE")]
        [TestCase("#define NAME(a,b) a ## b\nNAME(_Val,ue) = 1;")]
        [TestCase("#define abs(x) ((x) = 1)\nabs(_Value)")]
        public void ShaderSourceConstants_MutableOrUnknownUseKeepsUniform(string body)
        {
            var names = new HashSet<string>(StringComparer.Ordinal) { "_Value" };
            ShaderSourceConstants.RestrictToReadOnly("float _Value;\n" + body, names);
            Assert.That(names, Is.Empty);
        }

        [Test]
        public void ShaderSourceConstants_ReadsDoNotRequireNativeBufferMapping()
        {
            var names = new HashSet<string>(StringComparer.Ordinal) { "_Value", "_Other" };
            ShaderSourceConstants.RestrictToReadOnly("float _Value; float _Other; float f() { _Other = 1; return saturate(_Value) * 2; }", names);
            Assert.That(names, Is.EquivalentTo(new[] { "_Value" }));
        }

        [Test]
        public void ShaderSourceConstants_SourceReplacementIsSharedAndFeedsLaterInputAnalysis()
        {
            RequireRuntimeShaderTarget(); Mesh("Timing");
            var other = PolicyChild("Other").AddComponent<MeshRenderer>();
            var first = SharedShaderMaterial(renderer, .2f); var second = SharedShaderMaterial(other, .8f);
            var before = new[] { SharedShaderPixel(first, 0), SharedShaderPixel(second, 0) };
            var diagnostics = new List<string>();
            Assert.That(SharedShaderConstants.Apply(root, SharedShaderControllers(), null, SharedShaderTarget,
                null, null, asset => Track(asset), diagnostics, allowCompilerConstantFolding: _ => true), Is.EqualTo(1), string.Join("\n", diagnostics));
            var generated = renderer.sharedMaterial.shader;
            Assert.That(generated, Is.SameAs(other.sharedMaterial.shader));
            Assert.That(ShaderBuildSource.TryRead(generated, out string text, out string path), Is.True);
            Assert.That(text, Does.Match(@"static (?:const )?float _Shared"));
            Assert.That(text, Does.Not.Contain("static const float _Different"));
            Assert.That(text, Does.Not.Contain("static const float _Writable"));
            Assert.That(path, Does.EndWith(".shader"));
            var sources = new ShaderPropertyUsage();
            Assert.That(sources.GetPrograms(generated).Passes.Count, Is.EqualTo(2));
            Assert.That(Vector4.Distance(SharedShaderPixel(renderer.sharedMaterial, 0), before[0]), Is.LessThan(.00001f));
            Assert.That(Vector4.Distance(SharedShaderPixel(other.sharedMaterial, 0), before[1]), Is.LessThan(.00001f));
            Assert.That(diagnostics.Any(d => d.Contains("source-level constant folding")), Is.True);
        }

        [Test]
        public void ShaderSourceConstants_StrictPolicySurvivesMergedScope()
        {
            root.AddComponent<VRCAvatarDescriptor>();
            var strict = PolicyChild("Strict"); strict.AddComponent<ALTOOptimizationPolicy>().AllowShaderConstantFolding = false;
            var relaxed = PolicyChild("Relaxed");
            var output = PolicyChild("Output");
            var context = new BuildContext(root, null); OptimizationPolicy.Capture(context);
            var policy = context.GetState<OptimizationPolicy>();
            Assert.That(policy.AllowsShaderConstantFolding(relaxed.transform), Is.True);
            policy.InheritNumericalConstraints(output.transform, new[] { relaxed.transform, strict.transform });
            UnityEngine.Object.DestroyImmediate(strict);
            Assert.That(policy.AllowsShaderConstantFolding(output.transform), Is.False);
            Assert.That(policy.AllowsSmallNumericalDifferences(output.transform, NumericalOptimization.All), Is.True);
        }

        [Test]
        public void ShaderSourceConstants_ContentCacheReusesSourceAndRetainsNewCandidates()
        {
            RequireRuntimeShaderTarget();
            var original = Shader.Find(SharedShaderName);
            var values = new Dictionary<string, float> { ["_Shared"] = .375f };
            Assert.That(SharedShaderConstantSource.TryCreate(original, values, SharedShaderTarget, new ShaderPropertyUsage(),
                out var first, out string firstText, out _, out string reason, allowCompilerConstantFolding: true), Is.True, reason);
            Track(first);
            var stats = new SharedShaderConstantSource.Statistics();
            Assert.That(SharedShaderConstantSource.TryCreate(original, values, SharedShaderTarget, new ShaderPropertyUsage(),
                out var second, out string secondText, out _, out reason, statistics: stats, allowCompilerConstantFolding: true), Is.True, reason);
            Track(second);
            Assert.That(stats.CacheHits, Is.EqualTo(1)); Assert.That(stats.NativeCompiles, Is.Zero);
            Assert.That(secondText, Is.EqualTo(firstText));
            Assert.That(ShaderBuildSource.CanSpecialize(second, new[] { "_Shared" }), Is.False);
            Assert.That(ShaderBuildSource.CanSpecialize(second, new[] { "_Different" }), Is.True);
            values["_Shared"] = .625f;
            Assert.That(SharedShaderConstantSource.TryCreate(original, values, SharedShaderTarget, new ShaderPropertyUsage(),
                out var third, out string thirdText, out _, out reason, allowCompilerConstantFolding: true), Is.True, reason);
            Track(third); Assert.That(thirdText, Is.Not.EqualTo(firstText));
        }

        [Test]
        public void ShaderSourceConstants_RemainingDemandRevisitsNewlyCommonMaterialValues()
        {
            RequireRuntimeShaderTarget();
            var other = PolicyChild("Other").AddComponent<MeshRenderer>();
            SharedShaderMaterial(renderer, .2f); SharedShaderMaterial(other, .8f);
            var entries = SharedShaderControllers();
            int Apply() => SharedShaderConstants.Apply(root, entries, null, SharedShaderTarget,
                null, null, asset => Track(asset), new List<string>(), allowCompilerConstantFolding: _ => true);
            Assert.That(Apply(), Is.EqualTo(1));
            var first = renderer.sharedMaterial.shader;
            Assert.That(ShaderBuildSource.TryRemainingConstants(first, out var remaining), Is.True);
            Assert.That(remaining, Does.Contain("_Different").And.Not.Contain("_Shared"));
            Assert.That(Apply(), Is.Zero);
            other.sharedMaterial.SetFloat("_Different", .2f);
            Assert.That(Apply(), Is.EqualTo(1), "A finite remaining demand must not certify the whole shader as finished.");
            Assert.That(renderer.sharedMaterial.shader, Is.SameAs(other.sharedMaterial.shader).And.Not.SameAs(first));
            Assert.That(ShaderBuildSource.TryRead(renderer.sharedMaterial.shader, out string text, out _), Is.True);
            Assert.That(text, Does.Match(@"static (?:const )?float _Different"));
            Assert.That(Apply(), Is.Zero);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ShaderSourceConstants_ExhaustedWrapperStillRelocatesChangedDependency(bool replacedOwner)
        {
            RequireRuntimeShaderTarget();
            string directory = "Assets/__ALTO_RemainingDemand_" + Guid.NewGuid().ToString("N");
            System.IO.Directory.CreateDirectory(directory);
            try
            {
                string name = "Hidden/ALTO/RemainingDemand/" + Guid.NewGuid().ToString("N");
                var dependency = ImportDependencyShader(directory, "Dependency", DependencyShaderSource(name + "/Dependency", DependencyProgram("IMPORTED", true)));
                var wrapper = ImportDependencyShader(directory, "Wrapper", DependencyShaderSource(name + "/Wrapper", "UsePass \"" + dependency.name + "/IMPORTED\""));
                renderer.sharedMaterial = Track(new Material(wrapper));
                var other = PolicyChild("Other wrapper user").AddComponent<MeshRenderer>();
                other.sharedMaterial = Track(new Material(wrapper)); other.sharedMaterial.SetFloat("_Different", .8f);
                var entries = SharedShaderControllers(); var messages = new List<string>();
                int Apply() => SharedShaderConstants.Apply(root, entries, null, SharedShaderTarget,
                    null, null, asset => Track(asset), messages, allowCompilerConstantFolding: _ => true);
                Assert.That(Apply(), Is.EqualTo(1), string.Join("\n", messages));
                var first = renderer.sharedMaterial.shader;
                Assert.That(ShaderBuildSource.TryRemainingConstants(first, out var remaining), Is.True);
                Assert.That(remaining, Is.Empty, "The wrapper has no own program declarations.");
                int count = ShaderBuildSource.Dependencies(first).Count;
                if (replacedOwner)
                {
                    var owner = ShaderBuildSource.Dependencies(first).Single(s => s != first);
                    Assert.That(ShaderBuildSource.TryRead(owner, out string source, out _), Is.True);
                    var impostor = Track(ShaderUtil.CreateShaderAsset(source + "\n// A different import with the same public name.\n", true));
                    ShaderUtil.RegisterShader(impostor);
                    Assert.That(Shader.Find(owner.name), Is.SameAs(impostor).And.Not.SameAs(owner));
                    other.sharedMaterial.SetFloat("_Different", renderer.sharedMaterial.GetFloat("_Different"));
                    Assert.That(Apply(), Is.Zero, "A later name resolution cannot replace generation-time dependency ownership.");
                    Assert.That(renderer.sharedMaterial.shader, Is.SameAs(first));
                    return;
                }
                other.sharedMaterial.SetFloat("_Different", renderer.sharedMaterial.GetFloat("_Different"));
                Assert.That(Apply(), Is.EqualTo(1), string.Join("\n", messages));
                var second = renderer.sharedMaterial.shader;
                Assert.That(second, Is.Not.SameAs(first).And.SameAs(other.sharedMaterial.shader));
                var dependencies = ShaderBuildSource.Dependencies(second);
                Assert.That(dependencies.Count, Is.EqualTo(count));
                Assert.That(dependencies, Has.No.Member(first));
                Assert.That(dependencies.Where(s => s != second).Any(s => ShaderBuildSource.TryRead(s, out string text, out _) &&
                    System.Text.RegularExpressions.Regex.IsMatch(text, @"static (?:const )?float _Different")), Is.True);
                Assert.That(Apply(), Is.Zero);
            }
            finally { AssetDatabase.DeleteAsset(directory); }
        }

        [Test]
        public void ShaderSourceConstants_SharedMetadataRetainsPerMaterialValues()
        {
            RequireRuntimeShaderTarget();
            var targets = new List<Renderer> { renderer };
            for (int i = 1; i < 12; i++) targets.Add(PolicyChild("User " + i).AddComponent<MeshRenderer>());
            for (int i = 0; i < targets.Count; i++) SharedShaderMaterial(targets[i], i / 20f);
            var expected = targets.Select(r => SharedShaderPixel(r.sharedMaterial, 0)).ToArray();
            int metadata = 0, writerIndexes = 0;
            var previous = AnalysisCost.Observer;
            try
            {
                AnalysisCost.Observer = (name, _) =>
                {
                    if (name == "Shared shader constants/property metadata") metadata++;
                    if (name == "Shared shader constants/renderer writers") writerIndexes++;
                };
                Assert.That(SharedShaderConstants.Apply(root, SharedShaderControllers(), null, SharedShaderTarget,
                    null, null, asset => Track(asset), new List<string>(), allowCompilerConstantFolding: _ => true), Is.EqualTo(1));
            }
            finally { AnalysisCost.Observer = previous; }
            Assert.That(metadata, Is.EqualTo(1)); Assert.That(writerIndexes, Is.EqualTo(targets.Count));
            for (int i = 0; i < targets.Count; i++)
            {
                Assert.That(targets[i].sharedMaterial.shader, Is.SameAs(renderer.sharedMaterial.shader));
                Assert.That(Vector4.Distance(SharedShaderPixel(targets[i].sharedMaterial, 0), expected[i]), Is.LessThan(.00001f));
            }
        }

        [Test]
        public void ShaderSourceConstants_InitializedGlobalsRetainCompilerInputElimination()
        {
            RequireRuntimeShaderTarget();
            string path = ShaderBuildSource.PhysicalPath(MeshInputShader("Indexed"));
            string source = System.IO.File.ReadAllText(path)
                .Replace("Hidden/Noname/ALTO/MeshInputsIndexed", "Hidden/ALTO/InitializedGlobal/" + Guid.NewGuid().ToString("N"))
                .Replace("float _IndexEffect;", "float _IndexEffect; float ReadValue(float value) { return value; }")
                .Replace("_IndexEffect != 0", "ReadValue(_IndexEffect) != 0");
            var original = Track(ShaderUtil.CreateShaderAsset(source, true));
            ShaderBuildSource.Register(original, source, path);
            Assert.That(SharedShaderConstantSource.TryCreate(original, new Dictionary<string, float> { ["_IndexEffect"] = 0 },
                SharedShaderTarget, new ShaderPropertyUsage(), out var generated, out string output, out _, out string reason,
                allowCompilerConstantFolding: true), Is.True, reason);
            Track(generated); Assert.That(output, Does.Contain("static float _IndexEffect = 0"));
            renderer.sharedMaterial = Track(new Material(generated));
            var usage = new ShaderPropertyUsage();
            Assert.That(usage.MeshInputs.Issue(renderer.sharedMaterial, renderer, FrameIndex(), false, SharedShaderTarget), Is.Null,
                string.Join("\n", usage.MeshInputs.UnitDiagnostics));
        }
    }
}
