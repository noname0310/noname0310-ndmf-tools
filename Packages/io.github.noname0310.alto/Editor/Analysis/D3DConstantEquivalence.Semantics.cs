using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Noname.AvatarTools.Analysis
{
    internal static partial class D3DConstantEquivalence
    {
        // These are numerical classes, not bit identities. In particular Zero
        // includes both signs. They may prove a numeric comparison or integer
        // conversion, but never erase a dependency of an asuint/sign-bit reader.
        [Flags] private enum NumericFacts { None = 0, Finite = 1, Unit = 2, Zero = 4 }
        private const NumericFacts NumericZero = NumericFacts.Finite | NumericFacts.Unit | NumericFacts.Zero;

        private static readonly HashSet<string> IntegerSourceOperations = new(StringComparer.Ordinal)
        {
            "not", "ineg", "itof", "utof", "bfrev", "countbits", "firstbit_hi", "firstbit_lo", "firstbit_shi",
            "and", "or", "xor", "iadd", "imax", "imin", "ieq", "ine", "ige", "ilt", "ishl", "ishr", "ushr",
            "umax", "umin", "uge", "ult", "imad", "ubfe", "ibfe", "imul", "umul"
        };
        private static bool IntegerSources(string opcode) => IntegerSourceOperations.Contains(opcode);

        private sealed partial class Program
        {
            private NumericFacts Facts(Value value)
            {
                if (!(expressions[value.Expression].Literal is uint bits)) return value.Numeric;
                uint magnitude = bits & 0x7fffffff;
                if (magnitude == 0) return NumericZero;
                if (magnitude >= 0x7f800000) return NumericFacts.None;
                return NumericFacts.Finite | ((bits & 0x80000000) == 0 && magnitude <= 0x3f800000 ? NumericFacts.Unit : NumericFacts.None);
            }
            private bool Has(Value value, NumericFacts fact) => (Facts(value) & fact) == fact;
            private Value SemanticOperation(string nativeOperation, string operation, Value[] operands, HashSet<string> provenance)
            {
                Value Constant(uint bits) => new(expressions.Literal(bits), provenance);
                Value Identity(Value value) => new(value.Expression, provenance, value.Domain, value.Numeric);
                bool Bits(Value value, uint bits) => expressions[value.Expression].Literal == bits;
                if (operands.Length == 2)
                {
                    Value a = operands[0], b = operands[1];
                    if (operation == "and" && (Bits(a, 0) || Bits(b, 0) || IntegerComplements(a, b))) return Constant(0);
                    if ((operation == "or" || operation == "xor" || operation == "iadd") && Bits(a, 0)) return Identity(b);
                    if ((operation == "or" || operation == "xor" || operation == "iadd") && Bits(b, 0)) return Identity(a);
                    if (a.Expression == b.Expression)
                    {
                        if (operation == "xor" || operation == "ine") return Constant(0);
                        if (operation == "ieq") return Constant(uint.MaxValue);
                    }
                    if (Has(a, NumericFacts.Zero) && Has(b, NumericFacts.Zero))
                    {
                        if (operation == "eq" || operation == "ge") return Constant(uint.MaxValue);
                        if (operation == "ne" || operation == "lt") return Constant(0);
                    }
                }
                if ((operation == "ftoi" || operation == "ftou") && Has(operands[0], NumericFacts.Zero)) return Constant(0);

                NumericFacts facts = NumericFacts.None;
                // SAT maps even NaN to zero. This is a range fact; its exact zero
                // sign is deliberately not used. Native D3D instruction contract:
                // https://learn.microsoft.com/en-us/windows/win32/direct3dhlsl/saturate
                if (operation == "sat") facts = NumericFacts.Finite | NumericFacts.Unit | (Facts(operands[0]) & NumericFacts.Zero);
                else if (operation == "itof" || operation == "utof") facts = NumericFacts.Finite;
                else if (operation == "fabs") facts = Facts(operands[0]);
                else if (operation == "fneg") facts = Facts(operands[0]) & (NumericFacts.Finite | NumericFacts.Zero);
                else if (operation.StartsWith("round_", StringComparison.Ordinal) && Has(operands[0], NumericFacts.Zero)) facts = NumericZero;
                else if (operation == "add" && operands.All(v => Has(v, NumericFacts.Zero))) facts = NumericZero;
                else if (operation == "mul")
                {
                    if (Has(operands[0], NumericFacts.Zero) && Has(operands[1], NumericFacts.Finite) ||
                        Has(operands[1], NumericFacts.Zero) && Has(operands[0], NumericFacts.Finite)) facts = NumericZero;
                    else if (operands.All(v => Has(v, NumericFacts.Unit))) facts = NumericFacts.Finite | NumericFacts.Unit;
                }
                else if (operation == "dp2" || operation == "dp3" || operation == "dp4")
                {
                    int width = operation[2] - '0'; bool zero = true;
                    for (int lane = 0; lane < width; lane++)
                        zero &= Has(operands[lane], NumericFacts.Zero) && Has(operands[lane + width], NumericFacts.Finite) ||
                            Has(operands[lane + width], NumericFacts.Zero) && Has(operands[lane], NumericFacts.Finite);
                    if (zero) facts = NumericZero;
                }
                return new Value(expressions.Add(nativeOperation, operands.Select(v => v.Expression).ToArray()), provenance, numeric: facts);
            }
            private bool IntegerComplements(Value a, Value b)
            {
                var first = expressions[a.Expression]; var second = expressions[b.Expression];
                return ((first.Operation == "ieq" && second.Operation == "ine") || (first.Operation == "ine" && second.Operation == "ieq")) &&
                    (first.Children.SequenceEqual(second.Children) || first.Children.SequenceEqual(second.Children.Reverse()));
            }
            private (string Name, string Mask, string Precise) Destination(string text)
            {
                string precise = "";
                if (text.StartsWith("[", StringComparison.Ordinal))
                {
                    var annotation = Regex.Match(text, @"^\[precise(?:\(([xyzw]{1,4})\))?\]\s+(.+)$", RegexOptions.CultureInvariant);
                    Require(annotation.Success, "unsupported native destination annotation");
                    precise = annotation.Groups[1].Success ? annotation.Groups[1].Value : "*"; text = annotation.Groups[2].Value;
                }
                var target = Register.Match(text); Require(target.Success && text[0] != 'v', "unsupported native destination: " + text);
                string mask = target.Groups[2].Success ? target.Groups[2].Value : "xyzw";
                ValidateTemporary(target.Groups[1].Value, mask);
                Require(mask.Distinct().Count() == mask.Length && (precise == "*" || precise.All(c => mask.IndexOf(c) >= 0)), "unsupported native destination mask");
                return (target.Groups[1].Value, mask, precise);
            }
            private int ControlEnd(int start)
            {
                string opcode = instructions[start].Opcode;
                Require(opcode == "loop" || opcode == "switch", "unsupported native summary control");
                var stack = new Stack<string>(); stack.Push(opcode);
                for (int index = start + 1; index < instructions.Count; index++)
                {
                    string current = instructions[index].Opcode;
                    if (current == "loop" || current == "switch") { Require(stack.Count < 16, "native summary nesting budget exhausted"); stack.Push(current); }
                    else if (current == "endloop" || current == "endswitch")
                    {
                        Require(stack.Count != 0 && stack.Pop() == (current == "endloop" ? "loop" : "switch"), "unstructured native summary control");
                        if (stack.Count == 0) return index;
                    }
                }
                throw new Unverified("unterminated native summary control");
            }
            private int SummarizeControl(int start, Dictionary<string, Value[]> registers, ref Value discarded, Value[] controls)
            {
                // This is a dependency overapproximation, never a correspondence
                // proof. All data, predicates and dynamic addresses participate;
                // old destination values cover zero iterations and missing cases.
                // Pure writes get distinct opaque values with no numerical facts.
                int end = ControlEnd(start); var dependencies = new HashSet<Value>(controls) { discarded };
                var written = new HashSet<string>(StringComparer.Ordinal);
                var potentiallyWritten = new HashSet<string>(StringComparer.Ordinal);
                for (int index = start + 1; index < end; index++)
                {
                    var item = instructions[index]; if (item.Arguments.Length < 2) continue;
                    string opcode = item.Opcode.EndsWith("_sat", StringComparison.Ordinal) ? item.Opcode.Substring(0, item.Opcode.Length - 4) : item.Opcode;
                    int count = PureOperations.TryGetValue(opcode, out var operation) ? operation.Outputs.Length : 1;
                    for (int output = 0; output < count; output++)
                    {
                        string text = Regex.Replace(item.Arguments[output], @"^\[precise(?:\([xyzw]{1,4}\))?\]\s+", "");
                        var target = Register.Match(text); if (!target.Success) continue;
                        foreach (char component in target.Groups[2].Success ? target.Groups[2].Value : "xyzw")
                            potentiallyWritten.Add(target.Groups[1].Value + "." + "xyzw".IndexOf(component));
                    }
                }
                var inputValues = registers.Where(p => p.Key[0] == 'v').SelectMany(p => p.Value).Where(v => v != null).ToArray();
                var initial = new HashSet<string>(StringComparer.Ordinal);
                foreach (var pair in registers) for (int lane = 0; lane < 4; lane++) if (pair.Value[lane] != null) initial.Add(pair.Key + "." + lane);
                void Source(string text, HashSet<string> assigned, HashSet<string> produced)
                {
                    if (text.StartsWith("-", StringComparison.Ordinal)) text = text.Substring(1);
                    if (text.StartsWith("|", StringComparison.Ordinal) && text.EndsWith("|", StringComparison.Ordinal)) text = text.Substring(1, text.Length - 2);
                    var source = Register.Match(text); var dynamic = DynamicBuffer.Match(text);
                    if (dynamic.Success) { Source(dynamic.Groups[2].Value, assigned, produced); Require(declaredBuffers.ContainsKey(Number(dynamic.Groups[1].Value)), "undeclared native summary buffer"); return; }
                    var temporary = DynamicTemporary.Match(text);
                    if (temporary.Success)
                    {
                        Source(temporary.Groups[2].Value, assigned, produced);
                        Require(indexableTemporaries.TryGetValue(temporary.Groups[1].Value, out var declaration), "undeclared native summary temporary");
                        var addressRegister = Register.Match(temporary.Groups[2].Value);
                        string addressLane = addressRegister.Groups[1].Value + "." + "xyzw".IndexOf(addressRegister.Groups[2].Value[0]);
                        Require(!potentiallyWritten.Contains(addressLane), "native temporary summary address changes within the control block");
                        var address = Read(temporary.Groups[2].Value, 0, registers, false);
                        if (temporary.Groups[3].Success) address = Apply("iadd", address, Literal((uint)Number(temporary.Groups[3].Value)));
                        Require(TemporaryAddressBounded(address, declaration.Rows), "native temporary summary address is not proved in bounds");
                        string mask = temporary.Groups[4].Success ? temporary.Groups[4].Value : "xyzw";
                        for (int row = 0; row < declaration.Rows; row++) Source(temporary.Groups[1].Value + "[" + row + "]." + mask, assigned, produced);
                        return;
                    }
                    if (source.Success)
                    {
                        string name = source.Groups[1].Value;
                        for (int lane = 0; lane < 4; lane++)
                        {
                            int component = Lane(source.Groups[2].Value, lane);
                            ValidateTemporary(name, "xyzw"[component].ToString());
                            if (produced.Contains(name + "." + component)) continue;
                            if (registers.TryGetValue(name, out var values) && values[component] != null) dependencies.Add(values[component]);
                            else if (!assigned.Contains(name + "." + component)) dependencies.UnionWith(inputValues);
                        }
                        return;
                    }
                    // Constants and read-only bindings add no vertex semantic of
                    // their own. The only nonliteral address grammar is handled
                    // above; unsupported addressing/side effects fail closed.
                    var buffer = Buffer.Match(text); var resource = Resource.Match(text);
                    if (buffer.Success) { Require(declaredBuffers.ContainsKey(Number(buffer.Groups[1].Value)), "undeclared native summary buffer"); return; }
                    if (resource.Success) { Require(resourceModes.ContainsKey(resource.Groups[1].Value + resource.Groups[2].Value), "undeclared native summary resource"); return; }
                    Require(Regex.IsMatch(text, @"^l\(0x[0-9a-fA-F]+(?:,\s*0x[0-9a-fA-F]+){0,3}\)$", RegexOptions.CultureInvariant), "unsupported native summary operand: " + text);
                }
                void Scan(int begin, int last, HashSet<string> assigned, HashSet<string> produced, int depth)
                {
                    Require(depth <= 16, "native summary nesting budget exhausted");
                    var caseAssigned = new HashSet<string>(assigned, StringComparer.Ordinal);
                    var caseProduced = new HashSet<string>(produced, StringComparer.Ordinal);
                    for (int index = begin; index < last; index++)
                    {
                        Require(++executedInstructions <= 131072, "native control-flow proof budget exhausted");
                        var instruction = instructions[index]; string opcode = instruction.Opcode; var args = instruction.Arguments;
                        if (opcode == "if_nz" || opcode == "if_z")
                        {
                            Require(args.Length == 1 && instruction.End < last && instruction.End >= index, "unstructured native summary branch");
                            Source(args[0], assigned, produced); var yes = new HashSet<string>(assigned, StringComparer.Ordinal); var no = new HashSet<string>(assigned, StringComparer.Ordinal);
                            var yesProduced = new HashSet<string>(produced, StringComparer.Ordinal); var noProduced = new HashSet<string>(produced, StringComparer.Ordinal);
                            Scan(index + 1, instruction.Else < 0 ? instruction.End : instruction.Else, yes, yesProduced, depth + 1);
                            if (instruction.Else >= 0) Scan(instruction.Else + 1, instruction.End, no, noProduced, depth + 1);
                            yes.IntersectWith(no); assigned.Clear(); assigned.UnionWith(yes);
                            yesProduced.IntersectWith(noProduced); produced.Clear(); produced.UnionWith(yesProduced); index = instruction.End; continue;
                        }
                        if (opcode == "loop" || opcode == "switch")
                        {
                            Require(args.Length == (opcode == "loop" ? 0 : 1), "malformed native summary control");
                            if (args.Length != 0) Source(args[0], assigned, produced); int nestedEnd = ControlEnd(index);
                            Require(nestedEnd < last, "unstructured nested native summary");
                            Scan(index + 1, nestedEnd, new HashSet<string>(assigned, StringComparer.Ordinal), new HashSet<string>(produced, StringComparer.Ordinal), depth + 1); index = nestedEnd; continue;
                        }
                        if (opcode == "case" || opcode == "default")
                        {
                            Require(args.Length == (opcode == "case" ? 1 : 0), "malformed native summary case");
                            assigned.IntersectWith(caseAssigned); produced.IntersectWith(caseProduced); continue;
                        }
                        if (opcode == "break" || opcode == "continue" || opcode == "nop") { Require(args.Length == 0, "malformed native summary flow"); continue; }
                        if (opcode == "breakc_nz" || opcode == "breakc_z" || opcode == "continuec_nz" || opcode == "continuec_z")
                        { Require(args.Length == 1, "malformed native summary predicate"); Source(args[0], assigned, produced); continue; }
                        if (opcode.EndsWith("_sat", StringComparison.Ordinal)) opcode = opcode.Substring(0, opcode.Length - 4);
                        bool modeled = PureOperations.TryGetValue(opcode, out var operation);
                        int destinations = modeled ? operation.Outputs.Length : 1;
                        bool pure = opcode == "mov" || opcode == "movc" || Unary.Contains(opcode) || Binary.Contains(opcode) || Ternary.Contains(opcode) ||
                            opcode == "dp2" || opcode == "dp3" || opcode == "dp4" || modeled || IsLoad(opcode);
                        Require(pure && args.Length > destinations, "unsupported native summary instruction: " + opcode);
                        if (modeled) Require(args.Length == destinations + operation.Sources &&
                            (!instruction.Opcode.EndsWith("_sat", StringComparison.Ordinal) || operation.Saturate), "malformed native summary operation");
                        for (int source = destinations; source < args.Length; source++) Source(args[source], assigned, produced);
                        for (int output = 0; output < destinations; output++)
                        {
                            if (args[output] == "null" && destinations == 2) continue; var destination = Destination(args[output]);
                            foreach (char component in destination.Mask)
                            { string key = destination.Name + "." + "xyzw".IndexOf(component); written.Add(key); assigned.Add(key); produced.Add(key); }
                        }
                    }
                }
                var noneProduced = new HashSet<string>(StringComparer.Ordinal);
                if (instructions[start].Opcode == "switch")
                { Require(instructions[start].Arguments.Length == 1, "malformed native switch"); Source(instructions[start].Arguments[0], initial, noneProduced); }
                else Require(instructions[start].Arguments.Length == 0, "malformed native loop");
                Scan(start + 1, end, new HashSet<string>(initial, StringComparer.Ordinal), noneProduced, 0);
                var all = dependencies.ToArray(); var origins = all.SelectMany(v => v.Constants).ToArray();
                foreach (string key in written)
                {
                    int dot = key.LastIndexOf('.'); string register = key.Substring(0, dot); int lane = Number(key.Substring(dot + 1));
                    if (!registers.TryGetValue(register, out var values)) registers.Add(register, values = new Value[4]);
                    var sources = values[lane] != null ? all.Concat(new[] { values[lane] }).ToArray() : all.Concat(inputValues).ToArray();
                    values[lane] = new Value(expressions.Add("semantic-summary:" + start + ":" + key, sources.Select(v => v.Expression).ToArray()), sources.SelectMany(v => v.Constants));
                }
                // Nontermination is observable even when no loop result is read.
                // A switch itself is finite; any nested loop still contributes.
                if (instructions.Skip(start).Take(end - start).Any(i => i.Opcode == "loop"))
                    discarded = new Value(expressions.Add("semantic-loop-termination:" + start, all.Select(v => v.Expression).ToArray()), origins);
                return end;
            }
        }
    }
}
