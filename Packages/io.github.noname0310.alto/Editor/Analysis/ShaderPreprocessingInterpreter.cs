using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;

namespace Noname.AvatarTools.Analysis
{
    // Abstract execution follows the same directive and macro-expansion semantics
    // as concrete preprocessing. Only unknown successors fork. Joins retain a
    // finite set of macro definitions; a subsequent use expands those alternatives
    // instead of unioning text from definitions that have already been overwritten.
    internal sealed class PpInterpreter
    {
        private sealed class Choice : Exception
        {
            internal readonly string Name;
            internal readonly PpBinding Binding;
            internal Choice(string name, PpBinding binding) { Name = name; Binding = binding; }
        }
        private readonly PpProgram program;
        private readonly PpDialect dialect;
        private readonly bool abstractLocations;
        private readonly ShaderInputDemand projection;
        private readonly string projectionSignature;
        private readonly bool reuseTextProjection;
        private int instructions, expansions, emitted;
        private string rootFile;
        internal readonly List<PpToken[]> Output = new();
        internal BigInteger Dependencies;
        internal bool NonlocalCounterObserved;
        internal int Instructions => instructions;
        internal PpInterpreter(PpProgram program, PpDialect dialect = PpDialect.Standard, bool abstractLocations = false,
            ShaderInputDemand projection = null, bool reuseTextProjection = true)
        {
            this.program = program; this.dialect = dialect; this.abstractLocations = abstractLocations; this.projection = projection;
            this.reuseTextProjection = reuseTextProjection; projectionSignature = projection?.Signature;
        }

