using System;
using System.Collections.Generic;

namespace Noname.AvatarTools.Analysis
{
    // A may-emit summary for one immutable macro environment. It deliberately
    // forgets correlations: irrelevant uses can be rejected without enumerating
    // combinations, while any possible match goes through ordinary expansion.
    // This proves token absence only; it is not a compiled-use equivalence proof.
    internal sealed class PpProjection
    {
        private readonly PpState state;
        private readonly ShaderInputDemand demand;
        private readonly Func<PpToken, PpToken> location;
        private readonly Dictionary<(string Token, string Next, string File, int Line), bool> cache = new();
        private readonly HashSet<string> recursion = new(StringComparer.Ordinal);
        private PpToken invocation;
        internal PpProjection(PpState state, ShaderInputDemand demand, Func<PpToken, PpToken> location = null)
        { this.state = state; this.demand = demand; this.location = location; }

        internal bool MayContribute(PpToken token, PpToken following = null)
        {
            // Ordinary non-macro tokens dominate HLSL text. Reject these without
            // allocating an expansion array, recursion set or source-location key.
            var located = location == null ? token : location(token);
            if (demand.Matches(located, following, located)) return true;
            if (token.Text == "_Pragma" || token.Text == "__pragma" || token.Text == "__COUNTER__") return true;
            if (token.Kind != PpTokenKind.Identifier || state.Get(token.Text).Presence == false) return false;
            return MayContribute(new[] { token }, following);
        }

        internal bool MayContribute(PpToken[] tokens, PpToken following = null)
        {
            if (location != null)
            {
                var located = new PpToken[tokens.Length];
                for (int i = 0; i < tokens.Length; i++) located[i] = location(tokens[i]);
                tokens = located;
            }
            invocation = tokens.Length == 0 ? null : tokens[0];
            // Macro arguments are observed after substitution. An unused argument,
            // or an aggregate substituted into a member access, is not a whole read.
            if (tokens.Length != 0 && demand.Matches(tokens[0], tokens.Length > 1 ? tokens[1] : following))
                return true;
            if (tokens.Length == 1 && tokens[0].Kind == PpTokenKind.Identifier)
            {
                var key = (tokens[0].Text, following?.Text, invocation.File, invocation.Line);
                if (cache.TryGetValue(key, out bool previous)) return previous;
                bool result;
                try { result = Inspect(tokens, following, recursion, 0); }
                finally { recursion.Clear(); }
                cache.Add(key, result); return result;
            }
            try { return Inspect(tokens, following, recursion, 0); }
            finally { recursion.Clear(); }
        }

