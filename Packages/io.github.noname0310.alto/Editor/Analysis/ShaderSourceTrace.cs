using System;
using System.Collections.Generic;
using System.IO;

namespace Noname.AvatarTools.Analysis
{
    // Re-resolve every observed include, including empty and ambiguous results.
    // File creation, search-order changes and generated headers invalidate a hit.
    internal sealed class ShaderSourceTrace
    {
        private readonly Func<string, string, ShaderPreprocessorGraph.Source[]> resolve;
        private readonly Dictionary<(string Origin, string Include), ShaderPreprocessorGraph.Source[]> reads = new();
        internal ShaderSourceTrace(Func<string, string, ShaderPreprocessorGraph.Source[]> resolve) { this.resolve = resolve; }
        internal ShaderPreprocessorGraph.Source[] Resolve(string origin, string include)
        {
            var value = resolve(origin, include);
            reads[(origin, include)] = value;
            return value;
        }
        internal void Write(BinaryWriter writer)
        {
            writer.Write(reads.Count);
            foreach (var pair in reads)
            {
                writer.Write(pair.Key.Origin); writer.Write(pair.Key.Include); writer.Write(pair.Value.Length);
                foreach (var source in pair.Value) { writer.Write(source.Id); writer.Write(ContentFactCache.TextDigest(source.Text)); }
            }
        }
        internal static void Validate(BinaryReader reader, Func<string, string, ShaderPreprocessorGraph.Source[]> resolve)
        {
            int count = ContentFactCache.Count(reader, 16384);
            for (int i = 0; i < count; i++)
            {
                var values = resolve(reader.ReadString(), reader.ReadString());
                int length = ContentFactCache.Count(reader, 512);
                if (values == null || values.Length != length) throw new InvalidDataException("Include resolution changed");
                for (int j = 0; j < length; j++)
                    if (values[j].Id != reader.ReadString() || ContentFactCache.TextDigest(values[j].Text) != reader.ReadString())
                        throw new InvalidDataException("Include contents changed");
            }
        }
    }
}
