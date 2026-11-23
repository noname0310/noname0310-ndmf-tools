using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Noname.AvatarTools.Analysis
{
    // An input query is independent of the optimization requesting it. A family
    // matches every numeric semantic index; an indexed entry matches one input.
    internal sealed class ShaderInputQuery
    {
        internal readonly struct Semantic
        {
            internal readonly string Name;
            internal readonly uint? Index;
            internal Semantic(string name, uint? index = null)
            {
                if (string.IsNullOrEmpty(name) || !PpLexer.IdentifierStart(name[0]) ||
                    name.Any(c => !PpLexer.IdentifierPart(c))) throw new ArgumentException("Invalid shader semantic", nameof(name));
                int end = name.Length;
                while (end > 0 && name[end - 1] >= '0' && name[end - 1] <= '9') end--;
                if (end != name.Length)
                {
                    if (!uint.TryParse(name.Substring(end), NumberStyles.None, CultureInfo.InvariantCulture, out uint suffix) ||
                        index.HasValue && index.Value != suffix) throw new ArgumentException("Conflicting shader semantic index", nameof(name));
                    index = suffix; name = name.Substring(0, end);
                }
                Name = name.ToUpperInvariant(); Index = index;
            }
        }

        internal readonly Semantic[] Semantics;
        private readonly HashSet<string> identifiers;
        internal readonly string Signature;
        internal ShaderInputQuery(params Semantic[] semantics)
        {
            if (semantics == null || semantics.Length == 0 || semantics.Any(s => string.IsNullOrEmpty(s.Name)))
                throw new ArgumentException("An input query requires initialized semantics", nameof(semantics));
            Semantics = semantics.GroupBy(s => s.Name + ":" + s.Index, StringComparer.Ordinal).Select(g => g.First())
                .OrderBy(s => s.Name, StringComparer.Ordinal).ThenBy(s => s.Index).ToArray();
            Signature = string.Join(";", Semantics.Select(s => s.Name + ":" + (s.Index?.ToString(CultureInfo.InvariantCulture) ?? "*")));
        }

        // Named uniforms use the same preprocessor absence proof as semantics,
        // but identifier spelling is case-sensitive and has no numeric suffix family.
        private ShaderInputQuery(IEnumerable<string> names)
        {
            identifiers = new HashSet<string>(names, StringComparer.Ordinal);
            if (identifiers.Count == 0 || identifiers.Any(identifier => string.IsNullOrEmpty(identifier) ||
                !PpLexer.IdentifierStart(identifier[0]) || identifier.Any(c => !PpLexer.IdentifierPart(c))))
                throw new ArgumentException("Invalid shader identifiers", nameof(names));
            Semantics = Array.Empty<Semantic>();
            Signature = "identifiers:" + string.Join(";", identifiers.OrderBy(n => n, StringComparer.Ordinal));
        }
        internal static ShaderInputQuery Identifier(string name) => new(new[] { name });
        internal static ShaderInputQuery Identifiers(IEnumerable<string> names) => new(names);

        internal bool Matches(string name, uint index)
        {
            foreach (var semantic in Semantics)
                if (string.Equals(semantic.Name, name, StringComparison.OrdinalIgnoreCase) &&
                    (!semantic.Index.HasValue || semantic.Index.Value == index)) return true;
            return false;
        }

        internal bool MatchesToken(string text) => MatchesToken(text, 0, text.Length);

        internal bool MatchesToken(string text, int start, int length)
        {
            if (identifiers?.Contains(start == 0 && length == text.Length ? text : text.Substring(start, length)) == true) return true;
            foreach (var semantic in Semantics)
            {
                if (length < semantic.Name.Length || string.Compare(text, start, semantic.Name, 0, semantic.Name.Length,
                    StringComparison.OrdinalIgnoreCase) != 0) continue;
                uint index = 0; bool valid = true;
                for (int i = start + semantic.Name.Length; i < start + length; i++)
                {
                    char c = text[i];
                    if (c < '0' || c > '9') { valid = false; break; }
                    if (!semantic.Index.HasValue) continue;
                    uint digit = (uint)(c - '0');
                    if (index > (uint.MaxValue - digit) / 10) { valid = false; break; }
                    index = index * 10 + digit;
                }
                if (valid && (!semantic.Index.HasValue || semantic.Index.Value == index)) return true;
            }
            return false;
        }

        internal static readonly ShaderInputQuery VertexId = new(new Semantic("SV_VertexID"));
        internal static readonly ShaderInputQuery OtherMeshIndices = new(new Semantic("SV_PrimitiveID"), new Semantic("BLENDINDICES"));
        internal static readonly ShaderInputQuery MeshIndices = new(new Semantic("SV_VertexID"), new Semantic("SV_PrimitiveID"), new Semantic("BLENDINDICES"));
        internal static readonly ShaderInputQuery RigidSkinning = new(MeshIndices.Semantics.Concat(new[] { new Semantic("BLENDWEIGHT") }).ToArray());
        internal static ShaderInputQuery ForMeshChange(bool addSkinning) => addSkinning ? RigidSkinning : MeshIndices;
    }
}
