using System;
using System.IO;

namespace Noname.AvatarTools.Analysis
{
    // An input proof and the constants actually assumed by the native evaluator
    // form one artifact. Preserve assumptions read by branch/control evaluation,
    // even when they disappear from the final output expression.
    internal static class ShaderNativeInputFacts
    {
        private const string Kind = "native shader input evidence";

        internal static (D3DShaderInputs.Input[] Inputs, string Error, string[] Constants) Get(string identity,
            Func<(D3DShaderInputs.Input[] Inputs, string Error, string[] Constants)> analyze)
        {
            var cache = ContentFactCache.Shared;
            string key = identity == null ? null : cache.Key(Kind, identity);
            if (cache.TryRead(Kind, key, reader =>
            {
                var inputs = new D3DShaderInputs.Input[ContentFactCache.Count(reader, 16384)];
                for (int i = 0; i < inputs.Length; i++)
                {
                    string name = reader.ReadString(); uint index = reader.ReadUInt32(); byte mask = reader.ReadByte();
                    var kind = (D3DShaderInputs.InputKind)reader.ReadByte();
                    if (mask > 15 || !Enum.IsDefined(typeof(D3DShaderInputs.InputKind), kind)) throw new InvalidDataException();
                    inputs[i] = new D3DShaderInputs.Input(name, mask, index, kind);
                }
                var constants = new string[ContentFactCache.Count(reader, 16384)];
                for (int i = 0; i < constants.Length; i++) constants[i] = reader.ReadString();
                return (Inputs: inputs, Constants: constants);
            }, out var previous)) return (previous.Inputs, null, previous.Constants);
            var result = analyze();
            if (result.Inputs != null && result.Error == null && result.Constants != null)
                cache.Store(Kind, key, writer =>
                {
                    writer.Write(result.Inputs.Length);
                    foreach (var input in result.Inputs)
                    { writer.Write(input.Semantic); writer.Write(input.SemanticIndex); writer.Write(input.ReadMask); writer.Write((byte)input.Kind); }
                    writer.Write(result.Constants.Length);
                    foreach (string name in result.Constants) writer.Write(name);
                });
            return result;
        }
    }
}
