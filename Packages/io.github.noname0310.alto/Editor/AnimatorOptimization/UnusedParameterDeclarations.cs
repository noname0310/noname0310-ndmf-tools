using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.Expressions;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Noname.AvatarTools.AnimatorOptimization
{
    internal static class UnusedParameterDeclarations
    {
        internal static void Apply(GameObject avatar, VirtualAnimatorController[] controllers,
            IEnumerable<string> preservedParameters, AnimatorOptimizationReport report)
        {
            var external = ExternalParameterAccess.Collect();
            if (external.Uncertain) return;
            var nodes = controllers.SelectMany(c => c.AllReachableNodes()).Distinct().ToArray();
            var clips = nodes.OfType<VirtualClip>().ToArray();
            // Events and opaque motion assets may access Animator parameters by name.
            if (clips.Any(c => MotionAnalysis.Source(c) == null || MotionAnalysis.Source(c).events.Length != 0)) return;
            var names = new HashSet<string>(controllers.SelectMany(c => c.Parameters.Keys), StringComparer.Ordinal);
            var used = new HashSet<string>(AvatarParameterInputs.ExternalNames(avatar), StringComparer.Ordinal);
            used.UnionWith(AnimatorReachability.BuiltIns);
            used.UnionWith(preservedParameters ?? Array.Empty<string>());
            used.UnionWith(names.Where(n => external.IsRead(n) || external.IsWritten(n)));
            // SDK proxy motions are opaque, but do not make unrelated controllers opaque.
            // Keep their controller's entire interface (also in other controllers sharing
            // those names), in addition to the built-in and explicit binding roots below.
            foreach (var controller in controllers.Where(c => c.AllReachableNodes().OfType<VirtualClip>().Any(m => m.IsMarkerClip)))
                used.UnionWith(controller.Parameters.Keys);
            used.UnionWith(ExpressionMenuOptimizer.Menus(avatar.GetComponent<VRCAvatarDescriptor>()?.expressionsMenu)
                .SelectMany(m => m.controls ?? new()).SelectMany(ExpressionMenuOptimizer.Parameters));
            foreach (var transition in nodes.OfType<VirtualTransitionBase>())
                used.UnionWith(transition.Conditions.Select(c => c.parameter));
            foreach (var state in nodes.OfType<VirtualState>())
                used.UnionWith(new[] { state.TimeParameter, state.SpeedParameter, state.CycleOffsetParameter, state.MirrorParameter });
            foreach (var tree in nodes.OfType<VirtualBlendTree>())
            {
                if (tree.BlendType == UnityEditor.Animations.BlendTreeType.Direct)
                    used.UnionWith(tree.Children.Select(c => c.DirectBlendParameter));
                else
                {
                    used.Add(tree.BlendParameter);
                    if (tree.BlendType != UnityEditor.Animations.BlendTreeType.Simple1D) used.Add(tree.BlendParameterY);
                }
            }
            foreach (var clip in clips)
                used.UnionWith(clip.GetFloatCurveBindings().Concat(clip.GetObjectCurveBindings())
                    .Where(b => b.type == typeof(Animator)).Select(b => b.propertyName));
            foreach (var behaviour in nodes.OfType<VirtualState>().SelectMany(s => s.Behaviours)
                .Concat(nodes.OfType<VirtualStateMachine>().SelectMany(s => s.Behaviours)).Where(b => b != null).Distinct())
            {
                using var serialized = new SerializedObject(behaviour);
                var property = serialized.GetIterator();
                while (property.Next(true))
                    if (property.propertyType == SerializedPropertyType.String && names.Contains(property.stringValue))
                        used.Add(property.stringValue);
            }
            // Retain a name in every controller if any controller uses it: a declaration's
            // default value can initialize a parameter shared across playable layers.
            foreach (var controller in controllers)
                foreach (string name in controller.Parameters.Keys.Where(n => !used.Contains(n)).ToArray())
                {
                    controller.Parameters = controller.Parameters.Remove(name);
                    report.ParameterDeclarations++;
                }
        }
    }
}
