using System.Collections.Generic;
using UnityEditor;

namespace Noname.AvatarTools.Analysis
{
    internal static class SerializedReferences
    {
        // Strings and curves have native child properties too. Walking their
        // characters/keyframes cannot discover object references. Keep generic,
        // managed and exposed-reference containers, including unknown future types.
        internal static IEnumerable<SerializedProperty> Properties(SerializedObject serialized)
        {
            var target = serialized.targetObject;
            var empty = target == null ? null : SerializedReferenceSchema.ReferenceFreeFields(target.GetType());
            using var property = serialized.GetIterator();
            bool enter = true;
            while (property.Next(enter))
            {
                var type = property.propertyType;
                enter = MayContainReferences(type);
                if (enter && property.depth == 0 && empty != null && empty.Contains(property.name)) enter = false;
                if (enter && property.isArray && property.arraySize > 0)
                {
                    using var element = property.GetArrayElementAtIndex(0);
                    var elementType = element.propertyType;
                    enter = elementType == SerializedPropertyType.ObjectReference || MayContainReferences(elementType);
                }
                // The iterator is valid only until the next MoveNext. Readers must
                // consume it immediately or Copy it, just like a native iterator.
                if (type == SerializedPropertyType.ObjectReference) yield return property;
            }
        }

        internal static bool MayContainReferences(SerializedPropertyType type)
        {
            switch (type)
            {
                case SerializedPropertyType.Integer: case SerializedPropertyType.Boolean:
                case SerializedPropertyType.Float: case SerializedPropertyType.String:
                case SerializedPropertyType.Color: case SerializedPropertyType.ObjectReference:
                case SerializedPropertyType.LayerMask: case SerializedPropertyType.Enum:
                case SerializedPropertyType.Vector2: case SerializedPropertyType.Vector3:
                case SerializedPropertyType.Vector4: case SerializedPropertyType.Rect:
                case SerializedPropertyType.ArraySize: case SerializedPropertyType.Character:
                case SerializedPropertyType.AnimationCurve: case SerializedPropertyType.Bounds:
                case SerializedPropertyType.Gradient: case SerializedPropertyType.Quaternion:
                case SerializedPropertyType.FixedBufferSize: case SerializedPropertyType.Vector2Int:
                case SerializedPropertyType.Vector3Int: case SerializedPropertyType.RectInt:
                case SerializedPropertyType.BoundsInt: case SerializedPropertyType.Hash128:
                    return false;
                default: return true;
            }
        }
    }
}
