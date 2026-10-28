using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using nadena.dev.ndmf.util;
using Noname.AvatarTools.BlendShapeOptimization;
using Noname.AvatarTools.ParameterPolicy;
using UnityEditor;
using UnityEngine;
using VRC.Dynamics;
using VRC.SDK3.Dynamics.PhysBone.Components;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.Build
{
    internal sealed class CapturedPhysBoneRoot
    {
        internal Transform Root;
        internal bool Children;
        internal readonly HashSet<ObjectReference> OriginalBones = new();
        internal bool Contains(Transform target)
        {
            if (target == null) return false;
            if (Root != null && (target == Root || (Children && target.IsChildOf(Root)))) return true;
            for (var current = target; current != null; current = Children ? current.parent : null)
                if (OriginalBones.Contains(ObjectRegistry.GetReference(current))) return true;
            return false;
        }
    }

    internal sealed class PhysBoneFilterState
    {
        internal OptimizationBuildScope Scope;
        internal PhysBoneFilterMode Mode;
        internal bool RemoveColliders, Applied;
        internal PhysBoneBudget Budget;
        internal readonly HashSet<ObjectReference> PhysBones = new(), Colliders = new();
        internal readonly List<CapturedPhysBoneRoot> Roots = new();
    }

    internal static class PhysBoneFilterPass
    {
        private static readonly string[] ParameterSuffixes = { "_IsGrabbed", "_IsPosed", "_Angle", "_Stretch", "_Squish" };

        internal static void Capture(BuildContext context)
        {
            var all = context.AvatarRootObject.GetComponentsInChildren<ALTOPhysBoneFilter>(true);
            var enabled = all.Where(c => OptimizationBuildScope.Applies(c, context)).ToArray();
            if (enabled.Length > 1) throw new InvalidOperationException("Use one enabled ALTO PhysBone Filter per avatar and platform.");
            var state = context.GetState<PhysBoneFilterState>();
            foreach (var component in enabled)
            {
                state.Scope = OptimizationBuildScope.Capture(component, context.AvatarRootObject);
                state.Mode = component.FilterMode; state.RemoveColliders = component.RemoveUnreferencedColliders;
                state.Budget = component.Budget;
                foreach (var pb in (component.PhysBones ?? new()).Where(pb => pb != null))
                {
                    if (!state.Scope.Contains(pb.transform)) throw new InvalidOperationException("Selected PhysBones must be inside Scope Root: " + pb.name);
                    state.PhysBones.Add(ObjectRegistry.GetReference(pb));
                }
                foreach (var collider in (component.PreserveColliders ?? new()).Where(c => c != null))
                {
                    if (!collider.transform.IsChildOf(context.AvatarRootTransform))
                        throw new InvalidOperationException("Preserved colliders must belong to this avatar.");
                    state.Colliders.Add(ObjectRegistry.GetReference(collider));
                }
                foreach (var selected in component.BoneRoots ?? new())
                {
                    if (selected?.Root == null) continue;
                    if (!selected.Root.IsChildOf(context.AvatarRootTransform))
                        throw new InvalidOperationException("Selected Bone Roots must belong to this avatar.");
                    var captured = new CapturedPhysBoneRoot { Root = selected.Root, Children = selected.IncludeChildren };
                    foreach (var bone in selected.IncludeChildren ? selected.Root.GetComponentsInChildren<Transform>(true) : new[] { selected.Root })
                        captured.OriginalBones.Add(ObjectRegistry.GetReference(bone));
                    state.Roots.Add(captured);
                }
                // Existing PhysBones may be retargeted when an armature merges. Preserve the
                // selection of those components as well as the selected bone hierarchy.
                foreach (var pb in context.AvatarRootObject.GetComponentsInChildren<VRCPhysBone>(true))
                    if (state.Scope.Contains(pb.transform) && state.Roots.Any(r => r.Contains(pb.GetRootTransform())))
                        state.PhysBones.Add(ObjectRegistry.GetReference(pb));
            }
            foreach (var component in all) Object.DestroyImmediate(component);
        }

        internal static void Apply(BuildContext context)
        {
            var state = context.GetState<PhysBoneFilterState>();
            if (state.Scope == null) return;
            var avatar = context.AvatarRootObject;
            var all = avatar.GetComponentsInChildren<VRCPhysBone>(true)
                .Where(pb => BuildPlatformFilters.Includes(pb.transform, context)).ToArray();
            var remove = new HashSet<VRCPhysBone>(all.Where(pb => state.Scope.Contains(pb.transform) &&
                (state.Mode == PhysBoneFilterMode.RemoveListed) ==
                (state.PhysBones.Contains(ObjectRegistry.GetReference(pb)) || state.Roots.Any(r => r.Contains(pb.GetRootTransform())))));
            var report = context.GetState<OptimizationBuildReport>();
            report.Initialize(avatar, context.GetState<OptimizationBuildEnvironment>().Target.ToString());
            var animationLease = Noname.AvatarTools.Build.OptimizationSession.Acquire(context);
            var services = animationLease.Services;
            try
            {
                var controllers = services.ControllerContext.Controllers
                    .Where(e => !(e.Key is Object owner) || owner != null).ToDictionary(e => e.Key, e => e.Value);
                var policy = ParameterPolicyPass.Resolve(context, controllers);
                if (remove.Count > 0 && policy.Uncertain)
                    throw new InvalidOperationException("PhysBone removal requires a verified parameter-preservation policy. See the parameter mapping diagnostics.");
                ValidateAnimatedHierarchy(avatar, controllers, remove, services.ObjectPathRemapper.GetVirtualToRealPathMap());
                var colliderReferences = AnimatedColliderReferences(avatar, controllers,
                    services.ObjectPathRemapper.GetVirtualToRealPathMap());
                ApplyRemoval(context, state, all, remove, policy.Preserved, colliderReferences);
                state.Applied = true;
            }
            finally
            {
                animationLease.Dispose();
            }
        }

        internal static void ApplyRemoval(BuildContext context, PhysBoneFilterState state, VRCPhysBone[] all,
            HashSet<VRCPhysBone> remove, ISet<string> preserved, HashSet<ObjectReference> animatedColliders)
        {
            var report = context.GetState<OptimizationBuildReport>();
            foreach (var pb in remove)
            {
                if (pb.GetRootTransform() == null || !pb.GetRootTransform().IsChildOf(context.AvatarRootTransform))
                    throw new InvalidOperationException("Cannot filter a PhysBone targeting outside this avatar: " + pb.name);
                if (!string.IsNullOrEmpty(pb.parameter) && ParameterSuffixes.Any(s => preserved.Contains(pb.parameter + s)))
                    throw new InvalidOperationException("PhysBone removal conflicts with preserved parameter source '" + pb.parameter + "' on " + pb.name + ".");
            }

            var usedColliders = new HashSet<ObjectReference>(state.Colliders);
            usedColliders.UnionWith(animatedColliders);
            foreach (var pb in all.Where(pb => !remove.Contains(pb)))
                foreach (var collider in pb.colliders ?? new())
                    if (collider != null) usedColliders.Add(ObjectRegistry.GetReference(collider));
            // Unknown component consumers also keep a collider alive. A reference to a selected
            // PhysBone is a conflict, because silently nulling it is not a defined transformation.
            var byObject = remove.GroupBy(pb => pb.gameObject).ToDictionary(g => g.Key, g => g.ToArray());
            foreach (var owner in context.AvatarRootObject.GetComponentsInChildren<Component>(true))
            {
                if (owner == null || owner is Transform || (owner is VRCPhysBone pb && remove.Contains(pb)) ||
                    !BuildPlatformFilters.Includes(owner.transform, context)) continue;
                if (byObject.TryGetValue(owner.gameObject, out var dependencies) && dependencies.Any(pb => BuildFilterPass.Requires(owner, pb)))
                    throw new InvalidOperationException(owner.GetType().Name + " requires a selected PhysBone. Exclude the feature's object instead.");
                using var serialized = new SerializedObject(owner);
                foreach (var property in serialized.ObjectProperties())
                {
                    if (property.objectReferenceValue is VRCPhysBone selected && remove.Contains(selected))
                        throw new InvalidOperationException("Cannot remove a PhysBone referenced by " + owner.name + " (" + owner.GetType().Name +
                            ")." + property.propertyPath + ". Remove or reconfigure the consumer first.");
                    if (property.objectReferenceValue is VRCPhysBoneColliderBase collider)
                        usedColliders.Add(ObjectRegistry.GetReference(collider));
                }
            }
            var collidersToRemove = state.RemoveColliders
                ? context.AvatarRootObject.GetComponentsInChildren<VRCPhysBoneCollider>(true).Where(c =>
                    state.Scope.Contains(c.transform) && BuildPlatformFilters.Includes(c.transform, context) &&
                    c.globalCollision == VRCPhysBoneBase.AdvancedBool.False && !usedColliders.Contains(ObjectRegistry.GetReference(c))).ToArray()
                : Array.Empty<VRCPhysBoneCollider>();
            PreserveNestedBoundaries(all, remove);
            foreach (var pb in remove)
            {
                report.Messages.Add("Excluded PhysBone: " + AnimationUtility.CalculateTransformPath(pb.transform, context.AvatarRootTransform) +
                    " (root: " + AnimationUtility.CalculateTransformPath(pb.GetRootTransform(), context.AvatarRootTransform) + ")");
                Object.DestroyImmediate(pb);
            }
            foreach (var collider in collidersToRemove) Object.DestroyImmediate(collider);
            report.Messages.Add("PhysBone filter removed " + remove.Count + " components and " + collidersToRemove.Length +
                " unreferenced colliders; bone transforms were retained.");
        }

        internal static void PreserveNestedBoundaries(IEnumerable<VRCPhysBone> all, ISet<VRCPhysBone> removed)
        {
            var removedRoots = new HashSet<Transform>(removed.Select(pb => pb.GetRootTransform()));
            foreach (var pb in all.Where(pb => !removed.Contains(pb) && pb.ignoreOtherPhysBones))
            {
                var root = pb.GetRootTransform();
                var ignores = new HashSet<Transform>(pb.ignoreTransforms ?? new());
                foreach (var boundary in removedRoots.Where(t => t != null).OrderBy(t => t.GetComponentsInParent<Transform>(true).Length))
                    if (boundary != root && boundary.IsChildOf(root) &&
                        !ignores.Any(ignored => ignored != null && ignored != root && boundary.IsChildOf(ignored)) && ignores.Add(boundary))
                    {
                        pb.ignoreTransforms ??= new();
                        pb.ignoreTransforms.Add(boundary);
                    }
            }
        }

        internal static void ValidateAnimatedHierarchy(GameObject avatar, IDictionary<object, VirtualAnimatorController> controllers,
            ISet<VRCPhysBone> removed, IReadOnlyDictionary<string, string> paths = null)
        {
            if (removed.Count == 0) return;
            foreach (var entry in controllers)
            {
                var root = entry.Key is Animator animator ? animator.transform : avatar.transform;
                foreach (var clip in entry.Value.AllReachableNodes().OfType<VirtualClip>().Distinct())
                {
                    foreach (var binding in clip.GetObjectCurveBindings())
                        if (clip.GetObjectCurve(binding).Any(k => k.value is VRCPhysBone pb && removed.Contains(pb)))
                            throw new InvalidOperationException("An animation refers to a selected PhysBone as an object value: " + clip.Name);
                    foreach (var binding in clip.GetFloatCurveBindings().Concat(clip.GetObjectCurveBindings()))
                    {
                        if (!typeof(VRCPhysBoneBase).IsAssignableFrom(binding.type) ||
                            !(binding.propertyName == "rootTransform" || binding.propertyName == "ignoreOtherPhysBones" ||
                              binding.propertyName.StartsWith("ignoreTransforms", StringComparison.Ordinal))) continue;
                        foreach (var target in AnimationUsage.Resolve(avatar.transform, root, binding.path, paths))
                            foreach (var pb in target.GetComponents<VRCPhysBone>())
                                if (removed.Contains(pb) || removed.Any(other => other.GetRootTransform().IsChildOf(pb.GetRootTransform())))
                                    throw new InvalidOperationException("PhysBone filtering cannot preserve an animated hierarchy boundary: " +
                                        binding.path + " / " + binding.propertyName);
                    }
                }
            }
        }

        internal static HashSet<ObjectReference> AnimatedColliderReferences(GameObject avatar,
            IDictionary<object, VirtualAnimatorController> controllers, IReadOnlyDictionary<string, string> paths = null)
        {
            var result = new HashSet<ObjectReference>();
            foreach (var entry in controllers)
            {
                var root = entry.Key is Animator animator ? animator.transform : avatar.transform;
                foreach (var clip in entry.Value.AllReachableNodes().OfType<VirtualClip>().Distinct())
                {
                    foreach (var binding in clip.GetObjectCurveBindings())
                        foreach (var key in clip.GetObjectCurve(binding))
                            if (key.value is VRCPhysBoneColliderBase collider) result.Add(ObjectRegistry.GetReference(collider));
                    foreach (var binding in clip.GetFloatCurveBindings())
                        if (typeof(VRCPhysBoneColliderBase).IsAssignableFrom(binding.type) &&
                            (binding.propertyName.StartsWith("globalCollision", StringComparison.Ordinal) || binding.propertyName == "isGlobalCollider"))
                            foreach (var target in AnimationUsage.Resolve(avatar.transform, root, binding.path, paths))
                                foreach (var collider in target.GetComponents<VRCPhysBoneColliderBase>())
                                    result.Add(ObjectRegistry.GetReference(collider));
                }
            }
            return result;
        }

        internal static void CheckBudget(BuildContext context)
        {
            var state = context.GetState<PhysBoneFilterState>();
            if (!state.Applied) return;
            var target = context.GetState<OptimizationBuildEnvironment>().Target;
            var counts = PhysBoneResourceCounts.Capture(context.AvatarRootObject, target == BuildTarget.Android || target == BuildTarget.iOS);
            var report = context.GetState<OptimizationBuildReport>();
            report.Physics = counts;
            report.Messages.Add("Final NDMF PhysBone resources: " + counts + ".");
            var exceeded = counts.Exceeded(state.Budget).ToArray();
            if (exceeded.Length == 0) return;
            string message = "PhysBone budget exceeded: " + string.Join("; ", exceeded);
            if (state.Budget.OnExceeded == BudgetExceededAction.Error) throw new InvalidOperationException(message);
            report.Messages.Add(message);
            Debug.LogWarning("[ALTO] " + message, context.AvatarRootObject);
        }
    }
}
