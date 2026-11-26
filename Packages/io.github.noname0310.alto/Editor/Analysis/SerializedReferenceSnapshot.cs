using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Noname.AvatarTools.Build;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.Analysis
{
    // Owned by an analysis epoch. Proven mutations expire their reference owners;
    // unknown writes fall back to domain-wide or complete invalidation.
    internal sealed class SerializedReferenceSnapshot
    {
        internal readonly struct Entry
        {
            internal readonly string Path;
            internal readonly Object Value;
            internal Entry(string path, Object value) { Path = path; Value = value; }
        }

        private readonly Dictionary<Object, Entry[]> entries = new();
        private static readonly Dictionary<Type, string[]> nativeLayouts = new();

        // A layout is a type/schema fact, never an instance-value observation.
        // All managed fields must be reference-free, including empty lists and
        // null fields; native reference slots are read again on every cache miss.
        private static string[] ReferenceLayout(SerializedObject serialized, Type type)
        {
            if (nativeLayouts.TryGetValue(type, out var previous)) return previous;
            if (!SerializedReferenceSchema.ManagedFieldsAreReferenceFree(type)) return nativeLayouts[type] = null;
            var fields = SerializedReferenceSchema.ReferenceFreeFields(type);
            var paths = new List<string>();
            using var property = serialized.GetIterator();
            bool first = true;
            while (property.Next(first))
            {
                first = false;
                if (property.propertyType == SerializedPropertyType.ObjectReference)
                {
                    if (property.propertyPath != "m_GameObject" && property.propertyPath != "m_Script")
                        paths.Add(property.propertyPath);
                }
                else if (SerializedReferences.MayContainReferences(property.propertyType) && !fields.Contains(property.name))
                    return nativeLayouts[type] = null;
            }
            return nativeLayouts[type] = paths.ToArray();
        }

        internal void Invalidate(Object owner)
        {
            if (!ReferenceEquals(owner, null)) entries.Remove(owner);
        }

        internal void Invalidate(AnalysisDomain changed)
        {
            if (changed == AnalysisDomain.None) return;
            if (changed == AnalysisDomain.All) { entries.Clear(); return; }
            List<Object> dirty = null;
            foreach (var pair in entries)
                if (pair.Key == null || (ReferenceDomains(pair.Key) & changed) != 0)
                    (dirty ??= new()).Add(pair.Key);
            if (dirty != null) foreach (var owner in dirty) entries.Remove(owner);
            AnalysisCost.Count("Serialized reference snapshot/partial invalidation");
        }

        // Unknown/custom owners depend on every domain. These native asset
        // contracts have no hierarchy or controller-owned reference fields.
        // In particular do not infer immutability from an empty current list:
        // a later rewrite can populate a previously null serialized field.
        private static AnalysisDomain ReferenceDomains(Object owner)
        {
            if (owner.GetType() == typeof(Material)) return AnalysisDomain.Materials | AnalysisDomain.ShaderSources;
            if (owner.GetType() == typeof(Mesh)) return AnalysisDomain.Meshes;
            if (owner.GetType() == typeof(Shader)) return AnalysisDomain.ShaderSources;
            return AnalysisDomain.All;
        }

        internal Entry[] Get(Object owner)
        {
            if (entries.TryGetValue(owner, out var found))
            {
                AnalysisCost.Count("Serialized reference snapshot/cache hit");
                return found;
            }
            using var cost = AnalysisCost.Measure("Serialized reference snapshot/cache miss");
            List<Entry> result = null;
            foreach (var entry in Read(owner)) (result ??= new()).Add(entry);
            entries.Add(owner, found = result == null ? Array.Empty<Entry>() : result.ToArray());
            return found;
        }

        internal static IEnumerable<Entry> Read(Object owner)
        {
            using var serialized = new SerializedObject(owner);
            var layout = ReferenceLayout(serialized, owner.GetType());
            if (layout != null)
            {
                var result = new List<Entry>();
                bool valid = true;
                foreach (string path in layout)
                {
                    using var property = serialized.FindProperty(path);
                    if (property == null || property.propertyType != SerializedPropertyType.ObjectReference) { valid = false; break; }
                    if (property.objectReferenceValue != null) result.Add(new Entry(path, property.objectReferenceValue));
                }
                if (valid)
                {
                    AnalysisCost.Count("Serialized reference snapshot/schema layout reuse");
                    foreach (var entry in result) yield return entry;
                    yield break;
                }
            }
            foreach (var property in SerializedReferences.Properties(serialized))
            {
                string path = property.propertyPath;
                if (path == "m_GameObject" || path == "m_Script") continue;
                var value = property.objectReferenceValue;
                if (value != null) yield return new Entry(path, value);
            }
        }
    }
}
