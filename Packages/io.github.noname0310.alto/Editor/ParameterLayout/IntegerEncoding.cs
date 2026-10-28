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
        // Least significant bit first. Stored names, not regenerated guesses, form the wire contract.
        public List<string> Bits = new();

        internal static int Width(int minimum, int maximum)
        {
            if (minimum < 0 || maximum > 255 || maximum <= minimum || maximum - minimum >= 128)
                throw new InvalidOperationException("Integer compression requires 2 to 128 values within 0..255.");
            int width = 1;
            while ((1 << width) <= maximum - minimum) width++;
            return width;
        }

        internal int Encode(int value) => Math.Max(Minimum, Math.Min(Maximum, value)) - Minimum;
        internal int Decode(int bits) => Math.Min(Maximum, Minimum + bits);

        internal void Validate()
        {
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

        internal Parameter[] Declarations(Parameter logical)
        {
            Validate();
            if (logical.valueType != VRCExpressionParameters.ValueType.Int || !logical.networkSynced ||
                logical.defaultValue != (int)logical.defaultValue || logical.defaultValue < Minimum || logical.defaultValue > Maximum)
                throw new InvalidOperationException("Compressed input must be a synchronized Int with a whole-number default inside its range: " + Name);
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
