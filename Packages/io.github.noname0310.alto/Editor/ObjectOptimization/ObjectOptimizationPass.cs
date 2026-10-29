using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.ParameterPolicy;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Noname.AvatarTools.ObjectOptimization
{
    internal sealed class ObjectOptimizationState
    {
        internal ALTOTraceAndOptimizeObjects Component;
        internal OptimizationBuildScope Scope;
        internal readonly List<OptimizationBuildScope> ProtectedSubtrees = new();
        internal readonly HashSet<ObjectReference> ProtectedComponents = new();
        internal readonly HashSet<string> InitialReaders = new(StringComparer.Ordinal);
        internal readonly HashSet<string> PrunedSensorInputs = new(StringComparer.Ordinal);
        internal bool ConsumersCaptured;
        internal bool PruneUnusedSensorParameters;
        internal bool RemoveUnusedTransforms;
        internal bool RemoveRedundantConstraints;
        internal bool RemoveUnusedObjects;
        // Set only while the SDK finalizer analyzes completed generator output.
        internal bool RuntimeDependenciesOnly;
        internal Action<UnityEngine.Object> DestroyObject = target => UnityEngine.Object.DestroyImmediate(target);
        internal bool TryDestroy(UnityEngine.Object target)
        {
            if (target == null) return false;
            // Editor integrations can decline destruction without throwing. Only an
            // observed removal is progress for the dependency fixed point.
            DestroyObject(target);
            return target == null;
        }
        internal bool Enabled => Scope != null && (PruneUnusedSensorParameters || RemoveUnusedTransforms ||
            RemoveRedundantConstraints || RemoveUnusedObjects);
        internal bool Protected(Component value) => value != null &&
            (ProtectedSubtrees.Any(s => s.Contains(value.transform)) || ProtectedComponents.Contains(ObjectRegistry.GetReference(value)));
    }

    internal static class ObjectOptimizationPass
    {
        internal static void Capture(BuildContext context)
        {
            var all = context.AvatarRootObject.GetComponentsInChildren<ALTOTraceAndOptimizeObjects>(true);
            var enabled = all.Where(c => c.enabled && BuildPlatformFilters.IncludesSettings(c.transform, context)).ToArray();
            if (enabled.Length > 1) throw new InvalidOperationException("Use one enabled ALTO Trace And Optimize Objects per avatar.");
            foreach (var disabled in all.Except(enabled)) UnityEngine.Object.DestroyImmediate(disabled);
            if (enabled.Length == 0) return;
            var state = context.GetState<ObjectOptimizationState>();
            state.Component = enabled[0]; state.Scope = OptimizationBuildScope.Capture(enabled[0], context.AvatarRootObject);
            state.PruneUnusedSensorParameters = state.Component.PruneUnusedSensorParameters;
            state.RemoveUnusedTransforms = state.Component.RemoveUnusedTransformOutputs;
            state.RemoveRedundantConstraints = state.Component.RemoveRedundantConstraints;
            state.RemoveUnusedObjects = state.Component.RemoveUnusedObjects;
            foreach (var obj in (state.Component.PreserveObjects ?? new()).Where(o => o != null))
            {
                if (!obj.transform.IsChildOf(context.AvatarRootTransform))
                    throw new InvalidOperationException("Preserved objects must belong to this avatar: " + obj.name);
                var subtree = new OptimizationBuildScope { Root = obj.transform };
                subtree.OriginalTransforms.UnionWith(obj.GetComponentsInChildren<Transform>(true));
                state.ProtectedSubtrees.Add(subtree);
            }
            foreach (var component in (state.Component.PreserveComponents ?? new()).Where(c => c != null))
            {
                if (!component.transform.IsChildOf(context.AvatarRootTransform))
                    throw new InvalidOperationException("Preserved components must belong to this avatar: " + component.name);
                state.ProtectedComponents.Add(ObjectRegistry.GetReference(component));
            }
        }

        internal static void CaptureConsumers(BuildContext context,
            IEnumerable<KeyValuePair<object, VirtualAnimatorController>> entries)
        {
            var state = context.GetState<ObjectOptimizationState>();
            if (state.Scope == null || state.ConsumersCaptured) return;
            IncludeConsumers(state, context.AvatarRootObject, entries);
            state.ConsumersCaptured = true;
        }

        internal static void IncludeConsumers(ObjectOptimizationState state, GameObject avatar,
            IEnumerable<KeyValuePair<object, VirtualAnimatorController>> entries)
        {
            if (state.Scope == null) return;
            // A private child Animator input is not evidence that an avatar sensor
            // ever had a consumer. Unknown owners provide no positive removal proof.
            var controllers = entries.Where(e => e.Key is VRCAvatarDescriptor.AnimLayerType ||
                    e.Key is Animator owner && owner != null && owner.gameObject == avatar)
                .Select(e => e.Value).Distinct();
            state.InitialReaders.UnionWith(ParameterSpecialization.Reads(controllers.SelectMany(c => c.AllReachableNodes()))
                .Where(n => !string.IsNullOrEmpty(n)));
        }

        internal static void Apply(BuildContext context)
            => Apply(context, false);

        internal static void ApplyFinal(BuildContext context)
            => Apply(context, true);

        internal static int ApplyFinalAndCountChanges(BuildContext context)
            => Apply(context, true);

        private static int Apply(BuildContext context, bool finalHierarchy)
        {
            var state = context.GetState<ObjectOptimizationState>();
            // AAO removes editor-only settings components. Final dependency analysis uses
            // captured scope/preservation data and must not depend on that marker surviving.
            if (finalHierarchy ? !state.RemoveUnusedTransforms && !state.RemoveUnusedObjects && !state.PruneUnusedSensorParameters &&
                !state.RemoveRedundantConstraints : state.Component == null) return 0;
            var watch = Stopwatch.StartNew();
            var avatar = context.AvatarRootObject;
            var settings = state.Component;
            if (!finalHierarchy)
                settings.BuildPreservedComponents = avatar.GetComponentsInChildren<Component>(true)
                    .Where(c => c != null && c != settings && state.Protected(c)).ToArray();
            var report = context.GetState<OptimizationBuildReport>();
            report.Initialize(avatar, context.GetState<OptimizationBuildEnvironment>().Target.ToString());
            var animationLease = Noname.AvatarTools.Build.OptimizationSession.Acquire(context);
            var services = animationLease.Services;
            try
            {
                var entries = services.ControllerContext.Controllers.Where(e => !(e.Key is UnityEngine.Object owner) || owner != null)
                    .ToDictionary(e => e.Key, e => e.Value);
                var controllers = entries.Values.Distinct().ToArray();
                CaptureConsumers(context, entries);
                var policy = ParameterPolicyPass.Resolve(context, entries);
                var analysis = AnimatorReachability.Analyze(avatar, controllers, policy.Preserved);
                string unsupported = ObjectOptimizationSafety.UnsupportedRuntime(avatar);
                if (policy.Uncertain || analysis.FallbackReason != null || unsupported != null)
                {
                    report.Messages.Add("Object optimization retained all outputs: " +
                        (unsupported ?? analysis.FallbackReason ?? "unverified Preserve mapping"));
                    return 0;
                }
                var external = ExternalParameterAccess.Collect();
                if (external.Uncertain)
                {
                    report.Messages.Add("Object optimization retained external inputs: " + string.Join("; ", external.Diagnostics));
                    return 0;
                }
                int changes = 0;
                if (finalHierarchy ? state.PruneUnusedSensorParameters : settings.PruneUnusedSensorParameters)
                    changes += SensorParameterPruning.Apply(avatar, controllers, state, policy.Preserved, external, report.Messages);
                if (finalHierarchy ? state.RemoveUnusedTransforms : settings.RemoveUnusedTransformOutputs)
                    changes += UnusedTransformOutputs.Apply(avatar, entries, services.ObjectPathRemapper.GetVirtualToRealPathMap(),
                        state, policy.Preserved, report.Messages);
                if (!finalHierarchy && (settings.BakeConstantTransforms || settings.BakeConstantVisibility))
                    InvariantObjectCurves.Apply(avatar, entries, services.ObjectPathRemapper.GetVirtualToRealPathMap(),
                        state, analysis, report.Messages);
                if (finalHierarchy && state.RemoveRedundantConstraints)
                {
                    changes += ConstraintSourcePruning.Apply(avatar, entries, services.ObjectPathRemapper.GetVirtualToRealPathMap(),
                        state, policy.Preserved, report.Messages);
                    changes += RedundantConstraints.Apply(avatar, entries, services.ObjectPathRemapper.GetVirtualToRealPathMap(),
                        state, policy.Preserved, report.Messages);
                    changes += ParentConstraintOptimization.Apply(avatar, entries, services.ObjectPathRemapper.GetVirtualToRealPathMap(),
                        state, policy.Preserved, report.Messages);
                }
                // Defer collection until external generators and optimizers have consumed
                // their authoring components. Their explicit renderer/bone references stay valid.
                if (finalHierarchy && state.RemoveUnusedObjects)
                    changes += UnusedObjectCollection.Apply(avatar, entries, services.ObjectPathRemapper.GetVirtualToRealPathMap(),
                        state, policy.Preserved, report.Messages);
                report.Messages.Add("Object output simplification completed in " + watch.ElapsedMilliseconds +
                    " ms. Runtime dependencies, scope boundaries and explicit preservation were respected.");
                return changes;
            }
            finally
            {
                animationLease.Dispose();
            }
        }

        internal static void RemoveMarkers(BuildContext context)
        {
            foreach (var component in context.AvatarRootObject.GetComponentsInChildren<ALTOTraceAndOptimizeObjects>(true))
                UnityEngine.Object.DestroyImmediate(component);
        }
    }
}
