using System;
using System.Collections.Generic;
using System.Linq;
using Noname.AvatarTools.Analysis;
using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.Materials
{
    internal sealed class MaterialTextureBakeResult
    {
        internal Material Material;
        internal Texture2D Texture;
        internal string ReplacedTextureProperty;
        internal string Detail;

        // Material is the complete renderer replacement and may clear inputs that
        // were consumed by the bake. Texture mappings replace only this declared
        // property (including its scale/offset); other inputs still belong to source.
        internal Material TextureSource(Material source, string property)
        {
            if (Material == null || string.IsNullOrWhiteSpace(ReplacedTextureProperty) ||
                !string.Equals(property, ReplacedTextureProperty, StringComparison.Ordinal)) return source;
            if (Texture == null || !Material.HasTexture(property) || Material.GetTexture(property) != Texture)
                throw new InvalidOperationException("Texture bake declared a replacement for '" + property +
                    "' but its material does not reference the generated texture.");
            return Material;
        }
    }

    // Optional integrations return owned build copies. No source asset is modified.
    internal static class MaterialTextureBake
    {
        internal static Func<Material, HashSet<string>, MaterialTextureBakeResult> LilToon;

        internal static string Property(EditorCurveBinding binding)
        {
            const string prefix = "material.";
            if (binding.propertyName == null || !binding.propertyName.StartsWith(prefix, StringComparison.Ordinal)) return null;
            var property = binding.propertyName.Substring(prefix.Length);
            int channel = property.IndexOf('.');
            return channel < 0 ? property : property.Substring(0, channel);
        }

        internal static Dictionary<Material, HashSet<string>> AnimatedProperties(AvatarAnimationIndex animations,
            IEnumerable<Renderer> renderers, IEnumerable<AvatarAnimationIndex.BindingUse> swaps)
        {
            var result = new Dictionary<Material, HashSet<string>>();
            var unresolved = animations.Bindings.Where(b => !b.Complete).Select(b => Property(b.Binding))
                .Where(p => p != null).ToArray();
            foreach (var renderer in renderers)
            {
                var properties = animations.ForObject(renderer).Select(b => Property(b.Binding))
                    .Where(p => p != null).Concat(unresolved).ToArray();
                var materials = renderer.sharedMaterials.Concat(swaps.Where(b => b.Objects().Contains(renderer))
                    .SelectMany(b => b.ObjectKeys.Select(k => k.value).OfType<Material>()));
                foreach (var material in materials.Where(m => m != null).Distinct())
                {
                    if (!result.TryGetValue(material, out var set)) result.Add(material, set = new(StringComparer.Ordinal));
                    set.UnionWith(properties);
                }
            }
            return result;
        }
    }
}
