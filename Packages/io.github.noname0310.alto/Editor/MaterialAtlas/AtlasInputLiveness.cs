using System;
using System.Collections.Generic;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.ObjectOptimization;
using Noname.AvatarTools.ParameterPolicy;
using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.MaterialAtlas
{
    // Atlas packing is irreversible: collecting an object afterwards cannot reclaim
    // its texture area. Reuse object collection's liveness proof before packing, but
    // leave hierarchy and authoring references intact until their normal cleanup.
    internal sealed class AtlasInputLiveness
    {
        private readonly Dictionary<Renderer, string> omitted = new();
        internal bool IsOmitted(Renderer renderer) => renderer != null && omitted.ContainsKey(renderer);
        internal bool HasSurvivingInputs
        {
            get { foreach (var renderer in omitted.Keys) if (renderer != null) return true; return false; }
        }

        internal Renderer[] Select(Renderer[] renderers, ObjectOptimizationState settings,
            AvatarAnimationIndex index, ShaderPropertyUsage shaders, ICollection<string> messages)
        {
            var graph = RendererInputLiveness.Analyze(settings, index, shaders);
            if (graph == null) return renderers;
            var selected = new List<Renderer>(renderers.Length);
            foreach (var renderer in renderers)
            {
                if (graph.IsLive(renderer)) { selected.Add(renderer); continue; }
                string path = AnimationUtility.CalculateTransformPath(renderer.transform, index.Avatar.transform);
                omitted[renderer] = path;
                messages.Add("Atlas omitted unobservable renderer: " + path + ". Hierarchy collection remains deferred.");
            }
            return selected.ToArray();
        }

        internal void Validate(ObjectOptimizationState settings, AvatarAnimationIndex index, ShaderPropertyUsage shaders)
        {
            if (!HasSurvivingInputs) return;
            // Fresh facts at each generator boundary: dirty propagation does not rerun
            // texture packing. Fail explicitly if a later writer/reader invalidates an
            // omission instead of silently publishing an incompletely converted avatar.
            var graph = RendererInputLiveness.Analyze(settings, index, shaders);
            foreach (var pair in omitted)
                if (pair.Key != null && (graph == null || graph.IsLive(pair.Key)))
                    throw new InvalidOperationException("Atlas input liveness changed after packing: '" + pair.Value +
                        "' was proven unobservable, but a later build pass introduced a consumer or invalidated that proof. " +
                        "Run that pass before ALTO atlas preparation, or preserve the renderer in ALTO Trace And Optimize Objects.");
        }

    }
}
