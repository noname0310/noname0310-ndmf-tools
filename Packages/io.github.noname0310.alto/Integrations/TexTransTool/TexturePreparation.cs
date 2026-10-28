using System;
using System.Collections.Generic;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.MaterialAtlas
{
    internal sealed class TexturePreparation
    {
        private readonly Dictionary<(Texture, Vector2, Vector2, Rect, float, bool, Color, int), Texture2D> cache = new();
        private readonly BuildContext context;
        internal TexturePreparation(BuildContext context) { this.context = context; }

        internal static string Unsupported(Material material, AtlasTextureMapping mapping)
        {
            var texture = material.HasTexture(mapping.SourceProperty) ? material.GetTexture(mapping.SourceProperty) : null;
            if (texture != null && !(texture is Texture2D)) return "mapped texture is not a static Texture2D";
            if (texture != null && UnityEngine.Experimental.Rendering.GraphicsFormatUtility.IsHDRFormat(texture.graphicsFormat))
                return "HDR textures are not supported by the 8-bit atlas output";
            if (texture != null && AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(texture)) is TextureImporter importer &&
                importer.textureType == TextureImporterType.NormalMap) return "normal-map import encoding needs a dedicated conversion";
            if (material.HasTexture(mapping.SourceProperty))
            {
                var scale = material.GetTextureScale(mapping.SourceProperty); var offset = material.GetTextureOffset(mapping.SourceProperty);
                if (!Finite(scale.x) || !Finite(scale.y) || !Finite(offset.x) || !Finite(offset.y)) return "non-finite texture scale or offset";
            }
            return null;
        }

        internal Texture2D Get(Material source, AtlasTextureMapping mapping, Rect domain, float priority, int maximum)
        {
            var texture = source.HasTexture(mapping.SourceProperty) ? source.GetTexture(mapping.SourceProperty) as Texture2D : null;
            var scale = texture != null ? source.GetTextureScale(mapping.SourceProperty) : Vector2.one;
            var offset = texture != null ? source.GetTextureOffset(mapping.SourceProperty) : Vector2.zero;
            var key = ((Texture)texture, scale, offset, domain, priority, mapping.Linear, mapping.MissingTextureColor, maximum);
            if (cache.TryGetValue(key, out var cached)) return cached;
            if (texture == null)
            {
                var solid = new Texture2D(4, 4, TextureFormat.RGBA32, false, mapping.Linear) { name = "Atlas missing texture" };
                var colors = new Color[16]; for (int i = 0; i < colors.Length; i++) colors[i] = mapping.MissingTextureColor;
                solid.SetPixels(colors); solid.Apply(); context.AssetSaver.SaveAsset(solid); return cache[key] = solid;
            }
            if (domain == new Rect(0, 0, 1, 1) && scale == Vector2.one && offset == Vector2.zero && priority == 1)
                return cache[key] = texture;
            int width = Size(texture.width, domain.width * Mathf.Abs(scale.x), priority, maximum);
            int height = Size(texture.height, domain.height * Mathf.Abs(scale.y), priority, maximum);
            var rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32,
                mapping.Linear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB);
            var previous = RenderTexture.active; bool previousWrite = GL.sRGBWrite;
            var sampler = new Material(Shader.Find("Hidden/Noname/MaterialAtlasInput"));
            Texture2D baked = null;
            try
            {
                // Graphics.Blit sets _MainTex_ST itself, so sampling uses a separate uniform.
                sampler.SetVector("_AtlasSampleST", new Vector4(domain.width * scale.x, domain.height * scale.y,
                    domain.x * scale.x + offset.x, domain.y * scale.y + offset.y));
                GL.sRGBWrite = !mapping.Linear && QualitySettings.activeColorSpace == ColorSpace.Linear;
                Graphics.Blit(texture, rt, sampler);
                RenderTexture.active = rt;
                baked = new Texture2D(width, height, TextureFormat.RGBA32, false, mapping.Linear)
                { name = texture.name + " (Atlas input)", wrapMode = TextureWrapMode.Clamp, filterMode = texture.filterMode };
                baked.ReadPixels(new Rect(0, 0, width, height), 0, 0); baked.Apply();
                context.AssetSaver.SaveAsset(baked); return cache[key] = baked;
            }
            catch { if (baked != null) UnityEngine.Object.DestroyImmediate(baked); throw; }
            finally
            {
                GL.sRGBWrite = previousWrite; RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt); UnityEngine.Object.DestroyImmediate(sampler);
            }
        }

        private static int Size(int source, float span, float priority, int maximum) =>
            Mathf.Clamp(Mathf.NextPowerOfTwo(Mathf.CeilToInt(source * Mathf.Max(span, 0.01f) * priority)), 4, maximum);
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
