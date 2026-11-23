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
        public void SharedNativeShaderProgramsPreserveLogicalBudgetsAndRecheckMaterials()
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
            var exhausted = new ShaderPropertyUsage(artifacts).MeshInputs;
            typeof(ShaderMeshInputs).GetField("<PreprocessCalls>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(exhausted, ShaderMeshInputs.MaximumCallsPerSnapshot);
            Assert.That(exhausted.Issue(material, renderer, FrameIndex(), false, BuildTarget.StandaloneWindows64), Does.Contain("unverified"));
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
