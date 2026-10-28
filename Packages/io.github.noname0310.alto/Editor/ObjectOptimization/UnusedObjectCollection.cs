using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.Build;
using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.ObjectOptimization
{
    // Design reference: AAO mark/sweep dependency collection. Independently implemented
    // with shared binding analysis and captured scope policies; see CREDITS.md.
    internal static class UnusedObjectCollection
    {
        internal static int Apply(GameObject avatar, IDictionary<object, VirtualAnimatorController> controllers,
            IReadOnlyDictionary<string, string> paths, ObjectOptimizationState settings,
            IEnumerable<string> preservedParameters, ICollection<string> messages)
        {
            var index = AvatarAnimationIndex.Create(avatar, controllers, paths, preservedParameters);
            string unsupported = ObjectOptimizationSafety.UnsupportedRuntime(avatar) ?? index.UnsafeReason;
            if (unsupported != null)
            { messages.Add("Unused object collection skipped: " + unsupported); return 0; }
            var graph = AvatarDependencyGraph.Create(avatar, index, settings, collectObjects: true);
            var components = new HashSet<Component>(graph.Components.Where(c => !(c is Transform) &&
                (settings.RuntimeDependenciesOnly || !(c is INDMFEditorOnly) && !(c is VRC.SDKBase.IEditorOnly)) && !graph.IsLive(c) &&
                settings.Scope.Contains(c.transform) && !settings.Protected(c)));
            var objects = new HashSet<GameObject>(graph.Components.OfType<Transform>().Where(t => t != avatar.transform &&
                !graph.IsLive(t) && settings.Scope.Contains(t) && !settings.Protected(t)).Select(t => t.gameObject));

            // A retained component can require another component on the same object. Graph
            // edges normally prevent this; cycles in removal order conservatively retain both.
            var order = new List<Component>();
            var pending = new HashSet<Component>(components);
            while (pending.Count > 0)
            {
                var next = pending.FirstOrDefault(candidate => !pending.Any(other => other != candidate &&
                    other.gameObject == candidate.gameObject && BuildFilterPass.Requires(other, candidate)));
                if (next == null)
                {
                    foreach (var retained in pending)
                        for (var ancestor = retained.transform; ancestor != null && ancestor != avatar.transform; ancestor = ancestor.parent)
                            objects.Remove(ancestor.gameObject);
                    components.ExceptWith(pending);
                    messages.Add("Unused object collection retained " + pending.Count + " components with cyclic requirements.");
                    break;
                }
                order.Add(next); pending.Remove(next);
            }

            bool Removed(UnityEngine.Object target) => target is GameObject obj ? objects.Contains(obj) :
                target is Component component && (objects.Contains(component.gameObject) || components.Contains(component));
            // Preserved inputs can share a mutable clip with an unobservable
            // component. Capture target identities before deleting their hierarchy,
            // then remove a binding only if every target was actually removed.
            var candidates = index.Bindings.Where(u => u.Complete && !u.Immutable && !u.Binding.isPPtrCurve)
                .Select(u => (Use: u, Targets: u.Objects().ToArray()))
                .Where(c => c.Targets.Length > 0 && c.Targets.All(Removed)).ToArray();
            foreach (var component in order)
            {
                if (component == null) continue;
                // A previously ordered dependent may have survived a deletion veto.
                if (component.GetComponents<Component>().Any(owner => owner != null && owner != component &&
                    BuildFilterPass.Requires(owner, component))) continue;
                string description = AnimationUtility.CalculateTransformPath(component.transform, avatar.transform) + " / " + component.GetType().Name;
                messages.Add(settings.TryDestroy(component) ? "Unused component: " + description :
                    "Retained component after deletion was declined: " + description);
            }
            foreach (var obj in objects.Where(o => o != null &&
                (o.transform.parent == null || !objects.Contains(o.transform.parent.gameObject))).ToArray())
            {
                if (obj == null) continue;
                string path = AnimationUtility.CalculateTransformPath(obj.transform, avatar.transform);
                messages.Add(settings.TryDestroy(obj) ? "Unused object subtree: " + path :
                    "Retained object subtree after deletion was declined: " + path);
            }
            int curves = 0;
            foreach (var group in candidates.Where(c => c.Targets.All(t => t == null)).GroupBy(c => c.Use.Clip))
            {
                var bindings = group.Select(c => c.Use.Binding).ToArray();
                MotionAnalysis.RemoveCurves(group.Key, bindings); curves += bindings.Length;
            }
            int componentCount = components.Count(c => c == null), objectCount = objects.Count(o => o == null);
            messages.Add("Collected " + componentCount + " unused components, " + objectCount + " objects and " + curves +
                " orphaned animation curves using runtime dependency reachability.");
            return componentCount + objectCount + curves;
        }
    }
}
