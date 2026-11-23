using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Noname.AvatarTools.Analysis
{
    internal sealed class PpBinding
    {
        internal readonly PpMacro[] Alternatives;
        internal readonly BigInteger Dependencies;
        internal readonly bool AbstractInput;
        internal readonly bool? Presence;
        internal PpBinding(PpMacro[] alternatives, BigInteger dependencies = default, bool abstractInput = false)
        {
            Alternatives = alternatives; Dependencies = dependencies; AbstractInput = abstractInput;
            Presence = abstractInput ? null : alternatives.All(m => m != null) ? true : alternatives.All(m => m == null) ? false : null;
        }
        internal static readonly PpBinding Undefined = new(new PpMacro[] { null });
        internal static string Identity(PpMacro macro) => macro?.Identity ?? "<undefined>";
        internal static PpBinding Join(PpBinding a, PpBinding b, BigInteger guard)
        {
            if (ReferenceEquals(a, b)) return a;
            if (a.Alternatives.Length == 1 && b.Alternatives.Length == 1 && Identity(a.Alternatives[0]) == Identity(b.Alternatives[0]))
                return new PpBinding(a.Alternatives, a.Dependencies | b.Dependencies | (a.Presence == b.Presence ? BigInteger.Zero : guard),
                    a.AbstractInput || b.AbstractInput);
            var seen = new HashSet<string>(StringComparer.Ordinal); var right = new HashSet<string>(StringComparer.Ordinal);
            var merged = new List<PpMacro>(a.Alternatives.Length + b.Alternatives.Length);
            foreach (var macro in a.Alternatives)
            {
                if (!seen.Add(Identity(macro))) throw new ArgumentException("Duplicate macro alternatives");
                merged.Add(macro);
            }
            bool same = a.Presence == b.Presence && a.Alternatives.Length == b.Alternatives.Length;
            foreach (var macro in b.Alternatives)
            {
                string identity = Identity(macro);
                if (!right.Add(identity)) throw new ArgumentException("Duplicate macro alternatives");
                if (seen.Add(identity)) { merged.Add(macro); same = false; }
            }
            return new PpBinding(merged.ToArray(),
                a.Dependencies | b.Dependencies | (same ? BigInteger.Zero : guard), a.AbstractInput || b.AbstractInput);
        }
    }

    internal sealed class PpState
    {
        internal PpEnvironment Macros;
        internal Dictionary<string, PpValue> Once;
        internal PpValue Counter;
        internal int LineOffset;
        internal string LogicalFile;
        internal PpBudget Budget;
        internal Func<string, bool, PpBinding> InitialInput;
        internal Func<string, PpBinding> PeekInitialInput;
        internal Action<string, PpBinding> ObserveMacroRead;
        internal PpState() { Budget = new PpBudget(); Macros = new PpEnvironment(Budget); Once = new(StringComparer.Ordinal); Counter = new PpValue(0); }
        private PpState(PpState source)
        {
            Budget = source.Budget; Macros = source.Macros.Fork();
            Once = new Dictionary<string, PpValue>(source.Once, StringComparer.Ordinal);
            Counter = source.Counter; LineOffset = source.LineOffset; LogicalFile = source.LogicalFile; InitialInput = source.InitialInput;
            ObserveMacroRead = source.ObserveMacroRead;
            PeekInitialInput = source.PeekInitialInput;
        }
        internal PpState Clone()
        {
            Budget.Charge(1 + Once.Count);
            return new PpState(this);
        }
        internal PpBinding Get(string name, bool condition = false)
        {
            var value = Macros.TryGetValue(name, out var binding) ? binding : InitialInput?.Invoke(name, condition) ?? PpBinding.Undefined;
            ObserveMacroRead?.Invoke(name, value);
            return value;
        }
        internal PpBinding Peek(string name) => Macros.TryGetValue(name, out var binding) ? binding :
            PeekInitialInput != null ? PeekInitialInput(name) : InitialInput == null ? PpBinding.Undefined : null;
        internal void Join(PpState a, PpState b, BigInteger guard)
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            Budget = a.Budget; InitialInput = a.InitialInput; Budget.Charge(a.Once.Count + b.Once.Count);
            Macros = PpEnvironment.Join(a.Macros, b.Macros, k => PpBinding.Join(a.Get(k), b.Get(k), guard));
            Once = new Dictionary<string, PpValue>(a.Once.Count + b.Once.Count, StringComparer.Ordinal);
            foreach (var pair in a.Once)
                Once.Add(pair.Key, JoinValue(pair.Value, b.Once.TryGetValue(pair.Key, out var value) ? value : new PpValue(0), guard));
            foreach (var pair in b.Once)
                if (!Once.ContainsKey(pair.Key)) Once.Add(pair.Key, JoinValue(new PpValue(0), pair.Value, guard));
            Counter = JoinValue(a.Counter, b.Counter, guard);
            if (a.LineOffset != b.LineOffset || a.LogicalFile != b.LogicalFile)
                throw new PpException("Conditional #line state cannot be represented by one source location");
            LineOffset = a.LineOffset; LogicalFile = a.LogicalFile;
            Budget.JoinTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started;
        }
        private static PpValue JoinValue(PpValue a, PpValue b, BigInteger guard) => new(a.Bits == b.Bits ? a.Bits : null,
            a.Dependencies | b.Dependencies | (a.Bits.HasValue && a.Bits == b.Bits ? BigInteger.Zero : guard));
    }

    internal sealed class PpInstruction
    {
        internal string Directive;
        internal PpToken[] Tokens;
        internal PpMacro Macro;
        internal PpBinding Definition;
        internal PpTextProjectionCache ProjectionCache;
        internal readonly List<(string Directive, PpToken[] Condition, List<PpInstruction> Body)> Branches = new();
    }

    // Token parsing is shared by concrete preprocessing and abstract execution.
    // Directives are recognized before expansion; macro output cannot introduce one.
    internal sealed class PpDocument
    {
        internal readonly string Id;
        internal readonly List<PpInstruction> Body;
        internal readonly HashSet<string> ConditionalNames = new(StringComparer.Ordinal), DefinedNames = new(StringComparer.Ordinal);
        internal readonly List<string> Includes = new();
        internal bool MayImportKeywordPragmas;
        internal int TokenCount;
        internal PpDocument(string id, string source)
        {
            Id = id;
            // Non-backslash trigraphs inside comments cannot affect tokenization.
            // A trigraph backslash can splice lines before comments are recognized.
            if (source.Contains("??/")) throw new PpException("Trigraph line splicing requires compiler-specific preprocessing in " + id);
            string uncommented = ShaderPropertyUsage.StripComments(source);
            for (int i = 0; i + 2 < uncommented.Length; i++)
                if (uncommented[i] == '?' && uncommented[i + 1] == '?' && "=/'()!<>-".IndexOf(uncommented[i + 2]) >= 0)
                    throw new PpException("Trigraph spelling requires compiler-specific preprocessing in " + id);
            var tokens = PpLexer.Lex(source, id); TokenCount = tokens.Count;
            var lines = new List<PpToken[]>(); int start = 0;
            for (int i = 0; i <= tokens.Count; i++)
                if (i == tokens.Count || tokens[i].Kind == PpTokenKind.Newline)
                { lines.Add(tokens.Skip(start).Take(i - start).ToArray()); start = i + 1; }
            int cursor = 0; Body = Parse(lines, ref cursor, false);
        }
        private List<PpInstruction> Parse(List<PpToken[]> lines, ref int cursor, bool nested)
        {
            var result = new List<PpInstruction>(); var text = new List<PpToken>();
            void Flush() { if (text.Count != 0) { result.Add(new PpInstruction { Tokens = text.ToArray() }); text.Clear(); } }
            while (cursor < lines.Count)
            {
                var line = lines[cursor];
                if (line.Length == 0 || line[0].Text != "#")
                {
                    if (line.Length != 0) { text.Add(line[0].WithSpace(text.Count != 0 || line[0].Space)); text.AddRange(line.Skip(1)); }
                    cursor++; continue;
                }
                Flush();
                if (line.Length == 1) { cursor++; continue; }
                string directive = line[1].Text;
                if (directive == "elif" || directive == "else" || directive == "endif")
                { if (!nested) throw new PpException("Unmatched #" + directive + " in " + Id); return result; }
                cursor++; var operands = line.Skip(2).ToArray();
                if (directive == "if" || directive == "ifdef" || directive == "ifndef")
                {
                    var instruction = new PpInstruction { Directive = "conditional", Tokens = line }; bool seenElse = false;
                    while (true)
                    {
                        ConditionalNames.UnionWith(operands.Where(t => t.Kind == PpTokenKind.Identifier && t.Text != "defined").Select(t => t.Text));
                        instruction.Branches.Add((directive, operands, Parse(lines, ref cursor, true)));
                        if (cursor == lines.Count) throw new PpException("Unterminated conditional in " + Id);
                        line = lines[cursor++]; directive = line[1].Text; operands = line.Skip(2).ToArray();
                        if (directive == "endif") { if (operands.Length != 0) throw new PpException("Extra #endif operands"); break; }
                        if (seenElse) throw new PpException("Conditional branch after #else");
                        if (directive == "else") { seenElse = true; if (operands.Length != 0) throw new PpException("Extra #else operands"); }
                    }
                    result.Add(instruction); continue;
                }
                if (directive == "define" || directive == "undef")
                    if (operands.Length != 0) DefinedNames.Add(operands[0].Text);
                PpMacro definition = null;
                if (directive == "define")
                {
                    var macro = definition = Define(operands);
                    ConditionalNames.UnionWith(macro.Replacement.Where(t => t.Kind == PpTokenKind.Identifier &&
                        t.Text != "defined" && (macro.Parameters == null || !macro.Parameters.Contains(t.Text))).Select(t => t.Text));
                }
                if (directive == "include" || directive == "include_with_pragmas")
                {
                    bool literal = TryInclude(operands, out string include);
                    if (literal) Includes.Add(include);
                    MayImportKeywordPragmas |= directive == "include_with_pragmas" || !literal;
                }
                result.Add(new PpInstruction { Directive = directive, Tokens = operands, Macro = definition,
                    Definition = definition == null ? null : new PpBinding(new[] { definition }) });
            }
            Flush(); return result;
        }
        internal static bool TryInclude(IReadOnlyList<PpToken> tokens, out string include)
        {
            include = null;
            if (tokens.Count == 1 && tokens[0].Kind == PpTokenKind.String && tokens[0].Text.StartsWith("\"", StringComparison.Ordinal))
                include = tokens[0].Text.Substring(1, tokens[0].Text.Length - 2);
            else if (tokens.Count >= 3 && tokens[0].Text == "<" && tokens[tokens.Count - 1].Text == ">")
                include = string.Concat(tokens.Skip(1).Take(tokens.Count - 2).Select(t => t.Text));
            return !string.IsNullOrEmpty(include);
        }
        internal static PpMacro Define(PpToken[] tokens)
        {
            if (tokens.Length == 0 || tokens[0].Kind != PpTokenKind.Identifier) throw new PpException("Macro definition requires an identifier");
            string name = tokens[0].Text; string[] parameters = null; bool variadic = false; int cursor = 1;
            if (cursor < tokens.Length && tokens[cursor].Text == "(" && !tokens[cursor].Space)
            {
                cursor++; var names = new List<string>();
                while (cursor < tokens.Length && tokens[cursor].Text != ")")
                {
                    var token = tokens[cursor++];
                    if (token.Text == "...") { names.Add("__VA_ARGS__"); variadic = true; break; }
                    if (token.Kind != PpTokenKind.Identifier || names.Contains(token.Text)) throw new PpException("Invalid macro parameter list: " + name);
                    names.Add(token.Text);
                    if (cursor < tokens.Length && tokens[cursor].Text == ")") break;
                    if (cursor == tokens.Length || tokens[cursor++].Text != ",") throw new PpException("Invalid macro parameter separator: " + name);
                    if (cursor == tokens.Length || tokens[cursor].Text == ")") throw new PpException("Trailing macro parameter separator: " + name);
                }
                if (cursor == tokens.Length || tokens[cursor++].Text != ")") throw new PpException("Unterminated macro parameter list: " + name);
                parameters = names.ToArray();
            }
            var replacement = tokens.Skip(cursor).ToArray();
            if (replacement.Length != 0)
            {
                replacement[0] = replacement[0].WithSpace(false);
                if (replacement[0].Text == "##" || replacement[replacement.Length - 1].Text == "##") throw new PpException("Paste at macro replacement boundary: " + name);
            }
            return new PpMacro(name, replacement, parameters, variadic);
        }
    }

    internal sealed class PpProgram
    {
        internal readonly Dictionary<string, PpDocument> Documents = new(StringComparer.Ordinal);
        private readonly Func<string, string, ShaderPreprocessorGraph.Source[]> includes;
        private readonly Dictionary<(string, string), PpDocument> parsed;
        private readonly Dictionary<(string, string), PpDocument[]> resolved = new();
        private int tokens;
        internal PpProgram(Func<string, string, ShaderPreprocessorGraph.Source[]> includes, Dictionary<(string, string), PpDocument> parsed = null)
        { this.includes = includes; this.parsed = parsed ?? new Dictionary<(string, string), PpDocument>(); }
        internal PpDocument Parse(ShaderPreprocessorGraph.Source source)
        {
            if (source?.Text == null) throw new PpException("Unavailable preprocessor source");
            if (Documents.TryGetValue(source.Id, out var document)) return document;
            if (Documents.Count >= 512) throw new PpException("Preprocessor file limit exceeded");
            var key = (source.Id, source.Text);
            if (!parsed.TryGetValue(key, out document)) parsed.Add(key, document = new PpDocument(source.Id, source.Text));
            Documents.Add(source.Id, document);
            if ((tokens += document.TokenCount) > 1000000) throw new PpException("Preprocessor source token limit exceeded");
            return document;
        }
        internal PpDocument[] Resolve(string origin, string include)
        {
            var key = (origin, include);
            if (!resolved.TryGetValue(key, out var documents))
                resolved.Add(key, documents = (includes?.Invoke(origin, include) ?? Array.Empty<ShaderPreprocessorGraph.Source>()).Select(Parse).ToArray());
            return documents;
        }
        internal void Discover(PpDocument document)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal); var pending = new Stack<PpDocument>(); pending.Push(document);
            while (pending.Count != 0)
            {
                document = pending.Pop(); if (!visited.Add(document.Id)) continue;
                foreach (string include in document.Includes)
                    foreach (var target in Resolve(document.Id, include)) pending.Push(target);
            }
        }
    }
}
