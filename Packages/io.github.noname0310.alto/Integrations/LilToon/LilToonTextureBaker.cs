using System;
using System.Collections.Generic;
using System.Linq;
using Noname.AvatarTools.Materials;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Noname.ALTO.LilToon.Tests")]

namespace Noname.AvatarTools.LilToon
{
    // Uses lilToon's public baker and shader, not its interactive PNG-saving UI.
    // Eligibility is independently checked against lil_common_frag.hlsl (2.3.4).
    [InitializeOnLoad]
    internal static class LilToonTextureBaker
    {
        private static readonly Vector4 DefaultHsvg = new(0, 1, 1, 1);
        private static readonly string[] ToneProperties =
            { "_MainTex", "_MainTexHSVG", "_MainColorAdjustMask", "_MainGradationStrength", "_MainGradationTex" };

        static LilToonTextureBaker() => MaterialTextureBake.LilToon = Bake;

        internal static MaterialTextureBakeResult Bake(Material source, HashSet<string> animated)
        {
            if (!SupportedShader(source)) return null;
            bool tone = source.GetVector("_MainTexHSVG") != DefaultHsvg || Float(source, "_MainGradationStrength") != 0;
            bool second = Float(source, "_UseMain2ndTex") != 0, third = Float(source, "_UseMain3rdTex") != 0;
            if (!tone && !second && !third) return null;
            MaterialTextureBakeResult Retain(string reason) => new() { Detail = "lilToon bake retained input: " + reason };
            if (source.parent != null) return Retain("Material Variant inheritance");
            if (!(source.GetTexture("_MainTex") is Texture2D main)) return Retain("requires a static main Texture2D");
            if (animated.Contains("*") || animated.Contains("_MainTex")) return Retain("animated main texture");
            if (!Ldr(main)) return Retain("HDR or unsupported main texture storage");
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return Retain("no graphics device");
            var shader = Shader.Find("Hidden/ltsother_baker");
            if (shader == null || !shader.isSupported) return Retain("lilToon baker shader unavailable");

            bool toneStatic = !ToneProperties.Any(animated.Contains);
            bool bakeTone = tone && toneStatic;
            string layerIssue = null;
            if (second || third)
            {
                if (!toneStatic || animated.Any(IsLayerProperty) || animated.Contains("_Color")) layerIssue = "animated composition settings";
                else if (source.GetTextureScale("_MainTex") != Vector2.one || source.GetTextureOffset("_MainTex") != Vector2.zero ||
                         Vector(source, "_MainTex_ScrollRotate") != Vector4.zero || animated.Contains("_MainTex_ST") ||
                         animated.Contains("_MainTex_ScrollRotate") || Float(source, "_UseParallax") != 0 ||
                         Float(source, "_ShiftBackfaceUV") != 0 || animated.Contains("_ShiftBackfaceUV") ||
                         animated.Contains("_UseParallax") || animated.Contains("_UsePOM")) layerIssue = "main UV transform or parallax";
                else if (!UnitColor(source.GetColor("_Color"))) layerIssue = "HDR main color";
                else if (second) layerIssue = LayerIssue(source, "2nd", main);
                if (layerIssue == null && third) layerIssue = LayerIssue(source, "3rd", main);
            }
            bool layers = (second || third) && layerIssue == null;
            if (!bakeTone && !layers) return Retain(layerIssue ?? "animated color correction");
            if (!EnabledFeatures(source, bakeTone, layers && second, layers && third))
                return Retain("required shader features disabled or settings unavailable");
            var usedTextures = new List<string> { "_MainTex" };
            if (bakeTone) usedTextures.AddRange(new[] { "_MainColorAdjustMask", "_MainGradationTex" });
            if (layers && second) usedTextures.AddRange(new[] { "_Main2ndTex", "_Main2ndBlendMask" });
            if (layers && third) usedTextures.AddRange(new[] { "_Main3rdTex", "_Main3rdBlendMask" });
            foreach (var property in usedTextures)
            {
                var texture = source.GetTexture(property);
                if (texture != null && (!(texture is Texture2D input) || !Ldr(input))) return Retain("nonstatic or HDR texture: " + property);
            }
            if (bakeTone && (!Finite(source.GetVector("_MainTexHSVG")) || source.GetVector("_MainTexHSVG").w <= 0 ||
                            !Unit(Float(source, "_MainGradationStrength")))) return Retain("unsupported color correction range");
            Material baker = null, result = null;
            Texture2D output = null;
            var previousActive = RenderTexture.active;
            try
            {
                baker = new Material(shader);
                // Copy declared properties only. Runtime shader keywords must never
                // select unrelated baker modes such as alpha-mask/normal conversion.
                for (int i = 0; i < shader.GetPropertyCount(); i++)
                {
                    string name = shader.GetPropertyName(i);
                    if (!source.HasProperty(name)) continue;
                    switch (shader.GetPropertyType(i))
                    {
                        case ShaderPropertyType.Color: baker.SetColor(name, source.GetColor(name)); break;
                        case ShaderPropertyType.Vector: baker.SetVector(name, source.GetVector(name)); break;
                        case ShaderPropertyType.Texture:
                            baker.SetTexture(name, source.GetTexture(name));
                            baker.SetTextureScale(name, source.GetTextureScale(name));
                            baker.SetTextureOffset(name, source.GetTextureOffset(name)); break;
                        case ShaderPropertyType.Int: baker.SetInteger(name, source.GetInteger(name)); break;
                        default: baker.SetFloat(name, source.GetFloat(name)); break;
                    }
                }
                // These uniforms are intentionally absent from the baker's Properties
                // block. lilToon's own TextureBake sets them explicitly as well.
                foreach (string layer in new[] { "2nd", "3rd" })
                    foreach (string suffix in new[] { "DecalAnimation", "DecalSubParam", "_ScrollRotate" })
                    {
                        string name = "_Main" + layer + "Tex" + suffix;
                        baker.SetVector(name, Vector(source, name));
                    }
                if (!bakeTone)
                {
                    baker.SetVector("_MainTexHSVG", DefaultHsvg);
                    baker.SetFloat("_MainGradationStrength", 0);
                    baker.SetTexture("_MainColorAdjustMask", null);
                }
                baker.SetFloat("_UseMain2ndTex", layers && second ? 1 : 0);
                baker.SetFloat("_UseMain3rdTex", layers && third ? 1 : 0);
                // Alpha remains an independent runtime multiplier, including shadow passes.
                var color = layers ? source.GetColor("_Color") : Color.white; color.a = 1;
                baker.SetColor("_Color", color);
                lilToon.lilToonInspector.RunBake(ref output, main, baker);
                if (main.mipmapCount == 1 && output.mipmapCount > 1)
                {
                    var noMips = new Texture2D(output.width, output.height, TextureFormat.RGBA32, false);
                    noMips.SetPixels32(output.GetPixels32()); noMips.Apply(false);
                    Object.DestroyImmediate(output); output = noMips;
                }
                output.name = source.name + " (ALTO baked main)";
                output.wrapModeU = main.wrapModeU; output.wrapModeV = main.wrapModeV;
                output.filterMode = main.filterMode; output.anisoLevel = main.anisoLevel; output.mipMapBias = main.mipMapBias;
                result = new Material(source) { name = source.name + " (ALTO baked)" };
                result.SetTexture("_MainTex", output);
                if (bakeTone)
                {
                    result.SetVector("_MainTexHSVG", DefaultHsvg); result.SetFloat("_MainGradationStrength", 0);
                    result.SetTexture("_MainGradationTex", null); result.SetTexture("_MainColorAdjustMask", null);
                }
                if (layers)
                {
                    result.SetColor("_Color", new Color(1, 1, 1, source.GetColor("_Color").a));
                    if (second) { result.SetFloat("_UseMain2ndTex", 0); result.SetTexture("_Main2ndTex", null); result.SetTexture("_Main2ndBlendMask", null); }
                    if (third) { result.SetFloat("_UseMain3rdTex", 0); result.SetTexture("_Main3rdTex", null); result.SetTexture("_Main3rdBlendMask", null); }
                }
                return new MaterialTextureBakeResult { Material = result, Texture = output, ReplacedTextureProperty = "_MainTex",
                    Detail = "lilToon baked " + (bakeTone ? "color correction" : "") + (bakeTone && layers ? " and " : "") +
                             (layers ? "secondary textures" : "") + (layerIssue != null ? "; layers retained: " + layerIssue : "") + "." };
            }
            catch
            {
                if (output != null) Object.DestroyImmediate(output);
                if (result != null) Object.DestroyImmediate(result);
                throw;
            }
            finally
            {
                RenderTexture.active = previousActive;
                if (baker != null) Object.DestroyImmediate(baker);
            }
        }

