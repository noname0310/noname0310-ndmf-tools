using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Noname.AvatarTools.Analysis
{
    internal static partial class D3DConstantEquivalence
    {
        private static readonly Regex Register = new(@"^([rvo]\d+|x\d+\[\d+\])(?:\.([xyzw]{1,4}))?$", RegexOptions.CultureInvariant);
        private static readonly Regex Buffer = new(@"^cb(\d+)\[(\d+)\](?:\.([xyzw]{1,4}))?$", RegexOptions.CultureInvariant);
        private static readonly Regex DynamicBuffer = new(@"^cb(\d+)\[([rvo]\d+\.[xyzw])(?:\s*\+\s*(\d+))?\](?:\.([xyzw]{1,4}))?$", RegexOptions.CultureInvariant);
        private static readonly Regex Resource = new(@"^([ts])(\d+)(?:\.([xyzw]{1,4}))?$", RegexOptions.CultureInvariant);
        private static readonly HashSet<string> Unary = new(StringComparer.Ordinal)
        { "exp", "frc", "log", "rcp", "round_ne", "round_ni", "round_pi", "round_z", "rsq", "sqrt",
          "not", "ineg", "ftoi", "ftou", "itof", "utof", "bfrev", "countbits", "firstbit_hi", "firstbit_lo", "firstbit_shi", "f32tof16", "f16tof32" };
        private static readonly HashSet<string> Binary = new(StringComparer.Ordinal)
        { "add", "div", "max", "min", "mul", "eq", "ge", "lt", "ne", "and", "or", "xor", "iadd", "imax", "imin", "ieq", "ine", "ige", "ilt",
          "ishl", "ishr", "ushr", "umax", "umin", "uge", "ult" };
        private static readonly HashSet<string> Ternary = new(StringComparer.Ordinal) { "mad", "imad", "ubfe", "ibfe" };
        private static readonly HashSet<string> Loads = new(StringComparer.Ordinal)
        { "sample", "sample_l", "sample_b", "sample_d", "sample_c", "sample_c_lz", "ld", "ld_ms", "resinfo", "lod", "gather4", "gather4_c" };

        private sealed partial class Program
        {
            private int executedInstructions;
            internal Dictionary<string, Value> Evaluate(string outputSemantic = null)
            {
                var registers = new Dictionary<string, Value[]>(StringComparer.Ordinal);
                foreach (var input in inputs)
                {
                    if (!registers.TryGetValue(input.Register, out var lanes)) registers.Add(input.Register, lanes = new Value[4]);
                    inputModes.TryGetValue(input.Register, out string mode);
                    for (int lane = 0; lane < 4; lane++)
                    {
                        if ((input.Mask & (1 << lane)) == 0) continue; Require(lanes[lane] == null, "overlapping native input lanes");
                        Require(D3DShaderInputs.TrySemanticReadMask(input.Mask, (byte)(1 << lane), out byte logicalMask),
                            "unsupported native semantic component packing");
                        var dependency = new D3DShaderInputs.Input(input.Name, logicalMask, input.Index);
                        lanes[lane] = new Value(expressions.Add("input:" + input.Name + ":" + input.Index + ":" + lane + ":" + input.Shape + ":" + mode, input: dependency));
                    }
                }
                Value discarded = Literal(0); bool returned = Run(0, instructions.Count, registers, ref discarded, Array.Empty<Value>(), 0);
                Require(returned, "native program has no final return"); var result = new Dictionary<string, Value>(StringComparer.Ordinal);
                foreach (var output in outputs)
                {
                    if (outputSemantic != null && !string.Equals(output.Name, outputSemantic, StringComparison.OrdinalIgnoreCase)) continue;
                    Require(registers.TryGetValue(output.Register, out var lanes), "native output register is undefined");
                    for (int lane = 0; lane < 4; lane++)
                    {
                        if ((output.Mask & (1 << lane)) == 0) continue; Require(lanes[lane] != null, "native output lane is undefined");
                        result.Add(output.Name + ":" + output.Index + ":" + lane + ":" + output.Shape, lanes[lane]);
                    }
                }
                Require(result.Count != 0, "no requested native output semantic"); result.Add("discard", discarded); return result;
            }
            private bool Run(int begin, int end, Dictionary<string, Value[]> registers, ref Value discarded, Value[] controls, int depth)
            {
                Require(depth <= 16, "native branch proof budget exhausted");
                for (int i = begin; i < end; i++)
                {
                    Require(++executedInstructions <= 131072, "native control-flow proof budget exhausted");
                    var instruction = instructions[i]; string opcode = instruction.Opcode; var args = instruction.Arguments;
                    if (opcode == "ret") { Require(args.Length == 0, "malformed native return"); return true; }
                    if (opcode == "nop") continue;
                    if (semanticInputsOnly && (opcode == "loop" || opcode == "switch"))
                    { i = SummarizeControl(i, registers, ref discarded, controls); continue; }
                    if (opcode == "if_nz" || opcode == "if_z")
                    {
                        Require(args.Length == 1 && instruction.End >= 0, "malformed native branch");
                        Value condition = Read(args[0], 0, registers, false); if (opcode == "if_z") condition = Apply("ieq", condition, Literal(0));
                        int thenEnd = instruction.Else < 0 ? instruction.End : instruction.Else;
                        if (expressions[condition.Expression].Literal is uint known)
                        {
                            int first = known != 0 ? i + 1 : instruction.Else < 0 ? instruction.End : instruction.Else + 1;
                            int last = known != 0 ? thenEnd : instruction.End;
                            // A proven branch is executed by every invocation;
                            // derivative context is unchanged by removing it.
                            bool returned = Run(first, last, registers, ref discarded, controls, depth + 1);
                            AddControlOrigins(registers, condition.Constants); discarded = WithOrigins(discarded, condition.Constants);
                            if (returned) return true;
                        }
                        else
                        {
                            var yes = Clone(registers); var no = Clone(registers); Value killedYes = discarded, killedNo = discarded;
                            var yesControls = controls.Concat(new[] { condition }).ToArray();
                            var noControls = controls.Concat(new[] { Apply("ieq", condition, Literal(0)) }).ToArray();
                            bool yesReturned = Run(i + 1, thenEnd, yes, ref killedYes, yesControls, depth + 1);
                            bool noReturned = instruction.Else >= 0 && Run(instruction.Else + 1, instruction.End, no, ref killedNo, noControls, depth + 1);
                            // A conditional return leaves only the other invocations
                            // in its continuation. Preserve that execution context.
                            bool returns = yesReturned || noReturned;
                            if (returns && !yesReturned) yesReturned = Run(instruction.End + 1, end, yes, ref killedYes, yesControls, depth + 1);
                            if (returns && !noReturned) noReturned = Run(instruction.End + 1, end, no, ref killedNo, noControls, depth + 1);
                            Require(!returns || yesReturned && noReturned, "unverified native return continuation");
                            registers.Clear();
                            foreach (string name in yes.Keys.Union(no.Keys, StringComparer.Ordinal))
                            {
                                yes.TryGetValue(name, out var a); no.TryGetValue(name, out var b); var merged = new Value[4];
                                for (int lane = 0; lane < 4; lane++) if (a?[lane] != null && b?[lane] != null) merged[lane] = Select(condition, a[lane], b[lane]);
                                registers.Add(name, merged);
                            }
                            discarded = Select(condition, killedYes, killedNo);
                            if (returns) return true;
                        }
                        i = instruction.End; continue;
                    }
                    if (opcode == "discard_nz" || opcode == "discard_z")
                    {
                        Require(args.Length == 1, "malformed native discard"); Value value = Read(args[0], 0, registers, false);
                        value = Apply(opcode == "discard_z" ? "ieq" : "ine", value, Literal(0)); discarded = Apply("or", discarded, value); continue;
                    }
                    bool saturate = opcode.EndsWith("_sat", StringComparison.Ordinal); if (saturate) opcode = opcode.Substring(0, opcode.Length - 4);
                    if (opcode == "udiv")
                    {
                        Require(!saturate && args.Length == 4, "unsupported native unsigned divide");
                        Divide(args, registers); continue;
                    }
                    if (semanticInputsOnly && (opcode == "sincos" || opcode == "imul" || opcode == "umul"))
                    {
                        Require(!saturate && args.Length == (opcode == "sincos" ? 3 : 4), "unsupported native paired operation");
                        SemanticPair(opcode, args, registers); continue;
                    }
                    bool floatModifiers = FloatSources(opcode);
                    bool integerModifiers = semanticInputsOnly && IntegerSources(opcode);
                    Require(args.Length >= 2, "unsupported native instruction: " + opcode);
                    string target = args[0], preciseMask = "";
                    if (target.StartsWith("[", StringComparison.Ordinal))
                    {
                        var annotation = Regex.Match(target, @"^\[precise(?:\(([xyzw]{1,4})\))?\]\s+(.+)$", RegexOptions.CultureInvariant);
                        Require(annotation.Success, "unsupported native destination annotation: " + target);
                        preciseMask = annotation.Groups[1].Success ? annotation.Groups[1].Value : "*"; target = annotation.Groups[2].Value;
                        Require(preciseMask.Distinct().Count() == preciseMask.Length, "duplicate native precision component");
                    }
                    var destination = Register.Match(target);
                    Require(destination.Success && target[0] != 'v', "unsupported native destination: " + target);
                    string register = destination.Groups[1].Value, mask = destination.Groups[2].Success ? destination.Groups[2].Value : "xyzw";
                    ValidateTemporary(register, mask);
                    if (preciseMask == "*") preciseMask = mask;
                    Require(preciseMask.All(component => mask.IndexOf(component) >= 0), "native precision mask exceeds its destination");
                    var next = new Value[4];
                    for (int lane = 0; lane < 4; lane++)
                    {
                        if (mask.IndexOf("xyzw"[lane]) < 0) continue; Value value;
                        bool precise = preciseMask.IndexOf("xyzw"[lane]) >= 0;
                        string nativeOpcode = precise ? "precise:" + opcode : opcode;
                        if (opcode == "mov" && args.Length == 2) value = Read(args[1], lane, registers);
                        // MOVC tests raw condition bits, but modifiers on its
                        // two value operands have float semantics. Unmodified
                        // values still retain all bits, including integer data.
                        // https://learn.microsoft.com/windows/win32/direct3dhlsl/movc--sm4---asm-
                        else if (opcode == "movc" && args.Length == 4) value = Select(Read(args[1], lane, registers, false), Read(args[2], lane, registers), Read(args[3], lane, registers));
                        else if (Unary.Contains(opcode) && args.Length == 2) value = Apply(nativeOpcode, Read(args[1], lane, registers, floatModifiers, integerModifiers));
                        else if (Binary.Contains(opcode) && args.Length == 3) value = Apply(nativeOpcode, Read(args[1], lane, registers, floatModifiers, integerModifiers), Read(args[2], lane, registers, floatModifiers, integerModifiers));
                        else if (Ternary.Contains(opcode) && args.Length == 4) value = Apply(nativeOpcode, Read(args[1], lane, registers, floatModifiers, integerModifiers), Read(args[2], lane, registers, floatModifiers, integerModifiers), Read(args[3], lane, registers, floatModifiers, integerModifiers));
                        else if (semanticInputsOnly && opcode == "bfi" && args.Length == 5)
                            value = Apply(nativeOpcode, args.Skip(1).Select(source => Read(source, lane, registers, false)).ToArray());
                        else if ((opcode == "dp2" || opcode == "dp3" || opcode == "dp4") && args.Length == 3)
                        {
                            int width = opcode[2] - '0'; var operands = new List<Value>();
                            for (int source = 1; source <= 2; source++) for (int component = 0; component < width; component++) operands.Add(Read(args[source], component, registers));
                            // A replicated dot result shares one arithmetic
                            // operation; any precise destination constrains it.
                            value = Apply(preciseMask.Length != 0 ? "precise:" + opcode : opcode, operands.ToArray());
                        }
                        else if (IsLoad(opcode))
                        {
                            // Preserve every source lane and execution predicate:
                            // implicit derivatives/LOD may observe branch context.
                            var operands = new List<Value>(controls) { discarded };
                            for (int source = 1; source < args.Length; source++) for (int component = 0; component < 4; component++) operands.Add(Read(args[source], component, registers, false));
                            value = Apply(nativeOpcode + ":lane:" + lane + ":controls:" + controls.Length, operands.ToArray());
                        }
                        else throw new Unverified("unsupported native instruction: " + opcode);
                        next[lane] = saturate ? Apply(precise ? "precise:sat" : "sat", value) : value;
                    }
                    if (!registers.TryGetValue(register, out var old)) registers.Add(register, old = new Value[4]);
                    for (int lane = 0; lane < 4; lane++) if (next[lane] != null) old[lane] = next[lane];
                }
                return false;
            }
            private void Divide(string[] args, Dictionary<string, Value[]> registers)
            {
                // UDIV writes quotient then remainder; either destination may
                // be null. Evaluate both from the old register file before any
                // writes, including when a destination also supplies an input.
                // https://learn.microsoft.com/en-us/windows/win32/direct3dhlsl/udiv--sm4---asm-
                var pending = new Dictionary<string, Value[]>(StringComparer.Ordinal);
                for (int output = 0; output < 2; output++)
                {
                    string target = args[output]; if (target == "null") continue;
                    var destination = Register.Match(target);
                    Require(destination.Success && target[0] != 'v', "unsupported native divide destination: " + target);
                    string name = destination.Groups[1].Value;
                    string mask = destination.Groups[2].Success ? destination.Groups[2].Value : "xyzw";
                    ValidateTemporary(name, mask);
                    Require(mask.Distinct().Count() == mask.Length, "duplicate native divide destination component");
                    if (!pending.TryGetValue(name, out var lanes)) pending.Add(name, lanes = new Value[4]);
                    for (int lane = 0; lane < 4; lane++)
                    {
                        if (mask.IndexOf("xyzw"[lane]) < 0) continue;
                        Require(lanes[lane] == null, "overlapping native divide destinations");
                        lanes[lane] = Apply(output == 0 ? "udiv-quotient" : "udiv-remainder",
                            Read(args[2], lane, registers, false), Read(args[3], lane, registers, false));
                    }
                }
                Require(pending.Count != 0, "native divide has no observable destination");
                foreach (var pair in pending)
                {
                    if (!registers.TryGetValue(pair.Key, out var lanes)) registers.Add(pair.Key, lanes = new Value[4]);
                    for (int lane = 0; lane < 4; lane++) if (pair.Value[lane] != null) lanes[lane] = pair.Value[lane];
                }
            }
            private Value Read(string source, int lane, Dictionary<string, Value[]> registers, bool floatModifiers = true, bool integerModifiers = false)
            {
                bool negative = source.StartsWith("-", StringComparison.Ordinal); if (negative) source = source.Substring(1);
                bool absolute = source.StartsWith("|", StringComparison.Ordinal) && source.EndsWith("|", StringComparison.Ordinal);
                Require(floatModifiers || !absolute && (!negative || integerModifiers), "native integer or mixed-type source modifier is unsupported");
                if (absolute) source = source.Substring(1, source.Length - 2); Value value;
                if (source.StartsWith("l(", StringComparison.Ordinal) && source.EndsWith(")", StringComparison.Ordinal))
                {
                    string[] components = source.Substring(2, source.Length - 3).Split(','); Require(components.Length == 1 || components.Length == 4, "unsupported native literal width");
                    string component = components[components.Length == 1 ? 0 : lane].Trim();
                    Require(component.StartsWith("0x", StringComparison.Ordinal) && uint.TryParse(component.Substring(2), NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture, out _), "native literal does not preserve exact bits");
                    value = Literal(uint.Parse(component.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                }
                else
                {
                    var register = Register.Match(source); var buffer = Buffer.Match(source); var resource = Resource.Match(source);
                    var dynamicBuffer = DynamicBuffer.Match(source); var dynamicTemporary = DynamicTemporary.Match(source);
                    if (register.Success)
                    {
                        string key = register.Groups[1].Value; int component = Lane(register.Groups[2].Value, lane);
                        ValidateTemporary(key, "xyzw"[component].ToString());
                        Require(registers.TryGetValue(key, out var values) && values[component] != null, "native register read is undefined"); value = values[component];
                    }
                    else if (dynamicTemporary.Success) value = ReadTemporary(dynamicTemporary, lane, registers);
                    else if (buffer.Success)
                    {
                        int slot = Number(buffer.Groups[1].Value), row = Number(buffer.Groups[2].Value), component = Lane(buffer.Groups[3].Value, lane);
                        Require(declaredBuffers.ContainsKey(slot), "native buffer slot is undeclared");
                        if (opaqueBuffers.TryGetValue(slot, out var opaqueFields))
                        {
                            value = OpaqueBuffer(slot, Literal((uint)row), component, opaqueFields);
                            if (absolute) value = Apply("fabs", value); if (negative) value = Apply(floatModifiers ? "fneg" : "ineg", value); return value;
                        }
                        Require(buffers.TryGetValue(slot, out var description), "native buffer slot has no verified owner"); int offset = checked(row * 16 + component * 4);
                        Require(offset + 4 <= declaredBuffers[slot], "native literal buffer read exceeds its declaration");
                        var matches = description.Fields.Where(f => offset >= f.Offset && offset + 4 <= f.Offset + f.Size).ToArray();
                        Require(matches.Length == 1, "native uniform lane has no unique field identity"); var field = matches[0];
                        string key = description.Name + ":" + field.Name + ":" + field.Shape + ":" + (offset - field.Offset);
                        if (!uniformValues.TryGetValue(key, out value))
                        {
                            if (offset == field.Offset && field.Scalar && scalarNames.Contains(field.Name) && constants.TryGetValue(field.Name, out float scalar))
                            {
                                ObservedConstants?.Add(field.Name);
                                value = new Value(expressions.Literal(FloatBits(scalar)), new[] { field.Name });
                            }
                            else if (offset == field.Offset && field.IntegerScalar && integerScalarNames.Contains(field.Name) &&
                                ZeroIntegerUniforms?.Contains(field.Name) == true && constants.TryGetValue(field.Name, out float integerZero) && FloatBits(integerZero) == 0)
                            {
                                ObservedConstants?.Add(field.Name);
                                value = new Value(expressions.Literal(0), new[] { field.Name });
                            }
                            else value = new Value(expressions.Add("uniform:" + key, input: new D3DShaderInputs.Input(field.Name, 1, kind: D3DShaderInputs.InputKind.Uniform)),
                                domain: field.Scalar && scalarNames.Contains(field.Name) ? UniformDomain(field.Name) : null);
                            uniformValues.Add(key, value);
                        }
                    }
                    else if (dynamicBuffer.Success)
                    {
                        Require(allowOpaqueBuffers, "native dynamic buffer correspondence is unsupported");
                        int slot = Number(dynamicBuffer.Groups[1].Value), component = Lane(dynamicBuffer.Groups[4].Value, lane);
                        Require(declaredBuffers.ContainsKey(slot), "native dynamic buffer slot is undeclared");
                        Value address = Read(dynamicBuffer.Groups[2].Value, 0, registers, false);
                        if (dynamicBuffer.Groups[3].Success) address = Apply("iadd", address, Literal((uint)Number(dynamicBuffer.Groups[3].Value)));
                        var fields = opaqueBuffers.TryGetValue(slot, out var opaqueFields) ? opaqueFields :
                            buffers.TryGetValue(slot, out var owner) ? owner.Fields.ToArray() : null;
                        Require(fields != null, "native dynamic buffer has no complete metadata set");
                        value = OpaqueBuffer(slot, address, component, fields);
                    }
                    else if (resource.Success)
                    {
                        string key = resource.Groups[1].Value + resource.Groups[2].Value;
                        Require(resources.TryGetValue(key, out var binding), "native resource has no verified owner");
                        Require(resourceModes.TryGetValue(key, out string mode), "native resource has no declaration"); int component = Lane(resource.Groups[3].Value, lane);
                        value = new Value(expressions.Add("resource:" + binding.Identity + ":" + mode + ":" + component,
                            input: new D3DShaderInputs.Input(binding.Name, 1, kind: D3DShaderInputs.InputKind.Resource)));
                    }
                    else throw new Unverified("unsupported native operand: " + source);
                }
                // Source abs/neg modifiers are exact sign-bit operations, including
                // signed zero. No host floating arithmetic participates.
                if (absolute) value = Apply("fabs", value); if (negative) value = Apply(floatModifiers ? "fneg" : "ineg", value); return value;
            }
            private Value Literal(uint bits) => new(expressions.Literal(bits));
            private Value OpaqueBuffer(int slot, Value address, int lane, IEnumerable<Field> fields)
            {
                var dependencies = new List<Value> { address };
                foreach (string name in fields.Select(f => f.Name).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal))
                    dependencies.Add(new Value(expressions.Add("opaque-uniform:" + name,
                        input: new D3DShaderInputs.Input(name, 1, kind: D3DShaderInputs.InputKind.Uniform))));
                return Apply("opaque-buffer:" + slot + ":" + lane, dependencies.ToArray());
            }
            private Value Apply(string operation, params Value[] operands)
            {
                var provenance = new HashSet<string>(StringComparer.Ordinal); foreach (var operand in operands) provenance.UnionWith(operand.Constants);
                uint?[] literals = operands.Select(v => expressions[v.Expression].Literal).ToArray();
                // Exact bit/integer or exactly representable dyadic folds do not
                // depend on permission to refactor. Other operations retain the
                // native precision contract in their expression identity.
                string foldOperation = operation.StartsWith("precise:", StringComparison.Ordinal) ? operation.Substring(8) : operation;
                if (literals.All(v => v.HasValue) && Fold(foldOperation, literals.Select(v => v.Value).ToArray(), out uint bits)) return new Value(expressions.Literal(bits), provenance);
                if (semanticInputsOnly) return SemanticOperation(operation, foldOperation, operands, provenance);
                if (foldOperation == "add" && TryZeroAdd(operands, out var identity))
                    return new Value(identity.Expression, provenance, identity.Domain);
                if (foldOperation == "mad" && TryUnitMad(operation, operands, out var normalized))
                    return new Value(normalized.Expression, provenance, normalized.Domain);
                // Compiler vectorization can also reverse the final add operands.
                // Canonicalize that order only when every possible sum has one
                // normal result bit pattern; NaN/zero/tie behavior stays opaque.
                if (foldOperation == "add" && operands[0].Expression > operands[1].Expression && UniqueSums(operands[0], operands[1]))
                    operands = new[] { operands[1], operands[0] };
                uint[] domain = null;
                if ((foldOperation == "fabs" || foldOperation == "fneg") && KnownDomain(operands[0]) is uint[] sourceDomain &&
                    sourceDomain.All(value => (value & 0x7fffffff) <= 0x7f800000))
                    domain = sourceDomain.Select(value => foldOperation == "fabs" ? value & 0x7fffffff : value ^ 0x80000000).Distinct().ToArray();
                return new Value(expressions.Add(operation, operands.Select(v => v.Expression).ToArray()), provenance, domain);
            }
            private Value Select(Value condition, Value yes, Value no)
            {
                var origins = condition.Constants.Concat(yes.Constants).Concat(no.Constants);
                if (expressions[condition.Expression].Literal is uint literal)
                { var selected = literal != 0 ? yes : no; return new Value(selected.Expression, origins, selected.Domain, selected.Numeric); }
                if (yes.Expression == no.Expression) return new Value(yes.Expression, origins, UnionDomain(KnownDomain(yes), KnownDomain(no)), Facts(yes) & Facts(no));
                return new Value(expressions.Add("select", new[] { condition.Expression, yes.Expression, no.Expression }), origins,
                    UnionDomain(KnownDomain(yes), KnownDomain(no)), Facts(yes) & Facts(no));
            }
            private static Value WithOrigins(Value value, IEnumerable<string> origins) => new(value.Expression, value.Constants.Concat(origins), value.Domain, value.Numeric);
            private static void AddControlOrigins(Dictionary<string, Value[]> registers, IEnumerable<string> origins)
            { foreach (var values in registers.Values) for (int lane = 0; lane < 4; lane++) if (values[lane] != null) values[lane] = WithOrigins(values[lane], origins); }
        }
        private static Dictionary<string, Value[]> Clone(Dictionary<string, Value[]> values) => values.ToDictionary(p => p.Key, p => (Value[])p.Value.Clone(), StringComparer.Ordinal);
        private static bool FloatSources(string opcode) => opcode == "mov" || opcode == "mad" ||
            new[] { "exp", "frc", "log", "rcp", "round_ne", "round_ni", "round_pi", "round_z", "rsq", "sqrt", "ftoi", "ftou", "f32tof16",
                "add", "div", "max", "min", "mul", "eq", "ge", "lt", "ne", "dp2", "dp3", "dp4" }.Contains(opcode);
        private static bool IsLoad(string opcode)
        {
            if (Loads.Contains(opcode)) return true;
            int suffix = opcode.IndexOf("_indexable(", StringComparison.Ordinal);
            return suffix > 0 && Loads.Contains(opcode.Substring(0, suffix)) &&
                Regex.IsMatch(opcode.Substring(suffix), @"^_indexable\([a-z0-9_]+\)\((?:float|int|uint)(?:,(?:float|int|uint)){3}\)$", RegexOptions.CultureInvariant);
        }
        private static int Number(string text)
        { Require(int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value) && value >= 0, "invalid native integer operand"); return value; }
        private static int Lane(string swizzle, int lane)
        {
            if (swizzle.Length == 0) return lane;
            Require(swizzle.Length == 1 || swizzle.Length == 4, "unsupported native source swizzle"); return "xyzw".IndexOf(swizzle[swizzle.Length == 1 ? 0 : lane]);
        }
        private static string[] Split(string text)
        {
            if (text.Length == 0) return Array.Empty<string>(); var result = new List<string>(); int depth = 0, start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '(' || text[i] == '[') depth++; if (text[i] == ')' || text[i] == ']') depth--;
                Require(depth >= 0, "malformed native operand"); if (text[i] != ',' || depth != 0) continue;
                result.Add(text.Substring(start, i - start).Trim()); start = i + 1;
            }
            Require(depth == 0, "malformed native operand"); result.Add(text.Substring(start).Trim()); return result.ToArray();
        }
    }
}
