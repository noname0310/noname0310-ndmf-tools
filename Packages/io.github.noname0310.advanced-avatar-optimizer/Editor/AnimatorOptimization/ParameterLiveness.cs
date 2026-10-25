using System;
using System.Collections.Generic;
using System.Linq;
using Noname.AvatarTools.Analysis;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.Contact.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;
using VRC.SDKBase;

namespace Noname.AvatarTools.AnimatorOptimization
{
    // Backward liveness from observable outputs. Edges point from a computed parameter to the
    // parameters required to calculate it. A dead strongly connected component has no live root.
    internal static class ParameterLiveness
    {
        internal static void RemoveUnusedCurves(GameObject avatar, VirtualAnimatorController[] controllers, AnimatorOptimizationReport report)
        {
            var names = new HashSet<string>(controllers.SelectMany(c => c.Parameters.Keys), StringComparer.Ordinal);
            var declarations = new Dictionary<VirtualClip, HashSet<string>>();
            foreach (var controller in controllers)
            {
                var declared = controller.Parameters.Values.Where(p => p.type == AnimatorControllerParameterType.Float).Select(p => p.name).ToArray();
                foreach (var clip in controller.AllReachableNodes().OfType<VirtualClip>())
                    if (declarations.TryGetValue(clip, out var existing)) existing.IntersectWith(declared);
                    else declarations.Add(clip, new HashSet<string>(declared, StringComparer.Ordinal));
            }
            var live = new HashSet<string>(AnimatorReachability.BuiltIns, StringComparer.Ordinal);
            var dependencies = names.ToDictionary(n => n, _ => new HashSet<string>(StringComparer.Ordinal));
            var descriptor = avatar.GetComponent<VRCAvatarDescriptor>();
            if (descriptor != null && descriptor.expressionParameters != null)
                live.UnionWith(descriptor.expressionParameters.parameters.Where(p => p != null).Select(p => p.name));
            foreach (var receiver in avatar.GetComponentsInChildren<VRCContactReceiver>(true)) live.Add(receiver.parameter);
            foreach (var bone in avatar.GetComponentsInChildren<VRCPhysBone>(true))
                if (!string.IsNullOrEmpty(bone.parameter))
                    foreach (string suffix in new[] { "_IsGrabbed", "_IsPosed", "_Angle", "_Stretch", "_Squish" }) live.Add(bone.parameter + suffix);

            bool Parameter(VirtualClip clip, EditorCurveBinding binding) => binding.type == typeof(Animator) && binding.path == "" &&
                !binding.isDiscreteCurve && declarations[clip].Contains(binding.propertyName);
            void Output(VirtualClip clip, EditorCurveBinding binding, IEnumerable<string> inputs)
            {
                if (Parameter(clip, binding)) dependencies[binding.propertyName].UnionWith(inputs);
                else live.UnionWith(inputs);
            }
            void Visit(VirtualMotion motion, HashSet<string> inputs, HashSet<VirtualMotion> stack)
            {
                if (motion == null || !stack.Add(motion)) return;
                if (motion is VirtualClip clip)
                {
                    foreach (var binding in clip.GetFloatCurveBindings()) Output(clip, binding, inputs);
                    var source = MotionAnalysis.Source(clip);
                    if (clip.GetObjectCurveBindings().Any() || clip.IsMarkerClip || source == null ||
                        source.humanMotion || source.events.Length > 0) live.UnionWith(inputs);
                    if (clip.IsMarkerClip) live.UnionWith(clip.GetFloatCurveBindings().Where(b => Parameter(clip, b)).Select(b => b.propertyName));
                }
                else if (motion is VirtualBlendTree tree)
                {
                    var shared = new HashSet<string>(inputs);
                    if (tree.BlendType != BlendTreeType.Direct) { shared.Add(tree.BlendParameter); shared.Add(tree.BlendParameterY); }
                    else if (tree.NormalizedBlendValues) shared.UnionWith(tree.Children.Select(c => c.DirectBlendParameter));
                    // Tree weights also influence effective clip duration, and therefore exit times
                    // and normalized transition durations, even when the sampled values are unused.
                    if (MotionAnalysis.Clips(tree).Any(c => MotionAnalysis.EndTime(c) != 0 || c.Settings.stopTime != 0))
                    {
                        if (tree.BlendType == BlendTreeType.Direct) live.UnionWith(tree.Children.Select(c => c.DirectBlendParameter));
                        else { live.Add(tree.BlendParameter); live.Add(tree.BlendParameterY); }
                    }
                    foreach (var child in tree.Children)
                    {
                        var childInputs = new HashSet<string>(shared);
                        if (tree.BlendType == BlendTreeType.Direct) childInputs.Add(child.DirectBlendParameter);
                        Visit(child.Motion, childInputs, stack);
                    }
                }
                stack.Remove(motion);
            }
            var nodes = controllers.SelectMany(c => c.AllReachableNodes()).Distinct().ToArray();
            foreach (var transition in nodes.OfType<VirtualTransitionBase>())
                live.UnionWith(transition.Conditions.Select(c => c.parameter));
            foreach (var state in nodes.OfType<VirtualState>())
            {
                // State timing can control transitions and behaviour execution without any pose output.
                live.UnionWith(new[] { state.TimeParameter, state.SpeedParameter, state.CycleOffsetParameter, state.MirrorParameter });
                Visit(state.Motion, new HashSet<string> { state.TimeParameter, state.SpeedParameter, state.CycleOffsetParameter, state.MirrorParameter }, new());
            }
            var behaviours = nodes.OfType<VirtualState>().SelectMany(s => s.Behaviours)
                .Concat(nodes.OfType<VirtualStateMachine>().SelectMany(s => s.Behaviours)).Distinct();
            foreach (var behaviour in behaviours)
            {
                if (behaviour is VRC_AvatarParameterDriver driver)
                {
                    foreach (var operation in driver.parameters ?? new())
                        if (operation != null && operation.name != null && dependencies.TryGetValue(operation.name, out var inputs))
                        {
                            if (operation.type == VRC_AvatarParameterDriver.ChangeType.Copy) inputs.Add(operation.source);
                            if (operation.type == VRC_AvatarParameterDriver.ChangeType.Add) inputs.Add(operation.name);
                        }
                }
                else if (behaviour != null)
                {
                    // Preserve parameter-looking strings in platform behaviours, including audio controls.
                    using var serialized = new SerializedObject(behaviour);
                    var property = serialized.GetIterator();
                    while (property.Next(true))
                        if (property.propertyType == SerializedPropertyType.String && names.Contains(property.stringValue)) live.Add(property.stringValue);
                }
            }
            var pending = new Queue<string>(live.Where(n => n != null));
            while (pending.Count > 0)
                if (dependencies.TryGetValue(pending.Dequeue(), out var inputs))
                    foreach (string input in inputs)
                        if (input != null && live.Add(input)) pending.Enqueue(input);
            foreach (var clip in nodes.OfType<VirtualClip>().Where(c => !c.IsMarkerClip))
            {
                var source = MotionAnalysis.Source(clip);
                if (source == null || source.humanMotion || source.events.Length > 0) continue;
                var dead = clip.GetFloatCurveBindings().Where(b => Parameter(clip, b) && !live.Contains(b.propertyName)).ToArray();
                report.ParameterCurves += dead.Length;
                MotionAnalysis.RemoveCurves(clip, dead);
            }
        }
    }
}
