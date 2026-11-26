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
        private const string ConditionalLocations = "#if FLAG\n#line 10 \"left\"\n#else\n#line 20 \"right\"\n#endif\n";

        [Test]
        public void ShaderPreprocessingLocations_ZeroIsAValidGeneratedSourceLocation()
        {
            // Unity emits #line 0 in imported pass sources. HLSL #line accepts
            // this value; treating it as unknown defeats every entry-use slice.
            var query = ShaderInputQuery.Identifier("_Gain");
            var demand = new ShaderInputDemand(query);
            var slice = new ShaderInputUseSlice { Complete = true };
            slice.Regions.Add(("generated", 0, 0)); demand.Include(slice);
            var graph = LocationGraph("#line 0 \"generated\"\nfloat value=__LINE__;\n", demand);
            Assert.That(graph.Issue, Is.Null);
            var run = LocationRun("#line 0 \"generated\"\n#if __LINE__ != 0\n#error wrong line\n#endif\n", abstractLocations: false);
            Assert.That(run.Issue, Is.Null);
        }

        private static PpState LocationState(params string[] names)
        {
            var state = new PpState();
            for (int i = 0; i < names.Length; i++)
            {
                var dependency = BigInteger.One << i;
                state.Macros[names[i]] = new PpBinding(new[] { new PpMacro(names[i],
                    new[] { new PpToken(names[i], PpTokenKind.Symbol, dependencies: dependency) }) }, dependency, true);
            }
            return state;
        }

        private static (PpInterpreter Interpreter, PpState State, string Issue) LocationRun(string text,
            PpState state = null, bool abstractLocations = true, ShaderInputDemand demand = null, bool reuse = true,
            Dictionary<(string, string), PpDocument> documents = null, Dictionary<string, string> files = null,
            PpDialect dialect = PpDialect.Standard, string rootFile = "root")
        {
            var program = new PpProgram((_, name) => files != null && files.TryGetValue(name, out string body)
                ? new[] { new ShaderPreprocessorGraph.Source(name, body) } : Array.Empty<ShaderPreprocessorGraph.Source>(), documents);
            var source = program.Parse(new ShaderPreprocessorGraph.Source(rootFile, text));
            state ??= LocationState("FLAG", "INNER");
            var interpreter = new PpInterpreter(program, dialect, abstractLocations, demand, reuse);
            string issue = null;
            try { interpreter.Run(source, state); }
            catch (PpException exception) { issue = exception.Message; }
            return (interpreter, state, issue);
        }

        private static ShaderPreprocessorGraph LocationGraph(string text, ShaderInputDemand demand = null,
            Dictionary<string, string> files = null)
        {
            var source = new ShaderPreprocessorGraph.Source("root", text);
            ShaderPreprocessorGraph.Source[] Include(string _, string name) => files != null && files.TryGetValue(name, out string body)
                ? new[] { new ShaderPreprocessorGraph.Source(name, body) } : Array.Empty<ShaderPreprocessorGraph.Source>();
            return demand == null ? ShaderPreprocessorGraph.Analyze(source, Include, ShaderInputQuery.VertexId, new[] { "FLAG", "INNER" }) :
                ShaderPreprocessorGraph.AnalyzeDemand(source, Include, demand, new[] { "FLAG", "INNER" });
        }

        [Test]
        public void ShaderPreprocessingLocations_UnusedConditionalMappingsDoNotBecomeKeywordDependencies()
        {
            var graph = LocationGraph(ConditionalLocations + "float4 p : POSITION;\n");
            Assert.That(graph.Issue, Is.Null);
            Assert.That(graph.InputAbsent, Is.True);
            Assert.That(graph.CanIgnore("FLAG"), Is.True);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ShaderPreprocessingLocations_LineReaderRetainsItsConditionalMapping(bool alias)
        {
            string reader = alias ? "LINE_ALIAS" : "__LINE__";
            var graph = LocationGraph("#define LINE_ALIAS __LINE__\n" + ConditionalLocations +
                "#if " + reader + " == 100\nuint id : SV_VertexID;\n#endif\n");
            Assert.That(graph.Issue, Is.Null);
            Assert.That(graph.InputAbsent, Is.False);
            Assert.That(graph.CanIgnore("FLAG"), Is.False, "Builtin analysis seeds must not hide the mapping dependency.");
        }

        [TestCase("", true, true)]
        [TestCase("#line 100\n", false, true)]
        [TestCase("#line 100 \"fixed\"\n", false, false)]
        public void ShaderPreprocessingLocations_OverwritesDropOnlyTheWrittenFieldDependencies(string overwrite,
            bool lineDependent, bool fileDependent)
        {
            var result = LocationRun(ConditionalLocations + overwrite + "__LINE__ __FILE__\n");
            Assert.That(result.Issue, Is.Null);
            var tokens = result.Interpreter.Output.SelectMany(t => t).ToArray();
            Assert.That(tokens.Single(t => t.Text == "__LINE__").Dependencies,
                Is.EqualTo(lineDependent ? BigInteger.One : BigInteger.Zero));
            Assert.That(tokens.Single(t => t.Text == "__FILE__").Dependencies,
                Is.EqualTo(fileDependent ? BigInteger.One : BigInteger.Zero));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void ShaderPreprocessingLocations_EqualJoinedFieldDoesNotAcquireOtherFieldGuard(bool equalLine)
        {
            string text = equalLine
                ? "#if FLAG\n#line 10 \"left\"\n#else\n#line 12 \"right\"\n#endif\n"
                : "#if FLAG\n#line 10 \"same\"\n#else\n#line 20 \"same\"\n#endif\n";
            var result = LocationRun(text + "__LINE__ __FILE__\n");
            Assert.That(result.Issue, Is.Null);
            var tokens = result.Interpreter.Output.SelectMany(t => t).ToArray();
            Assert.That(tokens.Single(t => t.Text == "__LINE__").Dependencies, Is.EqualTo(equalLine ? BigInteger.Zero : BigInteger.One));
            Assert.That(tokens.Single(t => t.Text == "__FILE__").Dependencies, Is.EqualTo(equalLine ? BigInteger.One : BigInteger.Zero));
        }

        [Test]
        public void ShaderPreprocessingLocations_MacroDirectiveAlternativesKeepBothFieldDependencies()
        {
            const string text = "#if FLAG\n#define LOCATION 10 \"left\"\n#else\n#define LOCATION 20 \"right\"\n#endif\n" +
                "#line LOCATION\n__LINE__ __FILE__\n";
            var result = LocationRun(text);
            Assert.That(result.Issue, Is.Null);
            Assert.That(result.Interpreter.Output.SelectMany(t => t).Where(t => t.Kind == PpTokenKind.Symbol)
                .Select(t => t.Dependencies), Is.EqualTo(new[] { BigInteger.One, BigInteger.One }));
        }

        [Test]
        public void ShaderPreprocessingLocations_IncludePushPopAndMacroUseKeepCallerLocation()
        {
            var files = new Dictionary<string, string>
            {
                ["outer"] = "#pragma once\n#if INNER\n#line 500 \"inner-a\"\n#else\n#line 600 \"inner-b\"\n#endif\n" +
                    "#include \"nested\"\n#define CALLER_LINE __LINE__\n#define CALLER_FILE __FILE__\n",
                ["nested"] = "#line 900 \"nested-location\"\n#define VALUE 1\n"
            };
            var result = LocationRun(ConditionalLocations + "#include \"outer\"\n#include \"outer\"\nCALLER_LINE CALLER_FILE\n", files: files);
            Assert.That(result.Issue, Is.Null);
            Assert.That(result.Interpreter.Output.SelectMany(t => t).Where(t => t.Kind == PpTokenKind.Symbol)
                .Select(t => t.Dependencies), Is.EqualTo(new[] { BigInteger.One, BigInteger.One }),
                "INNER mappings must not escape an include or attach to a macro definition's later invocation.");
        }

        [TestCase("__LINE__")]
        [TestCase("__FILE__")]
        public void ShaderPreprocessingLocations_StringificationDistinguishesRawAndPrescannedBuiltins(string builtin)
        {
            var result = LocationRun("#define RAW(x) #x\n#define EXPANDED(x) RAW(x)\n" + ConditionalLocations +
                "RAW(" + builtin + ") EXPANDED(" + builtin + ")\n");
            Assert.That(result.Issue, Is.Null);
            var tokens = result.Interpreter.Output.SelectMany(t => t).ToArray();
            Assert.That(tokens[0].Kind, Is.EqualTo(PpTokenKind.String));
            Assert.That(tokens[0].Dependencies, Is.EqualTo(BigInteger.Zero));
            Assert.That(tokens[1].Kind, Is.EqualTo(PpTokenKind.Symbol));
            Assert.That(tokens[1].Dependencies, Is.EqualTo(BigInteger.One));
        }

        [Test]
        public void ShaderPreprocessingLocations_ProjectionCannotDiscardBuiltinPasteExpansion()
        {
            const string text = "#define CAT(a,b) a ## b\n#define EXPAND(a,b) CAT(a,b)\n#line 7 \"logical\"\nEXPAND(Value,__LINE__)\n";
            var demand = new ShaderInputDemand(ShaderInputQuery.Identifier("Value7"));
            var concrete = LocationRun(text, abstractLocations: false, demand: demand);
            Assert.That(concrete.Issue, Is.Null);
            Assert.That(PpLexer.Spell(concrete.Interpreter.Output.SelectMany(t => t)), Is.EqualTo("Value7"));
            var abstractResult = LocationRun(text, demand: demand);
            Assert.That(abstractResult.Issue, Does.Contain("Token paste requires a concrete symbolic replacement"));
        }

        [TestCase("__FILE__")]
        [TestCase("EXPAND(__LINE__)")]
        public void ShaderPreprocessingLocations_IncludeOperandsCannotUseInventedBuiltinValues(string operand)
        {
            string text = "#define STR(x) #x\n#define EXPAND(x) STR(x)\n#line 7 \"selected\"\n#include " + operand + "\n";
            var files = new Dictionary<string, string> { ["selected"] = "uint id : SV_VertexID;\n", ["7"] = "uint id : SV_VertexID;\n" };
            var concrete = LocationRun(text, abstractLocations: false, files: files);
            Assert.That(concrete.Issue, Is.Null);
            Assert.That(PpLexer.Spell(concrete.Interpreter.Output.SelectMany(t => t)), Does.Contain("SV_VertexID"));
            var graph = LocationGraph(text, files: files);
            Assert.That(graph.Issue, Does.Contain("Unresolved macro include operand"));
            Assert.That(graph.InputAbsent, Is.False);
        }

        [TestCase("__LINE__")]
        [TestCase("__FILE__")]
        [TestCase("__COUNTER__")]
        public void ShaderPreprocessingLocations_BuiltinDefinednessKeepsBothNativePossibilitiesAndAuthoredOverrides(string builtin)
        {
            string body = "#if defined(" + builtin + ")\nuint id : SV_VertexID;\n#endif\n";
            var graph = LocationGraph(body);
            Assert.That(graph.Issue, Is.Null);
            Assert.That(graph.InputAbsent, Is.False);
            Assert.That(LocationGraph("#ifndef " + builtin + "\nuint id : SV_VertexID;\n#endif\n").InputAbsent, Is.False);
            var concrete = LocationRun(body, new PpState(), false);
            Assert.That(concrete.Issue, Is.Null);
            Assert.That(PpLexer.Spell(concrete.Interpreter.Output.SelectMany(t => t)), Does.Contain("SV_VertexID"));
            Assert.That(LocationGraph("#undef " + builtin + "\n" + body).InputAbsent, Is.True);
            Assert.That(LocationGraph("#undef " + builtin + "\n#define " + builtin + " 12\n" + body).InputAbsent, Is.False);
            var overridden = LocationGraph("#undef " + builtin + "\n#define " + builtin + " SV_VertexID\nuint id : " + builtin + ";\n");
            Assert.That(overridden.Issue, Is.Null);
            Assert.That(overridden.InputAbsent, Is.False);
        }

        [Test]
        public void ShaderPreprocessingLocations_SymbolicRepeatedReadsDoNotInventEquality()
        {
            var graph = LocationGraph(ConditionalLocations +
                "#if __LINE__ == __LINE__\nfloat p;\n#else\nuint id : SV_VertexID;\n#endif\n");
            Assert.That(graph.Issue, Is.Null);
            Assert.That(graph.InputAbsent, Is.False, "Opaque tokens are not an equality proof or a concrete literal.");
            Assert.That(graph.CanIgnore("FLAG"), Is.False);
        }

        [Test]
        public void ShaderPreprocessingLocations_ProjectionPreservesPairsAndWidenedRegions()
        {
            var locations = PpLocations.Join(PpLocations.Start("a").Remap(9), PpLocations.Start("b").Remap(19), BigInteger.One);
            var token = PpLexer.Lex("ordinary", "physical", 1).Single();
            var slice = new ShaderInputUseSlice(); slice.Regions.Add(("a", 20, 20));
            var demand = new ShaderInputDemand(ShaderInputQuery.VertexId); demand.Include(slice);
            Assert.That(new PpProjection(new PpState(), demand, locations).MayContribute(token), Is.False,
                "The domain must not cross the line from one alternative with the filename from another.");
            slice.Regions.Add(("b", 20, 20)); demand.Include(slice);
            Assert.That(new PpProjection(new PpState(), demand, locations).MayContribute(token), Is.True);
            for (int i = 0; i <= PpLocations.MaximumMappings; i++)
                locations = PpLocations.Join(locations, PpLocations.Start("file" + i).Remap(i), BigInteger.One << (i + 1));
            Assert.That(locations.Mappings, Is.Null);
            var unrelated = new ShaderInputUseSlice(); unrelated.Regions.Add(("unknown-file", 999, 999));
            var region = new ShaderInputDemand(ShaderInputQuery.VertexId); region.Include(unrelated);
            Assert.That(new PpProjection(new PpState(), region, locations).MayContribute(token), Is.True);
            Assert.That(new PpProjection(new PpState(), new ShaderInputDemand(ShaderInputQuery.VertexId), locations).MayContribute(token), Is.False);
            var reset = locations.Remap(4, "fixed", true);
            Assert.That(reset.Mappings.Length, Is.EqualTo(1));
            Assert.That(reset.ReadLine(token), Is.EqualTo(5));
            Assert.That(reset.ReadFile(token), Is.EqualTo("fixed"));
            Assert.That(reset.LineDependencies | reset.FileDependencies, Is.EqualTo(BigInteger.Zero));
        }

        private static string ManyConditionalLocations() => string.Concat(Enumerable.Range(0, PpLocations.MaximumMappings + 2)
            .Select(i => "#if FLAG\n#line " + (1000 + i) + " \"file" + i + "\"\n#endif\n"));

        [TestCase(false)]
        [TestCase(true)]
        public void ShaderPreprocessingLocations_WideningDoesNotRejectUnusedMetadata(bool declarationsOnly)
        {
            var graph = LocationGraph(ManyConditionalLocations() + "float4 p : POSITION;\n",
                declarationsOnly ? new ShaderInputDemand(ShaderInputQuery.VertexId) : null);
            Assert.That(graph.Issue, Is.Null);
            Assert.That(graph.InputAbsent, Is.True);
            Assert.That(graph.CodeDependencies, Does.Not.Contain("FLAG"));
            var reader = LocationGraph(ManyConditionalLocations() + "#if __LINE__ == 2\nuint id : SV_VertexID;\n#endif\n");
            Assert.That(reader.Issue, Is.Null);
            Assert.That(reader.InputAbsent, Is.False);
            Assert.That(reader.CanIgnore("FLAG"), Is.False);
        }

        [TestCase("path\\\\file")]
        [TestCase("a\\\"b")]
        public void ShaderPreprocessingLocations_UnverifiedFilenameEscapesRetainLineAndWidenOnlyFile(string filename)
        {
            string prefix = "#line 20 \"" + filename + "\"\n";
            var line = LocationRun(prefix + "__LINE__\n", abstractLocations: false);
            Assert.That(line.Issue, Is.Null);
            Assert.That(PpLexer.Spell(line.Interpreter.Output.SelectMany(t => t)), Is.EqualTo("20"));
            var file = LocationRun(prefix + "__FILE__\n", abstractLocations: false);
            Assert.That(file.Issue, Does.Contain("Source filename has multiple or unknown locations"));
            var graph = LocationGraph(prefix + "float p : POSITION;\n");
            Assert.That(graph.Issue, Is.Null);
            Assert.That(graph.InputAbsent, Is.True);
            var region = new ShaderInputUseSlice(); region.Regions.Add(("unverified", 20, 20));
            var demand = new ShaderInputDemand(ShaderInputQuery.VertexId); demand.Include(region);
            var projected = LocationRun(prefix + "ordinary\n", demand: demand);
            Assert.That(projected.Issue, Is.Null);
            Assert.That(PpLexer.Spell(projected.Interpreter.Output.SelectMany(t => t)), Is.EqualTo("ordinary"));
        }

        [Test]
        public void ShaderPreprocessingLocations_CacheDoesNotConflatePairsOrDependencyMasks()
        {
            var first = PpLocations.Join(PpLocations.Start("a").Remap(9), PpLocations.Start("b").Remap(19), BigInteger.One);
            var cache = new PpTextProjectionCache();
            cache.Add("query", first, new Dictionary<string, PpBinding>(), 1);
            var state = new PpState { Locations = first };
            Assert.That(cache.TrySkip("query", state), Is.True);
            state.Locations = PpLocations.Join(PpLocations.Start("b").Remap(9), PpLocations.Start("a").Remap(19), BigInteger.One);
            Assert.That(cache.TrySkip("query", state), Is.False);
            state.Locations = PpLocations.Join(PpLocations.Start("a").Remap(9), PpLocations.Start("b").Remap(19), BigInteger.One << 8);
            Assert.That(cache.TrySkip("query", state), Is.False);
            var sameMapping = PpLocations.Join(PpLocations.Start("a").Remap(9, lineDependencies: BigInteger.One),
                PpLocations.Start("a").Remap(9, lineDependencies: BigInteger.One << 2), BigInteger.One << 3);
            Assert.That(sameMapping.LineDependencies, Is.EqualTo(BigInteger.One | (BigInteger.One << 2)));
            Assert.That(sameMapping.Mappings.Length, Is.EqualTo(1));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ShaderPreprocessingLocations_EmptyFilenameUsesRootForProjectionAndStaysEmptyForBuiltin(bool include)
        {
            const string body = "#line 40 \"\"\n#if FLAG\nordinary;\n#endif\n";
            var slice = new ShaderInputUseSlice(); slice.Regions.Add(("root", 40, 50));
            var demand = new ShaderInputDemand(ShaderInputQuery.VertexId); demand.Include(slice);
            var graph = include ? LocationGraph("#include \"header\"\n", demand, new Dictionary<string, string> { ["header"] = body }) :
                LocationGraph(body, demand);
            Assert.That(graph.Issue, Is.Null);
            Assert.That(graph.CodeDependencies, Does.Contain("FLAG"),
                "An anonymous #line record must match the same root path as native ShaderInputProgram locations.");
            var raw = LocationRun("#line 40 \"\"\n__FILE__\n", abstractLocations: false);
            Assert.That(raw.Issue, Is.Null);
            Assert.That(PpLexer.Spell(raw.Interpreter.Output.SelectMany(t => t)), Is.EqualTo("\"\""));
        }

        [Test]
        public void ShaderPreprocessingLocations_SharedHeaderCacheRetainsAnonymousRootContext()
        {
            var documents = new Dictionary<(string, string), PpDocument>();
            var files = new Dictionary<string, string> { ["header"] = "#line 40 \"\"\n" + ProjectionPadding + "ordinary;\n" };
            var slice = new ShaderInputUseSlice(); slice.Regions.Add(("second-root", 40, 40));
            var demand = new ShaderInputDemand(ShaderInputQuery.VertexId); demand.Include(slice);
            var first = LocationRun("#include \"header\"\n", demand: demand, documents: documents, files: files, rootFile: "first-root");
            var repeated = LocationRun("#include \"header\"\n", demand: demand, documents: documents, files: files, rootFile: "first-root");
            Assert.That(first.Issue, Is.Null);
            Assert.That(first.Interpreter.Output, Is.Empty);
            Assert.That(repeated.State.Budget.TextProjectionHits, Is.GreaterThan(0));
            var changed = LocationRun("#include \"header\"\n", demand: demand, documents: documents, files: files, rootFile: "second-root");
            var fresh = LocationRun("#include \"header\"\n", demand: demand, reuse: false, documents: documents, files: files, rootFile: "second-root");
            Assert.That(changed.Issue, Is.Null);
            Assert.That(changed.Interpreter.Output, Is.Not.Empty);
            Assert.That(changed.State.Budget.TextProjectionHits, Is.Zero);
            Assert.That(LocationFacts(changed), Is.EqualTo(LocationFacts(fresh)));
        }

        [Test]
        public void ShaderPreprocessingLocations_EmptyFilenamePreservesNativeInputCutDependency()
        {
            RequireD3DAnalysis();
            const string head = "#line 1 \"root\"\nstruct V {float4 p:POSITION;uint id:SV_VertexID;};\nfloat Cut(uint candidate) {\n";
            const string tail = "\n#line 100 \"\"\n}\nfloat4 vert(V v):SV_POSITION {return v.p+Cut(v.id);}\n";
            string baseline = head + "#line 40 \"\"\nreturn 0;" + tail;
            string active = head + "#line 40 \"\"\nreturn candidate;" + tail;
            var query = ShaderInputQuery.VertexId;
            Assert.That(D3DShaderInputs.Compile(baseline, "vert", UnityEditor.Rendering.ShaderType.Vertex, out var before, out string beforeIssue), Is.True, beforeIssue);
            Assert.That(D3DShaderInputs.Compile(active, "vert", UnityEditor.Rendering.ShaderType.Vertex, out var after, out string afterIssue), Is.True, afterIssue);
            Assert.That(query.MayReadCompiled(before), Is.False);
            Assert.That(query.MayReadCompiled(after), Is.True, "The native program actually observes vertex ID when the branch changes.");
            var program = new ShaderInputProgram(baseline, "root");
            var slice = new ShaderInputUseSlice(baseline, program, query, "vert", program.ScalarConstants, (source, entry, stage) =>
            {
                D3DShaderInputs.Compile(source, entry, stage, out var inputs, out string issue);
                return (inputs, issue);
            });
            Assert.That(slice.Complete, Is.True, slice.Issue);
            Assert.That(slice.Cuts, Is.EqualTo(1));
            Assert.That(slice.Names, Does.Not.Contain("candidate"), "This local argument is covered by source regions, not a global name match.");
            var demand = new ShaderInputDemand(query); demand.Include(slice);
            var graph = LocationGraph(head + "#line 40 \"\"\n#if FLAG\nreturn candidate;\n#else\nreturn 0;\n#endif" + tail, demand);
            Assert.That(graph.Issue, Is.Null);
            Assert.That(graph.CodeDependencies, Does.Contain("FLAG"), "A proved baseline cut cannot hide the alternate live native input.");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ShaderPreprocessingLocations_TextCacheAndFreshExecutionAgreeAcrossDomains(bool useRegions)
        {
            var documents = new Dictionary<(string, string), PpDocument>();
            var demand = new ShaderInputDemand(ShaderInputQuery.VertexId);
            if (useRegions)
            {
                var slice = new ShaderInputUseSlice(); slice.Regions.Add(("right", 22, 22)); demand.Include(slice);
            }
            foreach (string prefix in new[] { ConditionalLocations, ConditionalLocations + "#line 10\n", ManyConditionalLocations() })
                foreach (string suffix in new[] { "ordinary;", "#if __LINE__ == 7\nuint id : SV_VertexID;\n#endif\n",
                             "#define CAT(a,b) a ## b\n#define E(a,b) CAT(a,b)\nE(Value,__LINE__)\n" })
                {
                    string text = prefix + ProjectionPadding + "\n" + suffix;
                    var seed = LocationState("FLAG");
                    PpState Fresh()
                    {
                        var next = new PpState();
                        foreach (string name in seed.Macros.Keys) { seed.Macros.TryGetValue(name, out var binding); next.Macros[name] = binding; }
                        return next;
                    }
                    LocationRun(text, Fresh(), demand: demand, documents: documents);
                    var cached = LocationRun(text, Fresh(), demand: demand, documents: documents);
                    var fresh = LocationRun(text, Fresh(), demand: demand, reuse: false, documents: documents);
                    Assert.That(LocationFacts(cached), Is.EqualTo(LocationFacts(fresh)), text);
                }
        }

        private static string LocationFacts((PpInterpreter Interpreter, PpState State, string Issue) result) => result.Issue + "|" +
            string.Join(";", result.Interpreter.Output.SelectMany(t => t).Select(t => t.Text + ":" + t.Kind + ":" + t.Dependencies)) + "|" +
            result.Interpreter.Dependencies + "|" + result.Interpreter.Instructions + "|" + result.State.Budget.Choices + "|" +
            result.State.Budget.ExpansionCalls + "|" + result.State.Budget.DialectEffects + "|" + result.State.Budget.Remaining;

        [TestCase("#line 40 \\\n\"logical\"\n__LINE__\n", "40")]
        [TestCase("#line 40 \\\n\n__LINE__\n", "41")]
        public void ShaderPreprocessingLocations_ContinuedDirectiveAnchorsAtLastOperand(string body, string expected)
        {
            var result = LocationRun(body, abstractLocations: false);
            Assert.That(result.Issue, Is.Null);
            Assert.That(PpLexer.Spell(result.Interpreter.Output.SelectMany(t => t)), Is.EqualTo(expected));
        }

        [TestCase(false, "40")]
        [TestCase(true, "41")]
        public void ShaderPreprocessingLocations_ContinuedDirectivePreservesDistinctNativeModels(bool caching, string expected)
        {
            var result = LocationRun("#line 40 \\\n\"logical\"\n__LINE__\n", abstractLocations: false,
                dialect: caching ? PpDialect.UnityCaching : PpDialect.Standard);
            Assert.That(result.Issue, Is.Null);
            Assert.That(PpLexer.Spell(result.Interpreter.Output.SelectMany(t => t)), Is.EqualTo(expected));
        }

        [Test]
        public void ShaderPreprocessingLocations_ContinuedDirectiveAbstractProofCoversBothNativeModels()
        {
            var graph = LocationGraph("#if FLAG\n#line 10 \\\n\"same\"\n#else\n#line 12 \"same\"\n#endif\n" +
                "#if __LINE__ == 13\nuint id : SV_VertexID;\n#endif\n");
            Assert.That(graph.Issue, Is.Null);
            Assert.That(graph.InputAbsent, Is.False);
            Assert.That(graph.ExecutedDialects, Is.EqualTo(2));
            Assert.That(graph.CanIgnore("FLAG"), Is.False,
                "The Standard mapping is independent of FLAG, but the native UnityCaching mapping is not.");
        }

        [TestCase("#line 50 \"logical/file.hlsl\"\nALTO_BEGIN __LINE__ __FILE__ ALTO_END\n")]
        [TestCase("#line 10 \"first\"\n#line 75\nALTO_BEGIN __LINE__ __FILE__ ALTO_END\n")]
        [TestCase("#define LINE __LINE__\n#if 1\n#line 40 \"chosen\"\n#else\n#line 20 \"other\"\n#endif\nALTO_BEGIN LINE __FILE__ ALTO_END\n")]
        [TestCase("#line 40 \\\n\"logical\"\nALTO_BEGIN __LINE__ __FILE__ ALTO_END\n")]
        [TestCase("#line 40 \\\n\nALTO_BEGIN __LINE__ ALTO_END\n")]
        public void ShaderPreprocessingLocations_ConcreteSingletonMatchesNativePreprocessor(string body)
        {
            Assert.That(BetweenMarkers(PreprocessConcrete(body, dialect: PpDialect.UnityCaching)), Is.EqualTo(UnityPreprocessorTokens(body)));
        }
    }
}