        private bool Inspect(PpToken[] tokens, PpToken following, HashSet<string> active, int depth)
        {
            if (depth > 64) return true;
            for (int cursor = 0; cursor < tokens.Length; cursor++)
            {
                state.Budget.Charge(); var token = tokens[cursor];
                PpToken next = cursor + 1 < tokens.Length ? tokens[cursor + 1] : following;
                if (demand.Matches(token, next, invocation)) return true;
                if (token.Text == "_Pragma" || token.Text == "__pragma" || token.Text == "__COUNTER__") return true;
                if (token.Kind != PpTokenKind.Identifier) continue;
                var binding = state.Get(token.Text);
                if (binding.Presence == false) continue;
                if (!active.Add(token.Text)) return true;
                int consumedByAll = int.MaxValue;
                foreach (var macro in binding.Alternatives)
                {
                    if (macro == null) { consumedByAll = cursor; continue; }
                    if (macro.Variadic || macro.Stringifies) return true;
                    if (!macro.Function && (cursor + 1 == tokens.Length || tokens[cursor + 1].Text != "("))
                    {
                        // Object-like replacement lists are immutable. With no
                        // invocation suffix, rescanning needs no copied list.
                        if (macro.Pastes ? InspectPastes(macro.Replacement, next, active, depth + 1) :
                            Inspect(macro.Replacement, next, active, depth + 1)) return true;
                        consumedByAll = Math.Min(consumedByAll, cursor);
                        continue;
                    }
                    var replacement = new List<PpToken>(macro.Replacement); int consumed = cursor;
                    if (macro.Function)
                    {
                        if (cursor + 1 >= tokens.Length || tokens[cursor + 1].Text != "(") { consumedByAll = cursor; continue; }
                        var args = new List<PpToken[]>(); int start = cursor + 2, nesting = 0, close = start;
                        for (; close < tokens.Length; close++)
                        {
                            if (tokens[close].Text == "(") nesting++;
                            else if (tokens[close].Text == ")") { if (nesting == 0) break; nesting--; }
                            if (tokens[close].Text == "," && nesting == 0) { args.Add(Copy(tokens, start, close - start)); start = close + 1; }
                        }
                        if (close == tokens.Length) return true;
                        if (start != close || args.Count != 0 || macro.Parameters.Length != 0) args.Add(Copy(tokens, start, close - start));
                        if (args.Count != macro.Parameters.Length) return true;
                        consumed = close;
                        var parameters = new Dictionary<string, PpToken[]>(macro.Parameters.Length, StringComparer.Ordinal);
                        for (int i = 0; i < macro.Parameters.Length; i++) parameters.Add(macro.Parameters[i], args[i]);
                        replacement.Clear();
                        foreach (var tokenToReplace in macro.Replacement)
                            if (tokenToReplace.Kind == PpTokenKind.Identifier && parameters.TryGetValue(tokenToReplace.Text, out var arg)) replacement.AddRange(arg);
                            else replacement.Add(tokenToReplace);
                    }
                    // Rescan an expansion-generated function name together with
                    // its literal argument suffix, without multiplying independent
                    // macro choices elsewhere in the replacement.
                    int suffix = consumed + 1;
                    while (suffix < tokens.Length && tokens[suffix].Text == "(")
                    {
                        int nesting = 0;
                        do
                        {
                            if (tokens[suffix].Text == "(") nesting++;
                            else if (tokens[suffix].Text == ")") nesting--;
                            replacement.Add(tokens[suffix++]);
                        } while (suffix < tokens.Length && nesting != 0);
                        if (nesting != 0) return true;
                    }
                    PpToken after = suffix < tokens.Length ? tokens[suffix] : following;
                    if (InspectPastes(replacement.ToArray(), after, active, depth + 1)) return true;
                    consumedByAll = Math.Min(consumedByAll, suffix - 1);
                }
                active.Remove(token.Text);
                if (consumedByAll != int.MaxValue) cursor = consumedByAll;
            }
            return false;
        }

        private bool InspectPastes(PpToken[] tokens, PpToken following, HashSet<string> active, int depth)
        {
            if (depth > 64) return true;
            int paste = Array.FindIndex(tokens, t => t.Text == "##");
            if (paste < 0) return Inspect(tokens, following, active, depth);
            if (paste == 0 || paste + 1 == tokens.Length) return true;
            // Standard and caching preprocessors differ in argument prescan. Keep
            // both raw and expanded atoms; joining choices only at this paste site
            // avoids multiplying unrelated replacement lists in the whole program.
            var left = Atoms(tokens[paste - 1], new HashSet<string>(StringComparer.Ordinal), 0);
            var right = Atoms(tokens[paste + 1], new HashSet<string>(StringComparer.Ordinal), 0);
            if (left == null || right == null || left.Count * right.Count > 64) return true;
            foreach (string a in left)
                foreach (string b in right)
                {
                    var joined = PpLexer.Lex(a + b);
                    if (joined.Count != 1) return true;
                    var replacement = new PpToken[tokens.Length - 2];
                    Array.Copy(tokens, 0, replacement, 0, paste - 1);
                    replacement[paste - 1] = joined[0];
                    Array.Copy(tokens, paste + 2, replacement, paste, tokens.Length - paste - 2);
                    if (InspectPastes(replacement, following, active, depth + 1)) return true;
                }
            return false;
        }

        private static PpToken[] Copy(PpToken[] tokens, int start, int count)
        {
            var result = new PpToken[count];
            Array.Copy(tokens, start, result, 0, count); return result;
        }

        private HashSet<string> Atoms(PpToken token, HashSet<string> active, int depth)
        {
            state.Budget.Charge();
            if (depth > 32 || token.Kind == PpTokenKind.Symbol) return null;
            var result = new HashSet<string>(StringComparer.Ordinal) { token.Text };
            if (token.Kind != PpTokenKind.Identifier) return result;
            var binding = state.Get(token.Text);
            if (binding.Presence == false) return result;
            if (!active.Add(token.Text)) return null;
            foreach (var macro in binding.Alternatives)
            {
                if (macro == null) continue;
                if (macro.Function || macro.Replacement.Length != 1) return null;
                var values = Atoms(macro.Replacement[0], active, depth + 1);
                if (values == null) return null;
                result.UnionWith(values);
                if (result.Count > 64) return null;
            }
            active.Remove(token.Text); return result;
        }
    }
}
