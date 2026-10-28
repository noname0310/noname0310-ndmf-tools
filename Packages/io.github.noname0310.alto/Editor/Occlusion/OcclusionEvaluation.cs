using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.Build;
using UnityEngine;
using UnityEngine.Rendering;

namespace Noname.AvatarTools.Occlusion
{
    internal sealed class OcclusionTargetResult
    {
        internal SkinnedMeshRenderer Renderer;
        internal Mesh Source;
        internal OcclusionRegionMasks Masks;
        internal bool[] RemovedTriangles;
        internal string[] RemovedRegions;
        internal int RemovedCount => RemovedTriangles.Count(value => value);
    }

    internal sealed class OcclusionEvaluation
    {
        internal readonly List<OcclusionTargetResult> Targets = new();
        internal readonly List<string> Messages = new();
        internal long Milliseconds;

        internal static void Validate(ALTORemoveOccludedMesh settings, OptimizationBuildScope scope)
        {
            if (!Enum.IsDefined(typeof(RendererSelectionMode), settings.OccluderSelection) ||
                settings.ViewCount < 6 || settings.ViewCount > 1024 || settings.RenderResolution < 16 || settings.RenderResolution > 4096 ||
                settings.BoundaryMarginPixels < 0 || settings.BoundaryMarginPixels > 32 ||
                !MotionAnalysis.Finite(settings.DepthTolerance) || settings.DepthTolerance < 0 ||
                !MotionAnalysis.Finite(settings.DisplacementThreshold) || settings.DisplacementThreshold < 0)
                throw new InvalidOperationException("Invalid occlusion sampling settings.");
            var selected = new HashSet<SkinnedMeshRenderer>();
            foreach (var target in settings.Targets ?? new())
            {
                if (target?.Renderer == null || !scope.Contains(target.Renderer.transform) || !selected.Add(target.Renderer))
                    throw new InvalidOperationException("Occlusion targets must be distinct SkinnedMeshRenderers inside Scope Root.");
                if ((target.BlendShapes ?? new()).Any(string.IsNullOrEmpty)) throw new InvalidOperationException("Choose existing BlendShape regions.");
            }
            var overrides = new HashSet<Renderer>();
            foreach (var rule in settings.Occluders ?? new())
                if (rule?.Renderer == null || !(rule.Renderer is MeshRenderer || rule.Renderer is SkinnedMeshRenderer) ||
                    !scope.Contains(rule.Renderer.transform) || !overrides.Add(rule.Renderer) || !Enum.IsDefined(typeof(OcclusionCulling), rule.Culling))
                    throw new InvalidOperationException("Occluders must be distinct mesh renderers inside Scope Root with valid culling.");
            foreach (var pose in new[] { settings.ReferencePose }.Concat(settings.AdditionalPoses ?? new()))
                if (pose == null || !MotionAnalysis.Finite(pose.Time) || pose.Time < 0)
                    throw new InvalidOperationException("Occlusion poses require a finite, nonnegative sample time.");
            foreach (var renderer in settings.ExcludedOccluders ?? new())
                if (renderer != null && !scope.Contains(renderer.transform)) throw new InvalidOperationException("Excluded occluders must belong to Scope Root.");
        }

