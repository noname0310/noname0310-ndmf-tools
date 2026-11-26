using System;
using System.Collections.Generic;

namespace Noname.AvatarTools.Analysis
{
    // Syntactic non-use is stronger than a sampled compiler DCE result. Follow
    // every overload and every identifier reference, including all global
    // initializers, without making assumptions about values or branch outcomes.
    // The caller must still cover preprocessing choices to prove whole-shader
    // absence. This slice never certifies changed runtime bytecode.
    internal static class ShaderEntryUse
    {
        internal static ShaderInputUseSlice Analyze(ShaderInputProgram program, string entry,
            ShaderInputQuery query, out bool mayRead)
        {
            mayRead = true;
            var result = new ShaderInputUseSlice();
            if (program.Issue != null) { result.Issue = program.Issue; return result; }
            if (!program.Functions.TryGetValue(entry, out var roots) || roots.Count != 1)
            { result.Issue = "shader entry is ambiguous"; return result; }
            var pending = new Queue<string>(); pending.Enqueue(entry);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            bool found = false;
            // A later variant can add a caller of a currently uncalled reader,
            // including an effectful global initializer. Retain reader/caller
            // names for discovery without treating their bodies as reachable.
            var callers = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var readers = new Queue<string>();
            foreach (var overloads in program.Functions.Values)
            foreach (var function in overloads)
            {
                for (int i = function.Header; i <= function.End; i++)
                {
                    string name = program.Tokens[i].Text;
                    if (query.QueriesIdentifier(name)) readers.Enqueue(function.Name);
                    if (!program.Functions.ContainsKey(name)) continue;
                    if (!callers.TryGetValue(name, out var parents)) callers.Add(name, parents = new HashSet<string>(StringComparer.Ordinal));
                    parents.Add(function.Name);
                }
            }
            while (readers.Count != 0)
            {
                string name = readers.Dequeue();
                if (!result.Names.Add(name)) continue;
                if (callers.TryGetValue(name, out var parents)) foreach (string parent in parents) readers.Enqueue(parent);
            }
            void Visit(int begin, int end)
            {
                var locations = new Dictionary<string, (int First, int Last)>(StringComparer.Ordinal);
                for (int i = begin; i < end; i++)
                {
                    var token = program.Tokens[i];
                    if (string.IsNullOrEmpty(token.File)) result.LocationsComplete = false;
                    else if (locations.TryGetValue(token.File, out var range))
                        locations[token.File] = (Math.Min(range.First, token.Line), Math.Max(range.Last, token.Line));
                    else locations.Add(token.File, (token.Line, token.Line));
                    if (token.Kind != PpTokenKind.Identifier) continue;
                    // Opaque assembly or dynamic linkage has no name-based proof.
                    if (token.Text == "asm" || token.Text == "interface" || token.Text == "class")
                        result.Issue = "opaque shader entry dependency";
                    found |= query.QueriesIdentifier(token.Text);
                    result.Names.Add(token.Text);
                    if (program.Functions.ContainsKey(token.Text)) pending.Enqueue(token.Text);
                }
                foreach (var pair in locations) result.Regions.Add((pair.Key, pair.Value.First, pair.Value.Last));
            }
            foreach (var initializer in program.GlobalInitializers)
            {
                // A declared variable's name is not a read. In particular Unity
                // emits static constant declarations for all engine keywords;
                // rooting their names would reopen the entire variant universe.
                int rhs = initializer.Begin;
                while (rhs < initializer.End && program.At(rhs) != "=") rhs++;
                if (rhs < initializer.End) Visit(rhs + 1, initializer.End);
            }
            while (pending.Count != 0)
            {
                string name = pending.Dequeue();
                if (!visited.Add(name)) continue;
                result.Names.Add(name);
                foreach (var function in program.Functions[name]) Visit(function.Header, function.End + 1);
            }
            result.Complete = result.Issue == null && result.LocationsComplete;
            mayRead = found || !result.Complete;
            return result;
        }
    }
}
