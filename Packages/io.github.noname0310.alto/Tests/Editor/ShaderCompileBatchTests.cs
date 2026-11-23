using System;
using System.Threading;
using Noname.AvatarTools.Analysis;
using NUnit.Framework;
using UnityEditor.Rendering;
using UnityEngine;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        [Test]
        public void ShaderInputPreparationOverlapsCompilationAndPreservesLocatedFacts()
        {
            using var entered = new CountdownEvent(2);
            int mainThread = Thread.CurrentThread.ManagedThreadId;
            bool parserWasWorker = false;
            using var preparation = new ShaderInputPreparation("source.hlsl", (text, file) =>
            {
                if (file.Length != 0)
                {
                    parserWasWorker = Thread.CurrentThread.ManagedThreadId != mainThread;
                    entered.Signal();
                    if (!entered.Wait(TimeSpan.FromSeconds(5))) throw new InvalidOperationException("Work did not overlap");
                }
                return new ShaderInputProgram(text, file);
            });
            preparation.Start("float x;", "static const float x=0;");
            using var compiler = new ShaderCompileBatch(1, (source, entry, stage) =>
            {
                entered.Signal();
                if (!entered.Wait(TimeSpan.FromSeconds(5))) throw new InvalidOperationException("Work did not overlap");
                return (Array.Empty<D3DShaderInputs.Input>(), null);
            });
            compiler.Add("source", "main", ShaderType.Vertex);
            Assert.That(compiler.TryGet("source", "main", ShaderType.Vertex, out var proof), Is.True);
            Assert.That(proof.Error, Is.Null);
            var facts = preparation.Get("float x;", "static const float x=0;");
            Assert.That(parserWasWorker, Is.True);
            Assert.That(facts.Original.Tokens[0].File, Is.EqualTo("source.hlsl"));
            Assert.That(facts.Specialized.ScalarConstants["x"], Is.EqualTo("0"));
        }

        [Test]
        public void ShaderInputPreparationReportsConsumedFailuresButJoinsAbandonedWork()
        {
            int completed = 0;
            var preparation = new ShaderInputPreparation("source", (text, file) =>
            { Interlocked.Increment(ref completed); throw new InvalidOperationException("Parse failed"); });
            preparation.Start("source", "specialized");
            Assert.Throws<InvalidOperationException>(() => preparation.Get("source", "specialized"));
            Assert.DoesNotThrow(() => preparation.Dispose());
            Assert.DoesNotThrow(() => preparation.Dispose());
            Assert.That(completed, Is.EqualTo(2), "Both independent parses must be joined, including the abandoned failure.");
        }

        [Test]
        public void ShaderInputPreparationParsesBothProgramsConcurrently()
        {
            using var entered = new CountdownEvent(2);
            using var preparation = new ShaderInputPreparation("source.hlsl", (text, file) =>
            {
                entered.Signal();
                if (!entered.Wait(TimeSpan.FromSeconds(5))) throw new InvalidOperationException("Parses did not overlap");
                return new ShaderInputProgram(text, file);
            });
            preparation.Start("float x;", "static const float x=0;");
            var facts = preparation.Get("float x;", "static const float x=0;");
            Assert.That(facts.Original.Issue, Is.Null);
            Assert.That(facts.Specialized.ScalarConstants["x"], Is.EqualTo("0"));
        }

        [Test]
        public void ShaderCompileBatchRunsIndependentJobsAndConsumesInCallerOrder()
        {
            using var entered = new CountdownEvent(2);
            using var batch = new ShaderCompileBatch(2, (source, entry, stage) =>
            {
                entered.Signal();
                if (!entered.Wait(TimeSpan.FromSeconds(5))) throw new InvalidOperationException("Jobs did not overlap");
                return (new[] { new D3DShaderInputs.Input(source, 1) }, null);
            });
            Assert.That(batch.Add("first", "main", ShaderType.Vertex), Is.True);
            Assert.That(batch.Add("second", "main", ShaderType.Vertex), Is.True);
            Assert.That(batch.Add("first", "main", ShaderType.Vertex), Is.True);
            Assert.That(batch.Add("third", "main", ShaderType.Vertex), Is.False);
            Assert.That(batch.TryGet("second", "main", ShaderType.Vertex, out var second), Is.True);
            Assert.That(batch.TryGet("first", "main", ShaderType.Vertex, out var first), Is.True);
            Assert.That(second.Error, Is.Null); Assert.That(first.Error, Is.Null);
            Assert.That(first.Inputs[0].Semantic, Is.EqualTo("first"));
            Assert.That(second.Inputs[0].Semantic, Is.EqualTo("second"));
            Assert.That(batch.Count, Is.EqualTo(2));
        }

        [Test]
        public void ShaderCompileBatchJoinsAbandonedFailuresWithoutPublishingThem()
        {
            int completed = 0;
            var batch = new ShaderCompileBatch(2, (source, entry, stage) =>
            {
                Interlocked.Increment(ref completed);
                if (source == "invalid") throw new InvalidOperationException();
                return (Array.Empty<D3DShaderInputs.Input>(), null);
            });
            batch.Add("valid", "main", ShaderType.Vertex);
            batch.Add("invalid", "main", ShaderType.Vertex);
            Assert.That(batch.TryGet("valid", "main", ShaderType.Vertex, out var proof), Is.True);
            Assert.That(proof.Error, Is.Null);
            batch.Dispose(); batch.Dispose();
            Assert.That(completed, Is.EqualTo(2));
            Assert.That(batch.TryGet("invalid", "main", ShaderType.Vertex, out var failed), Is.True);
            Assert.That(failed.Inputs, Is.Null);
            Assert.That(failed.Error, Does.Contain("InvalidOperationException"));
        }

        [Test]
        public void ShaderCompileBatchNativeResultsMatchSerialReflection()
        {
            if (Application.platform != RuntimePlatform.WindowsEditor) Assert.Ignore("Requires the Windows shader compiler");
            string[] programs = {
                "float4 main(float4 p:POSITION,uint id:SV_VertexID):SV_Position{return p;}",
                "float4 main(float4 p:POSITION,uint id:SV_VertexID):SV_Position{return p+float4(id,0,0,0);}",
                "this is not a shader"
            };
            using var batch = new ShaderCompileBatch(programs.Length);
            foreach (string program in programs) batch.Add(program, "main", ShaderType.Vertex);
            foreach (string program in programs)
            {
                D3DShaderInputs.Compile(program, "main", ShaderType.Vertex, out var inputs, out string error);
                Assert.That(batch.TryGet(program, "main", ShaderType.Vertex, out var proof), Is.True);
                Assert.That(proof.Error, Is.EqualTo(error));
                Assert.That(proof.Inputs, Is.EqualTo(inputs));
            }
        }
    }
}
