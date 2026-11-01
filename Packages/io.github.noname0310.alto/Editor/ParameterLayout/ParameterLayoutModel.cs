using System;
using System.Collections.Generic;
using System.Linq;
using VRC.SDK3.Avatars.ScriptableObjects;
using Parameter = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters.Parameter;

namespace Noname.AvatarTools.ParameterLayout
{
    [Serializable]
    internal class ParameterLayoutEntry
    {
        public string Name;
        public VRCExpressionParameters.ValueType Type;
        // Zero in a pre-3.10.6 record means the historical eight-bit encoding.
        public int BitWidth;
        public bool FlippedRepresentation;
        internal int Cost => Type == VRCExpressionParameters.ValueType.Bool ? 1 : BitWidth == 0 ? 8 : BitWidth;
        internal (VRCExpressionParameters.ValueType Type, int Bits, bool Flipped) Encoding
            => (Type, Cost, Type != VRCExpressionParameters.ValueType.Bool && FlippedRepresentation);
        internal bool SameEncoding(ParameterLayoutEntry other) => other != null && Encoding == other.Encoding;
        internal bool SameEncoding(Parameter other) => SameEncoding(Capture(other));
        internal static ParameterLayoutEntry Capture(Parameter parameter) => new() {
            Name = parameter.name, Type = parameter.valueType, BitWidth = parameter.GetBitWidthByType(),
            FlippedRepresentation = parameter.flippedRepresentation };
        internal Parameter Declaration(string name = null) => new() {
            name = name ?? Name, valueType = Type, bitWidth = (byte)Cost, flippedRepresentation = FlippedRepresentation,
            networkSynced = true, saved = false };
        internal bool Valid => Enum.IsDefined(typeof(VRCExpressionParameters.ValueType), Type) &&
            (Type == VRCExpressionParameters.ValueType.Bool || Cost >= Parameter.MinBitWidth && Cost <= Parameter.MaxBitWidth);
    }

    [Serializable]
    internal sealed class ParameterLayoutRecord
    {
        public int Version = 4;
        public string BlueprintId;
        public ParameterLayoutPlatform ReferencePlatform;
        public string UploadedUtc;
        public string InvalidatedUtc;
        public List<ParameterLayoutEntry> Parameters = new();
        public List<IntegerEncoding> IntegerEncodings = new();
        // Unity's inline serialization materializes null nested classes on read.
        // Store presence explicitly so ordinary and integer-only records stay valid.
        public bool UsesMultiplexing;
        public MultiplexEncoding MultiplexingMetadata;
        internal MultiplexEncoding Multiplexing
        {
            get => UsesMultiplexing ? MultiplexingMetadata : null;
            set { UsesMultiplexing = value != null; MultiplexingMetadata = value; }
        }
    }

    internal sealed class ParameterLayoutResult
    {
        internal Parameter[] Parameters;
        internal int Padding;
        internal readonly HashSet<string> PaddingNames = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, string> PaddingReferenceNames = new(StringComparer.Ordinal);
        internal readonly List<string> MadeLocal = new();
    }

    // Align ordinary wire declarations. Only our explicitly recorded codecs
    // is understood; external transport protocols are never inferred.
    internal static class ParameterLayoutModel
    {
        internal static Parameter[] Read(Parameter[] parameters)
        {
            var result = (parameters ?? Array.Empty<Parameter>()).Where(p => p != null && !string.IsNullOrEmpty(p.name)).ToArray();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var parameter in result)
            {
                if (!names.Add(parameter.name)) throw new InvalidOperationException("Duplicate expression parameter: " + parameter.name);
                if (!Enum.IsDefined(typeof(VRCExpressionParameters.ValueType), parameter.valueType))
                    throw new InvalidOperationException("Invalid expression parameter type: " + parameter.name);
                if (float.IsNaN(parameter.defaultValue) || float.IsInfinity(parameter.defaultValue))
                    throw new InvalidOperationException("Non-finite expression parameter default: " + parameter.name);
            }
            return result;
        }

        internal static List<ParameterLayoutEntry> Capture(Parameter[] parameters) => Read(parameters)
            .Where(p => p.networkSynced).Select(ParameterLayoutEntry.Capture).ToList();

