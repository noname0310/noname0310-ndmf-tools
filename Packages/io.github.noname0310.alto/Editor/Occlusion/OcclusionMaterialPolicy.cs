using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Rendering;

namespace Noname.AvatarTools.Occlusion
{
    // ShaderLab metadata selects a geometric approximation. It is not a proof of
    // arbitrary shader output: shader displacement, stencil and alpha programs
    // cannot be inferred from a texture name. Ambiguous culling is not assumed.
    internal sealed class OcclusionMaterialPolicy
    {
        private static readonly Regex Cull = new(@"\bCull\s+(\[\s*([_A-Za-z][_A-Za-z0-9]*)\s*\]|Off\b|Front\b|Back\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private readonly OcclusionShaderPasses sources = new();

        internal bool TryCulling(Material material, OcclusionOccluder rule, out CullMode culling)
        {
            culling = CullMode.Back;
            if (material == null || material.shader == null) return false;
            if (rule?.AssumeOpaque != true && (material.renderQueue > 2500 ||
                !material.GetTag("RenderType", true, "").Equals("Opaque", StringComparison.OrdinalIgnoreCase))) return false;
            bool explicitCulling = rule != null && rule.Culling != OcclusionCulling.Automatic;
            if (explicitCulling && rule.AssumeOpaque)
            {
                if (!Enum.IsDefined(typeof(CullMode), (int)rule.Culling)) return false;
                culling = (CullMode)rule.Culling; return true;
            }
            var subshaders = sources.Get(material.shader); if (subshaders == null) return false;
            var common = new HashSet<CullMode> { CullMode.Off, CullMode.Back, CullMode.Front };
            foreach (var subshader in subshaders)
            {
                var possible = new HashSet<CullMode>();
                foreach (var pass in subshader)
                {
                    if (pass.LightMode != "" && pass.LightMode != "Always" && pass.LightMode != "ForwardBase") continue;
                    if (!string.IsNullOrEmpty(pass.Name) && !material.GetShaderPassEnabled(pass.Name)) continue;
                    if (rule?.AssumeOpaque != true && (!UnrestrictedStencil(pass.States, material) || !UnrestrictedSurface(pass.States, material))) continue;
                    if (!ResolveCull(pass.States, material, out var value)) continue;
                    possible.Add(value);
                    if (value == CullMode.Off) { possible.Add(CullMode.Back); possible.Add(CullMode.Front); }
                }
                common.IntersectWith(possible);
            }
            if (common.Count == 0) return false;
            if (explicitCulling)
            {
                if (!Enum.IsDefined(typeof(CullMode), (int)rule.Culling)) return false;
                culling = (CullMode)rule.Culling;
            }
            else culling = common.Contains(CullMode.Off) ? CullMode.Off : common.Contains(CullMode.Back) ? CullMode.Back : CullMode.Front;
            return true;
        }

        private static bool ResolveCull(string source, Material material, out CullMode culling)
        {
            culling = CullMode.Back;
            var matches = Cull.Matches(source); if (matches.Count == 0) return true;
            var command = matches[matches.Count - 1];
            if (!command.Groups[2].Success) return Enum.TryParse(command.Groups[1].Value, true, out culling);
            string property = command.Groups[2].Value; if (!material.HasProperty(property)) return false;
            float value = material.GetFloat(property);
            if (float.IsNaN(value) || float.IsInfinity(value) || !value.Equals(Mathf.Round(value)) || value < 0 || value > 2) return false;
            culling = (CullMode)(int)value; return true;
        }

        private static bool UnrestrictedStencil(string source, Material material)
        {
            foreach (Match block in Regex.Matches(source, @"\bStencil\s*\{([^{}]*)\}", RegexOptions.IgnoreCase))
            {
                string body = block.Groups[1].Value;
                if (Regex.IsMatch(body, @"\b(?:Comp|Pass|Fail|ZFail)(?:Front|Back)\b", RegexOptions.IgnoreCase)) return false;
                foreach (string command in new[] { "Comp", "Pass", "Fail", "ZFail" })
                    foreach (Match state in Regex.Matches(body, @"\b" + command + @"\s+(\[\s*([_A-Za-z][_A-Za-z0-9]*)\s*\]|[_A-Za-z0-9]+)", RegexOptions.IgnoreCase))
                    {
                        int expected = command == "Comp" ? (int)CompareFunction.Always : (int)StencilOp.Keep;
                        if (state.Groups[2].Success)
                        {
                            string property = state.Groups[2].Value;
                            if (!material.HasProperty(property) || !material.GetFloat(property).Equals((float)expected)) return false;
                        }
                        else if (!state.Groups[1].Value.Equals(command == "Comp" ? "Always" : "Keep", StringComparison.OrdinalIgnoreCase) &&
                            (!int.TryParse(state.Groups[1].Value, out int value) || value != expected)) return false;
                    }
            }
            return true;
        }

        private static bool UnrestrictedSurface(string source, Material material)
        {
            const string token = @"(\[\s*([_A-Za-z][_A-Za-z0-9]*)\s*\]|[_A-Za-z0-9]+)";
            int Resolve(Match match, Dictionary<string, int> names)
            {
                if (match.Groups[2].Success)
                {
                    string property = match.Groups[2].Value;
                    if (!material.HasProperty(property)) return -1;
                    float value = material.GetFloat(property);
                    return !float.IsNaN(value) && !float.IsInfinity(value) && value >= 0 && value <= 255 && value.Equals(Mathf.Round(value)) ? (int)value : -1;
                }
                string text = match.Groups[1].Value;
                if (names.TryGetValue(text, out int result)) return result;
                return int.TryParse(text, out result) ? result : -1;
            }
            bool Every(string command, Dictionary<string, int> names, Func<int, bool> accepted) =>
                Regex.Matches(source, @"\b" + command + @"\s+" + token, RegexOptions.IgnoreCase).Cast<Match>().All(m => accepted(Resolve(m, names)));
            var toggles = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Off"] = 0, ["On"] = 1 };
            var tests = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Less"] = 2, ["LEqual"] = 4 };
            var masks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["RGB"] = 14, ["RGBA"] = 15 };
            if (!Every("ZWrite", toggles, v => v == 1) || !Every("ZTest", tests, v => v == 2 || v == 4) ||
                !Every("AlphaToMask", toggles, v => v == 0) || !Every("ColorMask", masks, v => (v & 14) == 14 && v >= 0 && v <= 15)) return false;
            // Unknown or additive pass blending is not assumed to cover the body.
            var blendFactors = Enum.GetNames(typeof(BlendMode)).ToDictionary(n => n, n => (int)Enum.Parse(typeof(BlendMode), n), StringComparer.OrdinalIgnoreCase);
            foreach (Match match in Regex.Matches(source, @"\bBlend\s+([^\r\n{}]+)", RegexOptions.IgnoreCase))
            {
                string text = match.Groups[1].Value.Trim();
                if (Regex.IsMatch(text, @"^Off\b", RegexOptions.IgnoreCase)) continue;
                var factors = Regex.Matches(text, token);
                if (factors.Count < 2 || Resolve(factors[0], blendFactors) != (int)BlendMode.One ||
                    Resolve(factors[1], blendFactors) != (int)BlendMode.Zero) return false;
            }
            return true;
        }
    }
}
