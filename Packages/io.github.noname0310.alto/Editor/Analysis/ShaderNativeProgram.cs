using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine.Rendering;

namespace Noname.AvatarTools.Analysis
{
    // Detached compiler output. Material assumptions and queried inputs do not
    // belong to this artifact: the same bytecode can answer later value domains.
    // Keep all public binding metadata, including recursive structured fields.
    internal sealed class ShaderNativeProgram
    {
        internal sealed class Field
        {
            internal string Name;
            internal int Index, Rows, Columns, ArraySize, StructSize;
            internal ShaderConstantType ConstantType;
            internal ShaderParamType DataType;
            internal Field[] StructFields;
        }
        internal sealed class Buffer
        { internal string Name; internal int Size; internal Field[] Fields; }
        internal sealed class Texture
        {
            internal string Name;
            internal int Index, SamplerIndex, ArraySize;
            internal bool Multisampled;
            internal TextureDimension Dim;
        }
        internal bool Success;
        internal byte[] ShaderData;
        internal VertexAttribute[] Attributes;
        internal Buffer[] ConstantBuffers;
        internal Texture[] TextureBindings;

        public static implicit operator ShaderNativeProgram(ShaderData.VariantCompileInfo source)
        {
            Field Capture(ShaderData.ConstantInfo field) => new() { Name = field.Name, Index = field.Index,
                Rows = field.Rows, Columns = field.Columns, ArraySize = field.ArraySize, StructSize = field.StructSize,
                ConstantType = field.ConstantType, DataType = field.DataType, StructFields = field.StructFields?.Select(Capture).ToArray() };
            return new ShaderNativeProgram { Success = source.Success, ShaderData = source.ShaderData, Attributes = source.Attributes,
                ConstantBuffers = source.ConstantBuffers?.Select(b => new Buffer { Name = b.Name, Size = b.Size, Fields = b.Fields?.Select(Capture).ToArray() }).ToArray(),
                TextureBindings = source.TextureBindings?.Select(t => new Texture { Name = t.Name, Index = t.Index, SamplerIndex = t.SamplerIndex,
                    ArraySize = t.ArraySize, Multisampled = t.Multisampled, Dim = t.Dim }).ToArray() };
        }

        internal static ShaderNativeProgram Read(BinaryReader reader)
        {
            var result = new ShaderNativeProgram { Success = reader.ReadBoolean() };
            int bytes = ContentFactCache.Count(reader, 16 * 1024 * 1024);
            result.ShaderData = reader.ReadBytes(bytes);
            if (result.ShaderData.Length != bytes || !result.Success || bytes == 0) throw new InvalidDataException();
            result.Attributes = ReadArray(reader, () => (VertexAttribute)reader.ReadInt32());
            result.ConstantBuffers = ReadArray(reader, () => new Buffer { Name = ReadName(reader), Size = reader.ReadInt32(), Fields = ReadFields(reader, 0) });
            result.TextureBindings = ReadArray(reader, () => new Texture { Name = ReadName(reader), Index = reader.ReadInt32(),
                SamplerIndex = reader.ReadInt32(), ArraySize = reader.ReadInt32(), Multisampled = reader.ReadBoolean(), Dim = (TextureDimension)reader.ReadInt32() });
            return result;
        }

        internal void Write(BinaryWriter writer)
        {
            if (!Success || ShaderData == null || ShaderData.Length == 0 || ShaderData.Length > 16 * 1024 * 1024) throw new InvalidDataException();
            writer.Write(Success); writer.Write(ShaderData.Length); writer.Write(ShaderData);
            WriteArray(writer, Attributes, a => writer.Write((int)a));
            WriteArray(writer, ConstantBuffers, b => { WriteName(writer, b.Name); writer.Write(b.Size); WriteFields(writer, b.Fields, 0); });
            WriteArray(writer, TextureBindings, t => { WriteName(writer, t.Name); writer.Write(t.Index); writer.Write(t.SamplerIndex);
                writer.Write(t.ArraySize); writer.Write(t.Multisampled); writer.Write((int)t.Dim); });
        }

        private static Field[] ReadFields(BinaryReader reader, int depth)
        {
            if (depth > 32) throw new InvalidDataException();
            return ReadArray(reader, () => new Field { Name = ReadName(reader), Index = reader.ReadInt32(), Rows = reader.ReadInt32(),
                Columns = reader.ReadInt32(), ArraySize = reader.ReadInt32(), StructSize = reader.ReadInt32(),
                ConstantType = (ShaderConstantType)reader.ReadInt32(), DataType = (ShaderParamType)reader.ReadInt32(), StructFields = ReadFields(reader, depth + 1) });
        }
        private static void WriteFields(BinaryWriter writer, Field[] fields, int depth)
        {
            if (depth > 32) throw new InvalidDataException();
            WriteArray(writer, fields, f => { WriteName(writer, f.Name); writer.Write(f.Index); writer.Write(f.Rows); writer.Write(f.Columns);
                writer.Write(f.ArraySize); writer.Write(f.StructSize); writer.Write((int)f.ConstantType); writer.Write((int)f.DataType); WriteFields(writer, f.StructFields, depth + 1); });
        }
        private static T[] ReadArray<T>(BinaryReader reader, Func<T> read)
        {
            int count = reader.ReadInt32(); if (count == -1) return null;
            if (count < 0 || count > 16384) throw new InvalidDataException();
            var values = new T[count]; for (int i = 0; i < count; i++) values[i] = read(); return values;
        }
        private static void WriteArray<T>(BinaryWriter writer, T[] values, Action<T> write)
        {
            if (values == null) { writer.Write(-1); return; }
            if (values.Length > 16384) throw new InvalidDataException();
            writer.Write(values.Length); foreach (var value in values) write(value);
        }
        private static string ReadName(BinaryReader reader) => reader.ReadBoolean() ? reader.ReadString() : null;
        private static void WriteName(BinaryWriter writer, string name) { writer.Write(name != null); if (name != null) writer.Write(name); }
    }
}
