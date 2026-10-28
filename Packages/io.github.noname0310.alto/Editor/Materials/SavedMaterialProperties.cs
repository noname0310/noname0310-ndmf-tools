using System;
using System.Collections.Generic;
using System.Linq;
using Noname.AvatarTools.Analysis;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.Materials
{
    // Design reference: AAO 1.9.20 RemoveUnusedMaterialProperties/RemoveUnusedMaterialTextures.
    // Independently implemented absence analysis across shader branches, dependencies and
    // VRChat fallback inputs. Only generated copies are changed; variants remain untouched.
    internal static class SavedMaterialProperties
    {
        private static readonly string[] Arrays = { "m_TexEnvs", "m_Floats", "m_Colors", "m_Ints" };
        private static readonly HashSet<string> EditorLabels = new(StringComparer.Ordinal) { "m_Name" };

        internal static List<(string Array, int Index)> Unused(Material material, ShaderPropertyUsage shaders,
            VrcFallbackPropertyContract fallbacks)
        {
            var result = new List<(string, int)>();
            if (material == null || material.shader == null || material.parent != null) return result;
            var proof = shaders.Get(material.shader);
            if (proof.Uncertain != null) return result;
            using var serialized = new SerializedObject(material);
            foreach (string name in Arrays)
            {
                var array = serialized.FindProperty("m_SavedProperties." + name);
                if (array == null || !array.isArray) continue;
                for (int i = 0; i < array.arraySize; i++)
                {
                    string property = array.GetArrayElementAtIndex(i).FindPropertyRelative("first")?.stringValue;
                    if (!string.IsNullOrEmpty(property) && !proof.MayUse(property) && !fallbacks.MayUse(material, property)) result.Add((name, i));
                }
            }
            return result;
        }

        internal static Material CleanCopy(Material source, IReadOnlyList<(string Array, int Index)> unused)
        {
            var result = Object.Instantiate(source); result.name = source.name + " (Optimized)";
            try
            {
                using var serialized = new SerializedObject(result);
                foreach (var group in unused.GroupBy(p => p.Array))
                {
                    var array = serialized.FindProperty("m_SavedProperties." + group.Key);
                    foreach (int index in group.Select(p => p.Index).OrderByDescending(i => i))
                    {
                        int count = array.arraySize; array.DeleteArrayElementAtIndex(index);
                        if (array.arraySize == count) array.DeleteArrayElementAtIndex(index);
                    }
                }
                serialized.ApplyModifiedPropertiesWithoutUndo();
                return result;
            }
            catch { Object.DestroyImmediate(result); throw; }
        }

        internal static bool Equivalent(Material first, Material second)
        {
            if (first == second) return true;
            if (first == null || second == null || first.parent != null || second.parent != null) return false;
            // All render state, keywords, tags, shader passes, texture references and
            // saved values must match exactly. Names are editor labels.
            return SerializedValueEquality.Matches(first, second, EditorLabels);
        }
    }
}
