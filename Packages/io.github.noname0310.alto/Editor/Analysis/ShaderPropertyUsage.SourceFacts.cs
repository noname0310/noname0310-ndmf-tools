using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Noname.AvatarTools.Analysis
{
    internal sealed partial class ShaderPropertyUsage
    {
        // These summaries describe exact text, not a resolved shader or avatar.
        // Include lookup, native pass ownership, import state and material
        // premises are deliberately re-evaluated by each source snapshot.
        internal sealed class SourceSummary
        {
            private readonly Proof proof = new();
            internal string[] Includes;
            internal bool TrackedIncludes;
            internal int Cost => proof.Tokens.Sum(t => t.Length * 2 + 32) +
                proof.PastePatterns.Keys.Sum(t => t.Length * 2 + 128) + Includes.Sum(t => (t?.Length ?? 0) * 2 + 16);

            internal void Apply(Proof target)
            {
                target.Tokens.UnionWith(proof.Tokens);
                target.TokenPasting |= proof.TokenPasting;
                target.Uncertain ??= proof.Uncertain;
                foreach (var pattern in proof.PastePatterns)
                    if (!target.PastePatterns.ContainsKey(pattern.Key)) target.PastePatterns.Add(pattern.Key, pattern.Value);
            }

            internal static SourceSummary Get(string source)
            {
                const string kind = "shader lexical summary";
                var cache = ContentFactCache.Shared;
                string key = ContentFactCache.Enabled ? cache.Key(kind, ContentFactCache.TextDigest(source)) : null;
                if (cache.TryRead(kind, key, Read, out var previous)) return previous;
                var result = new SourceSummary { TrackedIncludes = !UntrackedInclude.IsMatch(source) };
                foreach (Match token in Tokens.Matches(source))
                    if (token.Value[0] != '"' && token.Value[0] != '\'')
                    {
                        result.proof.Tokens.Add(token.Value);
                        if (token.Value == "__TIME__" || token.Value == "__DATE__" || token.Value == "__TIMESTAMP__") result.TrackedIncludes = false;
                    }
                result.proof.TokenPasting = source.Contains("##");
                ScanPasting(source, result.proof);
                result.Includes = Include.Matches(source).Cast<Match>().Select(include =>
                {
                    var match = IncludePath.Match(include.Groups[1].Value);
                    return match.Success ? match.Groups[1].Value.Replace('\\', '/') : null;
                }).ToArray();
                cache.Store(kind, key, result.Write);
                return result;
            }

            private void Write(BinaryWriter writer)
            {
                writer.Write(TrackedIncludes); writer.Write(proof.TokenPasting); writer.Write(proof.Uncertain ?? "");
                WriteNames(writer, proof.Tokens); WriteNames(writer, proof.PastePatterns.Keys);
                writer.Write(Includes.Length);
                foreach (string include in Includes) { writer.Write(include != null); if (include != null) writer.Write(include); }
            }

            private static SourceSummary Read(BinaryReader reader)
            {
                var result = new SourceSummary { TrackedIncludes = reader.ReadBoolean() };
                result.proof.TokenPasting = reader.ReadBoolean(); result.proof.Uncertain = reader.ReadString();
                if (result.proof.Uncertain.Length == 0) result.proof.Uncertain = null;
                result.proof.Tokens.UnionWith(ReadNames(reader));
                foreach (string pattern in ReadNames(reader)) result.proof.PastePatterns.Add(pattern, new Regex(pattern, RegexOptions.CultureInvariant));
                result.Includes = new string[ContentFactCache.Count(reader)];
                for (int i = 0; i < result.Includes.Length; i++) result.Includes[i] = reader.ReadBoolean() ? reader.ReadString() : null;
                return result;
            }
        }

        internal sealed class LabSummary
        {
            internal string[] Identifiers, StateIdentifiers;
            internal ShaderRasterizationState Rasterization;
            internal string Issue;
            internal int Cost => (Identifiers?.Sum(t => t.Length * 2 + 24) ?? 0) +
                (StateIdentifiers?.Sum(t => t.Length * 2 + 24) ?? 0);

            internal static LabSummary Get(string source)
            {
                const string kind = "shaderlab summary";
                var cache = ContentFactCache.Shared;
                string key = ContentFactCache.Enabled ? cache.Key(kind, ContentFactCache.TextDigest(source)) : null;
                if (cache.TryRead(kind, key, reader => new LabSummary { Identifiers = ReadNames(reader),
                    StateIdentifiers = ReadNames(reader), Rasterization = ShaderRasterizationState.Read(reader) }, out var previous)) return previous;
                var result = new LabSummary();
                try
                {
                    var tokens = PpLexer.Lex(source);
                    var all = new HashSet<string>(StringComparer.Ordinal); var states = new HashSet<string>(StringComparer.Ordinal);
                    ReadShaderLabIdentifiers(tokens, all); ReadShaderLabIdentifiers(tokens, states, true);
                    result.Identifiers = all.ToArray(); result.StateIdentifiers = states.ToArray();
                    result.Rasterization = ShaderRasterizationState.Parse(tokens);
                    cache.Store(kind, key, writer => { WriteNames(writer, result.Identifiers); WriteNames(writer, result.StateIdentifiers); result.Rasterization.Write(writer); });
                }
                catch (Exception exception) { result.Issue = exception.Message; }
                return result;
            }
        }

        private static void WriteNames(BinaryWriter writer, ICollection<string> names)
        { writer.Write(names.Count); foreach (string name in names) writer.Write(name); }
        private static string[] ReadNames(BinaryReader reader)
        {
            var names = new string[ContentFactCache.Count(reader)];
            for (int i = 0; i < names.Length; i++) names[i] = reader.ReadString();
            return names;
        }
    }
}
