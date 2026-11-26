using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.Build;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        [Test]
        public void ShaderTextKeysRetainOrdinalContentEqualityAcrossDistinctStrings()
        {
            string text = new string('x', 300000) + "\ud800";
            var values = new Dictionary<ShaderTextKey, int> { [text] = 42, [null] = 17, [""] = 23 };
            Assert.That(values[new string(text.ToCharArray())], Is.EqualTo(42));
            Assert.That(values.ContainsKey(new string('x', 300000) + "\ud801"), Is.False);
            Assert.That(values[default(ShaderTextKey)], Is.EqualTo(17));
            Assert.That(values[""], Is.EqualTo(23));
            Assert.That(new ShaderTextKey("A"), Is.Not.EqualTo(new ShaderTextKey("a")));
            var registered = new ShaderTextKey(text);
            Assert.That(new ShaderTextKey(new string(text.ToCharArray())).Text, Is.SameAs(registered.Text));
            for (int i = 0; i < 5000; i++) _ = new ShaderTextKey("source registration eviction " + i);
            Assert.That(values[new string(text.ToCharArray())], Is.EqualTo(42), "Registration eviction cannot change content equality.");
        }

        [Test]
        public void ShaderInspectionArtifactsAnswerNewQueriesWithoutRescanning()
        {
            var artifacts = new ShaderAnalysisArtifacts();
            string source = "#line 3 \"SV_VertexID\"\nfloat _Value; uint id : CUSTOM3;\n#include \"unresolved\"\nSV_VertexID;";
            var first = artifacts.Inspection(source);
            Assert.That(first.Inspect(true, new ShaderInputQuery(new ShaderInputQuery.Semantic("CUSTOM", 3))), Is.EqualTo(ShaderProgramInspection.InputIssue));
            Assert.That(first.Inspect(true, ShaderInputQuery.Uniform("_Value")), Is.EqualTo(ShaderProgramInspection.InputIssue));
            Assert.That(first.Inspect(true, ShaderInputQuery.Uniform("_value")), Does.Contain("unsupported directive"));
            Assert.That(first.Inspect(true, ShaderInputQuery.VertexId), Does.Contain("unsupported directive"));
            Assert.That(first.Inspect(false, ShaderInputQuery.Uniform("_Value")), Does.Contain("no verified"));
            Assert.That(artifacts.Inspection(new string(source.ToCharArray())), Is.SameAs(first));
            Assert.That(artifacts.InspectionScans, Is.EqualTo(1));
            Assert.That(artifacts.Inspection(source + "\0").Inspect(true, ShaderInputQuery.Uniform("_Value")), Does.Contain("malformed"));
        }

        [Test]
        public void ShaderLexicalSummariesReuseTextWithoutSharingMutableProofs()
        {
            string source = "#define SUFFIX(x) x##Only\n#include \"first.hlsl\"\n#include OPTIONAL\n" +
                "#include_with_pragmas <second.hlsl>\nfloat _Visible; // " + Guid.NewGuid();
            var artifacts = new ShaderAnalysisArtifacts();
            var first = artifacts.SourceFacts(source);
            var proof = new ShaderPropertyUsage.Proof(); first.Apply(proof);
            Assert.That(proof.Tokens, Does.Contain("_Visible"));
            Assert.That(proof.TokenPasting, Is.True);
            Assert.That(first.Includes, Is.EqualTo(new string[] { "first.hlsl", null, "second.hlsl" }));
            proof.Tokens.Clear(); proof.PastePatterns.Clear();
            var other = new ShaderPropertyUsage.Proof();
            artifacts.SourceFacts(new string(source.ToCharArray())).Apply(other);
            Assert.That(artifacts.SourceScans, Is.EqualTo(1));
            Assert.That(other.Tokens, Does.Contain("_Visible")); Assert.That(other.PastePatterns.Count, Is.EqualTo(1));
            var fresh = new ShaderAnalysisArtifacts().SourceFacts(source); var restored = new ShaderPropertyUsage.Proof(); fresh.Apply(restored);
            Assert.That(restored.Tokens, Is.EquivalentTo(other.Tokens));
            Assert.That(restored.PastePatterns.Keys, Is.EquivalentTo(other.PastePatterns.Keys));
            Assert.That(fresh.Includes, Is.EqualTo(first.Includes));
            Assert.That(new ShaderAnalysisArtifacts().SourceFacts(source + "\n__TIME__\n").TrackedIncludes, Is.False);
            Assert.That(new ShaderAnalysisArtifacts().SourceFacts(source + "\n#include_next \"other\"\n").TrackedIncludes, Is.False);
        }

        [Test]
        public void ShaderLabSummariesKeepStatesAndSelectedPassesAcrossCacheReload()
        {
            string source = "Shader \"ALTO/" + Guid.NewGuid() + "\" { Properties { _Value(\"Value\",Float)=0 } SubShader {\n" +
                "Pass { Name \"OFF\" Conservative False ZWrite [_Depth] HLSLPROGRAM\nfloat _CodeOnly;\nENDHLSL\n}\n" +
                "Pass { Name \"ON\" Conservative True } } }";
            var artifacts = new ShaderAnalysisArtifacts(); var first = artifacts.LabFacts(source);
            Assert.That(first.Issue, Is.Null);
            Assert.That(first.Identifiers, Does.Contain("_Value")); Assert.That(first.Identifiers, Does.Not.Contain("_CodeOnly"));
            Assert.That(first.StateIdentifiers, Does.Not.Contain("_Value")); Assert.That(first.StateIdentifiers, Does.Contain("_Depth"));
            Assert.That(artifacts.LabFacts(new string(source.ToCharArray())), Is.SameAs(first)); Assert.That(artifacts.LabScans, Is.EqualTo(1));
            var loaded = new ShaderAnalysisArtifacts().LabFacts(source);
            Assert.That(loaded.Identifiers, Is.EquivalentTo(first.Identifiers)); Assert.That(loaded.StateIdentifiers, Is.EquivalentTo(first.StateIdentifiers));
            foreach (var selection in new[] { null, new HashSet<string>(new[] { "OFF" }), new HashSet<string>(new[] { "ON" }), new HashSet<string>(new[] { "MISSING" }) })
                Assert.That(loaded.Rasterization.Issue(selection), Is.EqualTo(ShaderRasterizationState.Parse(source).Issue(selection)));
            Assert.That(loaded.Rasterization.Issue(new HashSet<string>(new[] { "OFF" })), Is.Null);
            Assert.That(loaded.Rasterization.Issue(new HashSet<string>(new[] { "ON" })), Is.Not.Null);
            var changed = artifacts.LabFacts(source.Replace("Conservative False", "Conservative True"));
            Assert.That(changed.Rasterization.Issue(new HashSet<string>(new[] { "OFF" })), Is.Not.Null);
        }

        [Test]
        public void ShaderArtifactsSurviveInvalidationWithoutSharingMutableSnapshots()
        {
            var analysis = new OptimizationAnalysis(); var first = analysis.Shaders;
            var scan = first.Artifacts.Inspection("float plain;");
            analysis.Invalidate(AnalysisDomain.All);
            Assert.That(analysis.Shaders, Is.Not.SameAs(first));
            Assert.That(analysis.Shaders.Artifacts, Is.SameAs(first.Artifacts));
            Assert.That(analysis.Shaders.Artifacts.Inspection("float plain;"), Is.SameAs(scan));
            Assert.That(new OptimizationAnalysis().Shaders.Artifacts, Is.Not.SameAs(first.Artifacts));
        }

        [Test]
        public void SharedNativeShaderProgramsPreserveRequestCountsAndRecheckMaterials()
        {
            RequireD3DAnalysis();
            var artifacts = new ShaderAnalysisArtifacts();
            var material = Track(new Material(MeshInputShader("Constant"))); renderer.sharedMaterial = material;
            var first = new ShaderPropertyUsage(artifacts).MeshInputs;
            Assert.That(first.Issue(material, renderer, FrameIndex(), false, BuildTarget.StandaloneWindows64), Is.Null);
            var next = new ShaderPropertyUsage(artifacts).MeshInputs;
            Assert.That(next.Issue(material, renderer, FrameIndex(), false, BuildTarget.StandaloneWindows64), Is.Null);
            Assert.That(artifacts.PreprocessHits, Is.GreaterThan(0));
            Assert.That(next.NativePreprocessMilliseconds, Is.EqualTo(0));
            Assert.That(next.PreprocessCalls, Is.EqualTo(first.PreprocessCalls));
            Assert.That(next.CompileCalls, Is.EqualTo(first.CompileCalls));
            material.SetFloat("_IndexEffect", 1);
            var changed = new ShaderPropertyUsage(artifacts).MeshInputs;
            Assert.That(changed.Issue(material, renderer, FrameIndex(), false, BuildTarget.StandaloneWindows64), Does.Contain("may depend"));
            material.SetFloat("_IndexEffect", 0);
            var later = new ShaderPropertyUsage(artifacts).MeshInputs;
            typeof(ShaderMeshInputs).GetField("<PreprocessCalls>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(later, 2048);
            Assert.That(later.Issue(material, renderer, FrameIndex(), false, BuildTarget.StandaloneWindows64), Is.Null,
                "An earlier workload must not prevent reuse of the same complete proof.");
        }

        [TestCase(false)] [TestCase(true)]
        public void SharedNativeVertexProgramsServePositionAndNonFiniteProofsOnce(bool nonFiniteFirst)
        {
            RequireD3DAnalysis();
            string directory = NonFiniteShader("ShadowRecovery", out var material);
            try
            {
                NonFiniteFixture(); renderer.sharedMaterial = material;
                var inputs = new ShaderPropertyUsage().MeshInputs; var index = FrameIndex();
                var first = inputs.PositionInputs(material, renderer, index, BuildTarget.StandaloneWindows64, nonFiniteFirst);
                Assert.That(first.Issue, Is.Null);
                int calls = inputs.CompileCalls, native = inputs.NativeProgramCompileCalls;
                Assert.That(calls, Is.EqualTo(inputs.PreprocessCalls), "Each covered pass, tier and keyword variant reserves one native compile.");
                Assert.That(calls, Is.GreaterThan(64), "Exercise reuse across a substantial native variant set.");
                Assert.That(native, Is.EqualTo(calls), "The fresh shader's actual native variants compile exactly once.");
                var second = inputs.PositionInputs(material, renderer, index, BuildTarget.StandaloneWindows64, !nonFiniteFirst);
                Assert.That(second.Issue, Is.Null);
                Assert.That(inputs.CompileCalls, Is.EqualTo(calls));
                Assert.That(inputs.NativeProgramCompileCalls, Is.EqualTo(native),
                    "Reversing proof order reuses complete native programs, including their public binding metadata.");
                var nonFinite = nonFiniteFirst ? first : second;
                Assert.That(nonFinite.NonFinitePositionIsHidden, Is.False,
                    "The recovery pass must still prevent deletion; sharing cannot reuse another pass's result.");
                Assert.That(first.Reads.Keys, Is.EquivalentTo(second.Reads.Keys));
                foreach (var read in first.Reads) Assert.That(second.Reads[read.Key], Is.EqualTo(read.Value));
                TestContext.WriteLine("Covered variants: " + inputs.PreprocessCalls + "; logical compiles: " + calls + "; actual native compiles: " + native);
            }
            finally { AssetDatabase.DeleteAsset(directory); }
        }

        [Test]
        public void SharedNativeSemanticProofsCoverAllTiersWithoutDuplicateDiscoveryCompiles()
        {
            RequireD3DAnalysis();
            var shader = MeshInputShader("Constant");
            var pass = ShaderUtil.GetShaderData(shader).GetSubshader(0).GetPass(0);
            var inputs = new ShaderPropertyUsage().MeshInputs;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var preprocess = typeof(ShaderMeshInputs).GetMethod("Preprocess", flags);
            const int initial = 128;
            typeof(ShaderMeshInputs).GetProperty("CompileCalls", flags).SetValue(inputs, initial);
            var constants = new Dictionary<string, float> { ["_IndexEffect"] = 0 };
            foreach (var tier in ShaderMeshInputs.Tiers)
            {
                object[] args = { pass, UnityEditor.Rendering.ShaderType.Vertex, Array.Empty<string>(),
                    UnityEditor.Rendering.ShaderCompilerPlatform.D3D, BuildTarget.StandaloneWindows64, tier,
                    ShaderInputQuery.VertexId, constants, null, null };
                Assert.That(preprocess.Invoke(inputs, args), Is.Null,
                    "Earlier shaders must not prevent complete coverage; compile one actual program per tier.");
            }
            Assert.That(inputs.CompileCalls, Is.EqualTo(initial + ShaderMeshInputs.Tiers.Length));
            Assert.That(inputs.NativeProgramCompileCalls, Is.EqualTo(ShaderMeshInputs.Tiers.Length));
            constants["_IndexEffect"] = 1;
            object[] changed = { pass, UnityEditor.Rendering.ShaderType.Vertex, Array.Empty<string>(),
                UnityEditor.Rendering.ShaderCompilerPlatform.D3D, BuildTarget.StandaloneWindows64, ShaderMeshInputs.Tiers[0],
                ShaderInputQuery.VertexId, constants, null, null };
            Assert.That(preprocess.Invoke(inputs, changed), Is.EqualTo(ShaderProgramInspection.InputIssue),
                "Reusing the same native program must re-evaluate a changed scalar assumption after earlier shader requests.");
            Assert.That(inputs.CompileCalls, Is.EqualTo(initial + ShaderMeshInputs.Tiers.Length));
            Assert.That(inputs.NativeProgramCompileCalls, Is.EqualTo(ShaderMeshInputs.Tiers.Length));
        }

        [Test]
        public void CompleteNativeShaderWorkloadsReuseDurableFactsBeyondTheFormerGlobalLimit()
        {
            RequireD3DAnalysis();
            string directory = "Assets/__ALTOCompleteShaderWorkload_" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(directory);
            try
            {
                const int shaderCount = 3, passCount = 16;
                var materials = new Material[shaderCount];
                for (int shaderIndex = 0; shaderIndex < shaderCount; shaderIndex++)
                {
                    string path = directory + "/Probe" + shaderIndex + ".shader";
                    var code = new System.Text.StringBuilder("Shader \"Hidden/ALTO/CompleteWorkload/" +
                        Path.GetFileName(directory) + "/" + shaderIndex + "\" {\n" +
                        "Properties { _IndexEffect (\"Gate\", Float) = 0 }\nSubShader {\n");
                    for (int pass = 0; pass < passCount; pass++)
                        code.Append("Pass { Name \"PROBE" + pass + "\"\nHLSLPROGRAM\n" +
                            "#pragma target 4.5\n#pragma vertex vert\n#pragma fragment frag\n" +
                            "float _IndexEffect;\n" +
                            "float4 vert(float4 p:POSITION,uint id:SV_VertexID):SV_POSITION {\n" +
                            "if(_IndexEffect!=0) p.x+=(id%2)*0.01; return p; }\n" +
                            "float4 frag():SV_Target { return 1; }\nENDHLSL\n}\n");
                    code.Append("} }\n");
                    File.WriteAllText(path, code.ToString());
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                    var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
                    Assert.That(shader, Is.Not.Null);
                    Assert.That(ShaderUtil.ShaderHasError(shader), Is.False);
                    materials[shaderIndex] = Track(new Material(shader));
                }

                var cold = new ShaderPropertyUsage().MeshInputs;
                var timer = System.Diagnostics.Stopwatch.StartNew();
                foreach (var material in materials)
                {
                    renderer.sharedMaterial = material;
                    Assert.That(cold.Issue(material, renderer, FrameIndex(), false, BuildTarget.StandaloneWindows64), Is.Null);
                }
                long coldMilliseconds = timer.ElapsedMilliseconds;
                Assert.That(cold.NativeProgramCompileCalls, Is.EqualTo(shaderCount * passCount * ShaderMeshInputs.Tiers.Length));
                Assert.That(cold.NativeProgramCompileCalls, Is.GreaterThan(128),
                    "Exercise actual native coverage beyond the former snapshot-wide cutoff.");

                long hits = ShaderAnalysisCache.Shared.Hits;
                var warm = new ShaderPropertyUsage().MeshInputs;
                timer.Restart();
                // A different traversal order must produce the same complete proof.
                for (int i = materials.Length - 1; i >= 0; i--)
                {
                    renderer.sharedMaterial = materials[i];
                    Assert.That(warm.Issue(materials[i], renderer, FrameIndex(), false, BuildTarget.StandaloneWindows64), Is.Null);
                }
                long warmMilliseconds = timer.ElapsedMilliseconds;
                Assert.That(warm.CompileCalls, Is.EqualTo(cold.CompileCalls));
                Assert.That(warm.NativeProgramCompileCalls, Is.Zero,
                    "A new avatar analysis must reuse durable native dependency facts, not recompile every shader.");
                Assert.That(ShaderAnalysisCache.Shared.Hits - hits, Is.GreaterThanOrEqualTo(cold.NativeProgramCompileCalls));

                materials[0].SetFloat("_IndexEffect", 1);
                renderer.sharedMaterial = materials[0];
                Assert.That(warm.Issue(materials[0], renderer, FrameIndex(), false, BuildTarget.StandaloneWindows64),
                    Is.EqualTo(ShaderProgramInspection.InputIssue), "A cache hit cannot inherit a different material's constant premise.");
                TestContext.WriteLine("Cold: " + coldMilliseconds + " ms, " + cold.NativeProgramCompileCalls +
                    " native compilations; warm: " + warmMilliseconds + " ms, no native compilation before the changed-input query.");
            }
            finally { AssetDatabase.DeleteAsset(directory); }
        }

        [Test]
        public void NativeShaderIdentityTracksRawIncludesAndPreviouslyMissingFiles()
        {
            string directory = "Assets/__ALTOShaderArtifactTest_" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(directory);
            string path = directory + "/Probe.shader";
            try
            {
                File.WriteAllText(directory + "/body.hlsl", "float Ordinary() { return 0; }\n");
                File.WriteAllText(path, "Shader \"Hidden/ALTO/ArtifactProbe\" { SubShader { Pass {\nHLSLPROGRAM\n" +
                    "#pragma vertex vert\n#pragma fragment frag\n#include \"body.hlsl\"\n#if 0\n#include \"optional.hlsl\"\n#endif\n" +
                    "float4 vert(float4 v:POSITION):SV_POSITION{return v+Ordinary();}\nfloat4 frag():SV_Target{return 1;}\nENDHLSL\n} } }");
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
                string Identity()
                {
                    var sources = new ShaderPropertyUsage(); var pass = sources.GetPrograms(shader).Passes[0];
                    return sources.NativePreprocessIdentity(shader, pass.Shader, pass.Pass, pass.Identifier, "test compiler");
                }
                string original = Identity(); Assert.That(original, Is.Not.Null); Assert.That(Identity(), Is.EqualTo(original));
                // No AssetDatabase refresh: imported dependency hashes may still
                // be unchanged while a native include reader sees the new bytes.
                File.WriteAllText(directory + "/body.hlsl", "float Ordinary() { return 1; }\n");
                string edited = Identity(); Assert.That(edited, Is.Not.Null.And.Not.EqualTo(original));
                File.WriteAllText(directory + "/optional.hlsl", "float Later;\n");
                Assert.That(Identity(), Is.Not.Null.And.Not.EqualTo(edited));
                File.WriteAllText(directory + "/body.hlsl", "float Ordinary() { return 1; }\n#define WHEN __TIME__\n");
                Assert.That(Identity(), Is.Null, "Time-dependent preprocessing must not be memoized.");
            }
            finally { AssetDatabase.DeleteAsset(directory); }
        }
    }
}
