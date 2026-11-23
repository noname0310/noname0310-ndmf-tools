using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor.Rendering;

namespace Noname.AvatarTools.Analysis
{
    // Follow an input through local copies to compiler-proven independent calls.
    // This is a sufficient proof, not an approximate optimizer: other consumers,
    // aggregate escapes and ambiguous effects require the general compiler path.
    internal sealed class ShaderInputUseSlice
    {
        internal readonly HashSet<string> Names = new(StringComparer.Ordinal);
        internal readonly HashSet<string> WholeValues = new(StringComparer.Ordinal);
        internal readonly List<(string Name, string File, int First, int Last)> WholeValueScopes = new();
        internal readonly List<(string File, int First, int Last)> Regions = new();
        internal bool Complete;
        internal bool LocationsComplete = true;
        internal string Issue;
        internal int Cuts;
        private readonly ShaderInputProgram program;
        private readonly ShaderInputQuery query;
        private readonly HashSet<string> fields = new(StringComparer.Ordinal);
        private readonly HashSet<string> carriers = new(StringComparer.Ordinal);
        private readonly Dictionary<ShaderInputProgram.Function, HashSet<string>> calls = new();
        private static readonly HashSet<string> Mutations = new(new[] { "=", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", ">>=", "<<=", "++", "--" }, StringComparer.Ordinal);
        private readonly string source;
        private readonly IReadOnlyDictionary<string, string> constants;
        private readonly Func<string, string, ShaderType, (D3DShaderInputs.Input[] Inputs, string Error)> compile;

        internal ShaderInputUseSlice(string source, ShaderInputProgram program, ShaderInputQuery query, string entry,
            IReadOnlyDictionary<string, string> constants,
            Func<string, string, ShaderType, (D3DShaderInputs.Input[] Inputs, string Error)> compile)
        {
            this.source = source; this.program = program; this.query = query; this.constants = constants; this.compile = compile;
            try { Analyze(entry); Complete = true; }
            catch (PpException exception) { Issue = exception.Message; }
            finally
            {
                // Cached summaries retain dependency facts, not whole tokenized
                // programs or compiler callbacks from this analysis invocation.
                this.source = null; this.program = null; this.constants = null; this.compile = null;
                calls.Clear(); fields.Clear(); carriers.Clear();
            }
        }

        private void Analyze(string entry)
        {
            if (program.Issue != null) throw new PpException(program.Issue);
            if (!program.Functions.TryGetValue(entry, out var roots) || roots.Count != 1) throw new PpException("Ambiguous shader input-query entry");
            if (query.UniformQuery) { AnalyzeUniform(entry); return; }
            for (int i = 1; i + 1 < program.Tokens.Length; i++)
                if (program.At(i) == ":" && query.MatchesToken(program.At(i + 1)))
                {
                    if (program.Tokens[i - 1].Kind != PpTokenKind.Identifier) throw new PpException("Unsupported input semantic declarator");
                    fields.Add(program.At(i - 1));
                    AddRegion(i - 1, i + 2);
                }
            Names.UnionWith(fields);
            if (fields.Count == 0) return;
            foreach (var aggregate in program.Structs)
                if (program.ContainsAny(aggregate.Value.Begin, aggregate.Value.End, fields))
                    carriers.Add(aggregate.Key);
            bool expanded;
            do
            {
                expanded = false;
                foreach (var aggregate in program.Structs)
                    if (program.ContainsAny(aggregate.Value.Begin, aggregate.Value.End, carriers))
                        expanded |= carriers.Add(aggregate.Key);
            } while (expanded);
            Names.UnionWith(carriers);
            var functions = program.Functions.Values.SelectMany(f => f).ToArray();
            var readers = new HashSet<string>(StringComparer.Ordinal);
            foreach (var function in functions)
            {
                var called = new HashSet<string>(StringComparer.Ordinal); calls[function] = called;
                for (int i = function.Begin; i < function.End; i++)
                {
                    if (program.At(i + 1) == "(" && program.Functions.ContainsKey(program.At(i))) called.Add(program.At(i));
                    if (fields.Contains(program.At(i)) && program.At(i - 1) == ".") readers.Add(function.Name);
                }
            }
            // A currently uncalled reader may acquire a caller in another variant.
            // Its name and all known caller names participate in variant discovery.
            bool changed;
            do
            {
                changed = false;
                foreach (var function in functions)
                    if (calls[function].Any(readers.Contains)) changed |= readers.Add(function.Name);
            } while (changed);
            Names.UnionWith(readers); Names.Add(entry);
            var reachable = new HashSet<string>(StringComparer.Ordinal); var pending = new Stack<string>(); pending.Push(entry);
            while (pending.Count != 0)
            {
                string name = pending.Pop(); if (!reachable.Add(name)) continue;
                foreach (var function in program.Functions[name]) foreach (string call in calls[function]) pending.Push(call);
            }
            foreach (var function in functions.Where(f => reachable.Contains(f.Name))) AnalyzeFunction(function, function == roots[0]);
        }

        private void AnalyzeUniform(string entry)
        {
            // Reflection proves absence for a sampled program. Cover the complete
            // entry call graph (including conditions and effects), then rediscover
            // it in every projected variant. This is deliberately broader than
            // the pure-function cuts used for vertex attributes.
            var pending = new Queue<string>(); pending.Enqueue(entry);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var globals = new HashSet<string>(StringComparer.Ordinal);
            foreach (var functions in program.Functions.Values)
                foreach (var function in functions)
                    for (int i = function.Header; i <= function.End; i++)
                        if (query.QueriesIdentifier(program.At(i))) { pending.Enqueue(function.Name); break; }
            while (pending.Count != 0)
            {
                string name = pending.Dequeue();
                if (!visited.Add(name)) continue;
                Names.Add(name);
                foreach (var function in program.Functions[name])
                {
                    AddRegion(function.Header, function.End + 1);
                    for (int i = function.Header; i <= function.End; i++)
                    {
                        var token = program.Tokens[i];
                        if (token.Kind != PpTokenKind.Identifier) continue;
                        Names.Add(token.Text);
                        if (program.Functions.ContainsKey(token.Text)) pending.Enqueue(token.Text);
                        if (program.GlobalDeclarations.ContainsKey(token.Text)) AddGlobal(token.Text, globals);
                    }
                }
            }
        }

        private void AnalyzeFunction(ShaderInputProgram.Function function, bool entry)
        {
            if (!function.SignatureComplete) throw new PpException("Unsupported function signature in input-use slice");
            var locals = program.LocalVariables(function);
            var objects = locals.Values.Where(p => carriers.Contains(p.Type)).Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
            WholeValues.UnionWith(objects);
            if (objects.Count != 0)
            {
                var tokens = program.Tokens.Skip(function.Header).Take(function.End + 1 - function.Header).ToArray();
                if (tokens.Any(t => string.IsNullOrEmpty(t.File))) LocationsComplete = false;
                foreach (var group in tokens.Where(t => !string.IsNullOrEmpty(t.File)).GroupBy(t => t.File))
                    foreach (string name in objects) WholeValueScopes.Add((name, group.Key, group.Min(t => t.Line), group.Max(t => t.Line)));
            }
            var aliases = new HashSet<string>(StringComparer.Ordinal);
            if (entry)
                foreach (var parameter in function.Parameters)
                    if (fields.Contains(parameter.Name) && ShaderInputProgram.IsNumericType(parameter.Type)) aliases.Add(parameter.Name);
            bool changed;
            do
            {
                changed = false;
                for (int i = function.Begin; i < function.End; i++)
                {
                    bool member = fields.Contains(program.At(i)) && program.At(i - 1) == ".";
                    bool alias = aliases.Contains(program.At(i)) && program.At(i - 1) != ".";
                    if (!member && !alias) continue;
                    if (IsWrite(i)) continue;
                    if (TryCall(function, i, out int call, out int argument)) continue;
                    if (TryCopy(function, i, locals, out string copy)) changed |= aliases.Add(copy);
                }
            } while (changed);
            Names.UnionWith(aliases);
            var checkedCalls = new HashSet<(int, int)>();
            for (int i = function.Begin; i < function.End; i++)
            {
                bool member = fields.Contains(program.At(i)) && program.At(i - 1) == ".";
                bool alias = aliases.Contains(program.At(i)) && program.At(i - 1) != ".";
                if (member || alias)
                {
                    AddRegion(StatementStart(function, i), StatementEnd(function, i));
                    if (IsWrite(i)) continue;
                    if (TryCall(function, i, out int call, out int argument))
                    {
                        if (checkedCalls.Add((call, argument))) ProveCall(function, call, argument, locals);
                        continue;
                    }
                    if (TryCopy(function, i, locals, out _)) continue;
                    throw new PpException("Input escapes the proved local-use slice in " + function.Name);
                }
                if (!objects.Contains(program.At(i)) || program.At(i - 1) == "." || program.At(i + 1) == ".") continue;
                // Whole aggregates may be passed to inspectable functions. A cast,
                // return or opaque aggregate operation can expose an unseen member.
                if (IsWrite(i) || IsDeclaration(i, locals)) continue;
                if (!TryCall(function, i, out int target, out int index) || !program.Functions.TryGetValue(program.At(target), out var callees) ||
                    callees.Any(f => !f.SignatureComplete || index >= f.Parameters.Length || !carriers.Contains(f.Parameters[index].Type)))
                    throw new PpException("Unverified aggregate escape in input-use slice: " + function.Name);
                Names.Add(program.At(target)); AddRegion(target, program.Partner[target + 1] + 1);
            }
        }

        private bool IsDeclaration(int index, Dictionary<string, ShaderInputProgram.Parameter> locals) =>
            locals.TryGetValue(program.At(index), out var variable) && program.At(index - 1) == variable.Type;

        private bool IsWrite(int index)
        {
            int next = index + 1;
            while (program.At(next) == "[") next = program.Partner[next] + 1;
            return program.At(next) == "=";
        }

        private bool TryCopy(ShaderInputProgram.Function function, int use, Dictionary<string, ShaderInputProgram.Parameter> locals, out string name)
        {
            name = null; int start = StatementStart(function, use), end = StatementEnd(function, use);
            int assignment = start;
            while (assignment < use && program.At(assignment) != "=") assignment++;
            if (assignment >= use) return false;
            int root = program.LValueRoot(assignment - 1);
            if (root != assignment - 1 || !locals.TryGetValue(program.At(root), out var variable) ||
                !ShaderInputProgram.IsNumericType(variable.Type) || variable.ArrayLength != 0) return false;
            // Only value expressions without calls, writes or sequencing are copied.
            // Other expressions might perform observable work before a dead call.
            for (int i = assignment + 1; i < end; i++)
                if (Mutations.Contains(program.At(i)) || program.At(i) == "," ||
                    program.At(i + 1) == "(" && program.Tokens[i].Kind == PpTokenKind.Identifier && !ShaderInputProgram.IsNumericType(program.At(i))) return false;
            name = variable.Name; return true;
        }

        private bool TryCall(ShaderInputProgram.Function function, int use, out int call, out int argument)
        {
            for (int i = use - 1; i >= function.Begin; i--)
            {
                if (program.At(i) != "(" || program.Partner[i] <= use || program.Tokens[i - 1].Kind != PpTokenKind.Identifier) continue;
                string name = program.At(i - 1);
                if (new[] { "if", "while", "for", "switch", "return" }.Contains(name) ||
                    !program.Functions.ContainsKey(name) && ShaderInputFunctionProof.IsPureIntrinsic(name)) continue;
                int ordinal = 0;
                foreach (var span in program.Split(i + 1, program.Partner[i]))
                {
                    if (span.Begin <= use && use < span.End) { call = i - 1; argument = ordinal; return true; }
                    ordinal++;
                }
            }
            call = argument = -1; return false;
        }

        private void ProveCall(ShaderInputProgram.Function caller, int call, int argument, Dictionary<string, ShaderInputProgram.Parameter> locals)
        {
            if (!program.Functions.TryGetValue(program.At(call), out var candidates) || candidates.Count != 1)
                throw new PpException("Unknown or ambiguous input consumer");
            var callee = candidates[0];
            var args = program.Split(call + 2, program.Partner[call + 1]).ToArray();
            if (!callee.SignatureComplete || args.Length != callee.Parameters.Length) throw new PpException("Unverified input consumer arguments");
            if (program.ContainsAny(args[argument].Begin, args[argument].End, Mutations))
                throw new PpException("Observable argument evaluation in input-use slice");
            var values = new Dictionary<int, string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (i == argument) continue;
                if (Literal(args[i].Begin, args[i].End, locals, out string literal)) values[i] = literal;
                else if (args[i].End == args[i].Begin + 1 && ArrayLiteral(caller, program.At(args[i].Begin), locals, out literal)) values[i] = literal;
                foreach (var token in program.Tokens.Skip(args[i].Begin).Take(args[i].End - args[i].Begin))
                    if (token.Kind == PpTokenKind.Identifier) Names.Add(token.Text);
            }
            var proof = ShaderInputFunctionProof.Analyze(source, program, callee, argument, values, compile);
            if (!proof.Independent) throw new PpException(proof.Issue ?? "Input consumer may depend on the queried value: " + callee.Name);
            Cuts++; Names.UnionWith(proof.Functions); Names.UnionWith(proof.GlobalReads);
            foreach (string name in proof.GlobalReads) AddGlobal(name, new HashSet<string>(StringComparer.Ordinal));
            foreach (string name in proof.Functions)
                foreach (var function in program.Functions[name]) AddRegion(function.Header, function.End + 1);
        }

        private void AddGlobal(string name, HashSet<string> seen)
        {
            if (!seen.Add(name)) return;
            if (!program.GlobalDeclarations.TryGetValue(name, out var declaration))
                throw new PpException("Unverified global declaration in input-use slice: " + name);
            Names.Add(name); AddRegion(declaration.Begin, declaration.End);
            foreach (var token in program.Tokens.Skip(declaration.Begin).Take(declaration.End - declaration.Begin))
            {
                if (program.Functions.ContainsKey(token.Text)) throw new PpException("Unverified global initializer call in input-use slice");
                if (program.GlobalDeclarations.ContainsKey(token.Text)) AddGlobal(token.Text, seen);
            }
        }

        private bool Literal(int begin, int end, Dictionary<string, ShaderInputProgram.Parameter> locals, out string literal)
        {
            literal = null; var parts = new List<string>();
            for (int i = begin; i < end; i++)
            {
                var token = program.Tokens[i];
                if (token.Kind == PpTokenKind.Number || new[] { "true", "false", "-", "+", "{", "}", "," }.Contains(token.Text)) parts.Add(token.Text);
                else if (!locals.ContainsKey(token.Text) && constants != null && constants.TryGetValue(token.Text, out string value))
                {
                    Names.Add(token.Text); parts.Add(value);
                    if (program.GlobalDeclarations.TryGetValue(token.Text, out var declaration)) AddRegion(declaration.Begin, declaration.End);
                    else LocationsComplete = false;
                }
                else return false;
            }
            if (parts.Count == 0) return false;
            literal = string.Join(" ", parts); return true;
        }

        private bool ArrayLiteral(ShaderInputProgram.Function function, string name, Dictionary<string, ShaderInputProgram.Parameter> locals, out string literal)
        {
            literal = null;
            if (!locals.TryGetValue(name, out var local) || local.ArrayLength == 0) return false;
            int initializer = -1, declarationStart = -1;
            for (int i = function.Begin; i < function.End; i++)
            {
                if (program.At(i) != name) continue;
                if (IsDeclaration(i, locals) && program.At(i + 1) == "[")
                {
                    int assign = program.Partner[i + 1] + 1;
                    if (initializer >= 0 || program.At(assign) != "=" || program.At(assign + 1) != "{") return false;
                    initializer = assign + 1; declarationStart = i - 1; continue;
                }
                if (!TryCall(function, i, out int call, out int argument)) return false;
                if (!program.Functions.TryGetValue(program.At(call), out var callees) ||
                    callees.Any(f => !f.SignatureComplete || argument >= f.Parameters.Length || f.Parameters[argument].Output)) return false;
            }
            if (initializer < 0) return false;
            AddRegion(declarationStart, program.Partner[initializer] + 1);
            return Literal(initializer, program.Partner[initializer] + 1, locals, out literal);
        }

        private int StatementStart(ShaderInputProgram.Function function, int index)
        {
            while (index > function.Begin && program.At(index - 1) != ";" && program.At(index - 1) != "{" && program.At(index - 1) != "}") index--;
            return index;
        }
        private int StatementEnd(ShaderInputProgram.Function function, int index)
        {
            while (index < function.End && program.At(index) != ";" && program.At(index) != "}") index++;
            return Math.Min(index + 1, function.End);
        }
        private void AddRegion(int begin, int end)
        {
            if (program.Tokens.Skip(begin).Take(end - begin).Any(t => string.IsNullOrEmpty(t.File))) LocationsComplete = false;
            foreach (var group in program.Tokens.Skip(begin).Take(end - begin).Where(t => !string.IsNullOrEmpty(t.File)).GroupBy(t => t.File))
                Regions.Add((group.Key, group.Min(t => t.Line), group.Max(t => t.Line)));
        }
    }
}
