using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Noname.AvatarTools.Analysis;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDKBase;

namespace Noname.AvatarTools.ParameterPolicy
{
    internal sealed class ParameterConstant
    {
        internal string Name, InternalName;
        internal float Value;
        internal AnimatorControllerParameterType Type;
        internal HashSet<AnimatorControllerParameterType> Types = new();
        internal string TypeLabel => string.Join("/", Types.OrderBy(t => (int)t).Select(t => t.ToString()));

        internal AnimatorControllerParameter Declaration(AnimatorControllerParameterType? declaredType = null)
        {
            var type = declaredType ?? Type;
            return new AnimatorControllerParameter
            {
                name = InternalName, type = type, defaultFloat = Value,
                defaultInt = type == AnimatorControllerParameterType.Int ? (int)Value : 0,
                defaultBool = Value != 0
            };
        }

        internal bool Matches(AnimatorControllerParameter parameter) => Types.Contains(parameter.type) && (parameter.type switch
        {
            AnimatorControllerParameterType.Float => parameter.defaultFloat == Value,
            AnimatorControllerParameterType.Int => parameter.defaultInt == Value,
            AnimatorControllerParameterType.Bool => parameter.defaultBool == (Value != 0),
            _ => false
        });
    }

    internal sealed class ParameterSpecializationReport
    {
        internal int Reads, DriverWrites, ParameterCurves, Declarations;
        internal int Changes => Reads + DriverWrites + ParameterCurves + Declarations;
        public override string ToString() => Reads + " reads specialized, " + DriverWrites +
            " Driver writes and " + ParameterCurves + " parameter curves removed; " + Declarations + " constants declared";
    }

    // Runtime specialization, not an assumption supplied only to the analyzer. External writers
    // retain their public names. All supported readers use controller-local constants without writers.
    internal static class ParameterSpecialization
    {
        internal static Dictionary<string, ParameterConstant> Prepare(VirtualAnimatorController[] controllers,
            IReadOnlyDictionary<string, float> selected, ISet<string> preserved, IEnumerable<string> reservedNames = null,
            IReadOnlyDictionary<string, AnimatorControllerParameterType> externalTypes = null)
        {
            var result = new Dictionary<string, ParameterConstant>(StringComparer.Ordinal);
            var reserved = new HashSet<string>(controllers.SelectMany(c => c.Parameters.Keys), StringComparer.Ordinal);
            reserved.UnionWith(reservedNames ?? Array.Empty<string>());
            int index = 0;
            foreach (var pair in selected.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (AnimatorReachability.BuiltIns.Contains(pair.Key) || preserved.Contains(pair.Key))
                    throw new InvalidOperationException("Cannot freeze a built-in or preserved parameter/dependency: " + pair.Key);
                var declarations = controllers.Where(c => c.Parameters.ContainsKey(pair.Key)).Select(c => c.Parameters[pair.Key]).ToArray();
                var types = declarations.Select(p => p.type).Distinct().ToArray();
                if (types.Length == 0 && externalTypes != null && externalTypes.TryGetValue(pair.Key, out var externalType)) types = new[] { externalType };
                if (types.Length == 0)
                    throw new InvalidOperationException("Frozen parameter is absent after avatar generators ran: " + pair.Key);
                var type = types[0];
                if (!MotionAnalysis.Finite(pair.Value) || types.Any(t => (t != AnimatorControllerParameterType.Float &&
                    t != AnimatorControllerParameterType.Int && t != AnimatorControllerParameterType.Bool) ||
                    (t == AnimatorControllerParameterType.Bool && pair.Value != 0 && pair.Value != 1) ||
                    (t == AnimatorControllerParameterType.Int &&
                        (pair.Value < int.MinValue || (double)pair.Value > int.MaxValue || Math.Truncate(pair.Value) != pair.Value))))
                    throw new InvalidOperationException("Invalid constant for " + type + " parameter '" + pair.Key + "'. Bool requires 0 or 1; Int requires an in-range whole number; Trigger is unsupported.");
                string internalName;
                do { internalName = "__AAO_Constant_" + index++; } while (!reserved.Add(internalName));
                result.Add(pair.Key, new ParameterConstant { Name = pair.Key, InternalName = internalName, Type = type,
                    Types = new HashSet<AnimatorControllerParameterType>(types), Value = pair.Value });
            }
            return result;
        }

        internal static IEnumerable<StateMachineBehaviour> Behaviours(IEnumerable<VirtualNode> nodes) =>
            nodes.OfType<VirtualState>().SelectMany(s => s.Behaviours)
                .Concat(nodes.OfType<VirtualStateMachine>().SelectMany(s => s.Behaviours)).Distinct();

