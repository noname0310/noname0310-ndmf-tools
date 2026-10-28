using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.ObjectOptimization;
using Noname.AvatarTools.ParameterPolicy;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.Occlusion
{
    internal sealed class OcclusionBuildState
    {
        internal ALTORemoveOccludedMesh Settings;
        internal OptimizationBuildScope Scope;
        internal bool AfterMaterialConversion;
        internal readonly Dictionary<string, Transform> PoseObjects = new(StringComparer.Ordinal);
    }

    internal static class OcclusionPass
    {
        internal static void Capture(BuildContext context)
        {
            var all = context.AvatarRootObject.GetComponentsInChildren<ALTORemoveOccludedMesh>(true);
            var enabled = all.Where(c => OptimizationBuildScope.Applies(c, context)).ToArray();
            if (enabled.Length > 1) throw new InvalidOperationException("Use one enabled ALTO Remove Occluded Mesh per avatar and platform.");
            foreach (var inactive in all.Except(enabled)) Object.DestroyImmediate(inactive);
            if (enabled.Length == 0) return;
            var state = context.GetState<OcclusionBuildState>();
            state.Settings = enabled[0]; state.Scope = OptimizationBuildScope.Capture(state.Settings, context.AvatarRootObject);
            OcclusionEvaluation.Validate(state.Settings, state.Scope);
            foreach (var pose in new[] { state.Settings.ReferencePose }.Concat(state.Settings.AdditionalPoses ?? new()))
            {
                if (pose.Clip == null) continue;
                foreach (var binding in AnimationUtility.GetCurveBindings(pose.Clip).Concat(AnimationUtility.GetObjectReferenceCurveBindings(pose.Clip)))
                {
                    var target = AnimationUtility.GetAnimatedObject(context.AvatarRootObject, binding);
                    var transform = target is GameObject obj ? obj.transform : (target as Component)?.transform;
                    if (transform == null) throw new InvalidOperationException("An occlusion pose binding is missing: " + binding.path + "/" + binding.propertyName);
                    state.PoseObjects[binding.path] = transform;
                }
            }
        }

        internal static void Apply(BuildContext context)
        {
            var state = context.GetState<OcclusionBuildState>(); if (state.Settings == null) return;
            try
            {
                var avatar = context.AvatarRootObject; var settings = state.Settings;
                var report = context.GetState<OptimizationBuildReport>();
                report.Initialize(avatar, context.GetState<OptimizationBuildEnvironment>().Target.ToString());
                // Explicit authoring references removed by another generator no longer
                // select anything. Do not replace them with a guessed body or outfit.
                settings.Targets ??= new(); settings.Occluders ??= new();
                settings.Targets.RemoveAll(t => t?.Renderer == null);
                settings.Occluders.RemoveAll(t => t?.Renderer == null);
                if (settings.Targets.Count == 0) return;
                using var lease = OptimizationSession.Acquire(context);
                var services = lease.Services;
                var entries = services.ControllerContext.Controllers.Where(e => !(e.Key is Object owner) || owner != null).ToDictionary(e => e.Key, e => e.Value);
                var policy = ParameterPolicyPass.Resolve(context, entries);
                string unsupported = ObjectOptimizationSafety.UnsupportedRuntime(avatar);
                if (policy.Uncertain || unsupported != null)
                { report.Messages.Add("Occlusion removal retained all targets: " + (unsupported ?? "unverified Preserve mapping")); return; }
                var animations = AvatarAnimationIndex.Create(avatar, entries, services.ObjectPathRemapper.GetVirtualToRealPathMap(), policy.Preserved);
                var paths = state.PoseObjects.ToDictionary(p => p.Key, p => p.Value != null && p.Value.IsChildOf(avatar.transform)
                    ? AnimationUtility.CalculateTransformPath(p.Value, avatar.transform) : null, StringComparer.Ordinal);
                var evaluation = OcclusionEvaluation.Run(avatar, settings, state.Scope, animations,
                    r => context.GetState<ObjectOptimizationState>().Protected(r), paths);
                report.Messages.AddRange(evaluation.Messages);
                foreach (var target in evaluation.Targets.Where(t => t.RemovedCount > 0))
                {
                    var mesh = target.Masks.Remove(target.Source, target.RemovedTriangles);
                    ObjectRegistry.RegisterReplacedObject(target.Source, mesh);
                    context.AssetSaver.SaveAsset(mesh); target.Renderer.sharedMesh = mesh;
                }
            }
            finally { if (state.Settings != null) Object.DestroyImmediate(state.Settings); }
        }

        internal static void ChooseStage(BuildContext context)
        {
            var state = context.GetState<OcclusionBuildState>();
            state.AfterMaterialConversion = state.Settings != null &&
                context.GetState<MaterialAtlas.MaterialAtlasState>().Settings != null;
        }
    }
}
