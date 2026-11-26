using System;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.Analysis
{
    internal enum ConstantOutputKind { Scalar, Discrete, ObjectReference }

    // A known null reference is a value, not a failed proof. Keep discrete outputs
    // separate: object/discrete animation never uses scalar interpolation.
    internal readonly struct ConstantOutputValue : IEquatable<ConstantOutputValue>
    {
        internal readonly ConstantOutputKind Kind;
        internal readonly float Number;
        internal readonly Object Reference;

        private ConstantOutputValue(ConstantOutputKind kind, float number, Object reference)
        { Kind = kind; Number = number; Reference = reference; }

        internal static ConstantOutputValue Scalar(float value) => new(ConstantOutputKind.Scalar, value, null);
        internal static ConstantOutputValue Discrete(float value) => new(ConstantOutputKind.Discrete, value, null);
        internal static ConstantOutputValue Object(Object value) => new(ConstantOutputKind.ObjectReference, 0, value);
        internal bool Finite => Kind == ConstantOutputKind.ObjectReference || MotionAnalysis.Finite(Number);

        internal static bool TryRead(SerializedProperty property, out ConstantOutputValue value)
        {
            value = default;
            if (property == null) return false;
            switch (property.propertyType)
            {
                case SerializedPropertyType.Float:
                    double number = property.doubleValue;
                    if ((double)(float)number != number) return false;
                    value = Scalar((float)number); break;
                case SerializedPropertyType.Boolean: value = Discrete(property.boolValue ? 1 : 0); break;
                case SerializedPropertyType.Enum: case SerializedPropertyType.Integer:
                    long integer = property.propertyType == SerializedPropertyType.Enum ? property.intValue : property.longValue;
                    if (integer < int.MinValue || integer > int.MaxValue || (double)(float)integer != integer) return false;
                    value = Discrete(integer); break;
                case SerializedPropertyType.ObjectReference: value = Object(property.objectReferenceValue); break;
                default: return false;
            }
            return value.Finite;
        }

        public bool Equals(ConstantOutputValue other) => Kind == other.Kind &&
            (Kind == ConstantOutputKind.ObjectReference ? Reference == other.Reference :
                BitConverter.SingleToInt32Bits(Number) == BitConverter.SingleToInt32Bits(other.Number));
        public override bool Equals(object obj) => obj is ConstantOutputValue other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Kind,
            Kind == ConstantOutputKind.ObjectReference ? Reference != null ? Reference.GetInstanceID() : 0 :
                BitConverter.SingleToInt32Bits(Number));
        public override string ToString() => Kind == ConstantOutputKind.ObjectReference ?
            Reference != null ? Reference.name : "null" : Number.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
    }
}