        internal static bool SupportsBehaviour(StateMachineBehaviour behaviour) =>
            behaviour == null || behaviour is VRC_AvatarParameterDriver || behaviour is VRC_AnimatorLayerControl ||
            behaviour is VRC_PlayableLayerControl || behaviour is VRC_AnimatorTrackingControl ||
            behaviour is VRC_AnimatorLocomotionControl || behaviour is VRC_AnimatorTemporaryPoseSpace ||
            behaviour is VRC_AnimatorPlayAudio;

        internal static IEnumerable<string> Reads(IEnumerable<VirtualNode> source)
        {
            var nodes = source.ToArray();
            foreach (var transition in nodes.OfType<VirtualTransitionBase>())
                foreach (var condition in transition.Conditions) yield return condition.parameter;
            foreach (var state in nodes.OfType<VirtualState>())
            {
                yield return state.SpeedParameter; yield return state.TimeParameter;
                yield return state.CycleOffsetParameter; yield return state.MirrorParameter;
            }
            foreach (var tree in nodes.OfType<VirtualBlendTree>())
                if (tree.BlendType == BlendTreeType.Direct)
                { foreach (var child in tree.Children) yield return child.DirectBlendParameter; }
                else
                {
                    yield return tree.BlendParameter;
                    if (tree.BlendType != BlendTreeType.Simple1D) yield return tree.BlendParameterY;
                }
            foreach (var driver in Behaviours(nodes).OfType<VRC_AvatarParameterDriver>())
                foreach (var operation in driver.parameters ?? new())
                    if (operation != null && operation.type == VRC_AvatarParameterDriver.ChangeType.Copy) yield return operation.source;
        }

        internal static void Validate(VirtualAnimatorController[] controllers, IReadOnlyDictionary<string, ParameterConstant> constants)
        {
            var nodes = controllers.SelectMany(c => c.AllReachableNodes()).Distinct().ToArray();
            var internalNames = new HashSet<string>(constants.Values.Select(c => c.InternalName), StringComparer.Ordinal);
            foreach (var controller in controllers)
            {
                if (controller.Layers.Any(l => l.SyncedLayerIndex != -1))
                    throw new InvalidOperationException("Parameter freezing does not yet support synchronized-layer behaviour overrides: " + controller.Name);
                foreach (var constant in constants.Values)
                {
                    if (controller.Parameters.TryGetValue(constant.Name, out var original) && !constant.Types.Contains(original.type))
                        throw new InvalidOperationException("Frozen parameter type changed during the build: " + constant.Name);
                    if (controller.Parameters.TryGetValue(constant.InternalName, out var value) && !constant.Matches(value))
                        throw new InvalidOperationException("A later build pass changed an internal parameter constant: " + constant.Name);
                }
                foreach (var clip in controller.AllReachableNodes().OfType<VirtualClip>())
                    foreach (var binding in clip.GetFloatCurveBindings().Where(b => b.type == typeof(Animator)))
                    {
                        if (internalNames.Contains(binding.propertyName))
                            throw new InvalidOperationException("An animation writes to an internal parameter constant: " + binding.propertyName);
                        if (binding.path != "" || !constants.ContainsKey(binding.propertyName)) continue;
                        if (clip.IsMarkerClip || !controller.Parameters.TryGetValue(binding.propertyName, out var parameter) ||
                            parameter.type != AnimatorControllerParameterType.Float || binding.isDiscreteCurve)
                            throw new InvalidOperationException("Cannot safely specialize this parameter animation binding: " + clip.Name + " / " + binding.propertyName);
                    }
            }
            foreach (var behaviour in Behaviours(nodes))
            {
                if (behaviour == null) continue;
                if (behaviour is VRC_AvatarParameterDriver driver)
                {
                    foreach (var operation in driver.parameters ?? new())
                        if (operation != null && internalNames.Contains(operation.name))
                            throw new InvalidOperationException("A Driver writes to an internal parameter constant: " + operation.name);
                    continue;
                }
                if (!SupportsBehaviour(behaviour))
                    throw new InvalidOperationException("Parameter freezing cannot prove the inputs of StateMachineBehaviour: " + behaviour.GetType().FullName);
                // Play Audio's parameter mode requires an Expression Parameter, so a private local
                // constant is not a valid replacement. Reject until this consumer is specialized.
                using var serialized = new SerializedObject(behaviour);
                var property = serialized.GetIterator();
                while (property.Next(true))
                    if (property.propertyType == SerializedPropertyType.String &&
                        (constants.ContainsKey(property.stringValue) || internalNames.Contains(property.stringValue)))
                        throw new InvalidOperationException("Unsupported parameter consumer: " + behaviour.GetType().Name + "." + property.propertyPath + " = " + property.stringValue);
            }
            foreach (var clip in nodes.OfType<VirtualClip>())
            {
                var source = MotionAnalysis.Source(clip);
                if (source != null && source.events.Length > 0)
                    throw new InvalidOperationException("Parameter freezing cannot prove animation event side effects: " + clip.Name);
            }
        }

