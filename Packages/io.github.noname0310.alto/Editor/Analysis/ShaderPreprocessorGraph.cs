using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using UnityEngine.Rendering;

namespace Noname.AvatarTools.Analysis
{
    // Dependency information is a result of preprocessing, not a substitute for
    // its semantics. Native compilation may be shared only when the entire
    // emitted program and its compilation directives are independent of a choice.
    internal sealed class ShaderPreprocessorGraph
    {
        internal sealed class Source
        {
            internal string Id, Text;
            internal Source(string id, string text) { Id = id; Text = text; }
        }
        internal string Issue { get; private set; }
        internal bool InputAbsent { get; private set; }
        internal bool IncludesWithoutKeywordPragmas { get; private set; }
        internal bool IsDeclarationProjection { get; private set; }
        internal HashSet<string> CodeDependencies { get; private set; } = new(StringComparer.Ordinal);
        internal int Nodes { get; private set; }
        internal double DiscoveryMilliseconds { get; private set; }
        internal double EvaluationMilliseconds { get; private set; }
        internal double ExpansionMilliseconds { get; private set; }
        internal double TextMilliseconds { get; private set; }
        internal double JoinMilliseconds { get; private set; }
        internal int ExpansionCalls { get; private set; }
        internal int Choices { get; private set; }
        internal int ExecutedDialects { get; private set; }
        internal int TextProjectionHits { get; private set; }
        internal PpDialectEffects DialectEffects { get; private set; }
        private static readonly HashSet<string> PlatformDefinitions = Enum.GetNames(typeof(BuiltinShaderDefine)).ToHashSet(StringComparer.Ordinal);
        internal bool CanIgnore(string keyword) => !IsDeclarationProjection && Issue == null && !CodeDependencies.Contains(keyword);

        internal static ShaderPreprocessorGraph Analyze(Source source, Func<string, string, Source[]> includes, bool addSkinning,
            IEnumerable<string> keywordNames = null, IReadOnlyDictionary<string, string> definitions = null,
            Dictionary<(string, string), PpDocument> parsedDocuments = null)
            => Analyze(source, includes, ShaderInputQuery.ForMeshChange(addSkinning), keywordNames, definitions, parsedDocuments);

        internal static ShaderPreprocessorGraph Analyze(Source source, Func<string, string, Source[]> includes, ShaderInputQuery query,
            IEnumerable<string> keywordNames = null, IReadOnlyDictionary<string, string> definitions = null,
            Dictionary<(string, string), PpDocument> parsedDocuments = null)
            => AnalyzeCore(source, includes, query, keywordNames, definitions, parsedDocuments, null);

        internal static ShaderPreprocessorGraph AnalyzeDeclarations(Source source, Func<string, string, Source[]> includes, ShaderInputQuery query,
            IEnumerable<string> keywordNames = null, IReadOnlyDictionary<string, string> definitions = null,
            Dictionary<(string, string), PpDocument> parsedDocuments = null)
            => AnalyzeCore(source, includes, query, keywordNames, definitions, parsedDocuments, new ShaderInputDemand(query));

        internal static ShaderPreprocessorGraph AnalyzeDemand(Source source, Func<string, string, Source[]> includes, ShaderInputDemand demand,
            IEnumerable<string> keywordNames = null, IReadOnlyDictionary<string, string> definitions = null,
            Dictionary<(string, string), PpDocument> parsedDocuments = null)
            => AnalyzeCore(source, includes, demand.Query, keywordNames, definitions, parsedDocuments, demand);

