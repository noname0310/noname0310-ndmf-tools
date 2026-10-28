using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using nadena.dev.ndmf.animator;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase;

namespace Noname.AvatarTools.ParameterLayout
{
    internal static class IntegerEncodingAnimator
    {
        internal static void Append(CloneContext clone, VirtualAnimatorController controller, IntegerEncoding encoding, int initial,
            Action<UnityEngine.Object> save = null)
        {
            encoding.Validate();
            var identityType = ParameterTransportIdentity.Ensure(controller);
            AddParameter(encoding.Name, AnimatorControllerParameterType.Int, initial);
            int defaultBits = encoding.Encode(initial);
            for (int bit = 0; bit < encoding.Bits.Count; bit++)
                AddParameter(encoding.Bits[bit], AnimatorControllerParameterType.Bool, (defaultBits >> bit) & 1);

            // Append after existing layers so original physical layer indices remain stable.
            // Drivers write logical Ints only remotely and bits only locally. Saved restoration
            // therefore reads the original local Int; generated wire bits are never Saved.
            var layer = controller.AddLayer(new LayerPriority(int.MaxValue), "ALTO Int: " + encoding.Name);
            layer.DefaultWeight = 1;
            var machine = layer.StateMachine = VirtualStateMachine.Create(clone, layer.Name);
            var idle = machine.AddState("Initialize"); idle.WriteDefaultValues = false;
            machine.DefaultState = idle;
            var empty = VirtualClip.Create("Integer transport (no pose bindings)");
            idle.Motion = empty;
            for (int code = 0; code < 1 << encoding.Bits.Count; code++)
            {
                int value = encoding.Decode(code);
                if (code <= encoding.Maximum - encoding.Minimum)
                {
                    var encode = machine.AddState("Encode " + value, empty); encode.WriteDefaultValues = false;
                    var operations = encoding.Bits.Select((name, bit) => Set(name, (code >> bit) & 1));
                    Drive(encode, operations, true, save);
                    var condition = value == encoding.Minimum
                        ? Condition(encoding.Name, AnimatorConditionMode.Less, value + 1)
                        : value == encoding.Maximum
                            ? Condition(encoding.Name, AnimatorConditionMode.Greater, value - 1)
                            : Condition(encoding.Name, AnimatorConditionMode.Equals, value);
                    Enter(encode, new[] { ParameterTransportIdentity.Condition(identityType, true), condition });
                }
                var decode = machine.AddState("Decode " + code, empty); decode.WriteDefaultValues = false;
                Drive(decode, new[] { Set(encoding.Name, value) }, false, save);
                Enter(decode, new[] { ParameterTransportIdentity.Condition(identityType, false) }.Concat(encoding.Bits.Select((name, bit) =>
                    Condition(name, (code & (1 << bit)) != 0 ? AnimatorConditionMode.If : AnimatorConditionMode.IfNot))));
            }

            void AddParameter(string name, AnimatorControllerParameterType type, int value)
            {
                if (controller.Parameters.TryGetValue(name, out var existing))
                {
                    if (existing.type != type) throw new InvalidOperationException("Conflicting Animator parameter type for integer compression: " + name);
                    return;
                }
                controller.Parameters = controller.Parameters.Add(name, new AnimatorControllerParameter
                { name = name, type = type, defaultInt = value, defaultBool = value != 0 });
            }
            void Enter(VirtualState target, IEnumerable<AnimatorCondition> conditions)
            {
                var transition = VirtualStateTransition.Create(); transition.SetDestination(target);
                transition.Conditions = conditions.ToImmutableList(); transition.Duration = 0;
                transition.ExitTime = null; transition.HasFixedDuration = true; transition.CanTransitionToSelf = false;
                machine.AnyStateTransitions = machine.AnyStateTransitions.Add(transition);
            }
        }

        private static AnimatorCondition Condition(string name, AnimatorConditionMode mode, float value = 0) =>
            new() { parameter = name, mode = mode, threshold = value };
        private static VRC_AvatarParameterDriver.Parameter Set(string name, int value) =>
            new() { name = name, type = VRC_AvatarParameterDriver.ChangeType.Set, value = value };
        private static void Drive(VirtualState state, IEnumerable<VRC_AvatarParameterDriver.Parameter> operations, bool local,
            Action<UnityEngine.Object> save)
        {
            var driver = ScriptableObject.CreateInstance<VRCAvatarParameterDriver>();
            driver.localOnly = local; driver.parameters = operations.ToList();
            state.Behaviours = state.Behaviours.Add(driver);
            // Newly created behaviours do not enter NDMF's cloned-object commit cache.
            // Persist them explicitly, including during SDK finalization after NDMF disposal.
            save?.Invoke(driver);
        }
    }
}
