using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Rendering;

namespace Noname.AvatarTools.Analysis
{
    internal static partial class D3DConstantEquivalence
    {
        private sealed class Field { internal string Name, Shape; internal int Offset, Size; internal bool Scalar, IntegerScalar; }
        private sealed class ConstantBuffer
        { internal string Name; internal int Size; internal readonly List<Field> Fields = new(); }
        private sealed class SignatureValue
        { internal string Name, Register, Shape; internal uint Index; internal byte Mask; }
        private sealed class Instruction
        { internal string Opcode; internal string[] Arguments; internal int Else = -1, End = -1; }

        private sealed partial class Program
        {
            private readonly Expressions expressions;
            private readonly IReadOnlyDictionary<string, float> constants;
            private readonly Dictionary<int, ConstantBuffer> buffers = new();
            private readonly Dictionary<string, (string Identity, string Name)> resources = new(StringComparer.Ordinal);
            private readonly List<SignatureValue> inputs = new(), outputs = new();
            private readonly List<Instruction> instructions = new();
            private readonly Dictionary<string, string> inputModes = new(StringComparer.Ordinal), resourceModes = new(StringComparer.Ordinal);
            private readonly Dictionary<string, Value> uniformValues = new(StringComparer.Ordinal);
            private readonly HashSet<string> scalarNames = new(StringComparer.Ordinal);
            private readonly HashSet<string> integerScalarNames = new(StringComparer.Ordinal);
            internal ISet<string> ZeroIntegerUniforms;
            internal ISet<string> ObservedConstants;
            private IReadOnlyDictionary<string, float[]> uniformDomains;
            private bool allowOpaqueBuffers;
            private bool semanticInputsOnly;
            private readonly Dictionary<int, Field[]> opaqueBuffers = new();
            private Field[] allBufferFields;
            private Dictionary<int, int> declaredBuffers;
            internal string Configuration;
            internal string BindingIdentity => Configuration + "|" + string.Join(";", buffers.OrderBy(p => p.Key).Select(p =>
                p.Key + ":" + p.Value.Name + ":" + p.Value.Size + ":" + string.Join(",", p.Value.Fields.OrderBy(f => f.Offset).Select(f =>
                    f.Name + ":" + f.Offset + ":" + f.Size + ":" + f.Shape)))) + "|" +
                string.Join(";", resources.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + ":" + p.Value.Identity));
            private Program(Expressions expressions, IReadOnlyDictionary<string, float> constants)
            { this.expressions = expressions; this.constants = constants; }

