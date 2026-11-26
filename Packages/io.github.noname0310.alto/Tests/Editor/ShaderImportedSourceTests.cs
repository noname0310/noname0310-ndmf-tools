using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Noname.AvatarTools.Analysis;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    // A deliberately unrelated importer verifies that production analysis does
    // not depend on a shader family, extension, importer type or TextAsset name.
    [ScriptedImporter(1, "altoshaderfixture")]
    internal sealed class ShaderImportedFixtureImporter : ScriptedImporter
    {
        public override void OnImportAsset(AssetImportContext context)
        {
            string source = File.ReadAllText(context.assetPath);
            string include = Path.ChangeExtension(context.assetPath, ".hlsl");
            if (File.Exists(include)) context.DependsOnSourceAsset(include);
            var shader = ShaderUtil.CreateShaderAsset(context, source, false);
            context.AddObjectToAsset("shader", shader); context.SetMainObject(shader);
            // A plausible source sibling is not authoritative: its program
            // intentionally disagrees with the actual generated shader.
            var decoy = new TextAsset(source.Replace("p.x += id;", "p.x += 0;")) { name = "Shader Source" };
            context.AddObjectToAsset("decoy", decoy);
        }
    }

    public sealed partial class BlendShapeOptimizationTests
    {
        [Test]
        public void ImportedShaderUnknownFixedFunctionStateDoesNotInventProgramInputs()
        {
            var proof = new ShaderPropertyUsage.Proof { FixedFunctionUncertain = "unknown state schema" };
            Assert.That(proof.MayUse("_ReceiverOutput"), Is.True, "Contact and material pruning must preserve unknown fixed-function readers.");
            Assert.That(ShaderMeshInputs.SourceIssue(proof, ShaderInputQuery.VertexId), Is.Null);
            Assert.That(ShaderMeshInputs.SourceIssue(proof, ShaderInputQuery.Identifier("unity_InstanceID")), Is.Null);
        }

        [TestCase(false, false)] [TestCase(false, true)]
        [TestCase(true, false)] [TestCase(true, true)]
        public void ImportedShaderProgramsDoNotRequireEditableSource(bool persistent, bool usesIndex)
        {
            RequireD3DAnalysis();
            string directory = ImportedSourceDirectory();
            try
            {
                var shader = Track(ShaderUtil.CreateShaderAsset(ImportedSourceText(usesIndex), false));
                if (persistent) AssetDatabase.CreateAsset(shader, directory + "/Program.asset");
                Assert.That(ShaderBuildSource.TryRead(shader, out _, out _), Is.False);
                var sources = new ShaderPropertyUsage(); var programs = sources.GetPrograms(shader);
                Assert.That(programs.Uncertain, Is.Null);
                Assert.That(programs.Passes.Count, Is.EqualTo(1));
                Assert.That(programs.ShaderLabStatesComplete, Is.True, programs.ShaderLabStateIssue);
                Assert.That(sources.MeshInputs.Issue(shader, false, BuildTarget.StandaloneWindows64),
                    usesIndex ? Is.Not.Null : Is.Null);
                if (persistent) objects.Remove(shader);
            }
            finally { AssetDatabase.DeleteAsset(directory); }
        }

        [TestCase(false)] [TestCase(true)]
        public void ImportedShaderIgnoresUntrustedSourceSiblings(bool usesIndex)
        {
            RequireD3DAnalysis(); string directory = ImportedSourceDirectory();
            try
            {
                var shader = ImportedSourceAsset(directory, "Program", ImportedSourceText(usesIndex));
                Assert.That(AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(shader)).OfType<TextAsset>().Count(), Is.EqualTo(1));
                Assert.That(ShaderBuildSource.TryRead(shader, out _, out _), Is.False,
                    "A TextAsset name or matching shader name cannot authorize source rewriting.");
                var sources = new ShaderPropertyUsage();
                Assert.That(sources.GetPrograms(shader).Uncertain, Is.Null);
                Assert.That(sources.MeshInputs.Issue(shader, false, BuildTarget.StandaloneWindows64), usesIndex ? Is.Not.Null : Is.Null);
            }
            finally { AssetDatabase.DeleteAsset(directory); }
        }

        [Test]
        public void ImportedShaderPreservesFixedFunctionReadersWithoutBlockingUnrelatedUniformProofs()
        {
            RequireD3DAnalysis();
            var shader = Track(ShaderUtil.CreateShaderAsset(ImportedSourceText(false,
                "Cull [_Cull] Blend [_Src] [_Dst] Stencil { Ref [_Ref] Comp Always Pass Replace }"), false));
            var material = Track(new Material(shader)); renderer.sharedMaterial = material;
            var sources = new ShaderPropertyUsage(); var programs = sources.GetPrograms(shader);
            Assert.That(programs.ShaderLabStatesComplete, Is.True, programs.ShaderLabStateIssue);
            foreach (string name in new[] { "_Cull", "_Src", "_Dst", "_Ref" })
            {
                Assert.That(programs.ShaderLabStateIdentifiers, Does.Contain(name));
                Assert.That(sources.MayRead(material, renderer, FrameIndex(), name, new HashSet<string>(), BuildTarget.StandaloneWindows64), Is.True);
            }
            Assert.That(sources.MayRead(material, renderer, FrameIndex(), "_Unused", new HashSet<string>(), BuildTarget.StandaloneWindows64), Is.False);
            Assert.That(sources.MayRead(shader, "_CompletelyAbsent", BuildTarget.StandaloneWindows64), Is.False);
        }

        [TestCase(false)] [TestCase(true)]
        public void ImportedShaderPreservesRasterizationEffects(bool grab)
        {
            string source = ImportedSourceText(false);
            source = grab ? source.Replace("SubShader {", "SubShader { GrabPass { }") :
                source.Replace("Pass { Name", "Pass { Conservative True Name");
            var shader = Track(ShaderUtil.CreateShaderAsset(source, false));
            var sources = new ShaderPropertyUsage(); var programs = sources.GetPrograms(shader);
            Assert.That(programs.Uncertain, Is.Null);
            Assert.That(programs.DegenerateRasterizationIssue, Does.Contain(grab ? "GrabPass" : "conservative"));
        }

        [TestCase("Artist's _Label")]
        [TestCase("/* _Label */")]
        [TestCase("_Before // _Label")]
        public void ImportedShaderMetadataStringsAreNotPreprocessorSource(string label)
        {
            string source = ImportedSourceText(false, "Cull [_Cull] Tags { \"Artist\" = \"" + label + "\" }");
            var shader = Track(ShaderUtil.CreateShaderAsset(source, false));
            Assert.That(ShaderUtil.ShaderHasError(shader), Is.False);
            var programs = new ShaderPropertyUsage().GetPrograms(shader);
            Assert.That(programs.ShaderLabStatesComplete, Is.True, programs.ShaderLabStateIssue);
            Assert.That(programs.ShaderLabStateIdentifiers, Does.Contain("_Label"));
            Assert.That(programs.ShaderLabStateIdentifiers, Does.Contain("_Cull"));
            Assert.That(programs.DegenerateRasterizationIssue, Is.Null);
        }

        [TestCase(false)] [TestCase(true)]
        public void ImportedShaderResolvesUsePassAndFallbackWithNativeOwnership(bool fallback)
        {
            RequireD3DAnalysis(); string directory = ImportedSourceDirectory();
            try
            {
                string name = "Hidden/ALTO/ImportedOwner/" + Guid.NewGuid().ToString("N");
                var owner = ImportedSourceAsset(directory, "Owner", "Shader \"" + name + "\" { SubShader {" +
                    ImportedSourcePass(false, "SAFE") + ImportedSourcePass(true, "INDEX") + "} Fallback Off }");
                var rootShader = ImportedSourceAsset(directory, "Root", "Shader \"" + name + "/Root\" { SubShader {" +
                    (fallback ? ImportedSourcePass(false, "MAIN") : "UsePass \"" + name + "/SAFE\"") + "}" +
                    (fallback ? "Fallback \"" + name + "\"" : "Fallback Off") + "}");
                var sources = new ShaderPropertyUsage(); var programs = sources.GetPrograms(rootShader);
                Assert.That(programs.Uncertain, Is.Null);
                Assert.That(programs.NativeShaderDependencies, Does.Contain(owner));
                Assert.That(programs.Passes.Any(p => p.Shader == owner && p.Pass.Name == "INDEX"), Is.EqualTo(fallback));
                Assert.That(sources.MeshInputs.Issue(rootShader, false, BuildTarget.StandaloneWindows64), fallback ? Is.Not.Null : Is.Null);
                if (!fallback)
                {
                    var imported = programs.Passes.Single(p => p.Shader == rootShader).Pass;
                    var original = programs.Passes.Single(p => p.Shader == owner).Pass;
                    Assert.That(sources.ImportedNativePass(imported), Is.SameAs(original));
                }
            }
            finally { AssetDatabase.DeleteAsset(directory); }
        }

        [Test]
        public void ImportedShaderAnalysisCoversNondefaultKeywordVariants()
        {
            RequireD3DAnalysis();
            string text = ImportedSourceText(true).Replace("#pragma target 4.0", "#pragma target 4.0\n#pragma multi_compile __ ALTO_IMPORTED_ENABLED")
                .Replace("p.x += id;", "\n#if defined(ALTO_IMPORTED_ENABLED)\np.x += id;\n#endif\n");
            var shader = Track(ShaderUtil.CreateShaderAsset(text, false));
            var sources = new ShaderPropertyUsage();
            Assert.That(sources.MeshInputs.Issue(shader, false, BuildTarget.StandaloneWindows64), Is.Not.Null);
        }

        [Test]
        public void ImportedShaderPreprocessCacheInvalidatesChangedInclude()
        {
            RequireD3DAnalysis(); string directory = ImportedSourceDirectory();
            try
            {
                string header = directory + "/Program.hlsl";
                File.WriteAllText(header, "#define INDEX_EFFECT 0\n");
                AssetDatabase.ImportAsset(header, ImportAssetOptions.ForceSynchronousImport);
                string text = ImportedSourceText(true).Replace("#pragma target 4.0", "#pragma target 4.0\n#include \"" + header + "\"")
                    .Replace("p.x += id;", "if (INDEX_EFFECT) p.x += id;");
                var shader = ImportedSourceAsset(directory, "Program", text);
                var artifacts = new ShaderAnalysisArtifacts(); var sources = new ShaderPropertyUsage(artifacts);
                Assert.That(sources.MeshInputs.Issue(shader, false, BuildTarget.StandaloneWindows64), Is.Null);
                string first = sources.NativeBuildIdentity(shader, BuildTarget.StandaloneWindows64);
                Assert.That(first, Is.Not.Null);
                var warm = new ShaderPropertyUsage(artifacts);
                Assert.That(warm.NativeBuildIdentity(shader, BuildTarget.StandaloneWindows64), Is.EqualTo(first));
                File.WriteAllText(header, "#define INDEX_EFFECT 1\n");
                AssetDatabase.ImportAsset(header, ImportAssetOptions.ForceSynchronousImport);
                AssetDatabase.ImportAsset(AssetDatabase.GetAssetPath(shader), ImportAssetOptions.ForceSynchronousImport);
                shader = AssetDatabase.LoadAssetAtPath<Shader>(directory + "/Program.altoshaderfixture");
                var changed = new ShaderPropertyUsage(artifacts);
                Assert.That(changed.NativeBuildIdentity(shader, BuildTarget.StandaloneWindows64), Is.Not.EqualTo(first));
                Assert.That(changed.MeshInputs.Issue(shader, false, BuildTarget.StandaloneWindows64), Is.Not.Null);
            }
            finally { AssetDatabase.DeleteAsset(directory); }
        }

        private static string ImportedSourceDirectory()
        {
            string directory = "Assets/__ALTOImportedSource_" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(directory); return directory;
        }

        private static Shader ImportedSourceAsset(string directory, string name, string source)
        {
            string path = directory + "/" + name + ".altoshaderfixture";
            File.WriteAllText(path, source); AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
            Assert.That(shader, Is.Not.Null);
            Assert.That(ShaderUtil.ShaderHasError(shader), Is.False);
            return shader;
        }

        private static string ImportedSourceText(bool index, string state = "") =>
            "Shader \"Hidden/ALTO/ImportedSource/" + Guid.NewGuid().ToString("N") + "\" {" +
            "Properties { _Unused(\"Unused\",Float)=0 _Cull(\"Cull\",Float)=2 _Src(\"Source\",Float)=1 " +
            "_Dst(\"Destination\",Float)=0 _Ref(\"Stencil\",Float)=0 } SubShader {" + state + ImportedSourcePass(index, "MAIN") + "} Fallback Off }";

        private static string ImportedSourcePass(bool index, string name) => "Pass { Name \"" + name + "\"\nHLSLPROGRAM\n" +
            "#pragma target 4.0\n#pragma vertex vert\n#pragma fragment frag\nfloat _Unused;\n" +
            "float4 vert(float4 p:POSITION,uint id:SV_VertexID):SV_POSITION { " + (index ? "p.x += id;" : "") + " return p; }\n" +
            "float4 frag():SV_Target { return 1; }\nENDHLSL\n}";
    }
}
