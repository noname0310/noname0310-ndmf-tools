using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine.Rendering;

namespace Noname.AvatarTools.Analysis
{
    // The avatar's ordinary renderer schedule, not explicit DrawRenderer calls.
    // Unknown/custom pipelines retain every pass. Shader/property discovery keeps
    // every source; this selection is only used by mesh-input analysis.
    internal static class ShaderPassSchedule
    {
        private static readonly HashSet<string> BuiltinModes = new(new[] {
            "", "Always", "ForwardBase", "ForwardAdd", "Deferred", "ShadowCaster", "MotionVectors",
            "Vertex", "VertexLM", "VertexLMRGBM", "PrepassBase", "PrepassFinal", "Meta" }, StringComparer.OrdinalIgnoreCase);

        internal static bool MayExecute(string lightMode, bool builtinPipeline) =>
            !builtinPipeline || lightMode == null || BuiltinModes.Contains(lightMode);

        internal static bool MayExecute(ShaderData.Pass pass)
        {
            if (GraphicsSettings.currentRenderPipeline != null) return true;
            try { return MayExecute(pass.FindTagValue(new ShaderTagId("LightMode")).name, true); }
            catch (Exception) { return true; }
        }
    }
}