        internal void Run(PpDocument document, PpState state) { rootFile = document.Id; File(document, state, BigInteger.Zero, 0); }
        private void File(PpDocument document, PpState state, BigInteger guard, int depth)
        {
            if (depth > 64) throw new PpException("Preprocessor include nesting limit exceeded: " + document.Id);
            var once = state.Once.TryGetValue(document.Id, out var value) ? value : new PpValue(0);
            if (once.Truth == true) return;
            if (!once.Truth.HasValue)
            {
                var skipped = state.Clone(); skipped.Once[document.Id] = new PpValue(1);
                var included = state.Clone(); included.Once[document.Id] = new PpValue(0);
                File(document, included, guard | once.Dependencies, depth + 1);
                state.Join(skipped, included, once.Dependencies); return;
            }
            var locations = state.Locations;
            state.Locations = PpLocations.Start(document.Id);
            try { Execute(document.Body, document.Id, state, guard, depth); }
            finally { state.Locations = locations; }
        }
        private void Emit(PpToken[] tokens, BigInteger guard)
        {
            var output = new PpToken[tokens.Length];
            for (int i = 0; i < tokens.Length; i++)
            {
                var token = output[i] = tokens[i].Taint(guard);
                if ((token.Kind == PpTokenKind.Identifier || token.Kind == PpTokenKind.Symbol) && (token.Text == "_Pragma" || token.Text == "__pragma"))
                    throw new PpException("Pragma operators require compiler preprocessing");
                Dependencies |= token.Dependencies;
            }
            if ((emitted += output.Length) > 1000000) throw new PpException("Preprocessor emitted-token limit exceeded");
            if (output.Length != 0) Output.Add(output);
        }
        private void Execute(List<PpInstruction> body, string file, PpState state, BigInteger guard, int depth)
        {
            foreach (var instruction in body)
            {
                state.Budget.Charge();
                if (++instructions > 100000) throw new PpException("Preprocessor instruction limit exceeded");
                var tokens = instruction.Tokens;
                switch (instruction.Directive)
                {
                    case null:
                        Text(instruction, state, guard);
                        break;
                    case "conditional": Conditional(instruction, 0, file, state, guard, depth); break;
                    case "define":
                        var macro = instruction.Macro ?? PpDocument.Define(tokens);
                        if (macro.Variadic)
                        {
                            state.Budget.DialectEffects |= PpDialectEffects.Variadic;
                            if (dialect == PpDialect.UnityCaching) throw new PpException("Unity variadic macros require compiler preprocessing");
                        }
                        var previous = state.Get(macro.Name);
                        if (previous.Dependencies.IsZero)
                            foreach (var definition in previous.Alternatives)
                                if (definition != null && PpBinding.Identity(definition) != PpBinding.Identity(macro))
                                    throw new PpException("Nonidentical macro redefinition: " + macro.Name);
                        state.Macros[macro.Name] = instruction.Definition ?? new PpBinding(new[] { macro }); break;
                    case "undef":
                        IdentifierOperand(tokens, "undef"); state.Macros[tokens[0].Text] = PpBinding.Undefined; break;
                    case "include":
                    case "include_with_pragmas":
                        Include(tokens, file, state, guard, depth); break;
                    case "pragma":
                        Pragma(tokens, file, state, guard); break;
                    case "line":
                        Line(tokens, state);
                        break;
                    case "error": throw new PpException("Reachable #error: " + PpLexer.Spell(tokens));
                    default: throw new PpException("Unsupported preprocessor directive: " + instruction.Directive);
                }
            }
        }
        private void Line(PpToken[] tokens, PpState state)
        {
            PpLocations joined = null;
            foreach (var operand in Expand(tokens, state))
            {
                if (operand.Length < 1 || operand.Length > 2 || operand[0].Kind != PpTokenKind.Number ||
                    !int.TryParse(operand[0].Text, NumberStyles.None, CultureInfo.InvariantCulture, out int line) || line < 0 ||
                    operand.Length == 2 && (operand[1].Kind != PpTokenKind.String || !operand[1].Text.StartsWith("\"", StringComparison.Ordinal)))
                    throw new PpException("Unresolved #line location");
                string filename = operand.Length == 2 ? operand[1].Text.Substring(1, operand[1].Text.Length - 2) : null;
                // Escaped filename decoding is compiler-specific. Preserve the
                // line fact but do not invent a logical path for slice matching
                // or __FILE__. Plain filenames have one verified representation.
                // The native models disagree on continued operands: FXC anchors
                // at the last operand, Unity's caching preprocessing at the first.
                // Record the distinction so abstract queries execute both models.
                // A trailing spliced blank line still advances the following line.
                int first = tokens[0].Line, last = tokens[tokens.Length - 1].Line;
                if (first != last) state.Budget.DialectEffects |= PpDialectEffects.ContinuedLineDirective;
                int anchor = dialect == PpDialect.UnityCaching ? first : last;
                var next = state.Locations.Remap((long)line - anchor - 1, filename, operand.Length == 2,
                    operand[0].Dependencies, operand.Length == 2 ? operand[1].Dependencies : BigInteger.Zero,
                    filename == null || !filename.Contains('\\'));
                // A surrounding unknown branch joins at its own control boundary.
                // Unconditional overwrites therefore discard old location guards.
                joined = joined == null ? next : PpLocations.Join(joined, next, BigInteger.Zero);
            }
            state.Locations = joined ?? throw new PpException("Empty #line expansion");
        }
        private void Text(PpInstruction instruction, PpState state, BigInteger guard)
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            var tokens = instruction.Tokens;
            var cache = reuseTextProjection && projection != null && tokens.Length >= 32
                ? instruction.ProjectionCache ??= new PpTextProjectionCache() : null;
            if (cache != null && cache.TrySkip(projectionSignature, state, rootFile))
            { state.Budget.TextTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started; return; }
            var observer = state.ObserveMacroRead;
            Dictionary<string, PpBinding> reads = cache == null ? null : new(StringComparer.Ordinal);
            int remaining = state.Budget.Remaining;
            if (reads != null) state.ObserveMacroRead = (name, value) => {
                if (!reads.ContainsKey(name)) reads.Add(name, value);
                observer?.Invoke(name, value);
            };
            try
            {
                var demand = projection == null ? null : new PpProjection(state, projection, state.Locations, rootFile);
                foreach (var item in Chunks(tokens, state))
                {
                    if (item.Count == 1 && demand != null && !demand.MayContribute(tokens[item.Start], item.Next)) continue;
                    var chunk = new PpToken[item.Count]; Array.Copy(tokens, item.Start, chunk, 0, item.Count);
                    if (item.Count != 1 && demand != null && !demand.MayContribute(chunk, item.Next)) continue;
                    // Only a wholly rejected block has an empty state/output
                    // transfer. Stop recording as soon as any chunk contributes.
                    reads = null; state.ObserveMacroRead = observer;
                    var first = chunk[0];
                    if (first.Kind != PpTokenKind.Identifier || state.Get(first.Text).Presence == false &&
                        first.Text != "__LINE__" && first.Text != "__FILE__" && first.Text != "__COUNTER__") Emit(chunk, guard);
                    else foreach (var output in Expand(chunk, state)) Emit(output, guard);
                }
                if (reads != null) cache.Add(projectionSignature, state.Locations, reads, remaining - state.Budget.Remaining, rootFile);
            }
            finally
            {
                state.ObserveMacroRead = observer;
                state.Budget.TextTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started;
            }
        }
        private static IEnumerable<(int Start, int Count, PpToken Next)> Chunks(PpToken[] tokens, PpState state)
        {
            // Independent macro uses do not require a Cartesian product of their
            // replacement alternatives. An identifier and all adjacent argument
            // lists form one expansion unit; retain suffix calls for rescanning
            // cases such as F(x)(y). Unmatched expansion-created calls fail closed.
            for (int cursor = 0; cursor < tokens.Length;)
            {
                int start = cursor++;
                if (tokens[start].Kind == PpTokenKind.Identifier && state.Get(tokens[start].Text).Presence != false)
                    while (cursor < tokens.Length && tokens[cursor].Text == "(")
                    {
                        int nesting = 0;
                        do
                        {
                            if (tokens[cursor].Text == "(") nesting++;
                            else if (tokens[cursor].Text == ")") nesting--;
                            cursor++;
                        } while (cursor < tokens.Length && nesting != 0);
                        if (nesting != 0) throw new PpException("Unterminated macro argument list");
                    }
                yield return (start, cursor - start, cursor < tokens.Length ? tokens[cursor] : null);
            }
        }
        private void Conditional(PpInstruction instruction, int index, string file, PpState state, BigInteger guard, int depth)
        {
            if (index == instruction.Branches.Count) return;
            var arm = instruction.Branches[index]; PpValue condition;
            if (arm.Directive == "else") condition = new PpValue(1);
            else if (arm.Directive == "ifdef" || arm.Directive == "ifndef")
            {
                IdentifierOperand(arm.Condition, arm.Directive); condition = Defined(state, arm.Condition[0].Text);
                if (arm.Directive == "ifndef" && condition.Bits.HasValue) condition = new PpValue(condition.Truth.Value ? 0UL : 1UL, condition.Dependencies);
            }
            else
            {
                var expansions = Expand(arm.Condition, state, true);
                bool allTrue = true, allFalse = true;
                BigInteger dependencies = BigInteger.Zero;
                foreach (var expanded in expansions)
                {
                    foreach (var token in expanded)
                    {
                        if (token.Text != "?") continue;
                        state.Budget.DialectEffects |= PpDialectEffects.Conditional;
                        if (dialect == PpDialect.UnityCaching) throw new PpException("Unity conditional operators require compiler preprocessing");
                        break;
                    }
                    PpValue value;
                    try { value = PpExpression.Evaluate(expanded); }
                    catch (PpException exception) { throw new PpException(exception.Message + " in " + file + ": " + PpLexer.Spell(expanded)); }
                    allTrue &= value.Truth == true; allFalse &= value.Truth == false;
                    dependencies |= value.Dependencies;
                }
                if (expansions.Length == 0) throw new PpException("Empty conditional expansion");
                bool? truth = allTrue ? true : allFalse ? false : null;
                condition = new PpValue(truth.HasValue ? truth.Value ? 1UL : 0UL : null,
                    dependencies);
            }
            if (condition.Truth == true) Execute(arm.Body, file, state, guard | condition.Dependencies, depth);
            else if (condition.Truth == false) Conditional(instruction, index + 1, file, state, guard | condition.Dependencies, depth);
            else
            {
                var yes = state.Clone(); var no = state.Clone();
                Execute(arm.Body, file, yes, guard | condition.Dependencies, depth);
                Conditional(instruction, index + 1, file, no, guard | condition.Dependencies, depth);
                state.Join(yes, no, condition.Dependencies);
            }
        }
        private static void IdentifierOperand(PpToken[] tokens, string directive)
        {
            if (tokens.Length != 1 || tokens[0].Kind != PpTokenKind.Identifier) throw new PpException("#" + directive + " requires one identifier");
        }
        private static PpValue Defined(PpBinding binding)
        {
            bool? value = binding.Presence;
            return new PpValue(value.HasValue ? value.Value ? 1UL : 0UL : null, binding.Dependencies);
        }
        private static PpValue Defined(PpState state, string name)
        {
            var binding = state.Get(name, true);
            // Availability of compiler builtins, especially __COUNTER__, differs
            // between supported preprocessors. An absent authored macro is not a
            // proof that the native builtin is undefined. Explicit undef/define
            // writes remain exact through the ordinary binding presence fact.
            return BuiltinName(name) && !state.Macros.ContainsKey(name)
                ? new PpValue(null, binding.Dependencies) : Defined(binding);
        }
        private void Include(PpToken[] tokens, string file, PpState state, BigInteger guard, int depth)
        {
            var operands = PpDocument.TryInclude(tokens, out _) ? new[] { tokens } : Expand(tokens, state);
            List<PpState> successors = null; BigInteger dependencies = guard;
            foreach (var operand in operands)
            {
                if (!PpDocument.TryInclude(operand, out string include)) throw new PpException("Unresolved macro include operand");
                var targets = program.Resolve(file, include);
                if (targets.Length == 0) throw new PpException("Unresolved include: " + include);
                var selection = BigInteger.Zero;
                foreach (var token in operand) selection |= token.Dependencies;
                dependencies |= selection;
                // A uniquely resolved include is ordinary sequential execution.
                // Fork only when alternative operands or search paths actually
                // require a join. Otherwise every header needlessly freezes and
                // copies the environment and its pragma-once state.
                if (operands.Length == 1 && targets.Length == 1)
                { File(targets[0], state, guard | selection, depth + 1); return; }
                successors ??= new List<PpState>();
                foreach (var target in targets)
                { var next = state.Clone(); File(target, next, guard | selection, depth + 1); successors.Add(next); }
            }
            if (successors == null || successors.Count == 0) throw new PpException("Include has no successor");
            var merged = successors[0];
            for (int i = 1; i < successors.Count; i++) { var next = new PpState(); next.Join(merged, successors[i], dependencies); merged = next; }
            state.Macros = merged.Macros; state.Once = merged.Once; state.Counter = merged.Counter;
        }
        private void Pragma(PpToken[] tokens, string file, PpState state, BigInteger guard)
        {
            if (tokens.Length == 0) return;
            string name = tokens[0].Text;
            if (name == "once") { state.Once[file] = new PpValue(1); return; }
            if (name.StartsWith("multi_compile", StringComparison.Ordinal) || name.StartsWith("shader_feature", StringComparison.Ordinal) ||
                name == "skip_variants" || name == "dynamic_branch" || name == "dynamic_branch_local") return;
            // Pragmas that can change macro state cannot be treated as inert text.
            if (name == "push_macro" || name == "pop_macro") throw new PpException("Macro stack pragmas require separate compiler preprocessing");
            // Entry points, target/require selectors and backend switches are part
            // of compilation identity even if they emit no ordinary HLSL tokens.
            Emit(tokens, guard);
        }
        private PpToken[][] Expand(PpToken[] tokens, PpState state, bool condition = false)
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp(); state.Budget.ExpansionCalls++;
            try { return ExpandCore(tokens, state, condition); }
            finally { state.Budget.ExpansionTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started; }
        }

        private PpToken[][] ExpandCore(PpToken[] tokens, PpState state, bool condition)
        {
            // Most expansions have no abstract choice. Allocate a worklist and
            // branch environments only after encountering multiple definitions.
            Stack<Dictionary<string, PpMacro>> pending = null;
            List<(PpToken[] Tokens, PpValue Counter)> outcomes = null;
            Dictionary<string, PpMacro> chosen = null;
            int cases = 0;
            while (true)
            {
                if (++cases > 256 || ++expansions > 100000)
                {
                    var site = tokens.FirstOrDefault();
                    throw new PpException("Preprocessor expansion alternative limit exceeded" +
                        (site == null ? "" : " at " + site.File + ":" + site.Line + " (" + site.Text + ")"));
                }
                var counter = state.Counter;
                state.Budget.Charge((chosen?.Count ?? 0) + tokens.Length);
                PpMacro Select(string name)
                {
                    var binding = state.Get(name, condition); PpMacro macro = null;
                    if (chosen == null || !chosen.TryGetValue(name, out macro))
                    { if (binding.Alternatives.Length != 1) throw new Choice(name, binding); macro = binding.Alternatives[0]; }
                    if (macro == null) return null;
                    if ((macro.Dependencies | binding.Dependencies) == macro.Dependencies) return macro;
                    return new PpMacro(macro.Name, macro.Replacement, macro.Parameters, macro.Variadic, macro.Dependencies | binding.Dependencies);
                }
                PpToken Builtin(PpToken token)
                {
                    if (token.Text == "__COUNTER__") NonlocalCounterObserved = true;
                    bool overridden = state.Macros.ContainsKey(token.Text);
                    if (!overridden && abstractLocations && BuiltinName(token.Text))
                    {
                        // Imported ShaderLab wrappers and compiler-injected headers
                        // affect locations/counters. Do not guess their initial state.
                        BigInteger dependencies = token.Dependencies;
                        if (token.Text == "__LINE__") dependencies |= state.Locations.LineDependencies;
                        if (token.Text == "__FILE__") dependencies |= state.Locations.FileDependencies;
                        if (token.Text == "__COUNTER__")
                        { dependencies |= state.Macros.Values.Aggregate(BigInteger.Zero, (d, b) => d | b.Dependencies); }
                        return new PpToken(token.Text, PpTokenKind.Symbol, token.File, token.Line, token.Space, dependencies);
                    }
                    if (!overridden && token.Text == "__LINE__") return Number(state.Locations.ReadLine(token), token.Taint(state.Locations.LineDependencies));
                    if (!overridden && token.Text == "__FILE__") return new PpToken("\"" + state.Locations.ReadFile(token).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"", PpTokenKind.String,
                        token.File, token.Line, token.Space, token.Dependencies | state.Locations.FileDependencies);
                    if (!overridden && token.Text == "__COUNTER__")
                    {
                        var result = new PpToken(counter.Bits?.ToString(CultureInfo.InvariantCulture) ?? "<counter>", counter.Bits.HasValue ? PpTokenKind.Number : PpTokenKind.Symbol,
                            token.File, token.Line, token.Space, token.Dependencies | counter.Dependencies);
                        counter = new PpValue(counter.Bits.HasValue ? counter.Bits.Value + 1 : null, counter.Dependencies); return result;
                    }
                    var binding = state.Get(token.Text, condition);
                    if (binding.Dependencies == BigInteger.Zero) return null;
                    if (Select(token.Text) == null) return token.Taint(binding.Dependencies);
                    return null;
                }
                try
                {
                    var expanded = new PpExpander(Select, Builtin, dialect, condition ? name => Defined(state, name) : null, state.Budget).Expand(tokens).ToArray();
                    if (pending == null)
                    {
                        var counterDependencies = counter.Dependencies;
                        if (!counter.Bits.HasValue)
                            foreach (var binding in state.Macros.Values) counterDependencies |= binding.Dependencies;
                        state.Counter = new PpValue(counter.Bits, counterDependencies);
                        return new[] { expanded };
                    }
                    outcomes.Add((expanded, counter));
                }
                catch (Choice choice)
                {
                    state.Budget.Choices++;
                    pending ??= new Stack<Dictionary<string, PpMacro>>();
                    outcomes ??= new List<(PpToken[], PpValue)>();
                    foreach (var alternative in choice.Binding.Alternatives)
                    {
                        var next = chosen == null ? new Dictionary<string, PpMacro>(StringComparer.Ordinal) :
                            new Dictionary<string, PpMacro>(chosen, StringComparer.Ordinal);
                        next[choice.Name] = alternative; pending.Push(next);
                    }
                }
                if (pending.Count == 0) break;
                chosen = pending.Pop();
            }
            BigInteger dependencies = outcomes.Aggregate(BigInteger.Zero, (d, o) => d | o.Counter.Dependencies);
            ulong? count = outcomes.All(o => o.Counter.Bits == outcomes[0].Counter.Bits) ? outcomes[0].Counter.Bits : null;
            if (!count.HasValue) foreach (var binding in state.Macros.Values) dependencies |= binding.Dependencies;
            state.Counter = new PpValue(count, dependencies);
            var result = new PpToken[outcomes.Count][];
            for (int i = 0; i < result.Length; i++) result[i] = outcomes[i].Tokens;
            return result;
        }
        private static PpToken Number(long value, PpToken site) => new(value.ToString(CultureInfo.InvariantCulture), PpTokenKind.Number,
            site.File, site.Line, site.Space, site.Dependencies);
        internal static bool BuiltinName(string name) => name == "__LINE__" || name == "__FILE__" || name == "__COUNTER__";
    }
}
