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

        internal static IEnumerable<(Component Source, string Name)> Sensors(GameObject avatar)
        {
            foreach (var receiver in avatar.GetComponentsInChildren<VRCContactReceiver>(true))
                if (!string.IsNullOrEmpty(receiver.parameter)) yield return (receiver, receiver.parameter);
            foreach (var bone in avatar.GetComponentsInChildren<VRCPhysBone>(true))
                if (!string.IsNullOrEmpty(bone.parameter))
                    foreach (var suffix in PhysBoneSuffixes) yield return (bone, bone.parameter + suffix);
            foreach (var raycast in avatar.GetComponentsInChildren<VRCRaycast>(true))
                if (!string.IsNullOrEmpty(raycast.Parameter))
                    foreach (var suffix in RaycastSuffixes) yield return (raycast, raycast.Parameter + suffix);
        }

        internal static IEnumerable<string> ExternalNames(GameObject avatar)
        {
            var expressions = avatar.GetComponent<VRCAvatarDescriptor>()?.expressionParameters;
            if (expressions != null && expressions.parameters != null)
                foreach (var parameter in expressions.parameters)
                    if (parameter != null && !string.IsNullOrEmpty(parameter.name)) yield return parameter.name;
            foreach (var name in Sensors(avatar).Select(s => s.Name)) yield return name;
        }
    }
}
