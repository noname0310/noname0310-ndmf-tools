using System;
using System.Collections.Generic;
using System.Linq;
using Parameter = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters.Parameter;
using ValueType = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters.ValueType;

namespace Noname.AvatarTools.ParameterLayout
{
    internal sealed class ResolvedFloatPrecisionRule : FloatPrecisionSettings
    {
        internal bool Individual;
        internal readonly HashSet<string> Names = new(StringComparer.Ordinal);
        internal readonly List<string> Prefixes = new();
        internal bool Matches(string name) => Names.Contains(name) || Prefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal));
    }

    [Serializable]
    internal sealed class FloatPrecisionEncoding : ParameterLayoutEntry
    {
        public bool MinimumPrecision;
        internal bool Equivalent(FloatPrecisionEncoding other) => SameEncoding(other) && MinimumPrecision == other.MinimumPrecision;
        internal void Validate()
        {
            if (string.IsNullOrWhiteSpace(Name) || Type != ValueType.Float || !Valid)
                throw new InvalidOperationException("Invalid Float precision override contract: " + Name);
        }
    }

    internal static class FloatPrecisionOverrides
    {
        internal static void Validate(FloatPrecisionSettings settings)
        {
            if (settings == null || !Enum.IsDefined(typeof(FloatPrecisionMode), settings.Precision) ||
                !Enum.IsDefined(typeof(FloatPrecisionRepresentation), settings.Representation) ||
                settings.Precision != FloatPrecisionMode.KeepExisting && (settings.Bits < Parameter.MinBitWidth || settings.Bits > Parameter.MaxBitWidth))
                throw new InvalidOperationException("Choose a valid Float precision override (2 to 8 bits).");
        }

        internal static List<FloatPrecisionEncoding> Resolve(IEnumerable<ResolvedFloatPrecisionRule> rules, Parameter[] parameters)
        {
            var all = rules.ToArray();
            foreach (var rule in all)
            {
                Validate(rule);
                if (rule.Individual)
                    foreach (var name in rule.Names)
                    {
                        var parameter = parameters.FirstOrDefault(p => p.name == name);
                        if (parameter == null || parameter.valueType != ValueType.Float)
                            throw new InvalidOperationException("Individual Float precision override needs an existing Float input: " + name);
                    }
            }
            var result = new List<FloatPrecisionEncoding>();
            foreach (var parameter in parameters.Where(p => p.networkSynced && p.valueType == ValueType.Float))
            {
                var matched = all.Where(r => r.Matches(parameter.name)).ToArray();
                if (matched.Length == 0) continue;
                bool individual = matched.Any(r => r.Individual);
                var candidates = matched.Where(r => r.Individual == individual).Select(rule => new FloatPrecisionEncoding
                {
                    Name = parameter.name, Type = ValueType.Float,
                    BitWidth = rule.Precision == FloatPrecisionMode.KeepExisting ? parameter.GetBitWidthByType() : rule.Bits,
                    FlippedRepresentation = rule.Representation == FloatPrecisionRepresentation.KeepExisting
                        ? parameter.flippedRepresentation : rule.Representation == FloatPrecisionRepresentation.Unsigned,
                    MinimumPrecision = rule.Precision == FloatPrecisionMode.Minimum
                }).ToArray();
                if (candidates.Any(c => !c.Equivalent(candidates[0])))
                    throw new InvalidOperationException("Conflicting Float precision overrides at the same priority: " + parameter.name);
                result.Add(candidates[0]);
            }
            return result;
        }

        // Only build-owned copies are modified. The reference upload owns the
        // effective override on all platforms, including removed-input padding.
        internal static Parameter[] Apply(Parameter[] parameters, IEnumerable<FloatPrecisionEncoding> encodings)
        {
            var output = parameters.Select(ParameterLayoutModel.Copy).ToArray();
            var byName = output.ToDictionary(p => p.name, StringComparer.Ordinal);
            foreach (var encoding in encodings)
            {
                encoding.Validate();
                if (!byName.TryGetValue(encoding.Name, out var parameter) || !parameter.networkSynced) continue;
                if (parameter.valueType != ValueType.Float)
                    throw new InvalidOperationException("Recorded Float precision override conflicts with parameter type: " + encoding.Name);
                if (parameter.defaultValue < (encoding.FlippedRepresentation ? 0 : -1) || parameter.defaultValue > 1)
                    throw new InvalidOperationException("Float default is outside the overridden representation: " + encoding.Name);
                parameter.bitWidth = (byte)encoding.Cost;
                parameter.flippedRepresentation = encoding.FlippedRepresentation;
            }
            return output;
        }
    }
}
