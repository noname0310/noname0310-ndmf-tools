using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.BlendShapeOptimization;
using Noname.AvatarTools.Meshes;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.Build
{
    internal static class BlendShapeExclusionPass
    {
        private sealed class Plan
        {
            internal SkinnedMeshRenderer Renderer;
            internal Mesh Source, Output;
            internal BlendShapeRegionMasks Masks;
            internal bool[] Removed;
        }

        internal static void Apply(BuildContext context)
        {
            var state = context.GetState<RenderingFilterState>();
            if (state.Scope == null || state.BlendShapes.Count == 0) return;
            foreach (var target in state.BlendShapeTargets)
                if (target.Key == null)
                    throw new InvalidOperationException("The renderer selected for BlendShape exclusion was removed or merged before filtering: " + target.Value +
                        ". Select its generated destination instead.");
            using var lease = OptimizationSession.Acquire(context);
            var services = lease.Services;
            var entries = services.ControllerContext.Controllers.Where(e => !(e.Key is Object owner) || owner != null).ToDictionary(e => e.Key, e => e.Value);
            var targets = new HashSet<SkinnedMeshRenderer>(state.BlendShapes.Select(r => r.Renderer));
            ValidateMeshAnimations(context.AvatarRootObject, entries, targets, services.ObjectPathRemapper.GetVirtualToRealPathMap());
            var plans = new List<Plan>();
            // Validate all targets before assigning any mesh. Shared source meshes
            // are copied independently; a neighboring renderer keeps its geometry.
            foreach (var group in state.BlendShapes.GroupBy(r => r.Renderer))
            {
                var renderer = group.Key;
                if (!state.Scope.Contains(renderer.transform)) throw new InvalidOperationException("A BlendShape exclusion target left its Scope Root: " + renderer.name);
                if (renderer.GetComponent<Cloth>() != null) throw new InvalidOperationException("BlendShape exclusion cannot change Cloth topology: " + renderer.name);
                var source = renderer.sharedMesh;
                BlendShapeRegionMasks masks = null; bool[] removed = null;
                foreach (var rule in group)
                {
                    var current = BlendShapeRegionMasks.Create(source, rule.BlendShapes, rule.DisplacementThreshold);
                    if (masks == null) { masks = current; removed = new bool[current.Indices.Length / 3]; }
                    foreach (var region in current.Regions)
                        foreach (int triangle in region.Triangles) removed[triangle] = true;
                }
                if (removed.Any(value => value)) plans.Add(new Plan { Renderer = renderer, Source = source, Masks = masks, Removed = removed });
            }
            try
            {
                foreach (var plan in plans) plan.Output = plan.Masks.Remove(plan.Source, plan.Removed);
                var report = context.GetState<OptimizationBuildReport>();
                report.Initialize(context.AvatarRootObject, context.GetState<OptimizationBuildEnvironment>().Target.ToString());
                foreach (var plan in plans)
                {
                    ObjectRegistry.RegisterReplacedObject(plan.Source, plan.Output);
                    context.AssetSaver.SaveAsset(plan.Output); plan.Renderer.sharedMesh = plan.Output;
                    report.Messages.Add("BlendShape exclusion removed " + plan.Removed.Count(v => v) + " triangles from " +
                        AnimationUtility.CalculateTransformPath(plan.Renderer.transform, context.AvatarRootTransform) +
                        ". Vertex and morph indices remain stable until unused mesh data is compacted.");
                }
                report.Messages.Add("Explicit BlendShape region exclusions affected " + plans.Count + " renderers. Current morph weights did not select regions.");
            }
            catch
            {
                foreach (var plan in plans)
                    if (plan.Output != null && !EditorUtility.IsPersistent(plan.Output) && plan.Renderer.sharedMesh != plan.Output) Object.DestroyImmediate(plan.Output);
                throw;
            }
        }

        private static void ValidateMeshAnimations(GameObject avatar, IDictionary<object, VirtualAnimatorController> controllers,
            ISet<SkinnedMeshRenderer> targets, IReadOnlyDictionary<string, string> paths)
        {
            foreach (var entry in controllers)
            {
                var root = entry.Key is Animator animator ? animator.transform : avatar.transform;
                foreach (var clip in entry.Value.AllReachableNodes().OfType<VirtualClip>().Distinct())
                    foreach (var binding in clip.GetObjectCurveBindings())
                        if (binding.type == typeof(SkinnedMeshRenderer) && binding.propertyName == "m_Mesh" &&
                            AnimationUsage.Resolve(avatar.transform, root, binding.path, paths)
                                .SelectMany(t => t.GetComponents<SkinnedMeshRenderer>()).Any(targets.Contains))
                            throw new InvalidOperationException("BlendShape exclusion cannot safely rewrite animated mesh references: " + clip.Name + "/" + binding.path);
            }
        }
    }
}
