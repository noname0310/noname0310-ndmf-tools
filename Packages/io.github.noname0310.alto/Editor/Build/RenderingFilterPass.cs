using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using Noname.AvatarTools.BlendShapeOptimization;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.Build
{
    internal sealed class RenderingFilterState
    {
        internal OptimizationBuildScope Scope;
        internal readonly List<MaterialExclusion> Materials = new();
        internal readonly Dictionary<MaterialExclusion, string> SelectedRenderers = new();
        internal readonly List<BlendShapeMeshExclusion> BlendShapes = new();
        internal readonly Dictionary<SkinnedMeshRenderer, string> BlendShapeTargets = new();
        internal bool RemoveAdditionalMaterialPasses;
        internal RendererFilterTypes ExcludedRendererTypes;
        internal readonly HashSet<ObjectReference> PreserveAutomaticRenderers = new();
    }

    internal static class RenderingFilterPass
    {
        // Capture authoring references before early exclusions delete them. The
        // captured rules execute after generators, including generated renderers.
        internal static void Capture(BuildContext context, HashSet<Object> removed)
        {
            var avatar = context.AvatarRootObject;
            var filters = avatar.GetComponentsInChildren<ALTOPlatformOptimizationComponent>(true)
                .Where(c => c is ALTORenderingFilter || c is ALTOBuildFilter)
                .Where(c => OptimizationBuildScope.Applies(c, context) && !removed.Contains(c)).ToArray();
            if (filters.Length > 1)
                throw new InvalidOperationException("Use one enabled ALTO Rendering Filter per avatar and platform. A legacy Build Filter also provides rendering rules.");
            if (filters.Length == 0) return;
            var source = filters[0];
            var current = source as ALTORenderingFilter;
            var legacy = source as ALTOBuildFilter;
            var scope = OptimizationBuildScope.Capture(source, avatar);
            var materialExclusions = current != null ? current.MaterialExclusions : legacy.MaterialExclusions;
            var blendShapeExclusions = current != null ? current.BlendShapeExclusions : legacy.BlendShapeExclusions;
            var removeAdditionalMaterialPasses = current != null ? current.RemoveAdditionalMaterialPasses : legacy.RemoveAdditionalMaterialPasses;
            var excludedRendererTypes = current != null ? current.ExcludedRendererTypes : legacy.ExcludedRendererTypes;
            var preserveAutomaticRenderers = current != null ? current.PreserveAutomaticRenderers : legacy.PreserveAutomaticRenderers;
            var state = context.GetState<RenderingFilterState>();
            state.Scope = scope;
            if ((excludedRendererTypes & ~(RendererFilterTypes.ParticleSystems | RendererFilterTypes.Lines | RendererFilterTypes.Trails)) != 0)
                throw new InvalidOperationException("Choose valid renderer exclusion types.");
            state.RemoveAdditionalMaterialPasses = removeAdditionalMaterialPasses;
            state.ExcludedRendererTypes = excludedRendererTypes;
            foreach (var renderer in (preserveAutomaticRenderers ?? new()).Where(r => r != null))
            {
                ValidateScope(scope, renderer.transform);
                state.PreserveAutomaticRenderers.Add(ObjectRegistry.GetReference(renderer));
            }
            foreach (var rule in materialExclusions ?? new())
            {
                if (rule == null || (rule.Renderer == null && rule.Material == null && rule.Slot == -1)) continue;
                if (rule.Slot < -1) throw new InvalidOperationException("Material exclusion Slot must be -1 or a nonnegative index.");
                if (rule.Renderer != null)
                {
                    ValidateScope(scope, rule.Renderer.transform);
                    if (removed.Contains(rule.Renderer)) continue;
                    state.SelectedRenderers.Add(rule, AnimationUtility.CalculateTransformPath(rule.Renderer.transform, avatar.transform));
                }
                state.Materials.Add(rule);
            }
            foreach (var rule in blendShapeExclusions ?? new())
            {
                if (rule == null || rule.BlendShapes == null || rule.BlendShapes.Count == 0) continue;
                if (rule.Renderer == null) throw new InvalidOperationException("BlendShape exclusion requires a Skinned Mesh Renderer.");
                ValidateScope(scope, rule.Renderer.transform);
                if (removed.Contains(rule.Renderer)) continue;
                if (!Analysis.MotionAnalysis.Finite(rule.DisplacementThreshold) || rule.DisplacementThreshold < 0 || rule.BlendShapes.Any(string.IsNullOrEmpty))
                    throw new InvalidOperationException("BlendShape exclusion requires valid names and a finite nonnegative displacement threshold.");
                state.BlendShapes.Add(new BlendShapeMeshExclusion
                {
                    Renderer = rule.Renderer, DisplacementThreshold = rule.DisplacementThreshold,
                    BlendShapes = new List<string>(rule.BlendShapes)
                });
                state.BlendShapeTargets[rule.Renderer] = AnimationUtility.CalculateTransformPath(rule.Renderer.transform, avatar.transform);
            }
            if (state.Materials.Count > 0)
                context.GetState<OptimizationBuildReport>().Initialize(avatar, context.GetState<OptimizationBuildEnvironment>().Target.ToString());
        }

        private static void ValidateScope(OptimizationBuildScope scope, Transform target)
        {
            if (!scope.Contains(target))
                throw new InvalidOperationException("Rendering filter targets must belong to its Scope Root: " + target.name);
        }
    }
}
