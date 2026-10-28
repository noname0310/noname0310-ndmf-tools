using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using UnityEditor;
using UnityEditor.Animations;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase;

namespace Noname.AvatarTools.AnimatorOptimization
{
    // Design references: AAO 1.9.20 RemoveMeaninglessLayer and NDMF 1.14.8
    // GlobalTransformations.RemoveEmptyLayers. This independently implemented proof also
    // handles ordinary empty states. It runs only after all SDK generators have finished.
    internal static class UnusedLayers
    {
        internal static void Apply(VirtualAnimatorController[] controllers, AnimatorOptimizationReport report,
            Func<VirtualClip, EditorCurveBinding, bool> observable,
            IDictionary<object, VirtualAnimatorController> entries)
        {
            if (observable == null) return;
            var ownersToInspect = entries?.Values.Distinct().ToArray() ?? controllers;
            var byController = ownersToInspect.ToDictionary(c => c, c => c.AllReachableNodes().OfType<VirtualState>().SelectMany(s => s.Behaviours)
                .Concat(c.AllReachableNodes().OfType<VirtualStateMachine>().SelectMany(s => s.Behaviours))
                .OfType<VRC_AnimatorLayerControl>().Distinct().ToArray());
            var controls = byController.Values.SelectMany(c => c).ToArray();
            if (controls.Length != 0 && entries == null) return;
            foreach (var controller in controllers)
            {
                var layers = controller.Layers.ToArray();
                var remove = new HashSet<VirtualLayer>();
                var keep = new HashSet<int>(layers.Where(l => l.SyncedLayerIndex >= 0).Select(l => l.SyncedLayerIndex));
                int prefix = 0; // Unity gives the first layer special weight semantics.
                if (controls.Length != 0)
                {
                    var owners = entries.Where(e => e.Value == controller).Select(e => e.Key).ToArray();
                    if (owners.Length != 1 || owners[0] is not VRCAvatarDescriptor.AnimLayerType type || type == VRCAvatarDescriptor.AnimLayerType.Additive) continue;
                    foreach (var source in entries)
                    foreach (var control in byController[source.Value])
                    {
                        if (control.playable.ToString() != type.ToString()) continue;
                        if (source.Key is VRCAvatarDescriptor.AnimLayerType owner && owner == type)
                            keep.Add(control.layer); // NDMF virtualizes references within this playable.
                        else
                            // Cross-playable controls retain physical indices in NDMF. Keep the
                            // entire prefix so a different playable's index cannot shift.
                            prefix = Math.Max(prefix, control.layer);
                    }
                }
                for (int i = 1; i < layers.Length; i++)
                {
                    var layer = layers[i];
                    if (i <= prefix || keep.Contains(layer.VirtualLayerIndex) || layer.SyncedLayerIndex >= 0 || layer.AvatarMask != null ||
                        layer.BlendingMode != AnimatorLayerBlendingMode.Override || layer.StateMachine == null) continue;
                    var nodes = layer.AllReachableNodes().ToArray();
                    var states = nodes.OfType<VirtualState>().ToArray();
                    if (states.Length != 1 || states[0] != layer.StateMachine.DefaultState ||
                        nodes.OfType<VirtualTransitionBase>().Any() || nodes.OfType<VirtualStateMachine>().Count() != 1 ||
                        !states[0].WriteDefaultValues || states[0].Behaviours.Count != 0 || layer.StateMachine.Behaviours.Count != 0 ||
                        states[0].Motion is not VirtualClip clip) continue;
                    var sourceClip = MotionAnalysis.Source(clip);
                    if (clip.IsMarkerClip || clip.Legacy || clip.AdditiveReferencePoseClip != null || sourceClip == null ||
                        sourceClip.humanMotion || sourceClip.events.Length != 0 ||
                        clip.GetFloatCurveBindings().Concat(clip.GetObjectCurveBindings()).Any(b => observable(clip, b))) continue;
                    // Empty additive motions and WD-Off layers can retain/contribute a
                    // previous pose. Only ordinary WD-On overrides satisfy this proof.
                    remove.Add(layer);
                }
                // NDMF's bulk API also removes layers cloned without a priority lookup.
                // Count completed mutations so dependency convergence cannot retry forever.
                if (remove.Count == 0) continue;
                controller.RemoveLayers(remove.Contains);
                report.Layers += layers.Length - controller.Layers.Count();
            }
        }
    }
}
