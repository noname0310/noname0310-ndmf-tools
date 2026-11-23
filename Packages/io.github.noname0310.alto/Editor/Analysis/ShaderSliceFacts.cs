using System;
using System.IO;
using UnityEditor.Rendering;

namespace Noname.AvatarTools.Analysis
{
    internal static class ShaderSliceFacts
    {
        private const string Kind = "shader input slices";
        internal static string Key(string sourceId, string original, string specialized, ShaderInputQuery query, string entry)
            => !ContentFactCache.Enabled || D3DShaderInputs.CompilerFingerprint == null ? null : ContentFactCache.Shared.Key(Kind,
                sourceId, ContentFactCache.TextDigest(original), ContentFactCache.TextDigest(specialized), query.Signature, entry,
                D3DShaderInputs.CompilerFingerprint);

        internal static ShaderInputUseSlice Get(ShaderInputVariantProof.Sample sample, ShaderInputQuery query, string entry,
            Func<string, string, ShaderType, (D3DShaderInputs.Input[] Inputs, string Error)> compile)
        {
            string key = Key(sample.SourceId, sample.Original, sample.Specialized, query, entry);
            var cache = ContentFactCache.Shared;
            if (cache.TryRead(Kind, key, reader => (Trace: ShaderCompileTrace.Read(reader), Slice: Read(reader)), out var previous))
            {
                if (previous.Trace.Replay(compile)) return previous.Slice;
                cache.Reject(Kind);
            }
            var trace = new ShaderCompileTrace();
            var slice = ShaderInputVariantProof.Slice(sample, query, entry, (s, e, t) => trace.Capture(s, e, t, compile));
            if (slice.Complete && slice.LocationsComplete && trace.Complete)
                cache.Store(Kind, key, writer => { trace.Write(writer); Write(writer, slice); });
            return slice;
        }
        internal static void Write(BinaryWriter writer, ShaderInputUseSlice slice)
        {
            writer.Write(slice.Cuts);
            writer.Write(slice.Names.Count); foreach (string name in slice.Names) writer.Write(name);
            writer.Write(slice.WholeValues.Count); foreach (string name in slice.WholeValues) writer.Write(name);
            writer.Write(slice.WholeValueScopes.Count);
            foreach (var scope in slice.WholeValueScopes)
            { writer.Write(scope.Name); writer.Write(scope.File); writer.Write(scope.First); writer.Write(scope.Last); }
            writer.Write(slice.Regions.Count);
            foreach (var region in slice.Regions) { writer.Write(region.File); writer.Write(region.First); writer.Write(region.Last); }
        }
        internal static ShaderInputUseSlice Read(BinaryReader reader)
        {
            var slice = new ShaderInputUseSlice { Complete = true, LocationsComplete = true, Cuts = reader.ReadInt32() };
            int count = ContentFactCache.Count(reader); for (int i = 0; i < count; i++) slice.Names.Add(reader.ReadString());
            count = ContentFactCache.Count(reader); for (int i = 0; i < count; i++) slice.WholeValues.Add(reader.ReadString());
            count = ContentFactCache.Count(reader);
            for (int i = 0; i < count; i++) slice.WholeValueScopes.Add((reader.ReadString(), reader.ReadString(), reader.ReadInt32(), reader.ReadInt32()));
            count = ContentFactCache.Count(reader);
            for (int i = 0; i < count; i++) slice.Regions.Add((reader.ReadString(), reader.ReadInt32(), reader.ReadInt32()));
            return slice;
        }
    }
}
