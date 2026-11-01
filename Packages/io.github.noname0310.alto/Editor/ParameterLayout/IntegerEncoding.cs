using System;
using System.Collections.Generic;
using System.Linq;
using VRC.SDK3.Avatars.ScriptableObjects;
using Parameter = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters.Parameter;

namespace Noname.AvatarTools.ParameterLayout
{
    [Serializable]
    internal sealed class IntegerEncoding
    {
        public string Name;
        public int Minimum, Maximum;
        public int NativeBitWidth;
        public bool SignedRepresentation;
        internal bool IsNative => NativeBitWidth != 0;
        // Least significant bit first. Stored names, not regenerated guesses, form the wire contract.
        public List<string> Bits = new();

        internal static int Width(int minimum, int maximum)
        {
            if (minimum < -128 || maximum > 255 || maximum <= minimum || maximum - minimum >= 128)
                throw new InvalidOperationException("Integer compression requires 2 to 128 values within the SDK integer range.");
            int width = 1;
            while ((1 << width) <= maximum - minimum) width++;
            return width;
        }

        internal int Encode(int value) => Math.Max(Minimum, Math.Min(Maximum, value)) - Minimum;
        internal int Decode(int bits) => Math.Min(Maximum, Minimum + bits);

        internal void Validate()
        {
            if (IsNative)
            {
                int minimum = SignedRepresentation ? -(1 << (NativeBitWidth - 1)) : 0;
                int maximum = SignedRepresentation ? (1 << (NativeBitWidth - 1)) - 1 : (1 << NativeBitWidth) - 1;
                if (string.IsNullOrWhiteSpace(Name) || NativeBitWidth < 2 || NativeBitWidth > 7 || Bits == null || Bits.Count != 0 ||
                    Minimum != minimum || Maximum != maximum) throw new InvalidOperationException("Invalid native integer encoding: " + Name);
                return;
            }
            if (string.IsNullOrWhiteSpace(Name) || Bits == null || Bits.Count != Width(Minimum, Maximum) ||
                Bits.Any(string.IsNullOrWhiteSpace) || Bits.Distinct(StringComparer.Ordinal).Count() != Bits.Count || Bits.Contains(Name))
                throw new InvalidOperationException("Invalid integer encoding contract: " + Name);
        }

        internal static IntegerEncoding Create(string name, int minimum, int maximum, ISet<string> occupied)
        {
            var result = new IntegerEncoding { Name = name, Minimum = minimum, Maximum = maximum };
            int width = Width(minimum, maximum);
            for (int bit = 0; bit < width; bit++)
            {
                // The reference upload records the chosen names, including collision suffixes.
                string stem = "__AdvancedInt/" + name + "/" + bit, candidate = stem;
                for (int suffix = 1; !occupied.Add(candidate); suffix++) candidate = stem + "_" + suffix;
                result.Bits.Add(candidate);
            }
            result.Validate();
            return result;
        }

        internal static IntegerEncoding CreateForSdk(Parameter parameter, int minimum, int maximum, ISet<string> occupied)
        {
            if (parameter == null || parameter.valueType != VRCExpressionParameters.ValueType.Int)
                throw new InvalidOperationException("Integer compression requires an Int expression parameter.");
            int current = parameter.GetBitWidthByType();
            int sourceMin = parameter.flippedRepresentation ? -(1 << (current - 1)) : 0;
            int sourceMax = parameter.flippedRepresentation ? (1 << (current - 1)) - 1 : (1 << current) - 1;
            if (minimum < sourceMin || maximum > sourceMax)
                throw new InvalidOperationException("Integer range exceeds its SDK wire representation: " + parameter.name);
            for (int bits = 2; bits < current; bits++)
            {
                int low = parameter.flippedRepresentation ? -(1 << (bits - 1)) : 0;
                int high = parameter.flippedRepresentation ? (1 << (bits - 1)) - 1 : (1 << bits) - 1;
                if (minimum == low && maximum == high)
                    return new IntegerEncoding { Name = parameter.name, Minimum = minimum, Maximum = maximum,
                        NativeBitWidth = bits, SignedRepresentation = parameter.flippedRepresentation };
            }
            var result = Create(parameter.name, minimum, maximum, occupied);
            if (result.Bits.Count >= current)
                throw new InvalidOperationException("Integer encoding does not reduce the existing SDK bit width: " + parameter.name);
            if (parameter.useCustomLerpDuration && parameter.customLerpDuration != 0)
                throw new InvalidOperationException("This integer range requires packed bits, which cannot preserve SDK interpolation: " + parameter.name);
            return result;
        }

        internal Parameter[] Declarations(Parameter logical)
        {
            Validate();
            if (logical.valueType != VRCExpressionParameters.ValueType.Int || !logical.networkSynced ||
                logical.defaultValue != (int)logical.defaultValue || logical.defaultValue < Minimum || logical.defaultValue > Maximum)
                throw new InvalidOperationException("Compressed input must be a synchronized Int with a whole-number default inside its range: " + Name);
            if (IsNative)
            {
                if (logical.flippedRepresentation != SignedRepresentation || NativeBitWidth > logical.GetBitWidthByType())
                    throw new InvalidOperationException("Native integer representation differs from the recorded contract: " + Name);
                var native = ParameterLayoutModel.Copy(logical); native.bitWidth = (byte)NativeBitWidth;
                return new[] { native };
            }
            if (logical.useCustomLerpDuration && logical.customLerpDuration != 0)
                throw new InvalidOperationException("Recorded packed integer encoding cannot preserve SDK interpolation: " + Name);
            int initial = Encode((int)logical.defaultValue);
            var local = ParameterLayoutModel.Copy(logical); local.networkSynced = false;
            return Bits.Select((name, bit) => new Parameter
            {
                name = name, valueType = VRCExpressionParameters.ValueType.Bool, networkSynced = true,
                saved = false, defaultValue = (initial >> bit) & 1
            }).Concat(new[] { local }).ToArray();
        }
    }
}
