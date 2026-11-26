using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Noname.AvatarTools.Analysis
{
    // A bounded relational proof over Unity's actual compiled vertex program.
    // POSITION.xyz is NaN at every corner; its payload is deliberately unknown.
    // Other varying inputs cannot enter the position result. Equal finite values
    // may have different zero signs; bitwise control and reciprocal operations
    // must not turn that distinction into differing finite positions. Float arithmetic
    // can retain NaN or erase it to a draw-uniform result under driver refactoring:
    // both outcomes are nondrawing when every position lane satisfies this proof.
    // Integer reinterpretation/conversion of a NaN is not an equality proof.
    internal static class D3DVertexNonFiniteProof
    {
        // EqualOrNaN is a relation across the whole triangle: either every corner
        // is NaN, or every corner has the same numeric value (possibly signed 0).
        // It never means an arbitrary finite-or-NaN choice at each corner.
        // ExactOrNaN additionally fixes the finite zero sign. A predicate is a
        // draw-uniform integer mask containing exactly 0 or 0xffffffff.
        private enum Value : byte { Unknown, Uniform, Predicate, EqualOrNaN, ExactOrNaN }
        private sealed class Branch
        {
            internal Dictionary<string, Value[]> Before, Then;
        }

        private static readonly Regex Register = new(@"^([rvo]\d+)(?:\.([xyzw]{1,4}))?$", RegexOptions.CultureInvariant);
        private static readonly Regex Buffer = new(@"^cb\d+\[(\d+|[rvo]\d+\.[xyzw](?:\s*\+\s*\d+)?)\](?:\.[xyzw]{1,4})?$", RegexOptions.CultureInvariant);
        private static readonly HashSet<string> FloatUnary = new(StringComparer.Ordinal)
            { "exp", "frc", "log", "rcp", "round_ne", "round_ni", "round_pi", "round_z", "rsq", "sqrt" };
        private static readonly HashSet<string> FloatBinary = new(StringComparer.Ordinal)
            { "add", "div", "max", "min", "mul" };
        private static readonly HashSet<string> FloatCompare = new(StringComparer.Ordinal)
            { "eq", "ge", "lt", "ne" };
        private static readonly HashSet<string> UniformOnly = new(StringComparer.Ordinal)
            { "and", "or", "xor", "not", "iadd", "ineg", "imad", "imax", "imin", "ieq", "ine", "ige", "ilt",
              "ishl", "ishr", "ushr", "umax", "umin", "uge", "ult", "ftoi", "ftou", "itof", "utof",
              "bfrev", "countbits", "firstbit_hi", "firstbit_lo", "firstbit_shi", "ubfe", "ibfe", "bfi",
              "f32tof16", "f16tof32" };
        private static readonly HashSet<string> Loads = new(StringComparer.Ordinal)
            { "ld", "ld_ms", "ld_raw", "ld_structured", "sample", "sample_l", "sample_b", "sample_d",
              "sample_c", "sample_c_lz", "resinfo", "bufinfo", "lod", "gather4", "gather4_c", "gather4_po", "gather4_po_c" };

        [DllImport("d3dcompiler_47.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern int D3DDisassemble(byte[] code, UIntPtr size, uint flags, IntPtr comments, out IntPtr text);
        [DllImport("d3dcompiler_47.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern int D3DReflect(byte[] code, UIntPtr size, ref Guid iid, out IntPtr reflection);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr BlobPointer(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate UIntPtr BlobSize(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Description(IntPtr self, out ShaderDescription value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Parameter(IntPtr self, uint index, out Signature value);

        [StructLayout(LayoutKind.Sequential)]
        private struct ShaderDescription
        {
            internal uint Version;
            internal IntPtr Creator;
            internal uint Flags, ConstantBuffers, BoundResources, InputParameters, OutputParameters;
            internal uint InstructionCount, TempRegisterCount, TempArrayCount, DefCount, DclCount;
            internal uint TextureNormalInstructions, TextureLoadInstructions, TextureCompInstructions, TextureBiasInstructions, TextureGradientInstructions;
            internal uint FloatInstructionCount, IntInstructionCount, UintInstructionCount, StaticFlowControlCount, DynamicFlowControlCount;
            internal uint MacroInstructionCount, ArrayInstructionCount, CutInstructionCount, EmitInstructionCount;
            internal uint GSOutputTopology, GSMaxOutputVertexCount, InputPrimitive, PatchConstantParameters;
            internal uint GSInstanceCount, ControlPoints, HSOutputPrimitive, HSPartitioning, TessellatorDomain;
            internal uint BarrierInstructions, InterlockedInstructions, TextureStoreInstructions;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Signature
        {
            internal IntPtr SemanticName;
            internal uint SemanticIndex, Register, SystemValueType, ComponentType;
            internal byte Mask, ReadWriteMask;
            internal uint Stream, MinPrecision;
        }

        private static T Method<T>(IntPtr value, int slot) where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(value), slot * IntPtr.Size));

        internal static bool TryProve(byte[] bytecode, out string issue)
            => TryProve(bytecode, out issue, out _);

        internal static bool TryProve(byte[] bytecode, out string issue, out string disassembly)
            => TryProve(bytecode, out issue, out disassembly, out _);

        internal static bool TryProve(byte[] bytecode, out string issue, out string disassembly, out uint positionMinimumPrecision)
        {
            issue = null; disassembly = null; positionMinimumPrecision = 0;
            IntPtr reflection = IntPtr.Zero, blob = IntPtr.Zero;
            try
            {
                if (bytecode == null || bytecode.Length == 0) { issue = "missing native vertex bytecode"; return false; }
                var iid = new Guid("8d536ca1-0cca-4956-a837-786963755584");
                if (D3DReflect(bytecode, (UIntPtr)bytecode.Length, ref iid, out reflection) < 0 || reflection == IntPtr.Zero ||
                    Method<Description>(reflection, 3)(reflection, out var description) < 0 ||
                    description.Version >> 16 != 1 || description.InputParameters > 128 || description.OutputParameters > 128 ||
                    description.InstructionCount > 32768)
                { issue = "unsupported native vertex bytecode"; return false; }
                if (description.BarrierInstructions != 0 || description.InterlockedInstructions != 0 || description.TextureStoreInstructions != 0)
                { issue = "vertex program has observable memory effects"; return false; }
                var registers = new Dictionary<string, Value[]>(StringComparer.Ordinal);
                for (uint i = 0; i < description.InputParameters; i++)
                {
                    if (Method<Parameter>(reflection, 7)(reflection, i, out var input) < 0 || input.SemanticName == IntPtr.Zero)
                    { issue = "incomplete native vertex input signature"; return false; }
                    string semantic = Marshal.PtrToStringAnsi(input.SemanticName);
                    if (string.Equals(semantic, "POSITION", StringComparison.OrdinalIgnoreCase) && input.SemanticIndex == 0)
                        positionMinimumPrecision |= input.MinPrecision;
                    if (!registers.TryGetValue("v" + input.Register, out var values))
                        registers.Add("v" + input.Register, values = new Value[4]);
                    for (int lane = 0; lane < 4; lane++)
                    {
                        if ((input.Mask & (1 << lane)) == 0) continue;
                        if (string.Equals(semantic, "POSITION", StringComparison.OrdinalIgnoreCase) &&
                            input.SemanticIndex == 0 && input.ComponentType == 3)
                            values[lane] = lane == 3 ? Value.Uniform : Value.EqualOrNaN;
                        else if (string.Equals(semantic, "SV_InstanceID", StringComparison.OrdinalIgnoreCase))
                            values[lane] = Value.Uniform;
                    }
                }
                string position = null;
                for (uint i = 0; i < description.OutputParameters; i++)
                {
                    if (Method<Parameter>(reflection, 8)(reflection, i, out var output) < 0 || output.SemanticName == IntPtr.Zero)
                    { issue = "incomplete native vertex output signature"; return false; }
                    if (!string.Equals(Marshal.PtrToStringAnsi(output.SemanticName), "SV_POSITION", StringComparison.OrdinalIgnoreCase)) continue;
                    positionMinimumPrecision |= output.MinPrecision;
                    if (position != null || output.Mask != 15 || output.ComponentType != 3 || output.SemanticIndex != 0)
                    { issue = "unsupported native position output"; return false; }
                    position = "o" + output.Register;
                }
                if (position == null) { issue = "no native position output"; return false; }
                if (positionMinimumPrecision != 0)
                { issue = "reduced-precision position conversion has no nonfinite contract"; return false; }
                // D3D_DISASM_PRINT_HEX_LITERALS preserves exact IEEE bits. Decimal
                // display rounding must not turn a subnormal floor into a normal.
                if (D3DDisassemble(bytecode, (UIntPtr)bytecode.Length, 0x80, IntPtr.Zero, out blob) < 0 || blob == IntPtr.Zero)
                { issue = "native vertex disassembly unavailable"; return false; }
                ulong length = Method<BlobSize>(blob, 4)(blob).ToUInt64();
                if (length > 8 * 1024 * 1024) { issue = "native vertex disassembly exceeds the proof limit"; return false; }
                byte[] bytes = new byte[(int)length];
                Marshal.Copy(Method<BlobPointer>(blob, 3)(blob), bytes, 0, bytes.Length);
                disassembly = Encoding.UTF8.GetString(bytes).TrimEnd('\0');
                return Evaluate(disassembly, position, registers, out issue);
            }
            catch (Exception exception) { issue = "native nonfinite position proof failed: " + exception.GetType().Name; return false; }
            finally
            {
                if (blob != IntPtr.Zero) Marshal.Release(blob);
                if (reflection != IntPtr.Zero) Marshal.Release(reflection);
            }
        }

        private static bool Evaluate(string program, string position, Dictionary<string, Value[]> registers, out string issue)
        {
            issue = null; bool returned = false; var branches = new Stack<Branch>();
            foreach (string raw in program.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)) continue;
                int comment = line.IndexOf("//", StringComparison.Ordinal);
                if (comment >= 0) line = line.Substring(0, comment).TrimEnd();
                if (line == "vs_4_0" || line == "vs_4_1" || line == "vs_5_0") continue;
                int separator = line.IndexOf(' ');
                string opcode = separator < 0 ? line : line.Substring(0, separator);
                string operands = separator < 0 ? "" : line.Substring(separator + 1).Trim();
                if (opcode.StartsWith("dcl_", StringComparison.Ordinal))
                {
                    if (opcode.IndexOf("uav", StringComparison.Ordinal) >= 0)
                    { issue = "vertex program declares writable memory"; return false; }
                    continue;
                }
                if (returned) { issue = "instructions after a vertex return"; return false; }
                var args = Split(operands);
                if (opcode == "if_nz" || opcode == "if_z")
                {
                    if (args.Length != 1 || !IsUniform(Read(args[0], 0, registers)) || branches.Count >= 32)
                    { issue = "vertex control flow depends on an unverified input"; return false; }
                    branches.Push(new Branch { Before = Clone(registers) }); continue;
                }
                if (opcode == "else")
                {
                    if (branches.Count == 0 || branches.Peek().Then != null) { issue = "unsupported vertex control flow"; return false; }
                    branches.Peek().Then = registers; registers = Clone(branches.Peek().Before); continue;
                }
                if (opcode == "endif")
                {
                    if (branches.Count == 0) { issue = "unsupported vertex control flow"; return false; }
                    var branch = branches.Pop();
                    registers = Join(branch.Then ?? branch.Before, registers); continue;
                }
                if (opcode == "ret")
                {
                    if (branches.Count != 0 || args.Length != 0) { issue = "conditional vertex return"; return false; }
                    returned = true; continue;
                }
                if (opcode == "nop") continue;
                bool saturate = opcode.EndsWith("_sat", StringComparison.Ordinal);
                if (saturate) opcode = opcode.Substring(0, opcode.Length - 4);
                if (args.Length < 2 || !Register.IsMatch(args[0]) || args[0][0] == 'v')
                { issue = "unsupported native vertex instruction: " + opcode; return false; }
                var destination = Register.Match(args[0]);
                string mask = destination.Groups[2].Success ? destination.Groups[2].Value : "xyzw";
                var next = new Value[4];
                bool handled = opcode == "mov" && args.Length == 2 ||
                    FloatUnary.Contains(opcode) && args.Length == 2 ||
                    (FloatBinary.Contains(opcode) || FloatCompare.Contains(opcode)) && args.Length == 3 ||
                    opcode == "mad" && args.Length == 4 ||
                    opcode == "movc" && args.Length == 4 ||
                    (opcode == "dp2" || opcode == "dp3" || opcode == "dp4") && args.Length == 3 ||
                    UniformOnly.Contains(opcode) || Loads.Contains(opcode);
                if (!handled) { issue = "unsupported native vertex instruction: " + opcode; return false; }
                for (int lane = 0; lane < 4; lane++)
                {
                    if (mask.IndexOf("xyzw"[lane]) < 0) continue;
                    Value value = Value.Uniform;
                    int width = opcode == "dp2" ? 2 : opcode == "dp3" ? 3 : opcode == "dp4" ? 4 : 0;
                    for (int source = 1; source < args.Length; source++)
                    {
                        if (width != 0)
                            for (int component = 0; component < width; component++) value = Combine(value, Read(args[source], component, registers));
                        else value = Combine(value, Read(args[source], lane, registers));
                    }
                    if (Loads.Contains(opcode) || UniformOnly.Contains(opcode) && !IsUniform(value)) value = Value.Unknown;
                    else if (FloatCompare.Contains(opcode) && value != Value.Unknown) value = Value.Predicate;
                    if (opcode == "mov") value = Read(args[1], lane, registers);
                    if (opcode == "movc") value = IsUniform(Read(args[1], lane, registers))
                        ? JoinValue(Read(args[2], lane, registers), Read(args[3], lane, registers)) : Value.Unknown;
                    // A strictly positive normal floor (or negative normal ceiling)
                    // removes both NaN payload and signed-zero variation. This is
                    // exact binary classification, not an epsilon comparison.
                    if (value != Value.Unknown && (opcode == "max" || opcode == "min") &&
                        (NormalLiteral(args[1], lane, opcode == "min") || NormalLiteral(args[2], lane, opcode == "min")))
                        value = Value.Uniform;
                    // A sum of identical squares cannot have a differing zero
                    // sign. Operand identity includes every source swizzle and
                    // modifier, and all used lanes must already be known.
                    if (width != 0 && value != Value.Unknown && !IsUniform(value) &&
                        string.Equals(args[1], args[2], StringComparison.Ordinal)) value = Value.ExactOrNaN;
                    if (opcode == "and" && args.Length == 3)
                    {
                        var left = Read(args[1], lane, registers); var right = Read(args[2], lane, registers);
                        if (left == Value.Predicate) value = right;
                        else if (right == Value.Predicate) value = left;
                    }
                    if ((opcode == "rcp" || opcode == "rsq") && Read(args[1], lane, registers) == Value.EqualOrNaN ||
                        opcode == "div" && Read(args[2], lane, registers) == Value.EqualOrNaN) value = Value.Unknown;
                    next[lane] = value;
                }
                string name = destination.Groups[1].Value;
                if (!registers.TryGetValue(name, out var output)) registers.Add(name, output = new Value[4]);
                for (int lane = 0; lane < 4; lane++) if (mask.IndexOf("xyzw"[lane]) >= 0) output[lane] = next[lane];
            }
            if (!returned || branches.Count != 0 || !registers.TryGetValue(position, out var result) || result.Any(v => v == Value.Unknown))
            { issue = "native position can depend on differing vertex data or NaN payload bits"; return false; }
            return true;
        }

        private static Value Read(string operand, int lane, Dictionary<string, Value[]> registers)
        {
            operand = operand.Trim();
            bool modified = false;
            if (operand.StartsWith("-", StringComparison.Ordinal)) { operand = operand.Substring(1); modified = true; }
            if (operand.StartsWith("abs(", StringComparison.Ordinal) && operand.EndsWith(")", StringComparison.Ordinal))
            { operand = operand.Substring(4, operand.Length - 5); modified = true; }
            if (operand.StartsWith("|", StringComparison.Ordinal) && operand.EndsWith("|", StringComparison.Ordinal))
            { operand = operand.Substring(1, operand.Length - 2); modified = true; }
            if (operand.StartsWith("l(", StringComparison.Ordinal) && operand.EndsWith(")", StringComparison.Ordinal)) return Value.Uniform;
            var buffer = Buffer.Match(operand);
            if (buffer.Success)
            {
                string index = buffer.Groups[1].Value;
                if (uint.TryParse(index, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _))
                    return Value.Uniform;
                int addition = index.IndexOf('+');
                return IsUniform(Read(addition < 0 ? index : index.Substring(0, addition), 0, registers)) ? Value.Uniform : Value.Unknown;
            }
            var register = Register.Match(operand);
            if (!register.Success || !registers.TryGetValue(register.Groups[1].Value, out var values)) return Value.Unknown;
            string swizzle = register.Groups[2].Value;
            if (swizzle.Length > 1 && lane >= swizzle.Length) return Value.Unknown;
            int component = swizzle.Length == 0 ? lane : "xyzw".IndexOf(swizzle.Length == 1 ? swizzle[0] : swizzle[lane]);
            if (component < 0) return Value.Unknown;
            return modified && values[component] == Value.Predicate ? Value.Uniform : values[component];
        }

        private static bool IsUniform(Value value) => value == Value.Uniform || value == Value.Predicate;

        private static bool NormalLiteral(string operand, int lane, bool negative)
        {
            if (!operand.StartsWith("l(", StringComparison.Ordinal) || !operand.EndsWith(")", StringComparison.Ordinal)) return false;
            var fields = operand.Substring(2, operand.Length - 3).Split(',');
            if (fields.Length != 1 && lane >= fields.Length) return false;
            string token = fields[fields.Length == 1 ? 0 : lane].Trim();
            if (token.Length != 10 || !token.StartsWith("0x", StringComparison.Ordinal) ||
                !uint.TryParse(token.Substring(2), System.Globalization.NumberStyles.AllowHexSpecifier,
                    System.Globalization.CultureInfo.InvariantCulture, out uint bits)) return false;
            uint exponent = bits & 0x7f800000u;
            return exponent != 0 && exponent != 0x7f800000u && ((bits & 0x80000000u) != 0) == negative;
        }

        private static Value Combine(Value left, Value right) => left == Value.Unknown || right == Value.Unknown
            ? Value.Unknown : IsUniform(left) && IsUniform(right) ? Value.Uniform : Value.EqualOrNaN;

        private static Value JoinValue(Value left, Value right) => left == Value.Unknown || right == Value.Unknown ? Value.Unknown :
            left == Value.Predicate && right == Value.Predicate ? Value.Predicate :
            IsUniform(left) && IsUniform(right) ? Value.Uniform :
            left == Value.EqualOrNaN || right == Value.EqualOrNaN ? Value.EqualOrNaN : Value.ExactOrNaN;

        private static Dictionary<string, Value[]> Clone(Dictionary<string, Value[]> registers) =>
            registers.ToDictionary(p => p.Key, p => (Value[])p.Value.Clone(), StringComparer.Ordinal);

        private static Dictionary<string, Value[]> Join(Dictionary<string, Value[]> left, Dictionary<string, Value[]> right)
        {
            var result = new Dictionary<string, Value[]>(StringComparer.Ordinal);
            foreach (string name in left.Keys.Union(right.Keys))
            {
                var values = new Value[4];
                for (int lane = 0; lane < 4; lane++)
                    values[lane] = left.TryGetValue(name, out var a) && right.TryGetValue(name, out var b) ? JoinValue(a[lane], b[lane]) : Value.Unknown;
                result.Add(name, values);
            }
            return result;
        }

        private static string[] Split(string operands)
        {
            if (operands.Length == 0) return Array.Empty<string>();
            var result = new List<string>(); int depth = 0, start = 0;
            for (int i = 0; i < operands.Length; i++)
            {
                if (operands[i] == '(' || operands[i] == '[') depth++;
                else if (operands[i] == ')' || operands[i] == ']') depth--;
                else if (operands[i] == ',' && depth == 0) { result.Add(operands.Substring(start, i - start).Trim()); start = i + 1; }
            }
            result.Add(operands.Substring(start).Trim()); return result.ToArray();
        }
    }
}