        internal static void Validate(ParameterLayoutRecord record, bool allowInvalidated = false)
        {
            if (record == null || record.Version < 1 || record.Version > 4 || record.Parameters == null ||
                !Enum.IsDefined(typeof(ParameterLayoutPlatform), record.ReferencePlatform))
                throw new InvalidOperationException("Unsupported or incomplete parameter layout record. Upload the reference platform again.");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in record.Parameters)
                if (entry == null || string.IsNullOrEmpty(entry.Name) || !names.Add(entry.Name) ||
                    !entry.Valid)
                    throw new InvalidOperationException("The parameter layout record contains invalid or duplicate declarations.");
            if (record.IntegerEncodings == null || record.Version == 1 && record.IntegerEncodings.Count != 0)
                throw new InvalidOperationException("Invalid integer encoding metadata in the parameter layout record.");
            var logicalNames = new HashSet<string>(StringComparer.Ordinal);
            var wireNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var encoding in record.IntegerEncodings)
            {
                if (encoding == null) throw new InvalidOperationException("Missing integer encoding metadata.");
                encoding.Validate();
                if (encoding.IsNative)
                {
                    if (record.Version < 4 || !logicalNames.Add(encoding.Name) || !record.Parameters.Any(p => p.Name == encoding.Name &&
                        p.Type == VRCExpressionParameters.ValueType.Int && p.Cost == encoding.NativeBitWidth && p.FlippedRepresentation == encoding.SignedRepresentation))
                        throw new InvalidOperationException("Missing or conflicting native integer declaration: " + encoding.Name);
                    continue;
                }
                if (!logicalNames.Add(encoding.Name) || names.Contains(encoding.Name))
                    throw new InvalidOperationException("Duplicate or synchronized logical integer in the parameter layout: " + encoding.Name);
                foreach (var bit in encoding.Bits)
                    if (!wireNames.Add(bit) || !record.Parameters.Any(p => p.Name == bit && p.Type == VRCExpressionParameters.ValueType.Bool))
                        throw new InvalidOperationException("Missing or shared integer wire declaration: " + bit);
            }
            if (logicalNames.Overlaps(wireNames)) throw new InvalidOperationException("Integer logical names overlap wire names.");
            if (record.UsesMultiplexing && record.Multiplexing == null)
                throw new InvalidOperationException("Missing time multiplexing metadata.");
            if (record.Multiplexing != null)
            {
                if (record.Version < 3) throw new InvalidOperationException("This layout version cannot contain time multiplexing metadata.");
                record.Multiplexing.Validate();
                foreach (var input in record.Multiplexing.Inputs)
                    if (!logicalNames.Add(input.Name) || names.Contains(input.Name) || wireNames.Contains(input.Name))
                        throw new InvalidOperationException("Conflicting multiplexed logical input: " + input.Name);
                foreach (var parameter in record.Multiplexing.WireDeclarations())
                    if (!wireNames.Add(parameter.name) || logicalNames.Contains(parameter.name) ||
                        !record.Parameters.Any(p => p.Name == parameter.name && p.SameEncoding(parameter)))
                        throw new InvalidOperationException("Missing or conflicting multiplexing wire declaration: " + parameter.name);
            }
            if (!allowInvalidated && !string.IsNullOrEmpty(record.InvalidatedUtc))
                throw new InvalidOperationException("The reference platform was uploaded without a new parameter layout at " +
                    record.InvalidatedUtc + ". Upload it again with ALTO Parameter Layout enabled before building other platforms.");
        }

        internal static Parameter Copy(Parameter source) => new()
        {
            name = source.name, valueType = source.valueType, defaultValue = source.defaultValue,
            saved = source.saved, networkSynced = source.networkSynced,
            bitWidth = source.bitWidth, flippedRepresentation = source.flippedRepresentation,
            useCustomLerpDuration = source.useCustomLerpDuration, customLerpDuration = source.customLerpDuration
        };

        internal static ParameterLayoutResult Align(ParameterLayoutRecord record, Parameter[] current,
            AdditionalSyncedParameterPolicy additional, IEnumerable<string> reservedNames = null)
        {
            Validate(record);
            if (!Enum.IsDefined(typeof(AdditionalSyncedParameterPolicy), additional))
                throw new InvalidOperationException("Choose a valid policy for additional synchronized parameters.");
            var input = Read(current);
            var synchronized = input.Where(p => p.networkSynced).ToDictionary(p => p.name, StringComparer.Ordinal);
            var occupied = new HashSet<string>(input.Select(p => p.name), StringComparer.Ordinal);
            occupied.UnionWith(record.Parameters.Select(p => p.Name));
            if (reservedNames != null) occupied.UnionWith(reservedNames);
            var result = new ParameterLayoutResult(); var output = new List<Parameter>();
            for (int slot = 0; slot < record.Parameters.Count; slot++)
            {
                var entry = record.Parameters[slot];
                if (synchronized.TryGetValue(entry.Name, out var parameter))
                {
                    if (!entry.SameEncoding(parameter))
                        throw new InvalidOperationException("Parameter type, bit width or signed representation differs from the reference upload: " + entry.Name +
                            " (" + entry.Encoding + " -> " + ParameterLayoutEntry.Capture(parameter).Encoding + ").");
                    output.Add(Copy(parameter)); synchronized.Remove(entry.Name);
                }
                else
                {
                    // Never reuse a removed logical input's name: an Animator,
                    // sensor or menu may still observe its local value.
                    string stem = "__AdvancedParameterPadding_" + slot; string name = stem;
                    for (int suffix = 1; !occupied.Add(name); suffix++) name = stem + "_" + suffix;
                    output.Add(entry.Declaration(name));
                    result.Padding++;
                    result.PaddingNames.Add(name);
                    result.PaddingReferenceNames.Add(name, entry.Name);
                }
            }
            if (additional == AdditionalSyncedParameterPolicy.Error && synchronized.Count != 0)
                throw new InvalidOperationException("Synchronized parameters are absent from the reference upload: " +
                    string.Join(", ", synchronized.Keys.OrderBy(n => n, StringComparer.Ordinal)) +
                    ". Upload the reference platform first, or explicitly choose Keep Local.");
            foreach (var parameter in input)
                if (!parameter.networkSynced || synchronized.ContainsKey(parameter.name))
                {
                    var copy = Copy(parameter);
                    if (copy.networkSynced) { copy.networkSynced = false; result.MadeLocal.Add(copy.name); }
                    output.Add(copy);
                }
            result.Parameters = output.ToArray();
            return result;
        }

        internal static void ValidateLateDeclarations(IReadOnlyList<ParameterLayoutEntry> before, Parameter[] current)
        {
            var after = Capture(current).ToDictionary(p => p.Name, StringComparer.Ordinal);
            if (before.Count != after.Count || before.Any(p => !after.TryGetValue(p.Name, out var entry) || !p.SameEncoding(entry)))
                throw new InvalidOperationException("A later build processor changed synchronized parameter declarations after NDMF. " +
                    "Rebuild the reference platform with matching transport settings and records. " +
                    "ALTO Parameter Layout cannot infer an external transport protocol.");
        }

        // Only padding created by this build can be renamed. A matching wire position and
        // type are not evidence that an arbitrary encoded channel has the same meaning.
        internal static Parameter[] ReconcileLatePadding(IReadOnlyList<ParameterLayoutEntry> before, Parameter[] current,
            ISet<string> padding, Func<string, bool> isUnobserved,
            IDictionary<string, string> referenceNames = null, IReadOnlyList<Parameter> localBefore = null)
        {
            var input = Read(current);
            var after = Capture(input);
            var byName = after.ToDictionary(p => p.Name, StringComparer.Ordinal);
            if (before.Count == after.Count && before.All(p => byName.TryGetValue(p.Name, out var entry) && p.SameEncoding(entry)))
                return input;
            var names = new HashSet<string>(before.Select(p => p.Name), StringComparer.Ordinal);
            var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
            var locals = (localBefore ?? Array.Empty<Parameter>()).Where(p => !p.networkSynced)
                .ToDictionary(p => p.name, StringComparer.Ordinal);
            var restoredLocals = new Dictionary<string, Parameter>(StringComparer.Ordinal);
            if (before.Count == after.Count && padding != null && isUnobserved != null)
                for (int slot = 0; slot < before.Count; slot++)
                {
                    var expected = before[slot]; var actual = after[slot];
                    if (!padding.Contains(expected.Name) || byName.ContainsKey(expected.Name) || names.Contains(actual.Name) ||
                        !expected.SameEncoding(actual) || !isUnobserved(expected.Name)) continue;
                    if (locals.TryGetValue(actual.Name, out var original))
                    {
                        // A late layout tool may re-enable a known local input under
                        // the original reference name. Restore both its private value
                        // and our anonymous wire only when the exact slot provenance
                        // and local declaration match. Never discard its consumers.
                        var changed = input.Single(p => p.name == actual.Name);
                        if (referenceNames == null || !referenceNames.TryGetValue(expected.Name, out var referenceName) ||
                            referenceName != actual.Name || !actual.SameEncoding(original) ||
                            original.defaultValue != changed.defaultValue || original.saved != changed.saved) continue;
                        restoredLocals.Add(actual.Name, Copy(original));
                    }
                    else if (!isUnobserved(actual.Name)) continue;
                    replacements.Add(actual.Name, expected.Name);
                }
            var restored = new HashSet<string>(replacements.Values, StringComparer.Ordinal);
            var normalized = input.Where(p => !restored.Contains(p.name)).Select(p =>
            {
                if (!replacements.TryGetValue(p.name, out var name)) return p;
                return ParameterLayoutEntry.Capture(p).Declaration(name);
            }).Concat(restoredLocals.Values).ToArray();
            ValidateLateDeclarations(before, normalized);
            return normalized;
        }
    }
}
