using System;
using System.Collections.Generic;
using System.Linq;
using Noname.AvatarTools.Analysis;
using nadena.dev.ndmf.animator;
using UnityEngine;
using UnityEditor;
using VRC.SDKBase;
using System.Collections.Immutable;

namespace Noname.AvatarTools.AnimatorOptimization
{
    internal static class ParameterLiveness
    {
        internal static void RemoveUnusedCurves(GameObject avatar, VirtualAnimatorController[] controllers,
            AnimatorOptimizationReport report, IEnumerable<string> preservedParameters = null,
            Func<VirtualClip, EditorCurveBinding, bool> observableBinding = null, bool removeDeadDriverOperations = false,
            bool followStateOutputs = false, AnimatorReachability analysis = null, IEnumerable<string> dependencyRoots = null,
            IDictionary<object, VirtualAnimatorController> controllerEntries = null)
        {
            var graph = AnimatorParameterDependencies.Collect(avatar, controllers, forExpressions: followStateOutputs,
                observableBinding: observableBinding, analysis: analysis, controllerEntries: controllerEntries);
            var roots = new HashSet<string>(StringComparer.Ordinal);
            if (followStateOutputs) roots.UnionWith(AvatarParameterInputs.ExternalNames(avatar));
            var protectedRoots = dependencyRoots ?? preservedParameters ?? Array.Empty<string>();
            roots.UnionWith(protectedRoots);
            var live = graph.FindLive(roots);
            foreach (var clip in graph.Clips.Where(c => !c.IsMarkerClip))
            {
                var source = MotionAnalysis.Source(clip);
                if (source == null || source.humanMotion || source.events.Length > 0) continue;
                var needed = live.ForClip(clip);
                var dead = clip.GetFloatCurveBindings().Where(b => graph.IsParameter(clip, b) && !needed.Contains(b.propertyName)).ToArray();
                report.ParameterCurves += dead.Length;
                MotionAnalysis.RemoveCurves(clip, dead);
            }
            if (!removeDeadDriverOperations) return;
            var nodes = controllers.SelectMany(c => c.AllReachableNodes()).Distinct().ToArray();
            var drivers = nodes.OfType<VirtualState>().SelectMany(s => s.Behaviours)
                .Concat(nodes.OfType<VirtualStateMachine>().SelectMany(s => s.Behaviours)).OfType<VRC_AvatarParameterDriver>().Distinct();
            var preserved = new HashSet<string>(protectedRoots, StringComparer.Ordinal);
            var operations = new DriverOperationLiveness(avatar, controllers, preserved);
            foreach (var driver in drivers)
            {
                report.DriverOperations += operations.RemoveUnused(driver, live.ForBehaviour(driver));
            }
            bool Empty(StateMachineBehaviour behaviour) => behaviour is VRC_AvatarParameterDriver driver && driver.parameters?.Count == 0;
            foreach (var state in nodes.OfType<VirtualState>())
            {
                var kept = state.Behaviours.Where(b => !Empty(b)).ToImmutableList();
                report.Behaviours += state.Behaviours.Count - kept.Count;
                if (kept.Count != state.Behaviours.Count) state.Behaviours = kept;
            }
            foreach (var machine in nodes.OfType<VirtualStateMachine>())
            {
                var kept = machine.Behaviours.Where(b => !Empty(b)).ToImmutableList();
                report.Behaviours += machine.Behaviours.Count - kept.Count;
                if (kept.Count != machine.Behaviours.Count) machine.Behaviours = kept;
            }
        }
    }
}
