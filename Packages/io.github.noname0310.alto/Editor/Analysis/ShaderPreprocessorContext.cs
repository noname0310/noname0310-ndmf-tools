using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine.Rendering;

namespace Noname.AvatarTools.Analysis
{
    internal static class ShaderPreprocessorContext
    {
        // These are compiler target flags, not material/world keyword state.
        // Unknown targets keep the abstract initial environment instead.
        internal static Dictionary<string, string> Definitions(BuildTarget target, ShaderCompilerPlatform backend, ShaderType stage, GraphicsTier? tier = null)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            var tiers = (tier.HasValue ? new[] { tier.Value } : new[] { GraphicsTier.Tier1, GraphicsTier.Tier2, GraphicsTier.Tier3 })
                .Select(t => ShaderUtil.GetShaderPlatformKeywordsForBuildTarget(backend, target, t).Select(d => d.ToString()).ToHashSet(StringComparer.Ordinal)).ToArray();
            foreach (string name in Enum.GetNames(typeof(BuiltinShaderDefine)))
            {
                // Tier-selection defines are added separately by the compiler.
                if (name.StartsWith("UNITY_HARDWARE_TIER", StringComparison.Ordinal)) continue;
                if (tiers.All(t => t.Contains(name))) result[name] = "1";
                else if (tiers.All(t => !t.Contains(name))) result[name] = null;
            }
            if (backend == ShaderCompilerPlatform.D3D || backend == ShaderCompilerPlatform.Metal)
                foreach (string name in new[] { "VERTEX", "FRAGMENT", "HULL", "DOMAIN", "GEOMETRY", "COMPUTE" })
                    result["SHADER_STAGE_" + name] = name.Equals(stage.ToString(), StringComparison.OrdinalIgnoreCase) ? "1" : null;
            string api = backend switch
            {
                ShaderCompilerPlatform.D3D => "D3D11", ShaderCompilerPlatform.OpenGLCore => "GLCORE",
                ShaderCompilerPlatform.GLES3x => "GLES3", ShaderCompilerPlatform.Vulkan => "VULKAN", ShaderCompilerPlatform.Metal => "METAL", _ => null
            };
            if (api != null)
                foreach (string name in new[] { "D3D11", "D3D11_9X", "GLCORE", "GLES", "GLES3", "VULKAN", "METAL" })
                    result["SHADER_API_" + name] = name == api ? "1" : null;
            if (target == BuildTarget.StandaloneWindows || target == BuildTarget.StandaloneWindows64 || target == BuildTarget.StandaloneLinux64 ||
                target == BuildTarget.StandaloneOSX || target == BuildTarget.Android || target == BuildTarget.iOS)
                foreach (string console in new[] { "XBOXONE", "PSSL", "PS4", "PS5", "SWITCH", "GAMECORE", "GAMECORE_XBOXONE", "GAMECORE_SCARLETT", "PS3", "XBOX360", "PSP2", "N3DS", "WIIU" })
                    result["SHADER_API_" + console] = null;
            return result;
        }
    }
}
