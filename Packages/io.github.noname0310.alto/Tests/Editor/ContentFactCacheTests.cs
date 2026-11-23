using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Noname.AvatarTools.Analysis;
using NUnit.Framework;
using UnityEditor.Rendering;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed class ContentFactCacheTests
    {
        private string directory;
        [SetUp] public void SetUp()
        { directory = Path.Combine(Path.GetTempPath(), "ALTO-Facts-" + Guid.NewGuid().ToString("N")); ContentFactCache.Enabled = true; }
        [TearDown] public void TearDown()
        {
            ContentFactCache.Enabled = true;
            if (Directory.Exists(directory)) { foreach (string file in Directory.GetFiles(directory)) File.Delete(file); Directory.Delete(directory); }
        }

        [Test] public void FactCacheUsesExactContentsAndUnambiguousKeys()
        {
            var cache = new ContentFactCache(directory, "v1");
            Assert.That(ContentFactCache.TextDigest(new string(new[] { 'a', 'b' })), Is.EqualTo(ContentFactCache.TextDigest("ab")));
            Assert.That(ContentFactCache.TextDigest("\ud800"), Is.Not.EqualTo(ContentFactCache.TextDigest("\ud801")));
            Assert.That(cache.Key("one", "a|b", "c"), Is.Not.EqualTo(cache.Key("one", "a", "b|c")));
            Assert.That(cache.Key("one", new string[] { null }), Is.Not.EqualTo(cache.Key("one", "")));
            Assert.That(cache.Key("one", "a"), Is.Not.EqualTo(cache.Key("two", "a")));
            Assert.That(cache.Key("one", "a"), Is.Not.EqualTo(new ContentFactCache(directory, "v2").Key("one", "a")));
        }

        [Test] public void NativeContentDigestMatchesManagedSha256IncludingRawUtf16()
        {
            foreach (int size in new[] { 0, 17, 4096, 65537, 1048576 })
            {
                var bytes = new byte[size]; new Random(81).NextBytes(bytes);
                Assert.That(ContentDigest.Bytes(bytes), Is.EqualTo(ContentDigest.Bytes(bytes, managed: true)));
            }
            string text = new string('x', 32768) + "\ud800\ud801\udc00\uffff";
            var raw = new byte[text.Length * 2];
            for (int i = 0; i < text.Length; i++) { raw[i * 2] = (byte)text[i]; raw[i * 2 + 1] = (byte)(text[i] >> 8); }
            Assert.That(ContentFactCache.TextDigest(text), Is.EqualTo(BitConverter.ToString(ContentDigest.Bytes(raw, managed: true)).Replace("-", "").ToLowerInvariant()));
        }

        [Test] public void FactCacheSurvivesReloadAndRejectsCorruptStorage()
        {
            var cache = new ContentFactCache(directory, "v1"); string key = cache.Key("test", "source");
            cache.Store("test", key, writer => writer.Write(42));
            Assert.That(new ContentFactCache(directory, "v1").TryRead("test", key, r => r.ReadInt32(), out int value), Is.True);
            Assert.That(value, Is.EqualTo(42));
            string path = Directory.GetFiles(directory).Single(); byte[] bytes = File.ReadAllBytes(path); bytes[bytes.Length - 1] ^= 1;
            File.WriteAllBytes(path, bytes);
            Assert.That(new ContentFactCache(directory, "v1").TryRead("test", key, r => r.ReadInt32(), out _), Is.False);
            ContentFactCache.Enabled = false;
            Assert.That(cache.TryRead("test", key, r => r.ReadInt32(), out _), Is.False);
        }

        [Test] public void CachedDeclarationsRemainIndependentOfMaterialValuesAndCallerMutation()
        {
            string source = "float _Gate; float _Other; float4 vert() : SV_Position {return _Gate + _Other;} // " + Guid.NewGuid();
            var first = ShaderConstantInputs.PrepareCached(source); var original = ShaderConstantInputs.Prepare(source);
            var values = new Dictionary<string, float> { ["_Gate"] = 0 };
            Assert.That(first.Apply(values, out _), Is.EqualTo(original.Apply(values, out _)));
            first.Declarations.Clear();
            var second = ShaderConstantInputs.PrepareCached(new string(source.ToCharArray())); values["_Gate"] = 1;
            Assert.That(second.Apply(values, out _), Is.EqualTo(original.Apply(values, out _)));
        }

        [Test] public void CachedPreprocessingRevalidatesIncludeContentsAndResolution()
        {
            string id = "/" + Guid.NewGuid().ToString("N") + "/main.hlsl";
            var source = new ShaderPreprocessorGraph.Source(id, "#include \"input.hlsl\"\n");
            string header = "float ordinary;";
            bool second = false;
            ShaderPreprocessorGraph.Source[] Resolve(string origin, string include) => second
                ? new[] { new ShaderPreprocessorGraph.Source("/input.hlsl", header), new ShaderPreprocessorGraph.Source("/other/input.hlsl", "uint id : SV_VertexID;") }
                : new[] { new ShaderPreprocessorGraph.Source("/input.hlsl", header) };
            ShaderPreprocessorGraph Analyze() => ShaderPreprocessorGraph.AnalyzeCached(source, Resolve,
                ShaderInputQuery.VertexId, Array.Empty<string>(), null, null, null);
            Assert.That(Analyze().InputAbsent, Is.True); Assert.That(Analyze().InputAbsent, Is.True);
            header = "uint id : SV_VertexID;";
            Assert.That(Analyze().InputAbsent, Is.False);
            header = "float ordinary;"; Assert.That(Analyze().InputAbsent, Is.True);
            second = true;
            var expected = ShaderPreprocessorGraph.Analyze(source, Resolve, ShaderInputQuery.VertexId, Array.Empty<string>());
            var actual = Analyze(); Assert.That(actual.InputAbsent, Is.EqualTo(expected.InputAbsent));
            Assert.That(actual.Issue, Is.EqualTo(expected.Issue));
        }

        [Test] public void CompilerTraceReplaysRequestsAndCannotBypassExhaustedBudgets()
        {
            var trace = new ShaderCompileTrace(); int calls = 0;
            (D3DShaderInputs.Input[], string) Compile(string s, string e, ShaderType t)
            { calls++; return (new[] { new D3DShaderInputs.Input("ALTOQUERY", 0) }, null); }
            trace.Capture("source", "main", ShaderType.Vertex, Compile);
            using var data = new MemoryStream(); using var writer = new BinaryWriter(data, System.Text.Encoding.UTF8, true);
            trace.Write(writer); writer.Flush(); data.Position = 0;
            var loaded = ShaderCompileTrace.Read(new BinaryReader(data));
            Assert.That(loaded.Replay(Compile), Is.True); Assert.That(calls, Is.EqualTo(2));
            Assert.That(loaded.Replay((s, e, t) => (null, "budget exhausted")), Is.False);
            Assert.That(loaded.Replay((s, e, t) => (new[] { new D3DShaderInputs.Input("ALTOQUERY", 1) }, null)), Is.False);
        }

        [Test] public void CachedInputSlicesRemainIndependentAndReplayCurrentProbeResults()
        {
            if (D3DShaderInputs.CompilerFingerprint == null) Assert.Ignore("A stable native compiler identity is required for persistent input slices.");
            string id = "/" + Guid.NewGuid().ToString("N") + "/pass.hlsl";
            string source = "#line 1 \"" + id + "\"\nstruct V { float4 p:POSITION; uint value:SV_VertexID; };\n" +
                "float Consume(uint value, float gain) { return gain == 0 ? 0 : value * gain; }\n" +
                "float4 vert(V v):SV_POSITION { return v.p+Consume(v.value,0); }";
            bool budget = true, live = false; int probes = 0;
            (D3DShaderInputs.Input[], string) Compile(string s, string e, ShaderType t)
            { probes++; return budget ? (new[] { new D3DShaderInputs.Input("ALTOQUERY", live ? (byte)1 : (byte)0) }, null) : (null, "budget exhausted"); }
            ShaderInputUseSlice Run() => ShaderSliceFacts.Get(new ShaderInputVariantProof.Sample
                { SourceId = id, Original = source, Specialized = source }, ShaderInputQuery.VertexId, "vert", Compile);
            var first = Run(); Assert.That(first.Complete, Is.True, first.Issue); Assert.That(probes, Is.EqualTo(1));
            first.Names.Clear();
            var second = Run(); Assert.That(second.Complete, Is.True, second.Issue);
            Assert.That(second.Names, Does.Contain("Consume")); Assert.That(probes, Is.EqualTo(2));
            budget = false; Assert.That(Run().Complete, Is.False);
            budget = true; live = true; Assert.That(Run().Complete, Is.False);
            source = source.Replace("Consume(v.value,0)", "Consume(v.value,1)");
            Assert.That(Run().Complete, Is.False);
        }
    }
}
