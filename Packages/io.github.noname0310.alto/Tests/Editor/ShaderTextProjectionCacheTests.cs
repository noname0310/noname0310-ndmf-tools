using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Noname.AvatarTools.Analysis;
using NUnit.Framework;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private static readonly string ProjectionPadding = string.Concat(Enumerable.Range(0, 20).Select(i => "float plain" + i + "; "));

        [Test]
        public void ShaderTextProjectionSharesOnlyProvenEmptyTransfers()
        {
            var documents = new Dictionary<(string, string), PpDocument>();
            string text = ProjectionPadding + "uint value : SEMANTIC;";
            var noInput = new PpBinding(new[] { new PpMacro("SEMANTIC", PpLexer.Lex("POSITION").ToArray()) });
            var input = new PpBinding(new[] { new PpMacro("SEMANTIC", PpLexer.Lex("SV_VertexID").ToArray()) });
            var demand = new ShaderInputDemand(ShaderInputQuery.VertexId);
            var first = TextProjectionRun(text, documents, demand, noInput, true);
            var repeated = TextProjectionRun(text, documents, demand, noInput, true);
            Assert.That(first.Hits, Is.Zero);
            Assert.That(repeated.Hits, Is.GreaterThan(0));
            Assert.That(repeated.Facts, Is.EqualTo(TextProjectionRun(text, documents, demand, noInput, false).Facts));
            var changed = TextProjectionRun(text, documents, demand, input, true);
            Assert.That(changed.Facts, Is.EqualTo(TextProjectionRun(text, documents, demand, input, false).Facts));
            Assert.That(changed.Facts, Does.Contain("SV_VertexID"));
            Assert.That(changed.Hits, Is.Zero);
        }

        [Test]
        public void ShaderTextProjectionKeepsDemandAndSourceLocationChanges()
        {
            var documents = new Dictionary<(string, string), PpDocument>();
            var demand = new ShaderInputDemand(ShaderInputQuery.VertexId);
            string text = ProjectionPadding + "uint value : SEMANTIC;";
            var empty = PpBinding.Undefined;
            TextProjectionRun(text, documents, demand, empty, true);
            demand.Names.Add("plain5");
            var changed = TextProjectionRun(text, documents, demand, empty, true);
            Assert.That(changed.Facts, Is.EqualTo(TextProjectionRun(text, documents, demand, empty, false).Facts));
            Assert.That(changed.Facts, Does.Contain("plain5"));
            Assert.That(changed.Hits, Is.Zero);

            var cache = new PpTextProjectionCache();
            var state = new PpState();
            cache.Add("query", PpLocations.Start("a").Remap(10), new Dictionary<string, PpBinding>(), 12);
            Assert.That(cache.TrySkip("query", state), Is.False);
            state.Locations = PpLocations.Start("b").Remap(10);
            Assert.That(cache.TrySkip("query", state), Is.False);
            state.Locations = PpLocations.Start("a").Remap(10);
            Assert.That(cache.TrySkip("other query", state), Is.False);
            Assert.That(cache.TrySkip("query", state), Is.True);
        }

        [Test]
        public void ShaderTextProjectionKeepsLimitsAndMacroBoundaryFailures()
        {
            var documents = new Dictionary<(string, string), PpDocument>();
            var demand = new ShaderInputDemand(ShaderInputQuery.VertexId);
            string text = ProjectionPadding + "SEMANTIC(SV_ButNotQueried";
            var absent = PpBinding.Undefined;
            TextProjectionRun(text, documents, demand, absent, true);
            var function = new PpBinding(new[] { new PpMacro("SEMANTIC", PpLexer.Lex("x").ToArray(), new[] { "x" }) });
            var failure = TextProjectionRun(text, documents, demand, function, true);
            Assert.That(failure.Facts, Is.EqualTo(TextProjectionRun(text, documents, demand, function, false).Facts));
            Assert.That(failure.Facts, Does.Contain("Unterminated macro argument list"));

            string projected = ProjectionPadding + string.Concat(Enumerable.Repeat("SEMANTIC(float4(1,2,3,4));", 24));
            var ignored = new PpBinding(new[] { new PpMacro("SEMANTIC", PpLexer.Lex("x").ToArray(), new[] { "x" }) });
            TextProjectionRun(projected, documents, demand, ignored, true);
            for (int budget = 0; budget < 70; budget++)
                Assert.That(TextProjectionRun(projected, documents, demand, ignored, true, budget).Facts,
                    Is.EqualTo(TextProjectionRun(projected, documents, demand, ignored, false, budget).Facts), "budget " + budget);
        }

        [Test]
        public void ShaderTextProjectionDoesNotResolveNewAbstractInputsDuringValidation()
        {
            var cache = new PpTextProjectionCache();
            cache.Add("query", PpLocations.Start("root"), new Dictionary<string, PpBinding> { ["LAZY"] = PpBinding.Undefined }, 2);
            var state = new PpState { Locations = PpLocations.Start("root") }; int calls = 0;
            state.InitialInput = (_, _) => { calls++; return PpBinding.Undefined; };
            Assert.That(cache.TrySkip("query", state), Is.False);
            Assert.That(calls, Is.Zero);
            state.PeekInitialInput = _ => PpBinding.Undefined;
            Assert.That(cache.TrySkip("query", state), Is.True);
            Assert.That(calls, Is.Zero);
        }

        [Test]
        public void ShaderTextProjectionReplaysCurrentDependencyProvenance()
        {
            var macro = new PpMacro("UNCHANGED", PpLexer.Lex("POSITION").ToArray());
            var before = new PpBinding(new[] { macro }, BigInteger.One);
            var after = new PpBinding(new[] { macro }, BigInteger.One << 30);
            var cache = new PpTextProjectionCache();
            cache.Add("query", PpLocations.Start("root"), new Dictionary<string, PpBinding> { ["UNCHANGED"] = before }, 2);
            var state = new PpState { Locations = PpLocations.Start("root") }; state.Macros["UNCHANGED"] = after;
            PpBinding observed = null;
            state.ObserveMacroRead = (_, binding) => observed = binding;
            Assert.That(cache.TrySkip("query", state), Is.True);
            Assert.That(observed, Is.SameAs(after));
        }

        [Test]
        public void ShaderTextProjectionMatchesUncachedGeneralMacroPrograms()
        {
            var random = new Random(53417);
            string[] parts = { "SEMANTIC", "F(SEMANTIC)", "CAT(SV_,VertexID)", "F(plain)", "WHOLE.member", "WHOLE", "_Pragma(\"example\")", "__COUNTER__", "__LINE__" };
            var documents = new Dictionary<(string, string), PpDocument>();
            for (int trial = 0; trial < 180; trial++)
            {
                string text = "#define F(x) x\n#define CAT(a,b) a ## b\n" + ProjectionPadding + parts[random.Next(parts.Length)] +
                    "\n#if FLAG\n" + ProjectionPadding + parts[random.Next(parts.Length)] + "\n#else\n" + ProjectionPadding + "\n#endif\n";
                var demand = new ShaderInputDemand(ShaderInputQuery.VertexId);
                if (trial % 3 == 0) demand.WholeValues.Add("WHOLE");
                var alternatives = new[] { PpBinding.Undefined,
                    new PpBinding(new[] { new PpMacro("SEMANTIC", PpLexer.Lex("POSITION").ToArray()) }),
                    new PpBinding(new[] { new PpMacro("SEMANTIC", PpLexer.Lex("SV_VertexID").ToArray()) }) };
                for (int context = 0; context < 6; context++)
                {
                    var binding = alternatives[context % alternatives.Length];
                    Assert.That(TextProjectionRun(text, documents, demand, binding, true, 1000000, context % 2 == 0).Facts,
                        Is.EqualTo(TextProjectionRun(text, documents, demand, binding, false, 1000000, context % 2 == 0).Facts), text);
                }
            }
        }

        private static (string Facts, int Hits) TextProjectionRun(string text, Dictionary<(string, string), PpDocument> documents,
            ShaderInputDemand demand, PpBinding semantic, bool reuse, int budget = 1000000, bool flag = false)
        {
            var program = new PpProgram(null, documents);
            var source = program.Parse(new ShaderPreprocessorGraph.Source("root", text));
            var state = new PpState(); state.Macros["SEMANTIC"] = semantic;
            state.Macros["FLAG"] = new PpBinding(new[] { new PpMacro("FLAG", PpLexer.Lex(flag ? "1" : "0").ToArray()) });
            state.Budget.Charge(1000000 - budget);
            var interpreter = new PpInterpreter(program, PpDialect.Standard, false, demand, reuse);
            string issue = null;
            try { interpreter.Run(source, state); } catch (PpException exception) { issue = exception.Message; }
            string output = string.Join(";", interpreter.Output.SelectMany(t => t).Select(t => t.Text + "@" + t.File + ":" + t.Line + ":" + t.Dependencies));
            return (issue + "|" + output + "|" + interpreter.Dependencies + "|" + interpreter.Instructions + "|" + state.Budget.Remaining + "|" +
                state.Budget.Choices + "|" + state.Budget.ExpansionCalls + "|" + state.Budget.DialectEffects + "|" + state.Counter.Bits,
                state.Budget.TextProjectionHits);
        }
    }
}
