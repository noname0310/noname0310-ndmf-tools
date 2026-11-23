using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor.Rendering;

namespace Noname.AvatarTools.Analysis
{
    // Replaying requests through the ordinary compiler lookup preserves logical
    // analysis budgets, even when the physical native compilation is cached.
    internal sealed class ShaderCompileTrace
    {
        private sealed class Call
        {
            internal string Source, Entry;
            internal ShaderType Stage;
            internal D3DShaderInputs.Input[] Inputs;
        }
        private readonly List<Call> calls = new();
        internal bool Complete = true;
        internal (D3DShaderInputs.Input[] Inputs, string Error) Capture(string source, string entry, ShaderType stage,
            Func<string, string, ShaderType, (D3DShaderInputs.Input[] Inputs, string Error)> compile)
        {
            var result = compile(source, entry, stage);
            if (result.Inputs == null || result.Error != null) Complete = false;
            else calls.Add(new Call { Source = source, Entry = entry, Stage = stage, Inputs = result.Inputs });
            return result;
        }
        internal bool Replay(Func<string, string, ShaderType, (D3DShaderInputs.Input[] Inputs, string Error)> compile)
        {
            foreach (var call in calls)
            {
                var result = compile(call.Source, call.Entry, call.Stage);
                if (result.Error != null || result.Inputs == null || result.Inputs.Length != call.Inputs.Length) return false;
                for (int i = 0; i < result.Inputs.Length; i++)
                {
                    var a = result.Inputs[i]; var b = call.Inputs[i];
                    if (a.Semantic != b.Semantic || a.SemanticIndex != b.SemanticIndex || a.ReadMask != b.ReadMask || a.Kind != b.Kind) return false;
                }
            }
            return true;
        }
        internal void Write(BinaryWriter writer)
        {
            writer.Write(calls.Count);
            foreach (var call in calls)
            {
                writer.Write(call.Source); writer.Write(call.Entry); writer.Write((int)call.Stage); writer.Write(call.Inputs.Length);
                foreach (var input in call.Inputs)
                { writer.Write(input.Semantic); writer.Write(input.SemanticIndex); writer.Write(input.ReadMask); writer.Write((byte)input.Kind); }
            }
        }
        internal static ShaderCompileTrace Read(BinaryReader reader)
        {
            var result = new ShaderCompileTrace(); int count = ContentFactCache.Count(reader, 4096);
            for (int i = 0; i < count; i++)
            {
                var call = new Call { Source = reader.ReadString(), Entry = reader.ReadString(), Stage = (ShaderType)reader.ReadInt32() };
                call.Inputs = new D3DShaderInputs.Input[ContentFactCache.Count(reader, 16384)];
                for (int j = 0; j < call.Inputs.Length; j++)
                {
                    string name = reader.ReadString(); uint index = reader.ReadUInt32(); byte mask = reader.ReadByte();
                    call.Inputs[j] = new D3DShaderInputs.Input(name, mask, index, (D3DShaderInputs.InputKind)reader.ReadByte());
                }
                result.calls.Add(call);
            }
            return result;
        }
    }
}
