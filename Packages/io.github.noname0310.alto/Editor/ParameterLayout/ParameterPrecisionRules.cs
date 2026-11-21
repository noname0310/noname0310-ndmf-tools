using System;
using System.Collections.Generic;
using System.Linq;
using Parameter = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters.Parameter;
using ValueType = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters.ValueType;

namespace Noname.AvatarTools.ParameterLayout
{
    internal sealed class ResolvedParameterPrecisionRule : ParameterPrecisionSettings
    {
        internal bool Individual;
        internal ParameterPrecisionType Type;
        internal readonly HashSet<string> Names = new(StringComparer.Ordinal);
        internal readonly List<string> Prefixes = new();
        internal bool Matches(string name) => Names.Contains(name) || Prefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal));
    }

    internal sealed class ResolvedParameterPrecisions
    {
        internal readonly List<FloatPrecisionEncoding> Floats = new();
        internal readonly List<ResolvedIntegerRange> Integers = new();
        internal readonly HashSet<string> SelectedIntegers = new(StringComparer.Ordinal);
    }

    internal sealed class ResolvedIntegerRange
    {
        internal string Name;
        internal int Minimum, Maximum;
    }

    internal static class ParameterPrecisionRules
    {
        internal static void Validate(ParameterPrecisionSettings rule, ParameterPrecisionType type = ParameterPrecisionType.Auto)
        {
            if (rule == null) throw new InvalidOperationException("Remove empty precision override rows.");
            if (type != ParameterPrecisionType.Int) FloatPrecisionOverrides.Validate(rule.Floats);
            if (type == ParameterPrecisionType.Float) return;
            if (rule.Integers == null || !Enum.IsDefined(typeof(IntegerRangeMode), rule.Integers.Range))
                throw new InvalidOperationException("Choose a valid Int range override.");
            if (rule.Integers.Range == IntegerRangeMode.LimitRange)
                IntegerEncoding.Width(rule.Integers.Minimum, rule.Integers.Maximum);
        }

        internal static ResolvedParameterPrecisions Resolve(IEnumerable<ResolvedParameterPrecisionRule> source, Parameter[] parameters)
        {
            var rules = source.ToArray();
            foreach (var rule in rules)
            {
                if (rule == null) throw new InvalidOperationException("Remove empty precision override rows.");
                if (!rule.Individual) { Validate(rule); continue; }
                if (!Enum.IsDefined(typeof(ParameterPrecisionType), rule.Type)) throw new InvalidOperationException("Choose a valid parameter precision type.");
                foreach (var name in rule.Names)
                {
                    var p = parameters.FirstOrDefault(p => p.name == name);
                    if (p == null || p.valueType is not (ValueType.Int or ValueType.Float))
                        throw new InvalidOperationException("Parameter precision override needs an existing Int or Float input: " + name);
                    if (rule.Type != ParameterPrecisionType.Auto && (rule.Type == ParameterPrecisionType.Float) != (p.valueType == ValueType.Float))
                        throw new InvalidOperationException("Parameter precision type differs from the built input: " + name);
                    Validate(rule, p.valueType == ValueType.Float ? ParameterPrecisionType.Float : ParameterPrecisionType.Int);
                }
            }
            var result = new ResolvedParameterPrecisions();
            foreach (var parameter in parameters.Where(p => p.networkSynced && p.valueType is ValueType.Int or ValueType.Float))
            {
                // A source's untouched type does not compete with another
                // source overriding that type. An individual Keep Existing row
                // is an explicit exception and still wins over source rules.
                var matched = rules.Where(r => r.Matches(parameter.name) && (r.Individual ||
                    (parameter.valueType == ValueType.Float
                        ? r.Floats.Precision != FloatPrecisionMode.KeepExisting || r.Floats.Representation != FloatPrecisionRepresentation.KeepExisting
                        : r.Integers.Range != IntegerRangeMode.KeepExisting))).ToArray();
                if (matched.Length == 0) continue;
                bool individual = matched.Any(r => r.Individual);
                matched = matched.Where(r => r.Individual == individual).ToArray();
                if (parameter.valueType == ValueType.Float)
                {
                    var floats = matched.Select(r =>
                    {
                        var f = new ResolvedFloatPrecisionRule { Individual = individual, Bits = r.Floats.Bits,
                            Precision = r.Floats.Precision, Representation = r.Floats.Representation };
                        f.Names.Add(parameter.name); return f;
                    });
                    result.Floats.AddRange(FloatPrecisionOverrides.Resolve(floats, new[] { parameter }));
                }
                else
                {
                    var range = matched[0].Integers;
                    if (matched.Any(r => r.Integers.Range != range.Range || range.Range == IntegerRangeMode.LimitRange &&
                        (r.Integers.Minimum != range.Minimum || r.Integers.Maximum != range.Maximum)))
                        throw new InvalidOperationException("Conflicting Int range overrides at the same priority: " + parameter.name);
                    result.SelectedIntegers.Add(parameter.name);
                    if (range.Range == IntegerRangeMode.LimitRange)
                        result.Integers.Add(new ResolvedIntegerRange { Name = parameter.name, Minimum = range.Minimum, Maximum = range.Maximum });
                }
            }
            return result;
        }
    }
}
