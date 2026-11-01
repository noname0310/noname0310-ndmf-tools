using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.Contact.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;

namespace Noname.AvatarTools.Analysis
{
    // Keep runtime writers shared by reachability, preservation and destructive input policies.
    internal static class AvatarParameterInputs
    {
        internal static readonly string[] PhysBoneSuffixes = { "_IsGrabbed", "_IsPosed", "_Angle", "_Stretch", "_Squish" };
        internal static readonly string[] RaycastSuffixes = { VRCRaycast.PARAM_HIT, VRCRaycast.PARAM_RATIO, VRCRaycast.PARAM_DISTANCE };
        internal static bool RaycastProduces(VRCRaycast raycast) => raycast.ResultTransform != null ||
            raycast.Version != VRCRaycast.RaycastComponentVersion.Version_1_0;

        internal static IEnumerable<(Component Source, string Name)> Sensors(GameObject avatar)
        {
            foreach (var receiver in avatar.GetComponentsInChildren<VRCContactReceiver>(true))
                foreach (string name in ContactOutputs.Parameters(receiver)) yield return (receiver, name);
            foreach (var bone in avatar.GetComponentsInChildren<VRCPhysBone>(true))
                if (!string.IsNullOrEmpty(bone.parameter))
                    foreach (var suffix in PhysBoneSuffixes) yield return (bone, bone.parameter + suffix);
            foreach (var raycast in avatar.GetComponentsInChildren<VRCRaycast>(true))
                if (RaycastProduces(raycast) && !string.IsNullOrEmpty(raycast.Parameter))
                    foreach (var suffix in RaycastSuffixes) yield return (raycast, raycast.Parameter + suffix);
        }

        internal static IEnumerable<string> ExternalNames(GameObject avatar)
            => ExpressionNames(avatar).Concat(Sensors(avatar).Select(s => s.Name))
                .Concat(new NativeFaceTracking(avatar).ForwardedParameters());

        internal static IEnumerable<string> ExpressionNames(GameObject avatar)
        {
            var expressions = avatar.GetComponent<VRCAvatarDescriptor>()?.expressionParameters;
            if (expressions != null && expressions.parameters != null)
                foreach (var parameter in expressions.parameters)
                    if (parameter != null && !string.IsNullOrEmpty(parameter.name)) yield return parameter.name;
        }
    }
}