        private static bool SupportedShader(Material source)
        {
            if (source == null || source.shader == null) return false;
            // Accept packaged shaders, never custom/generated shaders that merely
            // reuse lilToon's property names (SPS and other injected code included).
            string path = AssetDatabase.GetAssetPath(source.shader).Replace('\\', '/');
            if (!path.StartsWith("Packages/jp.lilxyzw.liltoon/Shader/", StringComparison.Ordinal)) return false;
            string name = source.shader.name;
            return source.HasProperty("_MainTexHSVG") && !new[] { "Lite", "Fur", "Gem", "FakeShadow", "Refraction", "Multi", "Overlay" }
                .Any(part => name.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool EnabledFeatures(Material material, bool tone, bool second, bool third)
        {
            var settings = ScriptableObject.CreateInstance<lilToonSetting>();
            try
            {
                // Read the persisted project settings without invoking lilToon's
                // internal initialization, migration or shader-rewrite functions.
                const string path = "ProjectSettings/lilToonSetting.json";
                if (System.IO.File.Exists(path)) JsonUtility.FromJsonOverwrite(System.IO.File.ReadAllText(path), settings);
                if (tone && (material.GetVector("_MainTexHSVG") != DefaultHsvg && !settings.LIL_FEATURE_MAIN_TONE_CORRECTION ||
                             Float(material, "_MainGradationStrength") != 0 && (!settings.LIL_FEATURE_MAIN_GRADATION_MAP || !settings.LIL_FEATURE_MainGradationTex) ||
                             material.GetTexture("_MainColorAdjustMask") != null && !settings.LIL_FEATURE_MainColorAdjustMask)) return false;
                // The baker always enables atlas UV handling, even for a single tile.
                if ((second || third) && (!settings.LIL_FEATURE_DECAL || !settings.LIL_FEATURE_ANIMATE_DECAL)) return false;
                if (second && (!settings.LIL_FEATURE_MAIN2ND || material.GetTexture("_Main2ndTex") != null && !settings.LIL_FEATURE_Main2ndTex ||
                               material.GetTexture("_Main2ndBlendMask") != null && !settings.LIL_FEATURE_Main2ndBlendMask)) return false;
                if (third && (!settings.LIL_FEATURE_MAIN3RD || material.GetTexture("_Main3rdTex") != null && !settings.LIL_FEATURE_Main3rdTex ||
                              material.GetTexture("_Main3rdBlendMask") != null && !settings.LIL_FEATURE_Main3rdBlendMask)) return false;
                return true;
            }
            catch (Exception) { return false; }
            finally { Object.DestroyImmediate(settings); }
        }

        private static bool IsLayerProperty(string name) => name.StartsWith("_Main2nd", StringComparison.Ordinal) ||
            name.StartsWith("_Main3rd", StringComparison.Ordinal) || name.StartsWith("_UseMain", StringComparison.Ordinal) ||
            name == "_Color2nd" || name == "_Color3rd" || name == "_AudioLink2Main2nd" || name == "_AudioLink2Main3rd";

        private static string LayerIssue(Material material, string layer, Texture2D main)
        {
            string tex = "_Main" + layer + "Tex", prefix = "_Main" + layer;
            foreach (string suffix in new[] { "_UVMode", "_Cull", "IsDecal", "IsLeftOnly", "IsRightOnly", "ShouldCopy", "ShouldFlipMirror", "ShouldFlipCopy", "IsMSDF", "AlphaMode" })
                if (Float(material, tex + suffix) != 0) return tex + suffix;
            var decalAnimation = Vector(material, tex + "DecalAnimation");
            // At zero speed lilToon uses z as the selected frame, not a frame count.
            // Its public baker always samples frame zero. Retain other selections.
            if (decalAnimation.w == 0 && decalAnimation.z != 0) return tex + " static atlas frame";
            if (Vector(material, tex + "_ScrollRotate") != Vector4.zero || decalAnimation.z > 1)
                return tex + " animation";
            if (Float(material, prefix + "EnableLighting") != 1 || Vector(material, prefix + "DistanceFade").z != 0 ||
                Vector(material, prefix + "DissolveParams").x != 0 || Float(material, "_AudioLink2Main" + layer) != 0)
                return prefix + " lighting, fade or dissolve";
            if (Float(material, tex + "BlendMode") == 1 || !UnitColor(material.GetColor("_Color" + layer))) return prefix + " HDR composition";
            if (!Finite(material.GetTextureScale(tex)) || !Finite(material.GetTextureOffset(tex)) ||
                !Finite(new Vector4(Float(material, tex + "Angle"), 0, 0, 0))) return tex + " non-finite UVs";
            var scale = material.GetTextureScale(tex); var offset = material.GetTextureOffset(tex);
            // A repeated main texture cannot encode arbitrary layer behavior outside
            // [0,1]. Require matching periodic domains or entirely clamped sampling.
            foreach (string property in new[] { tex, prefix + "BlendMask" })
            {
                if (material.GetTexture(property) is Texture2D texture)
                {
                    if (texture.width > main.width || texture.height > main.height) return property + " exceeds main texture resolution";
                    if (texture.wrapModeU != main.wrapModeU || texture.wrapModeV != main.wrapModeV) return property + " wrap mode mismatch";
                }
            }
            if ((main.wrapModeU != TextureWrapMode.Clamp || main.wrapModeV != TextureWrapMode.Clamp) &&
                (Float(material, tex + "Angle") != 0 || scale.x != Mathf.Round(scale.x) || scale.y != Mathf.Round(scale.y))) return tex + " nonperiodic UV transform";
            // Different clamp transforms also disagree when the mesh UV is outside
            // the main texture domain. Do not require inspecting one particular mesh.
            if ((main.wrapModeU != TextureWrapMode.Repeat || main.wrapModeV != TextureWrapMode.Repeat) &&
                (scale != Vector2.one || offset != Vector2.zero || Float(material, tex + "Angle") != 0)) return tex + " clamped UV transform";
            return null;
        }

        private static float Float(Material material, string name) => material.HasProperty(name) ? material.GetFloat(name) : 0;
        private static Vector4 Vector(Material material, string name) => material.HasProperty(name) ? material.GetVector(name) : Vector4.zero;
        private static bool Unit(float value) => value >= 0 && value <= 1;
        private static bool UnitColor(Color value) => Unit(value.r) && Unit(value.g) && Unit(value.b) && Unit(value.a);
        private static bool Finite(Vector4 value) => Enumerable.Range(0, 4).All(i => !float.IsNaN(value[i]) && !float.IsInfinity(value[i]));
        private static bool Ldr(Texture2D texture) => !new[] { TextureFormat.RGBAFloat, TextureFormat.RGBAHalf, TextureFormat.RGFloat,
            TextureFormat.RGHalf, TextureFormat.RFloat, TextureFormat.RHalf, TextureFormat.BC6H, TextureFormat.RGB9e5Float }.Contains(texture.format);
    }
}
