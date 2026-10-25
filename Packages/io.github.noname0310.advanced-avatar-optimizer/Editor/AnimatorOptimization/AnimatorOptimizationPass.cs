using System;
using System.Diagnostics;
using System.Linq;
using System.Collections.Generic;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using Noname.AvatarTools.BlendShapeOptimization;
using Noname.AvatarTools.Analysis;

namespace Noname.AvatarTools.AnimatorOptimization
{
    internal sealed class AnimatorOptimizationState
    {
        internal AnimatorOptimizationSettings Settings;
        internal readonly List<AnimatorOptimizationReport> Reports = new();
    }

    internal static class AnimatorOptimizationPass
    {
        internal static void Capture(BuildContext context)
        {
            var state = context.GetState<AnimatorOptimizationState>();
            foreach (var component in context.AvatarRootObject.GetComponentsInChildren<AdvancedTraceAndOptimizeAnimator>(true))
            {
                if (!component.enabled || AdvancedAvatarOptimizerPlugin.IsEditorOnly(component.transform)) continue;
                if (component.FindDescriptor()?.gameObject != context.AvatarRootObject)
                    throw new InvalidOperationException("Advanced Trace And Optimize Animator requires a VRC Avatar Descriptor ancestor.");
                var settings = AnimatorOptimizationSettings.From(component);
                if (state.Settings != null && !state.Settings.Same(settings))
                    throw new InvalidOperationException("Animator optimization components in the same avatar must use matching settings.");
                state.Settings = settings;
            }
        }

        internal static void Apply(BuildContext context)
        {
            foreach (var component in context.AvatarRootObject.GetComponentsInChildren<AdvancedTraceAndOptimizeAnimator>(true))
                UnityEngine.Object.DestroyImmediate(component);
            var state = context.GetState<AnimatorOptimizationState>();
            if (state.Settings == null || !state.Settings.Enabled) return;
            var watch = Stopwatch.StartNew();
            var services = context.ActivateExtensionContextRecursive<AnimatorServicesContext>();
            try
            {
                var entries = services.ControllerContext.Controllers.Where(e => !(e.Key is UnityEngine.Object owner) || owner != null).ToArray();
                var controllers = entries.Select(e => e.Value).Distinct().ToArray();
                var additive = new HashSet<VirtualAnimatorController>(entries.Where(e => e.Key is VRCAvatarDescriptor.AnimLayerType type &&
                    type == VRCAvatarDescriptor.AnimLayerType.Additive).Select(e => e.Value));
                var analysis = AnimatorReachability.Analyze(context.AvatarRootObject, controllers);
                var report = AnimatorOptimizer.Optimize(context.AvatarRootObject, controllers, state.Settings, additive, analysis);
                if (state.Settings.Materials && report.Fallback == null)
                    MaterialConstants.Optimize(context.AvatarRootObject, entries.ToDictionary(e => e.Key, e => e.Value),
                        services.ObjectPathRemapper.GetVirtualToRealPathMap(), material => context.AssetSaver.SaveAsset(material), report, analysis);
                state.Reports.Add(report);
                UnityEngine.Debug.Log($"[Advanced Avatar Optimizer] Animator: {report}; {watch.ElapsedMilliseconds} ms.", context.AvatarRootObject);
            }
            finally
            {
                context.DeactivateExtensionContext<AnimatorServicesContext>();
                context.DeactivateExtensionContext<VirtualControllerContext>();
            }
        }
    }
}