        internal static OcclusionEvaluation Run(GameObject avatar, ALTORemoveOccludedMesh settings,
            OptimizationBuildScope scope, AvatarAnimationIndex animations = null, Func<Renderer, bool> protectedRenderer = null,
            IReadOnlyDictionary<string, string> posePaths = null)
        {
            Validate(settings, scope);
            var result = new OcclusionEvaluation(); var watch = Stopwatch.StartNew();
            string unsupported = OcclusionRasterizer.UnsupportedReason() ?? animations?.UnsafeReason;
            if (unsupported != null) { result.Messages.Add("Occlusion analysis retained all targets: " + unsupported); return result; }
            var candidates = (settings.Targets ?? new()).Select(t => t.Renderer).ToHashSet();
            foreach (var target in settings.Targets ?? new())
            {
                var renderer = target.Renderer;
                if (target.BlendShapes == null || target.BlendShapes.Count == 0) continue;
                if (protectedRenderer?.Invoke(renderer) == true || MeshCanChange(animations, renderer))
                { result.Messages.Add(renderer.name + ": target preserved by protection or mesh animation."); continue; }
                if (renderer.sharedMesh == null || target.BlendShapes.Any(n => renderer.sharedMesh.GetBlendShapeIndex(n) < 0))
                { result.Messages.Add(renderer.name + ": selected BlendShape regions are unavailable after mesh generation; target retained."); continue; }
                result.Targets.Add(new OcclusionTargetResult { Renderer = renderer, Source = renderer.sharedMesh,
                    Masks = OcclusionRegionMasks.Create(renderer.sharedMesh, target.BlendShapes, settings.DisplacementThreshold) });
            }
            if (result.Targets.Count == 0) return result;
            var policy = new OcclusionMaterialPolicy();
            var rules = (settings.Occluders ?? new()).ToDictionary(o => o.Renderer);
            var excluded = (settings.ExcludedOccluders ?? new()).ToHashSet();
            var occluders = (settings.OccluderSelection == RendererSelectionMode.AllRenderersInScope
                ? avatar.GetComponentsInChildren<Renderer>(true).AsEnumerable() : rules.Keys)
                .Where(r => r != null && (r is SkinnedMeshRenderer || r is MeshRenderer) && scope.Contains(r.transform) &&
                    !candidates.Contains(r as SkinnedMeshRenderer) && !excluded.Contains(r)).ToArray();
            var safeOccluders = new List<Renderer>();
            var lodRenderers = avatar.GetComponentsInChildren<LODGroup>(true).Where(g => g.enabled)
                .SelectMany(g => g.GetLODs()).SelectMany(l => l.renderers).ToHashSet();
            foreach (var renderer in occluders)
            {
                rules.TryGetValue(renderer, out var rule);
                string issue = lodRenderers.Contains(renderer) ? "LOD visibility" : OccluderIssue(avatar, animations, renderer, rule);
                if (issue != null)
                    result.Messages.Add(renderer.name + ": not used as an occluder: " + issue);
                else safeOccluders.Add(renderer);
            }
            var flags = result.Targets.ToDictionary(t => t, _ => new List<uint[]>());
            var directions = OcclusionRasterizer.Directions(settings.ViewCount);
            var diagnostics = new HashSet<string>(StringComparer.Ordinal);
            int poseIndex = 0;
            foreach (var pose in new[] { settings.ReferencePose }.Concat(settings.AdditionalPoses ?? new()))
            {
                using var snapshot = new OcclusionPoseSnapshot(avatar, pose, posePaths);
                var surfaces = new List<OcclusionSurface>();
                foreach (var renderer in safeOccluders)
                {
                    if (!snapshot.Active(renderer)) continue;
                    var copy = snapshot.Copy(renderer);
                    rules.TryGetValue(renderer, out var rule);
                    if (copy.HasPropertyBlock() && !ExplicitSurface(rule)) { diagnostics.Add(renderer.name + ": property block output is not assumed opaque."); continue; }
                    var mesh = snapshot.WorldMesh(renderer); if (mesh == null) continue;
                    var materials = copy.sharedMaterials; var indices = new Dictionary<CullMode, List<int>>();
                    for (int submesh = 0; submesh < mesh.subMeshCount && submesh < materials.Length; submesh++)
                    {
                        if (mesh.GetTopology(submesh) != MeshTopology.Triangles || !policy.TryCulling(materials[submesh], rule, out var cull))
                        { diagnostics.Add(renderer.name + ": one or more submeshes have ambiguous or non-opaque shader state; those surfaces are excluded."); continue; }
                        if (!indices.TryGetValue(cull, out var part)) indices.Add(cull, part = new());
                        part.AddRange(mesh.GetIndices(submesh, applyBaseVertex: true));
                    }
                    foreach (var pair in indices.Where(p => p.Value.Count > 0))
                        surfaces.Add(new OcclusionSurface { Mesh = snapshot.Proxy(mesh, pair.Value.ToArray()), Culling = pair.Key });
                }
                foreach (var target in result.Targets)
                {
                    var names = target.Masks.Regions.Select(r => r.Name);
                    var mesh = snapshot.WorldMesh(target.Renderer, names);
                    if (mesh == null || mesh.vertexCount != target.Source.vertexCount)
                    {
                        flags[target].Add(new uint[target.Masks.Indices.Length / 3]);
                        diagnostics.Add(target.Renderer.name + ": pose changed mesh identity or bone scope; target retained.");
                    }
                    else flags[target].Add(OcclusionRasterizer.Evaluate(mesh.vertices, target.Masks.Indices, surfaces, directions,
                        settings.RenderResolution, settings.BoundaryMarginPixels, settings.DepthTolerance));
                }
                result.Messages.Add("Occlusion pose " + poseIndex++ + ": " + surfaces.Count + " opaque proxy surfaces sampled.");
            }
            foreach (var target in result.Targets)
            {
                target.RemovedTriangles = target.Masks.RemovalMask(flags[target], out target.RemovedRegions);
                result.Messages.Add(target.Renderer.name + ": " + target.RemovedCount + " triangles in " + target.RemovedRegions.Length +
                    " fully hidden regions" + (target.RemovedRegions.Length == 0 ? "." : " (" + string.Join(", ", target.RemovedRegions) + ")."));
            }
            result.Messages.AddRange(diagnostics); result.Milliseconds = watch.ElapsedMilliseconds;
            result.Messages.Add("Occlusion sampling completed in " + result.Milliseconds + " ms. Only sampled poses, views and geometric opacity are evaluated.");
            return result;
        }

        internal static bool MeshCanChange(AvatarAnimationIndex animations, Renderer renderer) => animations != null &&
            animations.ForObject(renderer).Concat(animations.ForObject(renderer.GetComponent<MeshFilter>()))
                .Any(u => u.Binding.propertyName == "m_Mesh" || u.Binding.propertyName == "sharedMesh");

        private static bool ExplicitSurface(OcclusionOccluder rule) => rule?.AssumeOpaque == true && rule.Culling != OcclusionCulling.Automatic;

        internal static string OccluderIssue(GameObject avatar, AvatarAnimationIndex animations, Renderer renderer, OcclusionOccluder rule = null)
        {
            if (animations == null) return null;
            if (MeshCanChange(animations, renderer)) return "animated mesh reference";
            var writers = animations.ForObject(renderer).Where(u => !u.Binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal) &&
                !(ExplicitSurface(rule) && u.Curve != null && u.Binding.propertyName.StartsWith("material.", StringComparison.Ordinal))).ToArray();
            if (writers.Length > 0) return "animated renderer properties [" +
                string.Join(", ", writers.Select(w => w.Binding.propertyName).Distinct().OrderBy(n => n, StringComparer.Ordinal)) + "]";
            for (var current = renderer.transform; current != null && current != avatar.transform; current = current.parent)
                if (animations.Writes(current.gameObject, "m_IsActive")) return "animated active state on " + current.name;
            return null;
        }
    }
}
