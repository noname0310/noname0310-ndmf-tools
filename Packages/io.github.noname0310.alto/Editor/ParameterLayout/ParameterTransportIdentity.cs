using System;
using nadena.dev.ndmf.animator;
using UnityEditor.Animations;
using UnityEngine;

namespace Noname.AvatarTools.ParameterLayout
{
    internal static class ParameterTransportIdentity
    {
        // VRChat converts its Bool identity to numeric 0/1 for Int/Float declarations.
        // Keep an existing declaration so other layers retain their parameter types.
        // https://creators.vrchat.com/avatars/animator-parameters/#mismatched-parameter-type-conversion
        internal static AnimatorControllerParameterType Ensure(VirtualAnimatorController controller)
        {
            var type = Read(controller);
            if (!controller.Parameters.ContainsKey("IsLocal"))
                controller.Parameters = controller.Parameters.Add("IsLocal", new AnimatorControllerParameter
                    { name = "IsLocal", type = type });
            return type;
        }

        internal static AnimatorControllerParameterType Read(VirtualAnimatorController controller)
        {
            if (controller.Parameters.TryGetValue("IsLocal", out var parameter))
            {
                if (parameter.type is not (AnimatorControllerParameterType.Bool or AnimatorControllerParameterType.Int or AnimatorControllerParameterType.Float))
                    throw new InvalidOperationException("Parameter transport requires a Bool, Int or Float IsLocal declaration.");
                return parameter.type;
            }
            return AnimatorControllerParameterType.Bool;
        }

        internal static AnimatorCondition Condition(AnimatorControllerParameterType type, bool local) => new()
        {
            parameter = "IsLocal",
            mode = type == AnimatorControllerParameterType.Bool ? local ? AnimatorConditionMode.If : AnimatorConditionMode.IfNot :
                type == AnimatorControllerParameterType.Int ? AnimatorConditionMode.Equals : local ? AnimatorConditionMode.Greater : AnimatorConditionMode.Less,
            threshold = type == AnimatorControllerParameterType.Float ? .5f : local ? 1 : 0
        };
    }
}
