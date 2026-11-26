using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Noname.AvatarTools.Analysis;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        [TestCase(false, false, false)]
        [TestCase(true, false, false)]
        [TestCase(false, true, false)]
        [TestCase(false, false, true)]
        [TestCase(true, false, true)]
        [TestCase(false, true, true)]
        public void SharedNativeUsePassArtifactsRequireExactOwnershipAndKeywordScope(bool changedScope, bool ambiguousOwner, bool generated)
        {
            RequireD3DAnalysis();
            string directory = "Assets/__ALTOImportedNative_" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(directory);
            try
            {
                string name = "Hidden/ALTO/ImportedNative/" + Guid.NewGuid().ToString("N");
                string pass = ImportedNativePass("SHARED", true);
                var owner = ImportedNativeShader(directory, "Owner", "Shader \"" + name + "\" { Properties { _IndexEffect(\"Index\",Float)=0 } SubShader {" +
                    pass + (ambiguousOwner ? pass : "") + "} Fallback Off }", generated);
                var root = ImportedNativeShader(directory, "Root", "Shader \"" + name + "/Root\" { Properties { _IndexEffect(\"Index\",Float)=0 } SubShader { UsePass \"" +
                    name + "/SHARED\" " + (changedScope ? ImportedNativePass("SCOPE", false) : "") + "} Fallback Off }", generated);
                if (generated) Assert.That(AssetDatabase.GetAssetPath(root), Is.EqualTo(AssetDatabase.GetAssetPath(owner)),
                    "Generated sources must remain distinct even in a shared asset container.");
                Assert.That(ShaderUtil.ShaderHasError(owner) || ShaderUtil.ShaderHasError(root), Is.False);
                var sources = new ShaderPropertyUsage(); var inventory = sources.GetPrograms(root);
                Assert.That(inventory.Uncertain, Is.Null);
                var original = inventory.Passes.First(p => p.Shader == owner && p.Pass.Name == "SHARED").Pass;
                var imported = inventory.Passes.First(p => p.Shader == root && p.Pass.Name == "SHARED").Pass;
                Assert.That(inventory.NativeShaderDependencies, Does.Contain(owner));
                bool share = !changedScope && !ambiguousOwner;
                Assert.That(sources.ImportedNativePass(imported), share ? Is.SameAs(original) : Is.SameAs(imported));
                var inputs = sources.MeshInputs;
                var constants = new Dictionary<string, float> { ["_IndexEffect"] = 0 };
                foreach (var tier in ShaderMeshInputs.Tiers)
                {
                    Assert.That(ImportedNativePreprocess(inputs, original, tier, constants), Is.Null);
                    Assert.That(ImportedNativePreprocess(inputs, imported, tier, constants), Is.Null);
                }
                int expected = ShaderMeshInputs.Tiers.Length * (share ? 1 : 2);
                Assert.That(inputs.CompileCalls, Is.EqualTo(expected));
                Assert.That(inputs.NativeProgramCompileCalls, Is.EqualTo(expected));
                Assert.That(inputs.NativeImportedProgramHits, Is.EqualTo(share ? ShaderMeshInputs.Tiers.Length : 0));
                constants["_IndexEffect"] = 1;
                Assert.That(ImportedNativePreprocess(inputs, imported, GraphicsTier.Tier1, constants),
                    Is.EqualTo(ShaderProgramInspection.InputIssue), "Native program reuse must re-evaluate the importing material's constants.");
                Assert.That(inputs.CompileCalls, Is.EqualTo(expected));
                Assert.That(inputs.NativeProgramCompileCalls, Is.EqualTo(expected));
            }
            finally { AssetDatabase.DeleteAsset(directory); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SharedNativeUsePassChainsReserveAndCompileOneCanonicalProgram(bool generated)
        {
            RequireD3DAnalysis();
            string directory = "Assets/__ALTOImportedNativeChain_" + Guid.NewGuid().ToString("N"); Directory.CreateDirectory(directory);
            try
            {
                string name = "Hidden/ALTO/ImportedNativeChain/" + Guid.NewGuid().ToString("N");
                Shader root = null;
                for (int i = 0; i < 3; i++)
                {
                    string body = i == 0 ? ImportedNativePass("SHARED", true) : "UsePass \"" + name + "/" + (i - 1) + "/SHARED\"";
                    root = ImportedNativeShader(directory, i.ToString(), "Shader \"" + name + "/" + i + "\" { SubShader {" + body + "} Fallback Off }", generated);
                }
                var sources = new ShaderPropertyUsage(); var inventory = sources.GetPrograms(root);
                Assert.That(inventory.Uncertain, Is.Null);
                var constants = new Dictionary<string, float> { ["_IndexEffect"] = 0 };
                foreach (var pass in inventory.Passes.OrderByDescending(p => p.Shader.name, StringComparer.Ordinal))
                    Assert.That(ImportedNativePreprocess(sources.MeshInputs, pass.Pass, GraphicsTier.Tier1, constants), Is.Null);
                Assert.That(sources.MeshInputs.PreprocessCalls, Is.EqualTo(3), "Each importing environment is checked.");
                Assert.That(sources.MeshInputs.CompileCalls, Is.EqualTo(1));
                Assert.That(sources.MeshInputs.NativeProgramCompileCalls, Is.EqualTo(1));
            }
            finally { AssetDatabase.DeleteAsset(directory); }
        }

        [Test]
        public void SharedNativeUsePassArtifactsAreInvalidatedByOwnerSourceChanges()
        {
            RequireD3DAnalysis();
            string directory = "Assets/__ALTOImportedNativeEdit_" + Guid.NewGuid().ToString("N"); Directory.CreateDirectory(directory);
            try
            {
                string name = "Hidden/ALTO/ImportedNativeEdit/" + Guid.NewGuid().ToString("N");
                string ownerPath = directory + "/Owner.shader", rootPath = directory + "/Root.shader";
                string text = "Shader \"" + name + "\" { Properties { _IndexEffect(\"Index\",Float)=0 } SubShader {" + ImportedNativePass("SHARED", true) + "} Fallback Off }";
                File.WriteAllText(ownerPath, text); AssetDatabase.ImportAsset(ownerPath, ImportAssetOptions.ForceSynchronousImport);
                File.WriteAllText(rootPath, "Shader \"" + name + "/Root\" { SubShader { UsePass \"" + name + "/SHARED\" } Fallback Off }");
                AssetDatabase.ImportAsset(rootPath, ImportAssetOptions.ForceSynchronousImport);
                var root = AssetDatabase.LoadAssetAtPath<Shader>(rootPath); var artifacts = new ShaderAnalysisArtifacts();
                var constants = new Dictionary<string, float> { ["_IndexEffect"] = 0 };
                var before = new ShaderPropertyUsage(artifacts);
                var first = before.GetPrograms(root).Passes.Single(p => p.Shader == root).Pass;
                ImportedNativeIdentities(before, root);
                Assert.That(ImportedNativePreprocess(before.MeshInputs, first, GraphicsTier.Tier1, constants), Is.Null);
                var warm = new ShaderPropertyUsage(artifacts);
                var repeated = warm.GetPrograms(root).Passes.Single(p => p.Shader == root).Pass;
                ImportedNativeIdentities(warm, root);
                Assert.That(ImportedNativePreprocess(warm.MeshInputs, repeated, GraphicsTier.Tier1, constants), Is.Null);
                Assert.That(warm.MeshInputs.NativePreprocessCacheHits, Is.GreaterThan(0), "Exercise the shared native source cache before invalidating it.");
                File.WriteAllText(ownerPath, text.Replace("if (_IndexEffect > 0) return p + id; return p;", "return p + id;"));
                AssetDatabase.ImportAsset(ownerPath, ImportAssetOptions.ForceSynchronousImport);
                AssetDatabase.ImportAsset(rootPath, ImportAssetOptions.ForceSynchronousImport);
                var after = new ShaderPropertyUsage(artifacts);
                var second = after.GetPrograms(root).Passes.Single(p => p.Shader == root).Pass;
                ImportedNativeIdentities(after, root);
                Assert.That(ImportedNativePreprocess(after.MeshInputs, second, GraphicsTier.Tier1, constants),
                    Is.EqualTo(ShaderProgramInspection.InputIssue));
                Assert.That(after.MeshInputs.NativeProgramCompileCalls, Is.EqualTo(1));
            }
            finally { AssetDatabase.DeleteAsset(directory); }
        }

        private static string ImportedNativePreprocess(ShaderMeshInputs inputs, ShaderData.Pass pass, GraphicsTier tier,
            IReadOnlyDictionary<string, float> constants)
        {
            object[] args = { pass, ShaderType.Vertex, Array.Empty<string>(), ShaderCompilerPlatform.D3D,
                BuildTarget.StandaloneWindows64, tier, ShaderInputQuery.VertexId, constants, null, null };
            return (string)typeof(ShaderMeshInputs).GetMethod("Preprocess", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(inputs, args);
        }

        [Test]
        public void NativeSemanticInventoryCachesAbsenceWithoutDependingOnArithmeticSupportOrMaterialValues()
        {
            RequireD3DAnalysis();
            string directory = "Assets/__ALTONativeSemanticInventory_" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(directory);
            try
            {
                string path = directory + "/Probe.shader";
                string text = "Shader \"Hidden/ALTO/NativeInventory/" + Guid.NewGuid().ToString("N") + "\" { SubShader { Pass {\nHLSLPROGRAM\n" +
                    "#pragma target 4.5\n#pragma vertex vert\n#pragma fragment frag\n#pragma multi_compile_local __ ALTO_READ_INDEX\n" +
                    "float _Iterations;\nfloat4 vert(float4 p:POSITION,uint id:SV_VertexID):SV_POSITION {\n" +
                    "[loop] for(int i=0;i<(int)_Iterations;i++) {p.x=sin(p.x+1); if(p.x>_Iterations)return p;}\n" +
                    "#if defined(ALTO_READ_INDEX)\np.y+=id;\n#endif\nreturn p;}\n" +
                    "float4 frag():SV_Target{return 1;}\nENDHLSL\n} } }";
                File.WriteAllText(path, text); AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
                Assert.That(ShaderUtil.ShaderHasError(shader), Is.False);
                var first = new ShaderPropertyUsage(); var firstPass = first.GetPrograms(shader).Passes.Single().Pass;
                ImportedNativeIdentities(first, shader);
                var native = firstPass.CompileVariant(ShaderType.Vertex, Array.Empty<string>(), ShaderCompilerPlatform.D3D,
                    BuildTarget.StandaloneWindows64, GraphicsTier.Tier1, true);
                Assert.That(native.Success, Is.True);
                Assert.That(D3DConstantEquivalence.TryInputs(native, new Dictionary<string, float>(), ShaderType.Vertex,
                    out _, out _, semanticInputsOnly: true), Is.False,
                    "The fixture must exercise a native program beyond the arithmetic evaluator.");
                Assert.That(ImportedNativePreprocess(first.MeshInputs, firstPass, GraphicsTier.Tier1, new Dictionary<string, float>()), Is.Null);
                Assert.That(first.MeshInputs.NativeProgramCompileCalls, Is.EqualTo(1));
                long hits = ShaderAnalysisCache.Shared.Hits;
                var warm = new ShaderPropertyUsage(); var warmPass = warm.GetPrograms(shader).Passes.Single().Pass;
                ImportedNativeIdentities(warm, shader);
                Assert.That(ImportedNativePreprocess(warm.MeshInputs, warmPass, GraphicsTier.Tier1,
                    new Dictionary<string, float> { ["_Iterations"] = 100 }), Is.Null);
                Assert.That(warm.MeshInputs.NativeProgramCompileCalls, Is.Zero);
                Assert.That(warm.MeshInputs.CompileCalls, Is.EqualTo(first.MeshInputs.CompileCalls));
                Assert.That(ShaderAnalysisCache.Shared.Hits, Is.GreaterThan(hits));
                object[] otherVariant = { warmPass, ShaderType.Vertex, new[] { "ALTO_READ_INDEX" }, ShaderCompilerPlatform.D3D,
                    BuildTarget.StandaloneWindows64, GraphicsTier.Tier1, ShaderInputQuery.VertexId, new Dictionary<string, float>(), null, null };
                Assert.That(typeof(ShaderMeshInputs).GetMethod("Preprocess", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(warm.MeshInputs, otherVariant), Is.EqualTo(ShaderProgramInspection.InputIssue));
                Assert.That(warm.MeshInputs.NativeProgramCompileCalls, Is.EqualTo(1));
                File.WriteAllText(path, text.Replace("return p;", "return p + id;"));
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                var changed = new ShaderPropertyUsage(); var changedPass = changed.GetPrograms(shader).Passes.Single().Pass;
                ImportedNativeIdentities(changed, shader);
                Assert.That(ImportedNativePreprocess(changed.MeshInputs, changedPass, GraphicsTier.Tier1,
                    new Dictionary<string, float>()), Is.EqualTo(ShaderProgramInspection.InputIssue));
                Assert.That(changed.MeshInputs.NativeProgramCompileCalls, Is.EqualTo(1));
            }
            finally { AssetDatabase.DeleteAsset(directory); }
        }

        private static Shader ImportedNativeShader(string directory, string stem, string source, bool generated)
        {
            string path = directory + "/" + stem + ".shader";
            if (!generated)
            {
                File.WriteAllText(path, source);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                return AssetDatabase.LoadAssetAtPath<Shader>(path);
            }
            var shader = ShaderUtil.CreateShaderAsset(source, true);
            string container = directory + "/Programs.asset";
            if (AssetDatabase.LoadMainAssetAtPath(container) == null) AssetDatabase.CreateAsset(shader, container);
            else AssetDatabase.AddObjectToAsset(shader, container);
            ShaderBuildSource.Register(shader, source, Path.GetFullPath(path));
            ShaderUtil.RegisterShader(shader);
            return shader;
        }

        private static void ImportedNativeIdentities(ShaderPropertyUsage sources, Shader root)
        {
            var values = (Dictionary<ShaderData.Pass, string>)typeof(ShaderMeshInputs)
                .GetField("preprocessingIdentities", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(sources.MeshInputs);
            foreach (var pass in sources.GetPrograms(root).Passes)
                values[pass.Pass] = sources.NativePreprocessIdentity(root, pass.Shader, pass.Pass, pass.Identifier, "native import cache regression");
            Assert.That(values.Values.All(v => v != null), Is.True);
        }

        private static string ImportedNativePass(string name, bool local) => "Pass { Name \"" + name + "\"\nHLSLPROGRAM\n" +
            "#pragma target 4.0\n#pragma vertex vert\n#pragma fragment frag\n#pragma multi_compile" + (local ? "_local" : "") + " __ ALTO_IMPORTED_INDEX\n" +
            "float _IndexEffect;\nfloat4 vert(float4 p:POSITION,uint id:SV_VertexID):SV_POSITION {\n" +
            "#if defined(ALTO_IMPORTED_INDEX)\nreturn p + id;\n#else\nif (_IndexEffect > 0) return p + id; return p;\n#endif\n}\n" +
            "float4 frag():SV_Target{return 1;}\nENDHLSL\n}";
    }
}
