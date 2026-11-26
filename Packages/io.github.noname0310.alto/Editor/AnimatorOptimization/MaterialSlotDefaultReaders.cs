using System;
using System.Collections.Generic;
using System.Linq;
using Noname.AvatarTools.Analysis;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Noname.AvatarTools.AnimatorOptimization
{
    // The native material animator captures defaults from the first material.
    // A constant slot output does not prove that later Write Defaults restoration,
    // partial layer weights or implicit vector channels cannot read those defaults.
    internal static class MaterialSlotDefaultReaders
    {
        private sealed class Group
        {
            internal string Property;
            internal string[] Bindings;
            internal string[] NativeChannels;
            internal float[] Initial;
        }

        internal static bool CanReplaceFirstMaterial(AvatarAnimationIndex index, Renderer renderer, Material replacement)
        {
            var bindings = index.ForObject(renderer).Where(u =>
                u.Binding.propertyName.StartsWith("material.", StringComparison.Ordinal)).ToArray();
            if (bindings.Length == 0) return true;
            var materials = renderer.sharedMaterials;
            if (materials.Length == 0 || materials[0] == null || replacement == null || renderer.HasPropertyBlock()) return false;
            var groups = new List<Group>();
            foreach (var group in bindings.GroupBy(u => MaterialAnimationInputs.PropertyName(u.Binding.propertyName)))
            {
                if (group.Key == null || group.Any(u => u.Binding.isPPtrCurve)) return false;
                if (!Contract(materials[0], group.Key, out var before) ||
                    !Contract(replacement, group.Key, out var after) || before != after) return false;
                var names = group.Select(u => u.Binding.propertyName).Distinct(StringComparer.Ordinal).ToArray();
                string prefix = "material." + group.Key;
                bool scalar = names.Contains(prefix);
                if (scalar && names.Length != 1) return false;
                string channels = scalar ? "" : "rgba".Contains(names[0][names[0].Length - 1]) ? "rgba" : "xyzw";
                if (!scalar && names.Any(n => !channels.Contains(n[n.Length - 1]))) return false;
                var native = scalar ? names : channels.Select(c => prefix + "." + c).ToArray();
                if (!Read(renderer, native, out var initial)) return false;
                groups.Add(new Group { Property = group.Key, Bindings = names, NativeChannels = native, Initial = initial });
            }
            var proposed = materials.ToArray(); proposed[0] = replacement;
            var changed = new List<Group>();
            // Read through Unity's animation binding API so Color/Gamma encoding
            // and implicit channels match the native Animator. This temporary
            // assignment touches only the build renderer and is always restored;
            // material assets and the immutable animation snapshot stay unchanged.
            try
            {
                renderer.sharedMaterials = proposed;
                foreach (var group in groups)
                {
                    if (!Read(renderer, group.NativeChannels, out var values)) return false;
                    if (values.Where((v, i) => BitConverter.SingleToInt32Bits(v) !=
                        BitConverter.SingleToInt32Bits(group.Initial[i])).Any()) changed.Add(group);
                }
            }
            finally { renderer.sharedMaterials = materials; }
            return changed.All(g => index.MaterialInputs.FullyDriven(renderer, g.Property, g.Bindings));
        }

        private static bool Read(Renderer renderer, string[] channels, out float[] values)
        {
            values = new float[channels.Length];
            for (int i = 0; i < channels.Length; i++)
                if (!AnimationUtility.GetFloatValue(renderer.gameObject,
                    EditorCurveBinding.FloatCurve("", renderer.GetType(), channels[i]), out values[i]) ||
                    !MotionAnalysis.Finite(values[i])) return false;
            return true;
        }

        private static bool Contract(Material material, string name, out (ShaderPropertyType Type, ShaderPropertyFlags Flags) contract)
        {
            contract = default;
            var shader = material.shader;
            if (shader == null) return false;
            var properties = Enumerable.Range(0, shader.GetPropertyCount()).Where(i => shader.GetPropertyName(i) == name).ToArray();
            if (properties.Length == 0 && name.EndsWith("_ST", StringComparison.Ordinal))
            {
                string texture = name.Substring(0, name.Length - 3);
                properties = Enumerable.Range(0, shader.GetPropertyCount()).Where(i => shader.GetPropertyName(i) == texture).ToArray();
                if (properties.Length != 1 || shader.GetPropertyType(properties[0]) != ShaderPropertyType.Texture) return false;
            }
            if (properties.Length != 1) return false;
            int property = properties[0];
            contract = (shader.GetPropertyType(property), shader.GetPropertyFlags(property));
            return true;
        }
    }
}