        private static ShaderPreprocessorGraph AnalyzeCore(Source source, Func<string, string, Source[]> includes, ShaderInputQuery query,
            IEnumerable<string> keywordNames, IReadOnlyDictionary<string, string> definitions,
            Dictionary<(string, string), PpDocument> parsedDocuments, ShaderInputDemand demand, bool repeatEquivalentDialects = false)
        {
            var result = new ShaderPreprocessorGraph { IsDeclarationProjection = demand != null };
            var timer = System.Diagnostics.Stopwatch.StartNew();
            bool evaluating = false;
            try
            {
                var program = new PpProgram(includes, parsedDocuments); var root = program.Parse(source); program.Discover(root);
                result.IncludesWithoutKeywordPragmas = program.Documents.Values.All(d => !d.MayImportKeywordPragmas &&
                    d.Includes.All(include => program.Resolve(d.Id, include).Length != 0));
                result.DiscoveryMilliseconds = timer.Elapsed.TotalMilliseconds;
                timer.Restart(); evaluating = true;
                var names = program.Documents.Values.SelectMany(d => d.ConditionalNames).ToHashSet(StringComparer.Ordinal);
                var locallyDefined = program.Documents.Values.SelectMany(d => d.DefinedNames).ToHashSet(StringComparer.Ordinal);
                // Local include guards and derived macros start undefined. Public
                // Unity/compiler inputs and imported pass keywords remain external
                // even when a header subsequently defines or undefines them.
                if (keywordNames != null) names.RemoveWhere(n => !IsCompilerInput(n) && !PlatformDefinitions.Contains(n));
                else names.ExceptWith(locallyDefined.Where(n => !IsCompilerInput(n)));
                var keywords = (keywordNames ?? Array.Empty<string>()).ToHashSet(StringComparer.Ordinal);
                names.UnionWith(keywords);
                if (definitions != null) names.ExceptWith(definitions.Keys);
                var ordered = new List<string>(); var inputs = new Dictionary<string, PpBinding>(StringComparer.Ordinal); var state = new PpState();
                PpBinding Input(string name)
                {
                    if (inputs.TryGetValue(name, out var previous)) return previous;
                    BigInteger dependency = BigInteger.One << ordered.Count; ordered.Add(name);
                    // An external keyword is an abstract numeric value with
                    // independently tracked definedness. Do not enumerate its
                    // truth assignments just to discover a data dependency.
                    var replacement = new PpToken(name, PpTokenKind.Symbol, dependencies: dependency);
                    var binding = new PpBinding(new[] { new PpMacro(name, new[] { replacement }, dependencies: dependency) }, dependency, true);
                    inputs.Add(name, binding); return binding;
                }
                foreach (string name in names.OrderBy(n => n, StringComparer.Ordinal)) state.Macros[name] = Input(name);
                // Compiler inputs may first appear through token pasting, a macro
                // include or another replacement list. Lookup must not depend on
                // whether the name happened to occur in an earlier #if directive.
                state.InitialInput = (name, condition) =>
                {
                    if (inputs.TryGetValue(name, out var input)) return input;
                    if (IsCompilerInput(name) || PlatformDefinitions.Contains(name)) return Input(name);
                    // With an explicit keyword universe, ordinary HLSL names are
                    // permanently undefined in the initial environment. Cache the
                    // answer alongside external inputs; later local definitions
                    // still take precedence in PpState.Get.
                    if (keywordNames == null && !locallyDefined.Contains(name))
                        return condition ? Input(name) : PpBinding.Undefined;
                    inputs.Add(name, PpBinding.Undefined);
                    return PpBinding.Undefined;
                };
                // Cache validation must not introduce a new abstract input or
                // change its dependency ordinal before ordinary interpretation.
                state.PeekInitialInput = name => inputs.TryGetValue(name, out var input) ? input :
                    IsCompilerInput(name) || PlatformDefinitions.Contains(name) ? null : PpBinding.Undefined;
                if (definitions != null)
                    foreach (var definition in definitions)
                        state.Macros[definition.Key] = definition.Value == null ? PpBinding.Undefined :
                            new PpBinding(new[] { new PpMacro(definition.Key, PpLexer.Lex(definition.Value).ToArray()) });
                bool hasInput = false; BigInteger dependencies = BigInteger.Zero;
                foreach (var dialect in new[] { PpDialect.Standard, PpDialect.UnityCaching })
                {
                    // Whitespace provenance cannot change directive parsing:
                    // directives are recognized before expansion. Its only
                    // value-producing consumer is macro stringification.
                    if (dialect == PpDialect.UnityCaching && !state.Budget.DialectSensitive && !repeatEquivalentDialects) break;
                    result.ExecutedDialects++;
                    var interpreter = new PpInterpreter(program, dialect, true, demand); interpreter.Run(root, state.Clone());
                    result.Nodes += interpreter.Instructions; dependencies |= interpreter.Dependencies;
                    if (interpreter.NonlocalCounterObserved) result.CodeDependencies.UnionWith(keywords);
                    foreach (var token in interpreter.Output.SelectMany(o => o))
                    {
                        if (token.Kind == PpTokenKind.Identifier || token.Kind == PpTokenKind.Symbol)
                        {
                            result.CodeDependencies.Add(token.Text);
                            hasInput |= query.MatchesToken(token.Text);
                        }
                    }
                }
                for (int i = 0; i < ordered.Count; i++)
                    if ((dependencies & (BigInteger.One << i)) != BigInteger.Zero) result.CodeDependencies.Add(ordered[i]);
                result.InputAbsent = !hasInput;
                double millisecondsPerTick = 1000d / System.Diagnostics.Stopwatch.Frequency;
                result.ExpansionMilliseconds = state.Budget.ExpansionTicks * millisecondsPerTick;
                result.TextMilliseconds = state.Budget.TextTicks * millisecondsPerTick;
                result.JoinMilliseconds = state.Budget.JoinTicks * millisecondsPerTick;
                result.ExpansionCalls = state.Budget.ExpansionCalls; result.Choices = state.Budget.Choices;
                result.TextProjectionHits = state.Budget.TextProjectionHits;
                result.DialectEffects = state.Budget.DialectEffects;
            }
            catch (Exception exception)
            { result.Issue = "preprocessor analysis incomplete: " + exception.Message; result.InputAbsent = false; }
            finally
            {
                if (evaluating) result.EvaluationMilliseconds = timer.Elapsed.TotalMilliseconds;
                else result.DiscoveryMilliseconds = timer.Elapsed.TotalMilliseconds;
            }
            return result;
        }

        private static bool IsCompilerInput(string name) => name.StartsWith("SHADER_API_", StringComparison.Ordinal) ||
            name.StartsWith("SHADER_STAGE_", StringComparison.Ordinal) || name.StartsWith("UNITY_COMPILER_", StringComparison.Ordinal) ||
            name.StartsWith("UNITY_HARDWARE_TIER", StringComparison.Ordinal) || name.StartsWith("SHADER_AVAILABLE_", StringComparison.Ordinal) ||
            name.StartsWith("SHADER_TARGET_", StringComparison.Ordinal) || name.StartsWith("__", StringComparison.Ordinal) ||
            name == "SHADER_TARGET" || name == "UNITY_VERSION";
    }
}