            internal static Program Read(ShaderNativeProgram compiled, ShaderType stage,
                Expressions expressions, IReadOnlyDictionary<string, float> constants, bool allowOpaqueBuffers = false,
                IReadOnlyDictionary<string, float[]> uniformDomains = null, bool semanticInputsOnly = false)
            {
                Require(stage == ShaderType.Vertex || stage == ShaderType.Fragment, "native correspondence is unsupported for this shader stage");
                Require(compiled.Success && compiled.ShaderData != null && compiled.ShaderData.Length > 0 &&
                    compiled.ShaderData.Length <= 16 * 1024 * 1024, "missing or oversized native bytecode");
                var result = new Program(expressions, constants)
                { allowOpaqueBuffers = allowOpaqueBuffers, uniformDomains = uniformDomains, semanticInputsOnly = semanticInputsOnly };
                IntPtr reflection = IntPtr.Zero, blob = IntPtr.Zero;
                try
                {
                    var iid = new Guid("8d536ca1-0cca-4956-a837-786963755584");
                    Require(D3DReflect(compiled.ShaderData, (UIntPtr)compiled.ShaderData.Length, ref iid, out reflection) >= 0 && reflection != IntPtr.Zero,
                        "native shader reflection is unavailable");
                    Require(Method<Description>(reflection, 3)(reflection, out var description) >= 0, "native shader description is unavailable");
                    Require(description.Version >> 16 == (stage == ShaderType.Vertex ? 1u : 0u) && description.InputParameters <= 256 &&
                        description.OutputParameters <= 256 && description.InstructionCount <= 32768, "unsupported native shader interface");
                    Require(description.BarrierInstructions == 0 && description.InterlockedInstructions == 0 && description.TextureStoreInstructions == 0,
                        "native shader has observable memory effects");
                    result.ReadSignatures(reflection, description.InputParameters, 7, result.inputs, "v");
                    result.ReadSignatures(reflection, description.OutputParameters, 8, result.outputs, "o");
                    Require(D3DDisassemble(compiled.ShaderData, (UIntPtr)compiled.ShaderData.Length, 0x80, IntPtr.Zero, out blob) >= 0 && blob != IntPtr.Zero,
                        "native shader disassembly is unavailable");
                    ulong length = Method<BlobSize>(blob, 4)(blob).ToUInt64(); Require(length <= 8 * 1024 * 1024, "native disassembly proof budget exhausted");
                    var bytes = new byte[(int)length]; Marshal.Copy(Method<BlobPointer>(blob, 3)(blob), bytes, 0, bytes.Length);
                    var declarations = result.Parse(Encoding.UTF8.GetString(bytes).TrimEnd('\0'));
                    result.declaredBuffers = declarations;
                    if (stage == ShaderType.Vertex) result.Configuration += ";attributes:" +
                        string.Join(",", compiled.Attributes ?? Array.Empty<UnityEngine.Rendering.VertexAttribute>());
                    result.ReadBuffers(reflection, description, compiled, declarations);
                    result.ReadResources(reflection, description, compiled);
                    foreach (var group in (result.allBufferFields ?? result.buffers.Values.SelectMany(b => b.Fields).ToArray()).GroupBy(f => f.Name, StringComparer.Ordinal))
                        if (group.Count() == 1)
                        {
                            if (group.First().Scalar) result.scalarNames.Add(group.Key);
                            if (group.First().IntegerScalar) result.integerScalarNames.Add(group.Key);
                        }
                    return result;
                }
                finally { if (blob != IntPtr.Zero) Marshal.Release(blob); if (reflection != IntPtr.Zero) Marshal.Release(reflection); }
            }
            private void ReadSignatures(IntPtr reflection, uint count, int method, List<SignatureValue> destination, string prefix)
            {
                for (uint i = 0; i < count; i++)
                {
                    Require(Method<Parameter>(reflection, method)(reflection, i, out var signature) >= 0 && signature.SemanticName != IntPtr.Zero,
                        "incomplete native signature");
                    Require(signature.MinPrecision == 0 && signature.Stream == 0, "native reduced precision or stream signature is unsupported");
                    Require(D3DShaderInputs.TrySemanticReadMask(signature.Mask, signature.Mask, out _), "unsupported native semantic component packing");
                    string name = Marshal.PtrToStringAnsi(signature.SemanticName).ToUpperInvariant();
                    Require(!destination.Any(s => s.Name == name && s.Index == signature.SemanticIndex), "duplicate native semantic signature");
                    destination.Add(new SignatureValue { Name = name, Index = signature.SemanticIndex, Register = prefix + signature.Register,
                        // Physical register lanes can be repacked within a
                        // semantic. Its mask is part of the component identity.
                        Mask = signature.Mask, Shape = signature.ComponentType + ":" + signature.SystemValueType + ":" + signature.Mask });
                }
            }
            private Dictionary<int, int> Parse(string assembly)
            {
                var declared = new Dictionary<int, int>(); var branches = new Stack<int>(); var configuration = new List<string>();
                foreach (string raw in assembly.Split('\n'))
                {
                    string line = raw.Trim(); int comment = line.IndexOf("//", StringComparison.Ordinal);
                    if (comment >= 0) line = line.Substring(0, comment).TrimEnd(); if (line.Length == 0) continue;
                    if (Regex.IsMatch(line, @"^[vp]s_[45]_[01]$", RegexOptions.CultureInvariant)) { configuration.Add(line); continue; }
                    Require(line.IndexOf('{') < 0 && line.IndexOf('}') < 0 && line.IndexOf("min16", StringComparison.Ordinal) < 0,
                        "native reduced-precision instruction is unsupported");
                    int split = line.IndexOf(' '); string opcode = split < 0 ? line : line.Substring(0, split);
                    string operand = split < 0 ? "" : line.Substring(split + 1).Trim();
                    if (opcode == "dcl_constantbuffer")
                    {
                        var match = Regex.Match(line, @"^dcl_constantbuffer CB(\d+)\[(\d+)\], (?:immediateIndexed|dynamicIndexed)$", RegexOptions.CultureInvariant);
                        Require(match.Success, "unsupported native buffer declaration");
                        int slot = Number(match.Groups[1].Value), rows = Number(match.Groups[2].Value);
                        Require(rows > 0 && rows <= 4096 && !declared.ContainsKey(slot), "duplicate or oversized native buffer declaration");
                        declared.Add(slot, rows * 16); continue;
                    }
                    if (opcode.StartsWith("dcl_", StringComparison.Ordinal))
                    {
                        if (opcode == "dcl_indexableTemp") { DeclareTemporary(operand); continue; }
                        if (opcode == "dcl_temps") { Require(Number(operand) <= 4096, "native register budget exhausted"); continue; }
                        if (opcode.StartsWith("dcl_input", StringComparison.Ordinal) || opcode.StartsWith("dcl_output", StringComparison.Ordinal))
                        {
                            var register = Regex.Match(operand, @"(?:^|\s)([vo]\d+)(?:\.[xyzw]{1,4})?(?:,|$)", RegexOptions.CultureInvariant);
                            Require(register.Success, "unsupported native input/output declaration");
                            if (opcode.StartsWith("dcl_input", StringComparison.Ordinal))
                            {
                                string mode = opcode + ":" + operand.Substring(0, register.Index).Trim(); string key = register.Groups[1].Value;
                                Require(!inputModes.TryGetValue(key, out string previous) || previous == mode, "inconsistent native input interpolation"); inputModes[key] = mode;
                            }
                            continue;
                        }
                        if (opcode.StartsWith("dcl_resource", StringComparison.Ordinal) || opcode == "dcl_sampler")
                        {
                            var register = Regex.Match(operand, @"(?:^|\s)([ts]\d+)(?:,|$)", RegexOptions.CultureInvariant);
                            Require(register.Success, "unsupported native resource declaration"); string key = register.Groups[1].Value;
                            Require(!resourceModes.ContainsKey(key), "duplicate native resource declaration");
                            resourceModes.Add(key, opcode + ":" + operand.Replace(key, "<slot>")); continue;
                        }
                        Require(opcode == "dcl_globalFlags", "unsupported native execution declaration: " + opcode); configuration.Add(line); continue;
                    }
                    var instruction = new Instruction { Opcode = opcode, Arguments = Split(operand) }; int index = instructions.Count;
                    instructions.Add(instruction); Require(instructions.Count <= 32768, "native instruction proof budget exhausted");
                    if (opcode == "if_nz" || opcode == "if_z") { Require(branches.Count < 16, "native branch proof budget exhausted"); branches.Push(index); }
                    else if (opcode == "else")
                    { Require(branches.Count != 0 && instructions[branches.Peek()].Else < 0, "unstructured native branch"); instructions[branches.Peek()].Else = index; }
                    else if (opcode == "endif")
                    { Require(branches.Count != 0, "unstructured native branch"); instructions[branches.Pop()].End = index; }
                }
                Require(branches.Count == 0, "unterminated native branch"); Configuration = string.Join(";", configuration); return declared;
            }
            private void ReadBuffers(IntPtr reflection, ShaderDescription description, ShaderNativeProgram compiled, Dictionary<int, int> declared)
            {
                Require(description.ConstantBuffers <= 64 && description.BoundResources <= 1024 && declared.Count <= 64, "native buffer proof budget exhausted");
                if (description.Creator != IntPtr.Zero)
                {
                    var slots = new Dictionary<string, int>(StringComparer.Ordinal);
                    for (uint i = 0; i < description.BoundResources; i++)
                    {
                        Require(Method<ResourceInfo>(reflection, 6)(reflection, i, out var binding) >= 0 && binding.Name != IntPtr.Zero, "incomplete native resource reflection");
                        if (binding.Type != 0) continue; string name = Marshal.PtrToStringAnsi(binding.Name);
                        Require(binding.BindCount == 1 && !slots.ContainsKey(name), "ambiguous native buffer binding"); slots.Add(name, (int)binding.BindPoint);
                    }
                    for (uint i = 0; i < description.ConstantBuffers; i++)
                    {
                        IntPtr buffer = Method<Child>(reflection, 4)(reflection, i); Require(buffer != IntPtr.Zero, "missing native buffer reflection");
                        Require(Method<BufferInfo>(buffer, 0)(buffer, out var info) >= 0, "incomplete native buffer reflection");
                        string name = Marshal.PtrToStringAnsi(info.Name);
                        // Reflection can retain unused definitions. Only bound
                        // buffers may supply declared runtime registers.
                        if (name == null || !slots.TryGetValue(name, out int slot) || !declared.ContainsKey(slot)) continue;
                        Require(info.Variables <= 16384 && info.Size <= 65536 && !buffers.ContainsKey(slot), "ambiguous native buffer reflection");
                        var entry = new ConstantBuffer { Name = name, Size = (int)info.Size };
                        for (uint j = 0; j < info.Variables; j++)
                        {
                            IntPtr variable = Method<Child>(buffer, 1)(buffer, j); Require(variable != IntPtr.Zero, "missing native variable reflection");
                            Require(Method<VariableInfo>(variable, 0)(variable, out var data) >= 0, "incomplete native variable reflection");
                            IntPtr type = Method<OnlyChild>(variable, 1)(variable); Require(type != IntPtr.Zero, "missing native variable type");
                            Require(Method<TypeInfo>(type, 0)(type, out var shape) >= 0, "incomplete native variable type");
                            Require(shape.Members == 0 || semanticInputsOnly, "native structured uniform layout is unsupported");
                            entry.Fields.Add(new Field { Name = Marshal.PtrToStringAnsi(data.Name), Offset = (int)data.StartOffset, Size = (int)data.Size,
                                Shape = shape.Class + ":" + shape.Type + ":" + shape.Rows + ":" + shape.Columns + ":" + shape.Elements + ":" + shape.Members,
                                Scalar = shape.Members == 0 && shape.Class == 0 && shape.Type == 3 && shape.Rows == 1 && shape.Columns == 1 && shape.Elements == 0 && data.Size == 4,
                                IntegerScalar = shape.Members == 0 && shape.Class == 0 && (shape.Type == 1 || shape.Type == 2 || shape.Type == 19) &&
                                    shape.Rows == 1 && shape.Columns == 1 && shape.Elements == 0 && data.Size == 4 });
                        }
                        Validate(entry); buffers.Add(slot, entry);
                    }
                    Require(buffers.Count == declared.Count && declared.All(p => buffers.TryGetValue(p.Key, out var b) && p.Value <= b.Size),
                        "native declared and reflected buffers disagree"); return;
                }
                // Stripped RDEF has no slot names. Row counts can omit unused tail
                // storage. Require one unique complete capacity-compatible mapping,
                // with exactly equal descriptor/declaration sets and no guesses.
                var metadata = (compiled.ConstantBuffers ?? Array.Empty<ShaderNativeProgram.Buffer>()).Select(UnityBuffer).ToArray();
                Require(metadata.Length == declared.Count && metadata.Select(b => b.Name).Distinct(StringComparer.Ordinal).Count() == metadata.Length,
                    "native declared and metadata buffer sets disagree");
                var slotsInOrder = declared.Keys.OrderBy(v => v).ToArray(); var mapping = new int[metadata.Length];
                var used = new bool[metadata.Length]; int solutions = 0; int[] unique = null; int visits = 0;
                void Search(int depth)
                {
                    Require(++visits <= 4096, "native buffer mapping budget exhausted"); if (solutions > 1) return;
                    if (depth == mapping.Length) { solutions++; unique = (int[])mapping.Clone(); return; }
                    for (int index = 0; index < metadata.Length; index++)
                    {
                        if (used[index] || metadata[index].Size < declared[slotsInOrder[depth]]) continue;
                        used[index] = true; mapping[depth] = index; Search(depth + 1); used[index] = false;
                    }
                }
                Search(0);
                if (solutions > 1 && allowOpaqueBuffers)
                {
                    // A constant-buffer load cannot acquire a vertex input merely
                    // because its uniform name is unknown. Analysis may retain all
                    // possible names and the address dependencies. Semantic-only
                    // queries may seed a slot only when its owner is forced across
                    // every complete mapping. Runtime correspondence never uses this.
                    allBufferFields = metadata.SelectMany(b => b.Fields).ToArray();
                    if (semanticInputsOnly) AddForcedBuffers(metadata, slotsInOrder, declared, unique);
                    foreach (int slot in slotsInOrder)
                        if (!buffers.ContainsKey(slot)) opaqueBuffers.Add(slot, allBufferFields);
                    return;
                }
                Require(solutions == 1, "native buffer slots have no unique metadata mapping");
                for (int i = 0; i < slotsInOrder.Length; i++) buffers.Add(slotsInOrder[i], metadata[unique[i]]);
            }
            private void AddForcedBuffers(ConstantBuffer[] metadata, int[] slots, Dictionary<int, int> declared, int[] baseline)
            {
                // A slot is known only if forbidding its edge in a complete
                // matching makes every complete mapping impossible. Augmenting
                // paths avoid enumerating permutations of ambiguous builtins.
                // This analysis-only fact never weakens runtime correspondence.
                int steps = 0;
                bool Alternative(int excludedSlot, int excludedBuffer)
                {
                    var owners = Enumerable.Repeat(-1, metadata.Length).ToArray();
                    bool Augment(int slot, bool[] visited)
                    {
                        for (int index = 0; index < metadata.Length; index++)
                        {
                            Require(++steps <= 262144, "native forced-buffer mapping budget exhausted");
                            if (visited[index] || slot == excludedSlot && index == excludedBuffer || metadata[index].Size < declared[slots[slot]]) continue;
                            visited[index] = true;
                            if (owners[index] < 0 || Augment(owners[index], visited)) { owners[index] = slot; return true; }
                        }
                        return false;
                    }
                    for (int slot = 0; slot < slots.Length; slot++)
                        if (!Augment(slot, new bool[metadata.Length])) return false;
                    return true;
                }
                for (int slot = 0; slot < slots.Length; slot++)
                    if (!Alternative(slot, baseline[slot])) buffers.Add(slots[slot], metadata[baseline[slot]]);
            }
            private ConstantBuffer UnityBuffer(ShaderNativeProgram.Buffer info)
            {
                var buffer = new ConstantBuffer { Name = info.Name, Size = info.Size };
                foreach (var field in info.Fields ?? Array.Empty<ShaderNativeProgram.Field>())
                {
                    if ((int)field.ConstantType == 2 || field.StructSize != 0 || field.StructFields?.Length > 0)
                    {
                        Require(semanticInputsOnly && (int)field.ConstantType == 2 && field.StructSize > 0 && field.StructSize <= 65536 &&
                            field.ArraySize >= 0 && field.ArraySize <= 4096, "native structured uniform metadata is unsupported");
                        // A structured constant-buffer region remains opaque.
                        // Its uniform storage cannot acquire a vertex semantic;
                        // every dynamic address still contributes dependencies.
                        // No member, including a scalar member with a matching
                        // material property name, is eligible for substitution.
                        buffer.Fields.Add(new Field { Name = field.Name, Offset = field.Index,
                            Size = checked(field.StructSize * Math.Max(1, field.ArraySize)),
                            Shape = "opaque-structure:" + field.StructSize + ":" + field.ArraySize, Scalar = false });
                        continue;
                    }
                    Require(field.Rows >= 1 && field.Rows <= 4 && field.Columns >= 1 && field.Columns <= 4 && field.ArraySize >= 0 && field.ArraySize <= 4096,
                        "native uniform dimensions are unsupported");
                    int elements = Math.Max(1, field.ArraySize);
                    int size = (int)field.ConstantType == 0 ? field.Columns * 4 : field.Rows * 16;
                    if (field.ArraySize > 0) size = ((size + 15) / 16) * 16 * elements;
                    buffer.Fields.Add(new Field { Name = field.Name, Offset = field.Index, Size = size,
                        Shape = (int)field.ConstantType + ":" + (int)field.DataType + ":" + field.Rows + ":" + field.Columns + ":" + field.ArraySize,
                        Scalar = (int)field.ConstantType == 0 && (int)field.DataType == 0 && field.Rows == 1 && field.Columns == 1 && field.ArraySize == 0,
                        IntegerScalar = (int)field.ConstantType == 0 && ((int)field.DataType == 1 || (int)field.DataType == 2) &&
                            field.Rows == 1 && field.Columns == 1 && field.ArraySize == 0 });
                }
                Validate(buffer); return buffer;
            }
            private static void Validate(ConstantBuffer buffer)
            {
                Require(!string.IsNullOrEmpty(buffer.Name) && buffer.Size > 0 && buffer.Size <= 65536 && buffer.Fields.Count > 0 && buffer.Fields.Count <= 16384,
                    "invalid or incomplete native buffer metadata");
                var names = new HashSet<string>(StringComparer.Ordinal); int end = 0;
                foreach (var field in buffer.Fields.OrderBy(f => f.Offset))
                {
                    Require(!string.IsNullOrEmpty(field.Name) && names.Add(field.Name) && field.Offset >= end && field.Offset % 4 == 0 &&
                        field.Size > 0 && field.Offset + field.Size <= buffer.Size, "overlapping or incomplete native uniform metadata"); end = field.Offset + field.Size;
                }
            }
            private void ReadResources(IntPtr reflection, ShaderDescription description, ShaderNativeProgram compiled)
            {
                if (description.Creator != IntPtr.Zero)
                {
                    for (uint i = 0; i < description.BoundResources; i++)
                    {
                        Require(Method<ResourceInfo>(reflection, 6)(reflection, i, out var resource) >= 0 && resource.Name != IntPtr.Zero, "incomplete native resource reflection");
                        if (resource.Type == 0) continue;
                        Require((resource.Type == 2 || resource.Type == 3) && resource.BindCount == 1, "unsupported native resource type");
                        string key = (resource.Type == 2 ? "t" : "s") + resource.BindPoint, name = Marshal.PtrToStringAnsi(resource.Name);
                        Require(!resources.ContainsKey(key), "ambiguous native resource binding");
                        resources.Add(key, (name + ":" + resource.Type + ":" + resource.ReturnType + ":" + resource.Dimension + ":" + resource.Samples, name));
                    }
                }
                else
                {
                    var bindings = compiled.TextureBindings ?? Array.Empty<ShaderNativeProgram.Texture>();
                    foreach (var binding in bindings)
                    {
                        Require(!string.IsNullOrEmpty(binding.Name) && binding.ArraySize <= 1 && binding.Index >= 0, "unsupported native texture metadata");
                        string texture = "t" + binding.Index; Require(!resources.ContainsKey(texture), "ambiguous native texture binding");
                        resources.Add(texture, (binding.Name + ":" + binding.Dim + ":" + binding.Multisampled, binding.Name));
                    }
                    foreach (var group in bindings.Where(b => b.SamplerIndex >= 0).GroupBy(b => b.SamplerIndex))
                    {
                        if (group.Count() != 1 && semanticInputsOnly) continue;
                        Require(group.Count() == 1, "native sampler ownership is ambiguous"); string name = group.First().Name;
                        resources.Add("s" + group.Key, ("sampler:" + name, name));
                    }
                }
                if (semanticInputsOnly)
                    foreach (string key in resourceModes.Keys.Where(key => !resources.ContainsKey(key)))
                    {
                        // Unity omits standalone/inline sampler names from its
                        // public binding metadata. A declared read-only resource
                        // cannot acquire a vertex attribute by its binding alone;
                        // every sample address and execution predicate remains a
                        // dependency. Opaque slots never prove named-uniform
                        // absence or correspondence between different programs.
                        resources.Add(key, ("opaque-native-resource:" + key, "<opaque-native-resource:" + key + ">"));
                    }
                Require(resourceModes.Keys.All(resources.ContainsKey), "native resource declaration has no unique metadata binding");
            }
        }
        [DllImport("d3dcompiler_47.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern int D3DReflect(byte[] code, UIntPtr length, ref Guid iid, out IntPtr reflection);
        [DllImport("d3dcompiler_47.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern int D3DDisassemble(byte[] code, UIntPtr length, uint flags, IntPtr comments, out IntPtr text);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr BlobPointer(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate UIntPtr BlobSize(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Description(IntPtr self, out ShaderDescription description);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Parameter(IntPtr self, uint index, out Signature description);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr Child(IntPtr self, uint index);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr OnlyChild(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int BufferInfo(IntPtr self, out BufferDescription description);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int VariableInfo(IntPtr self, out VariableDescription description);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int TypeInfo(IntPtr self, out TypeDescription description);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ResourceInfo(IntPtr self, uint index, out ResourceDescription description);
        private static T Method<T>(IntPtr value, int slot) where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(value), slot * IntPtr.Size));
        [StructLayout(LayoutKind.Sequential)] private struct BufferDescription
        { internal IntPtr Name; internal uint Type, Variables, Size, Flags; }
        [StructLayout(LayoutKind.Sequential)] private struct VariableDescription
        { internal IntPtr Name; internal uint StartOffset, Size, Flags; internal IntPtr DefaultValue; internal uint StartTexture, TextureSize, StartSampler, SamplerSize; }
        [StructLayout(LayoutKind.Sequential)] private struct TypeDescription
        { internal uint Class, Type, Rows, Columns, Elements, Members, Offset; internal IntPtr Name; }
        [StructLayout(LayoutKind.Sequential)] private struct ResourceDescription
        { internal IntPtr Name; internal uint Type, BindPoint, BindCount, Flags, ReturnType, Dimension, Samples; }
        [StructLayout(LayoutKind.Sequential)] private struct ShaderDescription
        {
            internal uint Version; internal IntPtr Creator;
            internal uint Flags, ConstantBuffers, BoundResources, InputParameters, OutputParameters;
            internal uint InstructionCount, TempRegisterCount, TempArrayCount, DefCount, DclCount;
            internal uint TextureNormalInstructions, TextureLoadInstructions, TextureCompInstructions, TextureBiasInstructions, TextureGradientInstructions;
            internal uint FloatInstructionCount, IntInstructionCount, UintInstructionCount, StaticFlowControlCount, DynamicFlowControlCount;
            internal uint MacroInstructionCount, ArrayInstructionCount, CutInstructionCount, EmitInstructionCount;
            internal uint GSOutputTopology, GSMaxOutputVertexCount, InputPrimitive, PatchConstantParameters;
            internal uint GSInstanceCount, ControlPoints, HSOutputPrimitive, HSPartitioning, TessellatorDomain;
            internal uint BarrierInstructions, InterlockedInstructions, TextureStoreInstructions;
        }
        [StructLayout(LayoutKind.Sequential)] private struct Signature
        {
            internal IntPtr SemanticName; internal uint SemanticIndex, Register, SystemValueType, ComponentType;
            internal byte Mask, ReadWriteMask; internal uint Stream, MinPrecision;
        }
    }
}
