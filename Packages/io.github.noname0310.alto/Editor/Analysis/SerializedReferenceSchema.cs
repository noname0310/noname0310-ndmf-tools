using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Noname.AvatarTools.Analysis
{
    // Only managed field types are summarized. Native containers and polymorphic
    // serialization keep the normal SerializedProperty traversal. No instance
    // values, array lengths or null references participate in these proofs.
    internal static class SerializedReferenceSchema
    {
        private static readonly Dictionary<Type, HashSet<string>> EmptyFields = new();
        private static readonly Dictionary<Type, bool> ReferenceFree = new();

        internal static HashSet<string> ReferenceFreeFields(Type owner)
        {
            if (EmptyFields.TryGetValue(owner, out var fields)) return fields;
            fields = new HashSet<string>(StringComparer.Ordinal);
            if (typeof(MonoBehaviour).IsAssignableFrom(owner) || typeof(ScriptableObject).IsAssignableFrom(owner))
            {
                var ambiguous = new HashSet<string>(StringComparer.Ordinal);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                for (var type = owner; type != null && !NativeType(type); type = type.BaseType)
                    foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if (!Serialized(field)) continue;
                        if (!seen.Add(field.Name)) ambiguous.Add(field.Name);
                        if (!field.IsDefined(typeof(SerializeReference), false) && IsReferenceFree(field.FieldType, new HashSet<Type>()))
                            fields.Add(field.Name);
                    }
                fields.ExceptWith(ambiguous);
            }
            EmptyFields.Add(owner, fields);
            return fields;
        }

        private static bool NativeType(Type type) => type.Assembly == typeof(UnityEngine.Object).Assembly ||
            (type.Assembly.GetName().Name ?? "").StartsWith("UnityEngine.", StringComparison.Ordinal);

        private static bool Serialized(FieldInfo field) => !field.IsStatic && !field.IsInitOnly && !field.IsNotSerialized &&
            (field.IsPublic || field.IsDefined(typeof(SerializeField), false) || field.IsDefined(typeof(SerializeReference), false));

        private static bool IsReferenceFree(Type type, ISet<Type> visiting)
        {
            if (ReferenceFree.TryGetValue(type, out bool result)) return result;
            if (typeof(UnityEngine.Object).IsAssignableFrom(type)) return false;
            // An untyped System.Object field has no inline serialization. A
            // polymorphic field is handled separately by its SerializeReference
            // attribute, including when it occurs inside a collection/container.
            if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(object)) return true;
            if (!visiting.Add(type)) return false;
            try
            {
                if (type.IsArray) result = type.GetArrayRank() == 1 && IsReferenceFree(type.GetElementType(), visiting);
                else if (type.IsGenericType) result = type.GetGenericTypeDefinition() == typeof(List<>) && IsReferenceFree(type.GetGenericArguments()[0], visiting);
                else if (NativeType(type) || type.IsInterface || !type.IsSerializable) result = false;
                else
                {
                    result = true;
                    for (var current = type; current != null && current != typeof(object) && current != typeof(ValueType); current = current.BaseType)
                    {
                        if (NativeType(current)) { result = false; break; }
                        foreach (var field in current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                            if (Serialized(field) && (field.IsDefined(typeof(SerializeReference), false) || !IsReferenceFree(field.FieldType, visiting)))
                            { result = false; break; }
                        if (!result) break;
                    }
                }
                ReferenceFree[type] = result;
                return result;
            }
            finally { visiting.Remove(type); }
        }
    }
}
