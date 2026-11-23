using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Noname.AvatarTools.Analysis;
using NUnit.Framework;
using UnityEditor.Rendering;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed class ShaderAnalysisCacheTests
    {
        private string directory;
        [SetUp] public void SetUp() => directory = Path.Combine(Path.GetTempPath(), "ALTO-ShaderCache-" + Guid.NewGuid().ToString("N"));
        [TearDown] public void TearDown()
        {
            if (Directory.Exists(directory))
            { foreach (string file in Directory.GetFiles(directory)) File.Delete(file); Directory.Delete(directory); }
            else if (File.Exists(directory)) File.Delete(directory);
        }
        private ShaderAnalysisCache Cache(string version = "analyzer-v1") => new(directory, version);
        private static D3DShaderInputs.Input[] Facts() => new[] {
            new D3DShaderInputs.Input("SV_VertexID", 0), new D3DShaderInputs.Input("TEXCOORD", 3, 2),
            new D3DShaderInputs.Input("_Active", 1, 0, D3DShaderInputs.InputKind.Uniform),
            new D3DShaderInputs.Input("_Inactive", 0, 0, D3DShaderInputs.InputKind.Uniform) };

        [Test]
        public void ShaderCacheReusesAProgramAcrossOwnersAndAddsIndependentInputQueries()
        {
            int calls = 0; var first = Cache();
            string key = first.Key("static const float gate=0;", "vert", ShaderType.Vertex, "compiler-v1");
            var facts = first.Compile(key, () => { calls++; return (Facts(), null); }).Inputs;
            Assert.That(first.Usage(key, ShaderInputQuery.VertexId, facts), Is.False);
            facts[0] = new D3DShaderInputs.Input("SV_VertexID", 1);
            var second = Cache();
            var reused = second.Compile(key, () => { calls++; return (Facts(), null); }).Inputs;
            Assert.That(reused[0].ReadMask, Is.Zero, "Caller mutation must not alter durable facts.");
            Assert.That(second.Usage(key, ShaderInputQuery.Uniform("_Active"), reused, true), Is.True);
            Assert.That(second.Usage(key, ShaderInputQuery.Uniform("_Inactive"), reused, true), Is.False);
            Assert.That(second.Usage(key, ShaderInputQuery.Uniform("_Inactive"), reused), Is.True,
                "A cached absence must not authorize queries without verified uniform coverage.");
            Assert.That(second.Usage(key, ShaderInputQuery.VertexId, reused), Is.False);
            Assert.That(second.Usage(key, new ShaderInputQuery(new ShaderInputQuery.Semantic("TEXCOORD", 2)), reused), Is.True);
            Assert.That(second.Usage(key, new ShaderInputQuery(new ShaderInputQuery.Semantic("TEXCOORD", 1)), reused), Is.False);
            Assert.That(calls, Is.EqualTo(1)); Assert.That(second.Hits, Is.EqualTo(1));
            Assert.That(Directory.GetFiles(directory, "*.bin").Length, Is.EqualTo(6));
        }

        [Test]
        public void ShaderCacheSeparatesSourceConstantsEntryStageCompilerAndAnalysisRevisions()
        {
            var cache = Cache(); string key = cache.Key("float gate=0;", "vert", ShaderType.Vertex, "v1");
            Assert.That(cache.Key("float gate=1;", "vert", ShaderType.Vertex, "v1"), Is.Not.EqualTo(key));
            Assert.That(cache.Key("float gate=0;", "other", ShaderType.Vertex, "v1"), Is.Not.EqualTo(key));
            Assert.That(cache.Key("float gate=0;", "vert", ShaderType.Fragment, "v1"), Is.Not.EqualTo(key));
            Assert.That(cache.Key("float gate=0;", "vert", ShaderType.Vertex, "v2"), Is.Not.EqualTo(key));
            Assert.That(Cache("analyzer-v2").Key("float gate=0;", "vert", ShaderType.Vertex, "v1"), Is.Not.EqualTo(key));
            Assert.That(cache.Key("float gate=0;", "vert", ShaderType.Vertex, null), Is.Null);
        }

        [Test]
        public void ShaderCacheDoesNotPersistFailuresAndRepairsTruncatedOrCorruptRecords()
        {
            var cache = Cache(); string key = cache.Key("source", "main", ShaderType.Vertex, "v1");
            int calls = 0;
            for (int i = 0; i < 2; i++) cache.Compile(key, () => { calls++; return (null, "transient failure"); });
            Assert.That(calls, Is.EqualTo(2));
            cache.Compile(key, () => (Facts(), null));
            string file = Directory.GetFiles(directory, "*.bin").Single();
            foreach (bool truncate in new[] { false, true })
            {
                byte[] bytes = File.ReadAllBytes(file);
                if (truncate) Array.Resize(ref bytes, 12); else bytes[bytes.Length - 1] ^= 1;
                File.WriteAllBytes(file, bytes);
                Assert.That(cache.Compile(key, () => { calls++; return (Facts(), null); }).Inputs, Is.Not.Null);
            }
            Assert.That(calls, Is.EqualTo(4));
            Assert.That(Cache().Compile(key, () => throw new Exception("Must reuse repaired record")).Inputs.Length, Is.EqualTo(4));
        }

        [Test]
        public void ShaderCacheIsOptionalWhenStorageIsUnavailable()
        {
            File.WriteAllText(directory, "A file cannot contain cache records.");
            var cache = Cache(); var key = cache.Key("source", "main", ShaderType.Vertex, "v1");
            Assert.That(cache.Compile(key, () => (Facts(), null)).Inputs.Length, Is.EqualTo(4));
            Assert.That(cache.Usage(key, ShaderInputQuery.VertexId, Facts()), Is.False);
        }

        [Test]
        public void ShaderCacheSerializesConcurrentRequestsForTheSameProgram()
        {
            int calls = 0; var cache = Cache(); var key = cache.Key("source", "main", ShaderType.Vertex, "v1");
            var tasks = Enumerable.Range(0, 16).Select(_ => Task.Run(() => cache.Compile(key, () =>
            { Interlocked.Increment(ref calls); return (Facts(), null); }))).ToArray();
            Assert.That(Task.WaitAll(tasks, TimeSpan.FromSeconds(10)), Is.True);
            Assert.That(calls, Is.EqualTo(1)); Assert.That(cache.Hits, Is.EqualTo(15));
            Assert.That(tasks.All(t => t.Result.Inputs[1].ReadMask == 3), Is.True);
        }
    }
}
