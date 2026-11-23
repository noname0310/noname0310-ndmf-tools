using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.Analysis
{
    // Owned by a read-only object-analysis transaction. A hierarchy, component
    // or animation rewrite invalidates the owner and all captured references.
    internal sealed class SerializedReferenceSnapshot
    {
        internal readonly struct Entry
        {
            internal readonly string Path;
            internal readonly Object Value;
            internal Entry(string path, Object value) { Path = path; Value = value; }
        }

        private readonly Dictionary<Object, Entry[]> entries = new();

        internal Entry[] Get(Object owner)
        {
            if (entries.TryGetValue(owner, out var found)) return found;
            List<Entry> result = null;
            foreach (var entry in Read(owner)) (result ??= new()).Add(entry);
            entries.Add(owner, found = result == null ? Array.Empty<Entry>() : result.ToArray());
            return found;
        }

        internal static IEnumerable<Entry> Read(Object owner)
        {
            using var serialized = new SerializedObject(owner);
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
