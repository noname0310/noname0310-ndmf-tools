using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using nadena.dev.ndmf.util;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.BlendShapeOptimization;
using UnityEditor;
using UnityEngine;
using VRC.SDKBase;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.Build
{
    internal sealed class AudioOutputFilterState
    {
        internal OptimizationBuildScope Scope;
        internal readonly HashSet<ObjectReference> Preserved = new();
        internal Action<Object> DestroyObject = value => Object.DestroyImmediate(value);
    }

    // The authoring component is removed before generators. Keep the policy, not
    // a snapshot of AudioSources, so every later boundary can inspect new output.
    internal static class AudioOutputFilterPass
    {
        internal static void Capture(BuildContext context, ALTOBuildExclusions filter, OptimizationBuildScope scope)
        {
            if (!filter.RemoveAudioSources) return;
            var state = context.GetState<AudioOutputFilterState>();
            state.Scope = scope;
            foreach (var source in filter.PreserveAudioSources ?? new())
            {
                if (source == null) continue;
                if (!scope.Contains(source.transform))
                    throw new InvalidOperationException("Preserved AudioSources must belong to the Build Exclusions' Scope Root: " + source.name);
                state.Preserved.Add(ObjectRegistry.GetReference(source));
            }
        }

        private static HashSet<Component> SelectedForRemoval(BuildContext context)
        {
            var state = context.GetState<AudioOutputFilterState>();
            var selected = new HashSet<Component>();
            if (state.Scope == null) return selected;
            foreach (var source in context.AvatarRootObject.GetComponentsInChildren<AudioSource>(true))
                if (state.Scope.Contains(source.transform) && BuildPlatformFilters.Includes(source.transform, context) &&
                    !state.Preserved.Contains(ObjectRegistry.GetReference(source))) selected.Add(source);
            foreach (var owner in selected.Select(c => c.gameObject).Distinct().ToArray())
                foreach (var modifier in owner.GetComponents<Component>())
                    if (modifier != null && AudioOutputDependencies.IsModifier(modifier) &&
                        AudioOutputDependencies.Outputs(modifier).All(selected.Contains)) selected.Add(modifier);
            return selected;
        }

        internal static bool HasPendingRemovals(BuildContext context) => SelectedForRemoval(context).Count != 0;

        internal static void Apply(BuildContext context)
        {
            if (!HasPendingRemovals(context)) return;
            using var lease = OptimizationSession.Acquire(context);
            Apply(context, lease.Services.ControllerContext.Controllers.Where(e => !(e.Key is Object owner) || owner != null)
                .ToDictionary(e => e.Key, e => e.Value), lease.Services.ObjectPathRemapper.GetVirtualToRealPathMap());
        }

        internal static void Apply(BuildContext context, IDictionary<object, VirtualAnimatorController> controllers,
            IReadOnlyDictionary<string, string> paths = null)
        {
            var remove = SelectedForRemoval(context);
            if (remove.Count == 0) return;
            var avatar = context.AvatarRootObject;
            var order = BuildFilterPass.RemovalOrder(remove);
            BuildFilterPass.ValidateIncomingReferences(context, null, new HashSet<Object>(remove));
            var nodes = controllers.Values.SelectMany(c => c.AllReachableNodes()).Distinct().ToArray();
            var behaviours = nodes.OfType<VirtualState>().SelectMany(s => s.Behaviours)
                .Concat(nodes.OfType<VirtualStateMachine>().SelectMany(s => s.Behaviours)).Distinct().ToArray();
            var playback = new HashSet<StateMachineBehaviour>();
            foreach (var audio in behaviours.OfType<VRC_AnimatorPlayAudio>())
            {
                var sources = new HashSet<AudioSource>();
                if (audio.Source != null) sources.Add(audio.Source);
                foreach (var target in AnimationUsage.Resolve(avatar.transform, avatar.transform, audio.SourcePath ?? "", paths))
                    sources.UnionWith(target.GetComponents<AudioSource>());
                if (sources.Count > 0 && sources.All(remove.Contains)) playback.Add(audio);
            }
            foreach (var behaviour in behaviours.Where(b => b != null && !playback.Contains(b)))
            {
                using var serialized = new SerializedObject(behaviour);
                foreach (var property in serialized.ObjectProperties())
                    if (property.objectReferenceValue is Component component && remove.Contains(component))
                        throw new InvalidOperationException("Cannot remove audio referenced by retained Animator behaviour " +
                            behaviour.GetType().Name + "." + property.propertyPath + ".");
            }

            // Shared clips can still control retained sources under a different
            // Animator root. Only remove a curve whose complete target set vanishes.
            var index = AvatarAnimationIndex.Create(avatar, controllers, paths);
            var curves = index.Bindings.Select(use => (Use: use, Targets: use.Objects().ToArray()))
                .Where(c => c.Use.Complete && c.Targets.Length > 0 &&
                    c.Targets.All(t => t is Component component && remove.Contains(component))).ToArray();
            foreach (var curve in curves)
                if (curve.Use.Immutable)
                    throw new InvalidOperationException("Cannot rewrite an immutable animation targeting excluded audio: " + curve.Use.Clip.Name);
            var orphaned = new HashSet<AvatarAnimationIndex.BindingUse>(curves.Select(c => c.Use));
            foreach (var use in index.Bindings.Where(u => !orphaned.Contains(u)))
                if ((use.ObjectKeys ?? Array.Empty<ObjectReferenceKeyframe>()).Any(k => k.value is Component c && remove.Contains(c)))
                    throw new InvalidOperationException("An animation refers to excluded audio as an object value: " + use.Clip.Name);

            var report = context.GetState<OptimizationBuildReport>();
            report.Initialize(avatar, context.GetState<OptimizationBuildEnvironment>().Target.ToString());
            int sourcesRemoved = remove.OfType<AudioSource>().Count();
            foreach (var component in order)
            {
                string description = AnimationUtility.CalculateTransformPath(component.transform, avatar.transform) + " / " + component.GetType().Name;
                context.GetState<AudioOutputFilterState>().DestroyObject(component);
                if (component != null) throw new InvalidOperationException("Audio removal was declined: " + description);
                report.Messages.Add("Excluded generated audio: " + description);
            }
            foreach (var group in curves.GroupBy(c => c.Use.Clip))
            {
                float end = MotionAnalysis.EndTime(group.Key);
                foreach (var curve in group)
                {
                    if (curve.Use.Binding.isPPtrCurve) group.Key.SetObjectCurve(curve.Use.Binding, null);
                    else group.Key.SetFloatCurve(curve.Use.Binding, null);
                }
                MotionAnalysis.PreserveDuration(group.Key, end);
            }
            foreach (var state in nodes.OfType<VirtualState>())
                if (state.Behaviours.Any(playback.Contains)) state.Behaviours = state.Behaviours.Where(b => !playback.Contains(b)).ToImmutableList();
            foreach (var machine in nodes.OfType<VirtualStateMachine>())
                if (machine.Behaviours.Any(playback.Contains)) machine.Behaviours = machine.Behaviours.Where(b => !playback.Contains(b)).ToImmutableList();
            report.Messages.Add("Audio filter removed " + sourcesRemoved + " generated sources, " + (remove.Count - sourcesRemoved) +
                " audio modifiers, " + curves.Length + " orphaned animation curves and " + playback.Count + " playback behaviours.");
        }
    }
}
