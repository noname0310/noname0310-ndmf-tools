using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Noname.AvatarTools.Analysis
{
    // #line is mutable preprocessing state, not emitted program text. Keep paired
    // mappings until the bounded domain widens; unused metadata never fails an
    // input query. Line and filename reads have independent dependencies, and a
    // line-only assignment preserves the existing filename state by identity.
    internal sealed class PpLocations
    {
        internal const int MaximumMappings = 64;
        internal readonly struct Mapping : IEquatable<Mapping>
        {
            internal readonly long Offset;
            internal readonly string File;
            internal Mapping(long offset, string file) { Offset = offset; File = file; }
            public bool Equals(Mapping other) => Offset == other.Offset && File == other.File;
            public override bool Equals(object obj) => obj is Mapping other && Equals(other);
            public override int GetHashCode() => Offset.GetHashCode() * 397 ^ (File?.GetHashCode() ?? 0);
            internal PpToken Locate(PpToken token, string defaultFile)
            {
                long line = token.Line + Offset;
                if (line < 0 || line > int.MaxValue) return null;
                // Unity's anonymous #line records refer to the root source in
                // native use slices. This projection fallback must not change
                // the raw empty string returned by a concrete __FILE__ read.
                string file = File == "" ? defaultFile ?? token.File : File ?? token.File;
                return line == token.Line && file == token.File ? token :
                    new PpToken(token.Text, token.Kind, file, (int)line, token.Space, token.Dependencies, token.Hidden);
            }
        }

        private sealed class Field<T>
        {
            internal readonly bool Known;
            internal readonly T Value;
            internal readonly BigInteger Dependencies;
            internal Field(bool known, T value, BigInteger dependencies)
            { Known = known; Value = value; Dependencies = dependencies; }
            internal static Field<T> Join(Field<T> a, Field<T> b, BigInteger guard)
            {
                if (ReferenceEquals(a, b)) return a;
                bool same = a.Known && b.Known && EqualityComparer<T>.Default.Equals(a.Value, b.Value);
                return new Field<T>(same, same ? a.Value : default,
                    a.Dependencies | b.Dependencies | (same ? BigInteger.Zero : guard));
            }
            internal bool Same(Field<T> other) => Known == other.Known && Dependencies == other.Dependencies &&
                (!Known || EqualityComparer<T>.Default.Equals(Value, other.Value));
        }

        // Null means unknown pairs, not an empty set. Singleton field facts can
        // survive widening and recover a concrete mapping after an assignment.
        internal readonly Mapping[] Mappings;
        private readonly Field<long> line;
        private readonly Field<string> file;
        internal BigInteger LineDependencies => line.Dependencies;
        internal BigInteger FileDependencies => file.Dependencies;
        internal string ProjectionRoot(string defaultFile) => Mappings == null || Mappings.Any(p => p.File == "") ? defaultFile : null;
        private PpLocations(Mapping[] mappings, Field<long> line, Field<string> file)
        { Mappings = mappings; this.line = line; this.file = file; }

        internal static PpLocations Start(string file) => new(new[] { new Mapping(0, file) },
            new Field<long>(true, 0, BigInteger.Zero), new Field<string>(true, file, BigInteger.Zero));

        internal PpLocations Remap(long offset, string filename = null, bool replaceFile = false,
            BigInteger lineDependencies = default, BigInteger fileDependencies = default, bool filenameKnown = true)
        {
            var nextLine = new Field<long>(true, offset, lineDependencies);
            var nextFile = replaceFile ? new Field<string>(filenameKnown, filenameKnown ? filename : null, fileDependencies) : file;
            Mapping[] pairs;
            if (nextFile.Known) pairs = new[] { new Mapping(offset, nextFile.Value) };
            else if (replaceFile || Mappings == null) pairs = null;
            else pairs = Order(Mappings.Select(p => new Mapping(offset, p.File)).Distinct());
            return new PpLocations(pairs, nextLine, nextFile);
        }

        internal static PpLocations Join(PpLocations a, PpLocations b, BigInteger guard)
        {
            if (ReferenceEquals(a, b)) return a;
            Mapping[] pairs = null;
            if (a.Mappings != null && b.Mappings != null)
            {
                var union = new HashSet<Mapping>(a.Mappings);
                union.UnionWith(b.Mappings);
                if (union.Count <= MaximumMappings) pairs = Order(union);
            }
            return new PpLocations(pairs, Field<long>.Join(a.line, b.line, guard), Field<string>.Join(a.file, b.file, guard));
        }

        private static Mapping[] Order(IEnumerable<Mapping> pairs) => pairs.OrderBy(p => p.Offset)
            .ThenBy(p => p.File, StringComparer.Ordinal).ToArray();

        internal long ReadLine(PpToken token)
        {
            if (!line.Known) throw new PpException("Source line has multiple or unknown locations");
            long value = token.Line + line.Value;
            if (value < 0 || value > int.MaxValue) throw new PpException("Source line is outside the supported integer range");
            return value;
        }

        internal string ReadFile(PpToken token)
        {
            if (!file.Known) throw new PpException("Source filename has multiple or unknown locations");
            return file.Value ?? token.File;
        }

        internal bool Same(PpLocations other)
        {
            if (ReferenceEquals(this, other)) return true;
            if (other == null || !line.Same(other.line) || !file.Same(other.file)) return false;
            if (Mappings == null || other.Mappings == null) return Mappings == other.Mappings;
            return Mappings.SequenceEqual(other.Mappings);
        }
    }
}
