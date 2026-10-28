using System;
using System.Collections.Generic;
using System.Linq;
using Noname.AvatarTools.Analysis;
using nadena.dev.ndmf.animator;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDK3.Dynamics.Contact.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;
using VRC.SDKBase;

namespace Noname.AvatarTools.ParameterPolicy
{
    internal static class ParameterFreezeSelection
    {
        private static VRCExpressionParameters.Parameter[] Expressions(GameObject avatar) =>
            avatar.GetComponent<VRCAvatarDescriptor>()?.expressionParameters?.parameters ?? Array.Empty<VRCExpressionParameters.Parameter>();

        internal static Dictionary<string, AnimatorControllerParameterType> ExternalTypes(GameObject avatar) =>
            Expressions(avatar).Where(p => p != null && !string.IsNullOrEmpty(p.name)).GroupBy(p => p.name)
                .ToDictionary(g => g.Key, g => g.Select(p => p.valueType switch
                {
                    VRCExpressionParameters.ValueType.Bool => AnimatorControllerParameterType.Bool,
                    VRCExpressionParameters.ValueType.Int => AnimatorControllerParameterType.Int,
                    _ => AnimatorControllerParameterType.Float
                }).Distinct().Single(), StringComparer.Ordinal);

        internal static HashSet<string> MenuInputs(GameObject avatar)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            var visited = new HashSet<VRCExpressionsMenu>();
            var pending = new Queue<VRCExpressionsMenu>();
            pending.Enqueue(avatar.GetComponent<VRCAvatarDescriptor>()?.expressionsMenu);
            while (pending.Count > 0)
            {
                var menu = pending.Dequeue();
                if (menu == null || !visited.Add(menu)) continue;
                foreach (var control in menu.controls ?? new())
                {
                    if (control == null) continue;
                    if (!string.IsNullOrEmpty(control.parameter?.name)) result.Add(control.parameter.name);
                    foreach (var parameter in control.subParameters ?? Array.Empty<VRCExpressionsMenu.Control.Parameter>())
                        if (!string.IsNullOrEmpty(parameter?.name)) result.Add(parameter.name);
                    pending.Enqueue(control.subMenu);
                }
            }
            return result;
        }

        internal static HashSet<string> DirectInputs(GameObject avatar)
        {
            var result = MenuInputs(avatar);
            result.UnionWith(AvatarParameterInputs.Sensors(avatar).Select(s => s.Name));
            return result;
        }

        internal static float DefaultValue(GameObject avatar, VirtualAnimatorController[] controllers, string name)
        {
            var declarations = controllers.Where(c => c.Parameters.ContainsKey(name)).Select(c => c.Parameters[name]).ToArray();
            var expressionTypes = ExternalTypes(avatar);
            var types = declarations.Select(p => p.type).Distinct().ToArray();
            if (types.Length == 0 && expressionTypes.TryGetValue(name, out var expressionType)) types = new[] { expressionType };
            if (types.Length == 0 || types.Contains(AnimatorControllerParameterType.Trigger))
                throw new InvalidOperationException("Cannot determine one typed build default for parameter: " + name + ". Use an explicit value for type conversions.");
            var expressions = Expressions(avatar).Where(p => p != null && p.name == name).ToArray();
            float ExpressionValue(VRCExpressionParameters.Parameter parameter, AnimatorControllerParameterType type)
            {
                float value = parameter.defaultValue;
                if (!MotionAnalysis.Finite(value)) return value;
                if (parameter.valueType == VRCExpressionParameters.ValueType.Bool) value = value != 0 ? 1 : 0;
                if (parameter.valueType == VRCExpressionParameters.ValueType.Int &&
                    ((double)value < int.MinValue || (double)value > int.MaxValue || Math.Truncate(value) != value))
                    throw new InvalidOperationException("Invalid Int expression default: " + name);
                // VRChat's parameter-to-Animator conversion uses nearest-even rounding; Driver
                // Copy uses different conversion rules. Keep Copy's canonical input when lossy.
                // https://creators.vrchat.com/avatars/animator-parameters/#mismatched-parameter-type-conversion
                float converted = type == AnimatorControllerParameterType.Bool ? (value != 0 ? 1 : 0) :
                    type == AnimatorControllerParameterType.Int ? Mathf.Round(value) : value;
                if (converted != value && ParameterSpecialization.Behaviours(controllers.SelectMany(c => c.AllReachableNodes()))
                    .OfType<VRC_AvatarParameterDriver>().SelectMany(d => d.parameters ?? new())
                    .Any(p => p != null && p.type == VRC_AvatarParameterDriver.ChangeType.Copy && p.source == name))
                    throw new InvalidOperationException("A lossy default type conversion would change a Driver Copy source: " + name +
                        ". Preserve this input or choose an explicit constant for all its consumers.");
                return converted;
            }
            var values = expressions.Length > 0
                ? expressions.SelectMany(p => types.Select(t => ExpressionValue(p, t))).Distinct().ToArray()
                : declarations.Select(p => p.type == AnimatorControllerParameterType.Bool ? (p.defaultBool ? 1f : 0f) :
                    p.type == AnimatorControllerParameterType.Int ? p.defaultInt : p.defaultFloat).Distinct().ToArray();
            if (values.Length != 1 || !MotionAnalysis.Finite(values[0]))
                throw new InvalidOperationException("Conflicting or unavailable build defaults for parameter: " + name + ". Choose an explicit constant value.");
            return values[0];
        }

        internal static HashSet<string> AutomaticInputs(GameObject avatar, VirtualAnimatorController[] controllers,
            ISet<string> preserved)
        {
            var direct = DirectInputs(avatar);
            var candidates = new HashSet<string>(direct, StringComparer.Ordinal);
            candidates.UnionWith(Expressions(avatar).Where(p => p != null && !string.IsNullOrEmpty(p.name)).Select(p => p.name));
            candidates.ExceptWith(AnimatorReachability.BuiltIns);
            candidates.ExceptWith(preserved);

            // Expression Parameters remain external inputs even when an Animator also
            // writes them: menus, networking and OSC can change their values. This is
            // an explicit behavior-changing policy, not a proof that they are constant.
            // Resolve expands Preserve through the computations supplying those values.
            // Internal-only calculations are not candidates unless a direct input exposes them.

            var declared = new HashSet<string>(controllers.SelectMany(c => c.Parameters.Keys), StringComparer.Ordinal);
            var consumed = new HashSet<string>(ParameterSpecialization.Reads(controllers.SelectMany(c => c.AllReachableNodes()))
                .Where(n => !string.IsNullOrEmpty(n)), StringComparer.Ordinal);
            foreach (var audio in ParameterSpecialization.Behaviours(controllers.SelectMany(c => c.AllReachableNodes())).OfType<VRC_AnimatorPlayAudio>())
                if (!string.IsNullOrEmpty(audio.ParameterName)) consumed.Add(audio.ParameterName);
            candidates.RemoveWhere(n => !declared.Contains(n) && !consumed.Contains(n));
            return candidates;
        }
    }
}
