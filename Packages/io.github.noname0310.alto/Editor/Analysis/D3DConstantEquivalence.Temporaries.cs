using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Noname.AvatarTools.Analysis
{
    internal static partial class D3DConstantEquivalence
    {
        private static readonly Regex TemporaryRow = new(@"^(x\d+)\[(\d+)\]$", RegexOptions.CultureInvariant);
        private static readonly Regex DynamicTemporary = new(@"^(x\d+)\[([rvo]\d+\.[xyzw])(?:\s*\+\s*(\d+))?\](?:\.([xyzw]{1,4}))?$", RegexOptions.CultureInvariant);

        private sealed partial class Program
        {
            private readonly Dictionary<string, (int Rows, int Width)> indexableTemporaries = new(StringComparer.Ordinal);

            private void DeclareTemporary(string operand)
            {
                Require(semanticInputsOnly, "native indexable temporary correspondence is unsupported");
                var match = Regex.Match(operand, @"^(x\d+)\[(\d+)\],\s*([1-4])$", RegexOptions.CultureInvariant);
                Require(match.Success, "unsupported native indexable temporary declaration");
                string name = match.Groups[1].Value; int rows = Number(match.Groups[2].Value), width = Number(match.Groups[3].Value);
                Require(rows > 0 && rows <= 256 && !indexableTemporaries.ContainsKey(name) &&
                    indexableTemporaries.Values.Sum(v => v.Rows * v.Width) + rows * width <= 4096, "native indexable temporary budget exhausted");
                indexableTemporaries.Add(name, (rows, width));
            }

            private void ValidateTemporary(string name, string components)
            {
                if (!name.StartsWith("x", StringComparison.Ordinal)) return;
                var match = TemporaryRow.Match(name);
                Require(semanticInputsOnly && match.Success && indexableTemporaries.TryGetValue(match.Groups[1].Value, out _),
                    "undeclared native indexable temporary");
                var declaration = indexableTemporaries[match.Groups[1].Value];
                Require(Number(match.Groups[2].Value) < declaration.Rows && components.All(c => "xyzw".IndexOf(c) < declaration.Width),
                    "native indexable temporary access exceeds its declaration");
            }

            private Value ReadTemporary(Match reference, int lane, Dictionary<string, Value[]> registers)
            {
                string name = reference.Groups[1].Value;
                Require(semanticInputsOnly && indexableTemporaries.TryGetValue(name, out _), "undeclared native dynamic temporary");
                var declaration = indexableTemporaries[name]; int component = Lane(reference.Groups[4].Value, lane);
                Require(component < declaration.Width, "native dynamic temporary component exceeds its declaration");
                Value address = Read(reference.Groups[2].Value, 0, registers, false);
                if (reference.Groups[3].Success) address = Apply("iadd", address, Literal((uint)Number(reference.Groups[3].Value)));
                if (expressions[address.Expression].Literal is uint row)
                {
                    Require(row < declaration.Rows && registers.TryGetValue(name + "[" + row + "]", out var values) && values[component] != null,
                        "native dynamic temporary read is undefined");
                    var value = registers[name + "[" + row + "]"][component];
                    return WithOrigins(value, address.Constants);
                }
                Require(TemporaryAddressBounded(address, declaration.Rows), "native dynamic temporary address is not proved in bounds");
                // A dynamic read can select every row. Keep the address and all
                // possible stored values, with no literal/range/domain facts.
                // Uninitialized rows and dynamic stores remain unsupported.
                var operands = new List<Value> { address };
                for (int i = 0; i < declaration.Rows; i++)
                {
                    Require(registers.TryGetValue(name + "[" + i + "]", out var values) && values[component] != null,
                        "native dynamic temporary has an uninitialized row");
                    operands.Add(values[component]);
                }
                return Apply("semantic-indexable-read:" + name + ":" + component, operands.ToArray());
            }

            private bool TemporaryAddressBounded(Value address, int rows)
            {
                var memo = new Dictionary<int, uint>();
                uint Maximum(int id, int depth)
                {
                    if (memo.TryGetValue(id, out uint known)) return known;
                    if (depth >= 16 || memo.Count >= 256) return uint.MaxValue;
                    var value = expressions[id]; if (value.Literal is uint bits) return bits;
                    uint result = uint.MaxValue; var children = value.Children;
                    if (children.Length == 2)
                    {
                        uint a = Maximum(children[0], depth + 1), b = Maximum(children[1], depth + 1);
                        if (value.Operation == "and" || value.Operation == "umin") result = Math.Min(a, b);
                        else if (value.Operation == "iadd" && (ulong)a + b <= uint.MaxValue) result = a + b;
                        else if (value.Operation == "udiv-remainder" && expressions[children[1]].Literal is uint divisor && divisor > 0) result = divisor - 1;
                    }
                    else if (value.Operation == "select" && children.Length == 3)
                        result = Math.Max(Maximum(children[1], depth + 1), Maximum(children[2], depth + 1));
                    memo[id] = result; return result;
                }
                return Maximum(address.Expression, 0) < rows;
            }
        }
    }
}
