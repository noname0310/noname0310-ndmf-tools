using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using nadena.dev.ndmf;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.ObjectOptimization;
using Noname.AvatarTools.ParameterPolicy;
using UnityEngine;
using VRC.Dynamics;
using VRC.SDK3.Dynamics.PhysBone.Components;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.Physics
{
    internal sealed class PhysBoneOptimizationState
    {
        internal ALTOTraceAndOptimizePhysBones Component;
        internal OptimizationBuildScope Scope;
        internal bool ShareColliders, OptimizeIsAnimated, ReplaceEndBones, MergeSimulations;
        internal readonly HashSet<ObjectReference> Preserved = new();
        internal readonly List<(Component Original, bool Protected)> Inputs = new();
    }

    // AAO 1.9.20 OptimizePhysBone.MergePhysBoneCollider is a design reference.
    // This independent implementation requires exact local settings and the same
    // effective root. It preserves collision list order and repeated application.
    internal static class PhysBoneOptimizationPass
    {
        private static readonly HashSet<string> ColliderMetadata = new(StringComparer.Ordinal)
        { "m_GameObject", "m_CorrespondingSourceObject", "m_PrefabInstance", "m_PrefabAsset", "rootTransform" };

        internal static void Capture(BuildContext context)
        {
            var all = context.AvatarRootObject.GetComponentsInChildren<ALTOTraceAndOptimizePhysBones>(true);
            var enabled = all.Where(c => c.enabled && BuildPlatformFilters.Includes(c.transform, context)).ToArray();
            if (enabled.Length > 1) throw new InvalidOperationException("Use one enabled ALTO Trace And Optimize PhysBones per avatar.");
            if (enabled.Length == 1)
            {
                var state = context.GetState<PhysBoneOptimizationState>(); var component = state.Component = enabled[0];
                state.Scope = OptimizationBuildScope.Capture(component, context.AvatarRootObject); state.ShareColliders = component.ShareEquivalentColliders;
                state.OptimizeIsAnimated = component.OptimizeIsAnimated;
                state.ReplaceEndBones = component.ReplaceUnusedEndBones;
                state.MergeSimulations = component.MergeCompatibleSimulations;
                foreach (var value in (component.PreservePhysBones ?? new()).Cast<Component>().Concat(component.PreserveColliders ?? new()).Where(c => c != null))
                {
                    if (!value.transform.IsChildOf(context.AvatarRootTransform)) throw new InvalidOperationException("Preserved physics components must belong to this avatar.");
                    state.Preserved.Add(ObjectRegistry.GetReference(value));
                }
            }
            foreach (var component in all.Except(enabled)) Object.DestroyImmediate(component);
        }

        internal static void Prepare(BuildContext context)
        {
            var state = context.GetState<PhysBoneOptimizationState>(); if (state.Component == null) return;
            state.Component.BuildInputs = context.AvatarRootObject.GetComponentsInChildren<Component>(true).Where(c => c is VRCPhysBone || c is VRCPhysBoneCollider).ToArray();
            foreach (var value in state.Component.BuildInputs)
                state.Inputs.Add((value, !state.Scope.Contains(value.transform) || state.Preserved.Contains(ObjectRegistry.GetReference(value)) ||
                    context.GetState<ObjectOptimizationState>().Protected(value)));
        }

        internal static bool Protected(PhysBoneOptimizationState state, Component value, ObjectOptimizationState objects)
        {
            if (value == null || !state.Scope.Contains(value.transform) || state.Preserved.Contains(ObjectRegistry.GetReference(value)) || objects.Protected(value)) return true;
            for (int i = 0; i < state.Inputs.Count; i++)
            {
                var mapped = state.Component != null && state.Component.BuildInputs.Length == state.Inputs.Count ? state.Component.BuildInputs[i] : state.Inputs[i].Original;
                if (mapped == value && state.Inputs[i].Protected) return true;
            }
            return false;
        }

        internal static void Apply(BuildContext context)
        {
            try { ApplyCore(context); }
            finally
            {
                foreach (var component in context.AvatarRootObject.GetComponentsInChildren<ALTOTraceAndOptimizePhysBones>(true)) Object.DestroyImmediate(component);
            }
        }

        private static void ApplyCore(BuildContext context)
        {
            var settings = context.GetState<PhysBoneOptimizationState>(); if (settings.Scope == null ||
                (!settings.ShareColliders && !settings.OptimizeIsAnimated && !settings.ReplaceEndBones && !settings.MergeSimulations)) return;
            var avatar = context.AvatarRootObject; var colliders = avatar.GetComponentsInChildren<VRCPhysBoneCollider>(true);
            var bones = avatar.GetComponentsInChildren<VRCPhysBone>(true);
            if (colliders.Length < 2 && bones.Length == 0) return;
            var watch = Stopwatch.StartNew(); var report = context.GetState<OptimizationBuildReport>();
            report.Initialize(avatar, context.GetState<OptimizationBuildEnvironment>().Target.ToString());
            using var lease = OptimizationSession.Acquire(context); var services = lease.Services;
            var entries = services.ControllerContext.Controllers.Where(e => !(e.Key is Object owner) || owner != null).ToDictionary(e => e.Key, e => e.Value);
            var policy = ParameterPolicyPass.Resolve(context, entries);
            var animations = AvatarAnimationIndex.Create(avatar, entries, services.ObjectPathRemapper.GetVirtualToRealPathMap(), policy.Preserved);
            string issue = ObjectOptimizationSafety.UnsupportedRuntime(avatar) ?? animations.UnsafeReason;
            if (policy.Uncertain || issue != null)
            { report.Messages.Add("PhysBone optimization retained inputs: " + (issue ?? "unverified Preserve mapping")); return; }
            var references = AvatarDependencyGraph.Create(avatar, animations, new ObjectOptimizationState
            { Scope = new OptimizationBuildScope { Root = avatar.transform } }, false);
            var objects = context.GetState<ObjectOptimizationState>();
            var mutations = settings.OptimizeIsAnimated || settings.ReplaceEndBones || settings.MergeSimulations ? TransformMutationAnalysis.Create(avatar, animations) : null;
            int animatedFlags = 0;
            if (settings.OptimizeIsAnimated)
            {
                if (mutations.Uncertain == null)
                    foreach (var bone in bones)
                    {
                        if (!bone.isAnimated || Protected(settings, bone, objects) || animations.ForObject(bone).Count > 0) continue;
                        if (references.ReferenceOwners.TryGetValue(bone, out var readers) && readers.Count > 0) continue;
                        var target = bone.GetRootTransform();
                        if (target == null || !target.IsChildOf(avatar.transform)) continue;
                        if (target.GetComponentsInChildren<Transform>(true).Any(t => objects.Protected(t) || mutations.WrittenByOther(t, bone))) continue;
                        // AAO's OptimizePhysBoneIsAnimated identifies a zero-to-nonzero
                        // owner-scale hazard. Check both the component and simulation
                        // root ancestors, also when the owner is outside the chain.
                        bool animatedScale = false;
                        foreach (var start in new[] { bone.transform, target })
                            for (var parent = start; parent != null && parent.IsChildOf(avatar.transform); parent = parent.parent)
                                animatedScale |= mutations.WrittenByOther(parent, bone, scaleOnly: true);
                        if (animatedScale) continue;
                        bone.isAnimated = false; animatedFlags++;
                    }
                else report.Messages.Add("Is Animated flags retained: " + mutations.Uncertain);
            }
            var animatedReferences = animations.Bindings.SelectMany(u => u.ObjectKeys ?? Array.Empty<UnityEditor.ObjectReferenceKeyframe>())
                .Select(k => k.value).OfType<VRCPhysBoneCollider>().ToHashSet();
            bool AlwaysActive(Transform target)
            {
                for (var current = target; current != null && current != avatar.transform; current = current.parent)
                    if (!current.gameObject.activeSelf || animations.Writes(current.gameObject, "m_IsActive")) return false;
                return true;
            }
            bool Eligible(VRCPhysBoneCollider collider)
            {
                if (Protected(settings, collider, objects) || !collider.enabled || !AlwaysActive(collider.transform) ||
                    collider.globalCollision != VRCPhysBoneBase.AdvancedBool.False || animations.ForObject(collider).Count > 0 || animatedReferences.Contains(collider)) return false;
                var root = collider.rootTransform != null ? collider.rootTransform : collider.transform;
                if (!root.IsChildOf(avatar.transform)) return false;
                if (collider.GetComponents<Component>().Any(c => c != null && c != collider && BuildFilterPass.Requires(c, collider))) return false;
                if (references.ReferenceOwners.TryGetValue(collider, out var readers) && readers.Any(r => !(r is VRCPhysBone))) return false;
                foreach (var bone in bones.Where(b => b.colliders != null && b.colliders.Contains(collider)))
                    if (Protected(settings, bone, objects) || animations.ForObject(bone).Any(u => u.Binding.propertyName.StartsWith("colliders", StringComparison.Ordinal))) return false;
                return true;
            }
            bool Same(VRCPhysBoneCollider a, VRCPhysBoneCollider b) =>
                (a.rootTransform != null ? a.rootTransform : a.transform) == (b.rootTransform != null ? b.rootTransform : b.transform) &&
                SerializedValueEquality.Matches(a, b, ColliderMetadata);
            var groups = new List<List<VRCPhysBoneCollider>>();
            foreach (var collider in settings.ShareColliders ? colliders.Where(Eligible) : Enumerable.Empty<VRCPhysBoneCollider>())
            {
                var group = groups.FirstOrDefault(g => Same(g[0], collider) &&
                    !bones.Any(b => b.colliders != null && b.colliders.Contains(collider) && g.Any(c => b.colliders.Contains(c))));
                if (group == null) groups.Add(group = new());
                group.Add(collider);
            }
            var replacements = groups.SelectMany(g => g.Skip(1).Select(c => (From: c, To: g[0]))).ToDictionary(p => p.From, p => p.To);
            foreach (var bone in bones)
                if (bone.colliders != null)
                    for (int i = 0; i < bone.colliders.Count; i++)
                        if (bone.colliders[i] is VRCPhysBoneCollider original && replacements.TryGetValue(original, out var target)) bone.colliders[i] = target;
            foreach (var original in replacements.Keys) Object.DestroyImmediate(original);
            int endBones = settings.ReplaceEndBones ? PhysBoneEndpoints.ReplaceUnused(avatar, bones, settings, objects, animations, references, mutations) : 0;
            int simulations = settings.MergeSimulations ? PhysBoneSimulationMerge.Apply(avatar, bones, settings, objects, animations, references, mutations,
                services.ObjectPathRemapper) : 0;
            report.Messages.Add("PhysBone optimization: " + replacements.Count + " equivalent local colliders shared; " + watch.ElapsedMilliseconds +
                " ms; " + animatedFlags + " unused Is Animated flags disabled; " + endBones + " unused leaf transforms replaced with endpoints. " +
                simulations + " compatible simulation components merged. " +
                "Global collisions, animation, external readers and collision multiplicity retained.");
        }
    }
}
