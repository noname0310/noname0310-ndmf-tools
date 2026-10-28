using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.ObjectOptimization
{
    // Design reference: AAO 1.9.20 dependency analysis; see CREDITS.md.
    // Bindings write values. Only reachable consumers make those values observable.
    internal static class UnusedTransformOutputs
    {
        internal static int Apply(GameObject avatar, IDictionary<object, VirtualAnimatorController> controllers,
            IReadOnlyDictionary<string, string> paths, ObjectOptimizationState settings,
            IEnumerable<string> preservedParameters, ICollection<string> messages)
        {
            var index = AvatarAnimationIndex.Create(avatar, controllers, paths, preservedParameters);
            if (index.UnsafeReason != null)
            { messages.Add("Unused transform analysis skipped: " + index.UnsafeReason); return 0; }
            var graph = AvatarDependencyGraph.Create(avatar, index, settings, collectObjects: false);
            var constraints = new HashSet<Component>(graph.Constraints.Keys.Where(c => !graph.IsLive(c) &&
                settings.Scope.Contains(c.transform) && !settings.Protected(c) &&
                !c.GetComponents<Component>().Any(owner => owner != null && owner != c && Build.BuildFilterPass.Requires(owner, c))));
            bool Dead(Transform target) => target != null && target != avatar.transform && settings.Scope.Contains(target) &&
                !settings.Protected(target) && !graph.IsLive(target);
            var removed = new Dictionary<VirtualClip, List<EditorCurveBinding>>();
            var componentCurves = new List<(AvatarAnimationIndex.BindingUse Use, UnityEngine.Object[] Targets)>();
            foreach (var use in index.Bindings.Where(u => u.Complete && !u.Immutable && !u.Binding.isPPtrCurve))
            {
                bool dead = use.Binding.type == typeof(Transform) || use.Binding.type == typeof(GameObject) && use.Binding.propertyName == "m_IsActive"
                    ? use.Targets.Length > 0 && use.Targets.All(Dead)
                    : use.Targets.Length > 0 && use.Targets.All(t =>
                    {
                        if (use.Binding.type == null || !typeof(Component).IsAssignableFrom(use.Binding.type)) return false;
                        var targets = t.GetComponents(use.Binding.type);
                        return targets.Length > 0 && targets.All(constraints.Contains);
                    });
                if (!dead) continue;
                if (use.Binding.type != typeof(Transform) && use.Binding.type != typeof(GameObject))
                {
                    componentCurves.Add((use, use.Objects().ToArray()));
                    continue;
                }
                if (!removed.TryGetValue(use.Clip, out var bindings)) removed.Add(use.Clip, bindings = new());
                bindings.Add(use.Binding);
            }
            int constraintCount = 0;
            foreach (var constraint in constraints)
            {
                string description = AnimationUtility.CalculateTransformPath(constraint.transform, avatar.transform) + " / " + constraint.GetType().Name;
                if (settings.TryDestroy(constraint))
                { constraintCount++; messages.Add("Unused constraint: " + description); }
                else messages.Add("Retained constraint after deletion was declined: " + description);
            }
            foreach (var candidate in componentCurves.Where(c => c.Targets.All(t => t == null)))
            {
                if (!removed.TryGetValue(candidate.Use.Clip, out var bindings)) removed.Add(candidate.Use.Clip, bindings = new());
                bindings.Add(candidate.Use.Binding);
            }
            foreach (var pair in removed) MotionAnalysis.RemoveCurves(pair.Key, pair.Value);
            messages.Add("Removed " + constraintCount + " unused constraints and " + removed.Sum(p => p.Value.Count) +
                " unobserved transform/activation/constraint curves; runtime consumers and their dependencies were retained.");
            return constraintCount + removed.Sum(p => p.Value.Count);
        }
    }
}
