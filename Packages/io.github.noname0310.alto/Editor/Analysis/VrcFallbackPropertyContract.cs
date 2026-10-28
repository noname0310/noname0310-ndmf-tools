using System;
using System.Collections.Generic;
using UnityEngine;

namespace Noname.AvatarTools.Analysis
{
    // Public SDK/client fallback contract, not an artist/shader compatibility list.
    // https://creators.vrchat.com/avatars/shader-fallback-system/ (checked 2026-10-26).
    // AAO 1.9.20 RemoveUnusedMaterialProperties also preserves fallback properties;
    // this implementation queries the actual fallback shader declarations instead.
    internal sealed class VrcFallbackPropertyContract
    {
        private readonly Dictionary<string, HashSet<string>> declarations = new(StringComparer.Ordinal);

        private HashSet<string> Properties(string shaderName)
        {
            if (declarations.TryGetValue(shaderName, out var cached)) return cached;
            var shader = Shader.Find(shaderName);
            if (shader == null) return declarations[shaderName] = null;
            var result = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < shader.GetPropertyCount(); i++) result.Add(shader.GetPropertyName(i));
            return declarations[shaderName] = result;
        }

        internal bool MayUse(Material material, string property)
        {
            if (material == null || string.IsNullOrEmpty(property)) return true;
            if (property == "_Ramp" || property == "_MatCap") return true;
            bool Has(string shaderName)
            {
                var names = Properties(shaderName);
                if (names == null || names.Contains(property)) return true;
                foreach (var suffix in new[] { "_ST", "_TexelSize" })
                    if (property.EndsWith(suffix, StringComparison.Ordinal) && names.Contains(property.Substring(0, property.Length - suffix.Length))) return true;
                return false;
            }
            // Standard parameters can be copied even when the source shader lacks them.
            if (Has("Standard") || Has("Standard (Specular setup)")) return true;
            string tag = material.GetTag("VRCFallback", false, "");
            if (tag == "toonstandard") return Has("VRChat/Mobile/Toon Standard");
            if (tag == "toonstandardoutline") return Has("VRChat/Mobile/Toon Standard (Outline)");
            return false;
        }
    }
}
