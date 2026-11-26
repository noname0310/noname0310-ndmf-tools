using System;
using System.Collections.Generic;
using System.Linq;

namespace Noname.AvatarTools.Analysis
{
    // This proof inspects the original native-preprocessed entry interface, not
    // arithmetic or a specialized source program. An unbound system/vertex
    // semantic cannot enter that invocation. Variant coverage remains the
    // caller's responsibility; aliases and unsupported interfaces return unknown.
    internal static class ShaderEntryInputs
    {
        private static readonly HashSet<string> FieldQualifiers = new(new[]
        { "linear", "centroid", "nointerpolation", "noperspective", "sample", "precise", "row_major", "column_major" }, StringComparer.Ordinal);

        internal static bool Absent(string source, string entry, ShaderInputQuery query)
        {
            if (query.UniformQuery || query.Semantics.Length == 0 || string.IsNullOrEmpty(entry) || string.IsNullOrEmpty(source)) return false;
            var program = new ShaderInputProgram(source);
            if (program.Issue != null || !program.Functions.TryGetValue(entry, out var entries) || entries.Count != 1 ||
                !entries[0].SignatureComplete || program.GlobalIdentifiers.Any(query.MatchesToken)) return false;
            var function = entries[0];
            bool indexed = query.Semantics.Any(s => s.Index.HasValue);
            if (indexed && function.Parameters.Any(p => p.ArrayLength != 0)) return false;
            for (int i = function.Header; i < function.Begin; i++) if (query.MatchesToken(program.At(i))) return false;
            var verified = new HashSet<string>(StringComparer.Ordinal); var active = new HashSet<string>(StringComparer.Ordinal);
            return function.Parameters.All(p => TypeAbsent(p.Type));

            bool TypeAbsent(string type)
            {
                if (ShaderInputProgram.IsNumericType(type)) return !indexed || !type.Contains("x") && !type.StartsWith("double", StringComparison.Ordinal);
                if (verified.Contains(type)) return true;
                if (active.Count >= 16 || !active.Add(type) || !program.Structs.TryGetValue(type, out var body)) return false;
                for (int i = body.Begin; i < body.End;)
                {
                    int begin = i; while (i < body.End && program.At(i) != ";") i++;
                    if (i == body.End) return false; int end = i++;
                    for (int token = begin; token < end; token++) if (query.MatchesToken(program.At(token))) return false;
                    while (begin < end && FieldQualifiers.Contains(program.At(begin))) begin++;
                    if (begin + 2 > end || !TypeAbsent(program.At(begin)) || program.Tokens[begin + 1].Kind != PpTokenKind.Identifier) return false;
                    begin += 2;
                    while (begin < end && program.At(begin) == "[")
                    {
                        if (indexed) return false;
                        if (begin + 3 > end || program.At(begin + 2) != "]" || !int.TryParse(program.At(begin + 1), out int length) || length < 1 || length > 256) return false;
                        begin += 3;
                    }
                    if (begin < end)
                    {
                        if (begin + 2 != end || program.At(begin) != ":" || program.Tokens[begin + 1].Kind != PpTokenKind.Identifier) return false;
                    }
                }
                active.Remove(type); verified.Add(type); return true;
            }
        }
    }
}
