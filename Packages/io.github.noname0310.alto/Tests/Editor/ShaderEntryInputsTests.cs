using System;
using System.IO;
using System.Linq;
using Noname.AvatarTools.Analysis;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        [TestCase("Numeric", true)] [TestCase("Struct", true)] [TestCase("Nested", true)] [TestCase("Array", true)]
        [TestCase("DirectIndex", false)] [TestCase("NestedIndex", false)] [TestCase("Alias", false)]
        [TestCase("MultipleDeclarators", false)] [TestCase("UnknownType", false)]
        public void NativeEntrySignatureAbsenceResolvesTheCompleteInputInterface(string shape, bool absent)
        {
            string input = shape == "Numeric" ? "float4 p:POSITION" : shape == "DirectIndex" ? "uint p:SV_VertexID" : "Input p";
            string types = shape == "Struct" ? "struct Input {float4 p:POSITION;};" :
                shape == "Nested" ? "struct Inner {float4 p:POSITION;}; struct Input {Inner p;};" :
                shape == "Array" ? "struct Input {float4 p[2]:TEXCOORD0;};" :
                shape == "NestedIndex" ? "struct Inner {uint id:SV_VertexID;};struct Input {Inner p;};" :
                shape == "Alias" ? "struct Actual{uint id:SV_VertexID;};typedef Actual Input;" :
                shape == "MultipleDeclarators" ? "struct Input{float4 a:POSITION,b:TEXCOORD;};" :
                shape == "UnknownType" ? "struct Input{ExternalType p;};" : "";
            string source = "struct Unused { uint id:SV_VertexID; };" + types + "float4 main(" + input + "):SV_POSITION{return 0;}";
            Assert.That(ShaderEntryInputs.Absent(source, "main", ShaderInputQuery.MeshIndices), Is.EqualTo(absent));
            Assert.That(ShaderEntryInputs.Absent(source, "main", ShaderInputQuery.Uniform("_Value")), Is.False,
                "An entry-interface proof never answers a uniform-use query.");
            Assert.That(ShaderEntryInputs.Absent(source, "missing", ShaderInputQuery.MeshIndices), Is.False);
        }

        [TestCase(false)] [TestCase(true)]
        public void NativeEntrySignatureAbsencePreservesKeywordIntroducedSystemInputs(bool primitive)
        {
            RequireD3DAnalysis();
            string directory = "Assets/__ALTOEntrySignature_" + Guid.NewGuid().ToString("N"); Directory.CreateDirectory(directory);
            try
            {
                string path = directory + "/Probe.shader";
                File.WriteAllText(path, "Shader \"Hidden/ALTO/EntrySignature/" + Guid.NewGuid().ToString("N") + "\" { SubShader { Pass {\nHLSLPROGRAM\n" +
                    "#pragma target 4.0\n#pragma vertex vert\n#pragma fragment frag\n#pragma multi_compile __ ALTO_SIGNATURE_INPUT\n" +
                    "struct VertexInput {float4 p:POSITION;uint id:SV_VertexID;};\nfloat4 vert(VertexInput input):SV_POSITION{return input.p;}\n" +
                    "#if defined(ALTO_SIGNATURE_INPUT)\nfloat4 frag(float4 p:SV_POSITION," + (primitive ? "uint id:SV_PrimitiveID" : "float2 uv:TEXCOORD0") + "):SV_Target{return " + (primitive ? "id" : "uv.x") + ";}\n" +
                    "#else\nfloat4 frag(float4 p:SV_POSITION):SV_Target{return 1;}\n#endif\nENDHLSL\n} } Fallback Off }");
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                var shader = AssetDatabase.LoadAssetAtPath<Shader>(path); Assert.That(ShaderUtil.ShaderHasError(shader), Is.False);
                var inputs = new ShaderPropertyUsage().MeshInputs;
                string issue = inputs.Issue(shader, false, BuildTarget.StandaloneWindows64);
                Assert.That(issue == null, Is.EqualTo(!primitive), issue);
                Assert.That(inputs.EntrySignatureProofHits, Is.GreaterThan(0));
                Assert.That(inputs.CompileCalls, Is.LessThan(inputs.PreprocessCalls), "Proved unbound interfaces consume no native compile slot.");
            }
            finally { AssetDatabase.DeleteAsset(directory); }
        }

        [TestCase("Array")] [TestCase("DirectArray")] [TestCase("Matrix")]
        public void NativeEntrySignatureAbsenceRetainsIndexedSemanticSpans(string shape)
        {
            RequireD3DAnalysis();
            string fields = shape == "Matrix" ? "float2x2 uv:TEXCOORD0;" : "float2 uv[2]:TEXCOORD0;";
            string source = shape == "DirectArray" ? "float4 main(float2 uv[2]:TEXCOORD0):SV_POSITION{return float4(uv[1],0,1);}" :
                "struct Input{" + fields + "};float4 main(Input v):SV_POSITION{return float4(v.uv[1],0,1);}";
            var query = new ShaderInputQuery(new ShaderInputQuery.Semantic("TEXCOORD", 1));
            Assert.That(ShaderEntryInputs.Absent(source, "main", query), Is.False);
            Assert.That(ShaderMeshInputs.InspectPreprocessed(true, source, query), Is.Not.Null,
                "The earlier lexical check must not bypass the interface/native proof.");
            Assert.That(D3DShaderInputs.Compile(source, "main", ShaderType.Vertex, out var inputs, out string issue), Is.True, issue);
            Assert.That(query.MayReadCompiled(inputs), Is.True, "Native reflection must witness the later occupied semantic slot.");
        }
    }
}
