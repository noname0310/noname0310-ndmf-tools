using System;
using System.Collections.Generic;
using System.IO;
using Noname.AvatarTools.Analysis;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        [Test]
        public void ShaderDependencyCacheIsIndependentOfSetEnumerationAndImportingConsumer()
        {
            RequireD3DAnalysis(); ShaderMeshInputs.CacheNativePrograms = true;
            string suffix = Guid.NewGuid().ToString("N"), directory = "Assets/__ALTOSharedPass_" + suffix;
            Directory.CreateDirectory(directory);
            try
            {
                string ownerName = "Hidden/ALTO/SharedPass/" + suffix;
                string ownerPath = directory + "/Pass.shader", firstPath = directory + "/First.shader", secondPath = directory + "/Second.shader";
                File.WriteAllText(ownerPath, "Shader \"" + ownerName + "\" { Properties { _Gate(\"Gate\",Float)=1 } SubShader { Pass { Name \"FORWARD\" HLSLPROGRAM\n" +
                    "#pragma target 4.5\n#pragma vertex vert\n#pragma fragment frag\nfloat _Gate;\n" +
                    "float4 vert(float4 p:POSITION,uint id:SV_VertexID):SV_POSITION{if(_Gate!=0)p.x+=id;return p;}\n" +
                    "float4 frag():SV_Target{return 1;}\nENDHLSL\n} } Fallback Off }");
                string Parent(string name) => "Shader \"Hidden/ALTO/" + suffix + "/" + name + "\" { Properties { _Gate(\"Gate\",Float)=1 } SubShader { UsePass \"" + ownerName + "/FORWARD\" } Fallback Off }";
                File.WriteAllText(firstPath, Parent("First")); File.WriteAllText(secondPath, Parent("Second"));
                AssetDatabase.ImportAsset(ownerPath, ImportAssetOptions.ForceSynchronousImport);
                AssetDatabase.ImportAsset(firstPath, ImportAssetOptions.ForceSynchronousImport);
                AssetDatabase.ImportAsset(secondPath, ImportAssetOptions.ForceSynchronousImport);
                var firstShader = AssetDatabase.LoadAssetAtPath<Shader>(firstPath);
                var forward = new ShaderPropertyUsage();
                string identity = forward.NativeBuildIdentity(firstShader, BuildTarget.StandaloneWindows64);
                Assert.That(identity, Is.Not.Null);
                var reverse = new ShaderPropertyUsage(); var dependencies = reverse.GetPrograms(firstShader).NativeShaderDependencies;
                Assert.That(dependencies.Count, Is.GreaterThanOrEqualTo(2));
                var copy = new List<Shader>(dependencies); copy.Reverse(); dependencies.Clear();
                foreach (var dependency in copy) dependencies.Add(dependency);
                Assert.That(reverse.NativeBuildIdentity(firstShader, BuildTarget.StandaloneWindows64), Is.EqualTo(identity),
                    "Dependency discovery order cannot change a content identity.");

                var firstMaterial = Track(new Material(firstShader)); renderer.sharedMaterial = firstMaterial;
                var first = new ShaderPropertyUsage().MeshInputs;
                Assert.That(first.Issue(firstMaterial, renderer, FrameIndex(), false, BuildTarget.StandaloneWindows64), Does.Contain("may depend"));
                Assert.That(first.NativeProgramCompileCalls, Is.GreaterThan(0));
                var secondMaterial = Track(new Material(AssetDatabase.LoadAssetAtPath<Shader>(secondPath))); renderer.sharedMaterial = secondMaterial;
                var second = new ShaderPropertyUsage().MeshInputs;
                Assert.That(second.Issue(secondMaterial, renderer, FrameIndex(), false, BuildTarget.StandaloneWindows64), Does.Contain("may depend"));
                Assert.That(second.NativeProgramCacheHits, Is.GreaterThan(0));
                Assert.That(second.NativeProgramCompileCalls, Is.Zero,
                    "After imported ownership and preprocessing are proved equal, the compiler artifact belongs to the effective pass.");
            }
            finally { AssetDatabase.DeleteAsset(directory); }
        }

        [Test]
        public void ShaderCacheEnvironmentTracksOnlyRelevantGlobalKeywords()
        {
            RequireD3DAnalysis();
            string suffix = Guid.NewGuid().ToString("N"), directory = "Assets/__ALTOKeywordIdentity_" + suffix;
            string relevant = "ALTO_RELEVANT_" + suffix, unrelated = "ALTO_UNRELATED_" + suffix;
            Directory.CreateDirectory(directory);
            try
            {
                string path = directory + "/Proof.shader";
                File.WriteAllText(path, "Shader \"Hidden/ALTO/KeywordIdentity/" + suffix + "\" { SubShader { Pass { HLSLPROGRAM\n" +
                    "#pragma vertex vert\n#pragma fragment frag\n#pragma multi_compile _ " + relevant + "\n" +
                    "float4 vert(float4 p:POSITION):SV_POSITION{return p;}\nfloat4 frag():SV_Target{return 1;}\nENDHLSL\n} } Fallback Off }");
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
                string Identity() => new ShaderPropertyUsage().NativeBuildIdentity(shader, BuildTarget.StandaloneWindows64);
                string before = Identity(); Assert.That(before, Is.Not.Null);
                Shader.EnableKeyword(unrelated);
                Assert.That(Identity(), Is.EqualTo(before), "An unrelated global keyword does not affect this program.");
                Shader.DisableKeyword(unrelated);
                Assert.That(Identity(), Is.EqualTo(before), "Registering another shader's keyword must not invalidate this shader.");
                Shader.EnableKeyword(relevant);
                Assert.That(Identity(), Is.Not.EqualTo(before), "Relevant global state remains an explicit dependency.");
                Shader.DisableKeyword(relevant);
                Assert.That(Identity(), Is.EqualTo(before));
            }
            finally { Shader.DisableKeyword(relevant); Shader.DisableKeyword(unrelated); AssetDatabase.DeleteAsset(directory); }
        }

        [Test]
        public void ShaderUnitCoveragePersistsOnlyCompletedProofsAndTheirObservedPremises()
        {
            RequireD3DAnalysis(); ShaderMeshInputs.CacheCompletedUnits = true;
            string directory = "Assets/__ALTOUnitCoverage_" + Guid.NewGuid().ToString("N"); Directory.CreateDirectory(directory);
            try
            {
                string include = directory + "/Gate.cginc", path = directory + "/Proof.shader";
                const string gate = "float InputEffect(float value,float gate) {\n#if defined(ALTO_FORCE_INPUT)\nreturn value;\n#else\nreturn gate==0 ? 0 : value*gate;\n#endif\n}\n";
                string source = File.ReadAllText(AssetDatabase.GetAssetPath(MeshInputShader("PartialProof")))
                    .Replace("Hidden/Noname/ALTO/MeshInputsPartialProof", "Hidden/ALTO/UnitCoverage/" + Guid.NewGuid().ToString("N"))
                    .Replace("#pragma target 4.5", "#pragma target 4.5\n#pragma shader_feature_local _ ALTO_FORCE_INPUT")
                    .Replace("float InputEffect(float value, float gate) { return gate == 0 ? 0 : value * gate; }", "#include \"Gate.cginc\"");
                File.WriteAllText(include, gate); File.WriteAllText(path, source);
                AssetDatabase.ImportAsset(include, ImportAssetOptions.ForceSynchronousImport);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                var material = Track(new Material(AssetDatabase.LoadAssetAtPath<Shader>(path))); renderer.sharedMaterial = material;
                var first = new ShaderPropertyUsage().MeshInputs;
                Assert.That(first.Issue(material, renderer, FrameIndex(), false, BuildTarget.StandaloneWindows64), Does.Contain("may depend"));
                Assert.That(first.UnitCertificateHits, Is.Zero);

                material.SetFloat("_Unrelated", .5f);
                var warm = new ShaderPropertyUsage().MeshInputs;
                Assert.That(warm.Issue(material, renderer, FrameIndex(), false, BuildTarget.StandaloneWindows64), Does.Contain("may depend"));
                Assert.That(warm.UnitCertificateHits, Is.GreaterThan(0));
                Assert.That(warm.PreprocessCalls, Is.LessThan(first.PreprocessCalls), "A new snapshot can reuse completed early units even though the final pass remains live.");

                material.EnableKeyword("ALTO_FORCE_INPUT");
                var keywords = new ShaderPropertyUsage().MeshInputs;
                Assert.That(keywords.Issue(material, renderer, FrameIndex(), false, BuildTarget.StandaloneWindows64), Does.Contain("may depend"));
                Assert.That(keywords.UnitCertificateHits, Is.Zero, "A different reachable keyword universe needs a new proof.");
                material.DisableKeyword("ALTO_FORCE_INPUT");

                var unknown = new ShaderPropertyUsage().MeshInputs;
                Assert.That(unknown.Issue(material, renderer, FrameIndex(), ShaderInputQuery.ForMeshChange(false), BuildTarget.StandaloneWindows64,
                    new HashSet<string> { "_FirstGate" }), Does.Contain("may depend"));
                Assert.That(unknown.UnitCertificateHits, Is.Zero, "An absent premise is not the previous zero value.");
                material.SetFloat("_FirstGate", 1);
                var changed = new ShaderPropertyUsage().MeshInputs;
                Assert.That(changed.Issue(material, renderer, FrameIndex(), false, BuildTarget.StandaloneWindows64), Does.Contain("may depend"));
                Assert.That(changed.UnitCertificateHits, Is.Zero, "A changed branch input must invalidate the early unit.");

                material.SetFloat("_FirstGate", 0); material.SetFloat("_LastGate", 0);
                var complete = new ShaderPropertyUsage().MeshInputs;
                Assert.That(complete.Issue(material, renderer, FrameIndex(), false, BuildTarget.StandaloneWindows64), Is.Null);
                Assert.That(complete.UnitCertificateHits, Is.GreaterThan(0));
                var differentQuery = new ShaderPropertyUsage().MeshInputs;
                Assert.That(differentQuery.Issue(material, renderer, FrameIndex(), ShaderInputQuery.Uniform("_Unrelated"), BuildTarget.StandaloneWindows64), Is.Not.Null);
                Assert.That(differentQuery.UnitCertificateHits, Is.Zero, "Semantic coverage does not prove a named uniform absent.");

                File.WriteAllText(include, gate.Replace("gate==0", "gate!=0"));
                AssetDatabase.ImportAsset(include, ImportAssetOptions.ForceSynchronousImport);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                var edited = new ShaderPropertyUsage().MeshInputs;
                Assert.That(edited.Issue(material, renderer, FrameIndex(), false, BuildTarget.StandaloneWindows64), Does.Contain("may depend"));
                Assert.That(edited.UnitCertificateHits, Is.Zero, "Changed included code invalidates the complete unit universe.");
            }
            finally { AssetDatabase.DeleteAsset(directory); }
        }

        [Test]
        public void NativeProgramCacheReevaluatesNewMaterialValuesWithoutRecompiling()
        {
            RequireD3DAnalysis(); ShaderMeshInputs.CacheNativePrograms = true;
            string directory = "Assets/__ALTONativeProgram_" + Guid.NewGuid().ToString("N"); Directory.CreateDirectory(directory);
            try
            {
                string path = directory + "/Proof.shader";
                string source = "Shader \"Hidden/ALTO/NativeProgram/" + Guid.NewGuid().ToString("N") + "\" { Properties { _Gate(\"Gate\",Float)=0 } SubShader { Pass { HLSLPROGRAM\n" +
                    "#pragma target 4.5\n#pragma vertex vert\n#pragma fragment frag\nfloat _Gate;\n" +
                    "float4 vert(float4 p:POSITION,uint id:SV_VertexID):SV_POSITION {if(_Gate!=0)p.x+=id;return p;}\n" +
                    "float4 frag():SV_Target{return 1;}\nENDHLSL\n} } Fallback Off }";
                File.WriteAllText(path, source); AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                var material = Track(new Material(AssetDatabase.LoadAssetAtPath<Shader>(path))); renderer.sharedMaterial = material;
                var cold = new ShaderPropertyUsage().MeshInputs;
                Assert.That(cold.Issue(material, renderer, FrameIndex(), false, BuildTarget.StandaloneWindows64), Is.Null);
                Assert.That(cold.NativeProgramCompileCalls, Is.GreaterThan(0));
                material.SetFloat("_Gate", 1);
                var changed = new ShaderPropertyUsage().MeshInputs;
                Assert.That(changed.Issue(material, renderer, FrameIndex(), false, BuildTarget.StandaloneWindows64), Does.Contain("may depend"));
                Assert.That(changed.NativeProgramCacheHits, Is.GreaterThan(0));
                Assert.That(changed.NativeProgramCompileCalls, Is.Zero, "New constant assumptions reuse the bytecode, not the old dependency result.");
                File.WriteAllText(path, source.Replace("if(_Gate!=0)", "if(_Gate==0)"));
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                var edited = new ShaderPropertyUsage().MeshInputs;
                Assert.That(edited.Issue(material, renderer, FrameIndex(), false, BuildTarget.StandaloneWindows64), Is.Null);
                Assert.That(edited.NativeProgramCompileCalls, Is.GreaterThan(0), "Changed source cannot reuse an older compiler artifact.");
            }
            finally { AssetDatabase.DeleteAsset(directory); }
        }

        [Test]
        public void ShaderCoverageCacheReusesCompleteProofAndInvalidatesConstantsAndIncludes()
        {
            RequireD3DAnalysis();
            ShaderMeshInputs.CacheCompleteCoverage = true;
            string directory = "Assets/__ALTOCoverage_" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(directory);
            try
            {
                string name = "Hidden/ALTO/Coverage/" + Guid.NewGuid().ToString("N");
                string include = directory + "/Shared.cginc", path = directory + "/Proof.shader";
                const string program = "float _Gate; float4 vert(float4 p:POSITION,uint id:SV_VertexID):SV_POSITION { if(_Gate!=0) return p+id; return p; }\nfloat4 frag():SV_Target{return 1;}\n";
                File.WriteAllText(include, program);
                File.WriteAllText(path, "Shader \"" + name + "\" { Properties { _Gate(\"Gate\",Float)=0 } SubShader { Pass { CGPROGRAM\n" +
                    "#pragma target 4.0\n#pragma vertex vert\n#pragma fragment frag\n#include \"Shared.cginc\"\nENDCG\n} } Fallback Off }");
                AssetDatabase.ImportAsset(include, ImportAssetOptions.ForceSynchronousImport);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                var material = Track(new Material(AssetDatabase.LoadAssetAtPath<Shader>(path)));
                renderer.sharedMaterial = material;
                var first = new ShaderPropertyUsage().MeshInputs;
                Assert.That(first.Issue(material, renderer, FrameIndex(), false, BuildTarget.StandaloneWindows64), Is.Null);
                Assert.That(first.CoverageCacheHits, Is.Zero);
                var warm = new ShaderPropertyUsage().MeshInputs;
                Assert.That(warm.Issue(material, renderer, FrameIndex(), false, BuildTarget.StandaloneWindows64), Is.Null);
                Assert.That(warm.CoverageCacheHits, Is.EqualTo(1)); Assert.That(warm.PreprocessCalls, Is.Zero);
                material.SetFloat("_Gate", 1);
                var changed = new ShaderPropertyUsage().MeshInputs;
                Assert.That(changed.Issue(material, renderer, FrameIndex(), false, BuildTarget.StandaloneWindows64), Is.Not.Null);
                Assert.That(changed.CoverageCacheHits, Is.Zero);
                material.SetFloat("_Gate", 0);
                File.WriteAllText(include, program.Replace("if(_Gate!=0)", "if(_Gate==0)"));
                AssetDatabase.ImportAsset(include, ImportAssetOptions.ForceSynchronousImport);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                var edited = new ShaderPropertyUsage().MeshInputs;
                Assert.That(edited.Issue(material, renderer, FrameIndex(), false, BuildTarget.StandaloneWindows64), Is.Not.Null);
                Assert.That(edited.CoverageCacheHits, Is.Zero);
            }
            finally { AssetDatabase.DeleteAsset(directory); }
        }
    }
}
