using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.AnimatorOptimization
{
    internal static class UnusedStateLogic
    {
        internal static void Apply(VirtualAnimatorController[] controllers,
            AnimatorOptimizationReport report, Func<VirtualClip, EditorCurveBinding, bool> observableBinding)
        {
            var defaults = new DefaultWriteAnalysis();
            foreach (var layer in controllers.SelectMany(c => c.Layers))
            {
                if (layer.SyncedLayerIndex != -1 || layer.StateMachine == null) continue;
                var machine = layer.StateMachine;
                var nodes = layer.AllReachableNodes().ToArray();
                var states = nodes.OfType<VirtualState>().ToArray();
                var clips = nodes.OfType<VirtualClip>().ToArray();
                var initial = machine.DefaultState;
                // Retain the existing default state and layer index. SDK layer controls can
                // address these layers even when their state logic has no output.
                if (initial == null || !machine.States.Any(s => s.State == initial) || states.Length == 0 ||
                    states.Any(s => s.Behaviours.Count != 0) || nodes.OfType<VirtualStateMachine>().Any(s => s.Behaviours.Count != 0)) continue;
                if (clips.Any(c => c.IsMarkerClip || c.Legacy || c.AdditiveReferencePoseClip != null ||
                    MotionAnalysis.Source(c) == null || MotionAnalysis.Source(c).humanMotion || MotionAnalysis.Source(c).events.Length != 0 ||
                    c.GetFloatCurveBindings().Concat(c.GetObjectCurveBindings()).Any(b => observableBinding == null || observableBinding(c, b)))) continue;
                // Preserving an input preserves its value and observable consumers, not an
                // otherwise effect-free state graph. In particular, a shared empty clip must
                // not connect the control dependencies of unrelated layers.
                // Keep the state's implicit defaults while removing unobservable control flow.
                bool allOff = states.All(s => !s.WriteDefaultValues);
                bool uniformDefaults = defaults.Uniform(states);
                if (!allOff && !uniformDefaults) continue;
                // This layer has no effects, behaviours or preserved inputs. Its duration is
                // no longer observable once its transitions are removed. Reuse an ordinary
                // leaf to retain WD semantics without evaluating a dead Blend Tree.
                if (initial.Motion is VirtualBlendTree && (allOff || uniformDefaults))
                {
                    var leaf = MotionAnalysis.Clips(initial.Motion).FirstOrDefault();
                    if (leaf != null)
                    {
                        report.Trees += initial.Motion.AllReachableNodes().OfType<VirtualBlendTree>().Count();
                        initial.Motion = leaf;
                    }
                }
                int transitions = nodes.OfType<VirtualTransitionBase>().Count();
                if (states.Length == 1 && transitions == 0 && machine.StateMachines.Count == 0 &&
                    initial.TimeParameter == null && initial.SpeedParameter == null &&
                    initial.CycleOffsetParameter == null && initial.MirrorParameter == null) continue;
                report.States += states.Length - 1;
                report.Transitions += transitions;
                report.StateMachines++;
                initial.Transitions = initial.Transitions.Clear();
                initial.TimeParameter = initial.SpeedParameter = initial.CycleOffsetParameter = initial.MirrorParameter = null;
                machine.States = machine.States.Where(s => s.State == initial).ToImmutableList();
                machine.StateMachines = machine.StateMachines.Clear();
                machine.AnyStateTransitions = machine.AnyStateTransitions.Clear();
                machine.EntryTransitions = machine.EntryTransitions.Clear();
                machine.StateMachineTransitions = machine.StateMachineTransitions.Clear();
            }
        }
    }
}
