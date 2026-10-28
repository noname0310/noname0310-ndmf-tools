using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using nadena.dev.ndmf.util;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.ParameterPolicy;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Dynamics.Contact.Components;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.Build
{
    internal sealed class CapturedContactObjectRoot
    {
        internal Transform Root;
        internal bool Children;
        internal readonly HashSet<ObjectReference> Originals = new();
        internal bool Contains(Transform target)
        {
            if (target == null) return false;
            if (Root != null && (target == Root || Children && target.IsChildOf(Root))) return true;
            for (var current = target; current != null; current = Children ? current.parent : null)
                if (Originals.Contains(ObjectRegistry.GetReference(current))) return true;
            return false;
        }
    }

    internal sealed class ContactFilterState
    {
        internal OptimizationBuildScope Scope;
        internal ContactFilterMode Mode;
        internal readonly HashSet<ObjectReference> Contacts = new();
        internal readonly List<CapturedContactObjectRoot> Roots = new();
        internal Action<Object> DestroyObject = target => Object.DestroyImmediate(target);
    }

    internal static class ContactFilterPass
    {
        private static IEnumerable<Component> Contacts(GameObject avatar) =>
            avatar.GetComponentsInChildren<VRCContactSender>(true).Cast<Component>()
                .Concat(avatar.GetComponentsInChildren<VRCContactReceiver>(true));

        internal static void Capture(BuildContext context)
        {
            var all = context.AvatarRootObject.GetComponentsInChildren<ALTOContactFilter>(true);
            var enabled = all.Where(c => OptimizationBuildScope.Applies(c, context)).ToArray();
            if (enabled.Length > 1) throw new InvalidOperationException("Use one enabled ALTO Contact Filter per avatar and platform.");
            var state = context.GetState<ContactFilterState>();
            foreach (var component in enabled)
            {
                if (!Enum.IsDefined(typeof(ContactFilterMode), component.FilterMode))
                    throw new InvalidOperationException("Choose a valid Contact Filter mode.");
                state.Scope = OptimizationBuildScope.Capture(component, context.AvatarRootObject);
                state.Mode = component.FilterMode;
                foreach (var contact in (component.Senders ?? new()).Cast<Component>().Concat(component.Receivers ?? new()).Where(c => c != null))
                {
                    if (!state.Scope.Contains(contact.transform))
                        throw new InvalidOperationException("Selected Contacts must be inside Scope Root: " + contact.name);
                    state.Contacts.Add(ObjectRegistry.GetReference(contact));
                }
                foreach (var selection in component.ObjectRoots ?? new())
                {
                    if (selection?.Root == null) continue;
                    if (!state.Scope.Contains(selection.Root))
                        throw new InvalidOperationException("Selected Contact Object Roots must be inside Scope Root: " + selection.Root.name);
                    var captured = new CapturedContactObjectRoot { Root = selection.Root, Children = selection.IncludeChildren };
                    foreach (var transform in selection.IncludeChildren ? selection.Root.GetComponentsInChildren<Transform>(true) : new[] { selection.Root })
                        captured.Originals.Add(ObjectRegistry.GetReference(transform));
                    state.Roots.Add(captured);
                }
                foreach (var contact in Contacts(context.AvatarRootObject))
                    if (state.Scope.Contains(contact.transform) && state.Roots.Any(r => r.Contains(contact.transform)))
                        state.Contacts.Add(ObjectRegistry.GetReference(contact));
            }
            foreach (var component in all) Object.DestroyImmediate(component);
        }

        private static HashSet<Component> SelectedForRemoval(BuildContext context)
        {
            var state = context.GetState<ContactFilterState>();
            if (state.Scope == null) return new();
            return new(Contacts(context.AvatarRootObject).Where(c => state.Scope.Contains(c.transform) &&
                BuildPlatformFilters.Includes(c.transform, context) &&
                (state.Mode == ContactFilterMode.RemoveListed) ==
                (state.Contacts.Contains(ObjectRegistry.GetReference(c)) || state.Roots.Any(r => r.Contains(c.transform)))));
        }

        internal static bool HasPendingRemovals(BuildContext context) => SelectedForRemoval(context).Count > 0;

        internal static void Apply(BuildContext context)
        {
            if (!HasPendingRemovals(context)) return;
            using var lease = OptimizationSession.Acquire(context);
            var services = lease.Services;
            Apply(context, services.ControllerContext.Controllers.Where(e => !(e.Key is Object owner) || owner != null)
                .ToDictionary(e => e.Key, e => e.Value), services.ObjectPathRemapper.GetVirtualToRealPathMap());
        }

        internal static void Apply(BuildContext context, IDictionary<object, VirtualAnimatorController> controllers,
            IReadOnlyDictionary<string, string> paths = null)
        {
            var remove = SelectedForRemoval(context);
            if (remove.Count == 0) return;
            var policy = ParameterPolicyPass.Resolve(context, controllers);
            if (policy.Uncertain)
                throw new InvalidOperationException("Contact removal requires a verified parameter-preservation policy. See the parameter mapping diagnostics.");
            ApplyRemoval(context, remove, policy.Preserved, controllers, paths);
        }

        internal static void ApplyRemoval(BuildContext context, HashSet<Component> remove, ISet<string> preserved,
            IDictionary<object, VirtualAnimatorController> controllers, IReadOnlyDictionary<string, string> paths = null)
        {
            var avatar = context.AvatarRootObject;
            var state = context.GetState<ContactFilterState>();
            foreach (var contact in remove)
            {
                if (!(contact is VRCContactSender || contact is VRCContactReceiver) || !state.Scope.Contains(contact.transform))
                    throw new InvalidOperationException("Contact removal requires scoped SDK Contact components.");
                if (contact is VRCContactReceiver receiver && !string.IsNullOrEmpty(receiver.parameter) && preserved.Contains(receiver.parameter))
                    throw new InvalidOperationException("Contact removal conflicts with preserved parameter '" + receiver.parameter + "' on " + receiver.name + ".");
            }
            foreach (var owner in avatar.GetComponentsInChildren<Component>(true))
            {
                if (owner == null || owner is Transform || remove.Contains(owner) || !BuildPlatformFilters.Includes(owner.transform, context)) continue;
                if (owner.GetComponents<Component>().Any(c => remove.Contains(c) && BuildFilterPass.Requires(owner, c)))
                    throw new InvalidOperationException(owner.GetType().Name + " requires a selected Contact. Exclude or reconfigure the consumer first.");
                using var serialized = new SerializedObject(owner);
                foreach (var property in serialized.ObjectProperties())
                    if (property.objectReferenceValue is Component selected && remove.Contains(selected))
                        throw new InvalidOperationException("Cannot remove a Contact referenced by " + owner.name + " (" + owner.GetType().Name +
                            ")." + property.propertyPath + ". Remove or reconfigure the consumer first.");
            }

            // A shared clip may also drive a retained Contact under another Animator root.
            // Capture all targets before mutation, then remove only exclusively orphaned bindings.
            var index = AvatarAnimationIndex.Create(avatar, controllers, paths);
            var candidates = index.Bindings.Select(use => (Use: use, Targets: use.Objects().ToArray()))
                .Where(c => c.Use.Complete && c.Targets.Length > 0 && c.Targets.All(t => t is Component component && remove.Contains(component))).ToArray();
            foreach (var candidate in candidates)
                if (candidate.Use.Immutable)
                    throw new InvalidOperationException("Cannot rewrite an immutable animation targeting a selected Contact: " + candidate.Use.Clip.Name);
            var orphaned = new HashSet<AvatarAnimationIndex.BindingUse>(candidates.Select(c => c.Use));
            foreach (var use in index.Bindings.Where(u => !orphaned.Contains(u)))
                if ((use.ObjectKeys ?? Array.Empty<ObjectReferenceKeyframe>()).Any(k => k.value is Component c && remove.Contains(c)))
                    throw new InvalidOperationException("An animation refers to a selected Contact as an object value: " + use.Clip.Name);

            var report = context.GetState<OptimizationBuildReport>();
            report.Initialize(avatar, context.GetState<OptimizationBuildEnvironment>().Target.ToString());
            int senders = remove.OfType<VRCContactSender>().Count(), receivers = remove.OfType<VRCContactReceiver>().Count();
            foreach (var contact in remove)
            {
                string description = AnimationUtility.CalculateTransformPath(contact.transform, context.AvatarRootTransform) + " / " + contact.GetType().Name;
                state.DestroyObject(contact);
                if (contact != null) throw new InvalidOperationException("Contact removal was declined: " + description);
                report.Messages.Add("Excluded Contact: " + description);
            }
            int curves = 0;
            foreach (var group in candidates.Where(c => c.Targets.All(t => t == null)).GroupBy(c => c.Use.Clip))
            {
                float end = MotionAnalysis.EndTime(group.Key);
                foreach (var candidate in group)
                {
                    if (candidate.Use.Binding.isPPtrCurve) group.Key.SetObjectCurve(candidate.Use.Binding, null);
                    else group.Key.SetFloatCurve(candidate.Use.Binding, null);
                    curves++;
                }
                MotionAnalysis.PreserveDuration(group.Key, end);
            }
            report.Messages.Add("Contact filter removed " + senders + " Senders, " + receivers + " Receivers and " + curves +
                " orphaned animation curves; transforms and unrelated parameter declarations were retained for dependency cleanup.");
        }
    }
}
