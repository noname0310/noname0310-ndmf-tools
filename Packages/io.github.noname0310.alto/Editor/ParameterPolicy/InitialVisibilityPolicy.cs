using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.ObjectOptimization;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.ParameterPolicy
{
    // This is an explicit initialization policy. The lossless object optimizer
    // deliberately retains the original state until the first Animator evaluation.
    internal static class InitialVisibilityPolicy
    {
        private sealed class ClipContext
        {
            internal object Owner;
            internal VirtualAnimatorController Controller;
            internal VirtualLayer Layer;
        }

        internal static void Apply(BuildContext context)
        {
            var state = context.GetState<ParameterPolicyState>();
            if (!state.Enabled || !state.BakeInitialVisibility) return;
            using var lease = OptimizationSession.Acquire(context);
            var entries = lease.Services.ControllerContext.Controllers
                .Where(e => !(e.Key is Object owner) || owner != null).ToDictionary(e => e.Key, e => e.Value);
            var policy = ParameterPolicyPass.Resolve(context, entries);
            if (policy.Uncertain) return;
            var external = ExternalParameterAccess.Collect();
            if (external.Uncertain)
            {
                context.GetState<OptimizationBuildReport>().Messages.Add("Initial visibility retained: unverified external parameter declarations.");
                return;
            }
            var analysis = AnimatorReachability.Analyze(context.AvatarRootObject, entries.Values, policy.Preserved);
            var index = AvatarAnimationIndex.Create(context.AvatarRootObject, entries,
                lease.Services.ObjectPathRemapper.GetVirtualToRealPathMap(), policy.Preserved);
            Bake(context.AvatarRootObject, index, analysis, state.Scope,
                context.GetState<ObjectOptimizationState>(), context.GetState<OptimizationBuildReport>().Messages);
        }

        internal static int Bake(GameObject avatar, AvatarAnimationIndex index, AnimatorReachability analysis,
            OptimizationBuildScope scope, ObjectOptimizationState preservation, ICollection<string> messages)
        {
            string unsafeReason = index.UnsafeReason ?? analysis.FallbackReason ?? ObjectOptimizationSafety.UnsupportedRuntime(avatar);
            if (unsafeReason != null)
            {
                messages.Add("Initial visibility retained: " + unsafeReason);
                return 0;
            }
            var contexts = new Dictionary<VirtualClip, List<ClipContext>>();
            foreach (var entry in index.ControllerEntries)
            foreach (var layer in entry.Value.Layers)
            foreach (var clip in layer.AllReachableNodes().OfType<VirtualClip>().Distinct())
            {
                if (!contexts.TryGetValue(clip, out var found)) contexts.Add(clip, found = new());
                found.Add(new ClipContext { Owner = entry.Key, Controller = entry.Value, Layer = layer });
            }
            var nativeWriters = VisibilityMutationAnalysis.NativeWriters(avatar);
            var liveClips = new HashSet<VirtualClip>(index.Controllers.SelectMany(analysis.Clips));
            var removals = new Dictionary<VirtualClip, HashSet<EditorCurveBinding>>();
            var bindingCache = new Dictionary<VirtualMotion, HashSet<EditorCurveBinding>>();
            int changed = 0;
            var groups = index.Bindings.Where(u => !u.Binding.isPPtrCurve &&
                (u.Binding.type == typeof(GameObject) && u.Binding.propertyName == "m_IsActive" ||
                 u.Binding.type != null && typeof(Renderer).IsAssignableFrom(u.Binding.type) && u.Binding.propertyName == "m_Enabled"))
                .SelectMany(u => u.Objects().Where(o => o != null).Select(o => (Target: o, Use: u)))
                .GroupBy(p => (p.Target, p.Use.Binding.propertyName));
            foreach (var group in groups)
            {
                var target = group.Key.Target;
                var transform = target is GameObject obj ? obj.transform : ((Component)target).transform;
                bool initial = target is GameObject gameObject ? gameObject.activeSelf : ((Renderer)target).enabled;
                var uses = group.Select(p => p.Use).Distinct().ToArray();
                var first = uses[0];
                // Unreachable states can remain solely to preserve Write Defaults binding
                // coverage. Their curve values are not possible outputs. Pick a candidate
                // from a live clip, then prove it against every reachable writer state below.
                var live = uses.FirstOrDefault(u => liveClips.Contains(u.Clip));
                if (live == null || !MotionAnalysis.Constant(live.Curve, out float value) || value != 0 && value != 1 ||
                    initial == (value == 1)) continue;
                if (transform == avatar.transform || !scope.Contains(transform) ||
                    preservation?.Protected(transform) == true || target is Component component && preservation?.Protected(component) == true) continue;
                string reason = null;
                if (nativeWriters.Contains(target)) reason = "a native component can change visibility";
                // Preserved inputs are unknown in the value analysis. A constant
                // output may still be independent of those inputs, even when the
                // same clip/tree contains other outputs that must remain dynamic.
                else if (uses.Any(u => u.Immutable || !u.Complete || u.Targets.Length != 1 || u.Roots.Length != 1 ||
                    u.Roots[0] != avatar.transform || u.Objects().Count() != 1)) reason = "an immutable or ambiguous animation binding";
                else if (uses.Select(u => u.Binding).Distinct().Count() != 1) reason = "multiple animation binding aliases";
                var writers = uses.SelectMany(u => contexts[u.Clip]).ToArray();
                if (reason == null && writers.Any(c => !IsFx(c.Owner))) reason = "visibility is not exclusively controlled by FX";
                if (reason == null)
                {
                    var cache = new Dictionary<VirtualMotion, bool>();
                    foreach (var writer in writers.GroupBy(c => (c.Controller, c.Layer)).Select(g => g.First()))
                    {
                        var layer = writer.Layer;
                        // VRChat also derives the playable's mask from the first
                        // controller layer. A mask on that layer can affect other layers.
                        if (layer.AvatarMask != null || writer.Controller.Layers.First().AvatarMask != null)
                        { reason = "a layer or FX playable mask can filter the output"; break; }
                        if (layer.BlendingMode != AnimatorLayerBlendingMode.Override || layer.SyncedLayerIndex != -1)
                        { reason = "an additive or synchronized layer can change the output"; break; }
                        if (layer != writer.Controller.Layers.First() && layer.DefaultWeight != 1)
                        { reason = "FX layer '" + layer.Name + "' does not have full default weight"; break; }
                        if (FxPropertyOutput.WeightCanChange(index.ControllerEntries, writer.Controller, writer.Layer, analysis))
                        { reason = "an SDK behaviour can change the FX playable or writer layer weight"; break; }
                        var states = layer.AllReachableNodes().OfType<VirtualState>().Where(analysis.IsReachable).ToArray();
                        if (states.Length == 0 || states.Any(s => !ConstantPropertyOutput.Matches(s.Motion, first.Binding, value,
                            false, analysis, bindingCache, cache, new())))
                        { reason = "a reachable state or motion does not guarantee the constant output"; break; }
                    }
                }
                string path = AnimationUtility.CalculateTransformPath(transform, avatar.transform) + " / " + group.Key.propertyName;
                if (reason != null) { messages.Add("Initial visibility retained '" + path + "': " + reason + "."); continue; }
                if (target is GameObject activeObject) activeObject.SetActive(value == 1);
                else ((Renderer)target).enabled = value == 1;
                foreach (var use in uses)
                {
                    if (!removals.TryGetValue(use.Clip, out var bindings)) removals.Add(use.Clip, bindings = new());
                    bindings.Add(use.Binding);
                }
                changed++;
                messages.Add("Initial visibility policy: '" + path + "' = " + value + ".");
            }
            foreach (var pair in removals) MotionAnalysis.RemoveCurves(pair.Key, pair.Value);
            messages.Add("Initial visibility policy changed " + changed + " properties and removed " +
                removals.Sum(p => p.Value.Count) + " constant curves.");
            return changed;
        }

        private static bool IsFx(object owner) => owner is VRCAvatarDescriptor.AnimLayerType type && type == VRCAvatarDescriptor.AnimLayerType.FX;

    }
}