        internal static ParameterSpecializationReport Apply(VirtualAnimatorController[] controllers,
            IReadOnlyDictionary<string, ParameterConstant> constants)
        {
            Validate(controllers, constants);
            var report = new ParameterSpecializationReport();
            string Read(string name)
            {
                if (name == null || !constants.TryGetValue(name, out var value)) return name;
                report.Reads++;
                return value.InternalName;
            }
            // Declare a constant only in controllers that read it. Every reader, including Driver
            // Copy, gets the same default; unrelated playable layers need no extra parameters.
            foreach (var controller in controllers)
            {
                var reads = new HashSet<string>(Reads(controller.AllReachableNodes()).Where(n => n != null), StringComparer.Ordinal);
                foreach (var constant in constants.Values)
                    if (!controller.Parameters.ContainsKey(constant.InternalName) &&
                        (reads.Contains(constant.Name) || reads.Contains(constant.InternalName)))
                    {
                        controller.Parameters = controller.Parameters.Add(constant.InternalName,
                            constant.Declaration(controller.Parameters.TryGetValue(constant.Name, out var original) ? original.type : constant.Type));
                        report.Declarations++;
                    }
            }
            var nodes = controllers.SelectMany(c => c.AllReachableNodes()).Distinct().ToArray();
            foreach (var transition in nodes.OfType<VirtualTransitionBase>())
                if (transition.Conditions.Any(c => c.parameter != null && constants.ContainsKey(c.parameter)))
                    transition.Conditions = transition.Conditions.Select(c => new AnimatorCondition
                    { mode = c.mode, threshold = c.threshold, parameter = Read(c.parameter) }).ToImmutableList();
            foreach (var state in nodes.OfType<VirtualState>())
            {
                var speed = Read(state.SpeedParameter); if (speed != state.SpeedParameter) state.SpeedParameter = speed;
                var time = Read(state.TimeParameter); if (time != state.TimeParameter) state.TimeParameter = time;
                var offset = Read(state.CycleOffsetParameter); if (offset != state.CycleOffsetParameter) state.CycleOffsetParameter = offset;
                var mirror = Read(state.MirrorParameter); if (mirror != state.MirrorParameter) state.MirrorParameter = mirror;
            }
            foreach (var tree in nodes.OfType<VirtualBlendTree>())
                if (tree.BlendType == BlendTreeType.Direct)
                {
                    if (tree.Children.Any(c => c.DirectBlendParameter != null && constants.ContainsKey(c.DirectBlendParameter)))
                        tree.Children = tree.Children.Select(c => new VirtualBlendTree.VirtualChildMotion
                        {
                            Motion = c.Motion, DirectBlendParameter = Read(c.DirectBlendParameter), CycleOffset = c.CycleOffset,
                            Mirror = c.Mirror, Position = c.Position, Threshold = c.Threshold, TimeScale = c.TimeScale
                        }).ToImmutableList();
                }
                else
                {
                    var x = Read(tree.BlendParameter); if (x != tree.BlendParameter) tree.BlendParameter = x;
                    if (tree.BlendType != BlendTreeType.Simple1D)
                    { var y = Read(tree.BlendParameterY); if (y != tree.BlendParameterY) tree.BlendParameterY = y; }
                }
            var changedBehaviours = new HashSet<StateMachineBehaviour>();
            foreach (var driver in Behaviours(nodes).OfType<VRC_AvatarParameterDriver>())
            {
                if (driver.parameters == null) continue;
                int writes = driver.parameters.RemoveAll(p => p != null && p.name != null && constants.ContainsKey(p.name));
                report.DriverWrites += writes;
                int beforeReads = report.Reads;
                foreach (var operation in driver.parameters)
                    if (operation != null && operation.type == VRC_AvatarParameterDriver.ChangeType.Copy)
                        operation.source = Read(operation.source);
                if (writes > 0 || beforeReads != report.Reads) changedBehaviours.Add(driver);
            }
            // Notify the virtual graph after editing serialized behaviour instances.
            foreach (var state in nodes.OfType<VirtualState>())
                if (state.Behaviours.Any(changedBehaviours.Contains)) state.Behaviours = state.Behaviours;
            foreach (var machine in nodes.OfType<VirtualStateMachine>())
                if (machine.Behaviours.Any(changedBehaviours.Contains)) machine.Behaviours = machine.Behaviours;
            foreach (var clip in nodes.OfType<VirtualClip>())
            {
                var bindings = clip.GetFloatCurveBindings().Where(b => b.type == typeof(Animator) && b.path == "" && constants.ContainsKey(b.propertyName)).ToArray();
                if (bindings.Length == 0) continue;
                float duration = MotionAnalysis.EndTime(clip);
                foreach (var binding in bindings) clip.SetFloatCurve(binding, null);
                MotionAnalysis.PreserveDuration(clip, duration);
                report.ParameterCurves += bindings.Length;
            }
            return report;
        }
    }
}
