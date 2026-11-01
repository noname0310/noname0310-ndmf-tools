using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using VRC.Dynamics;
using VRC.SDK3.Dynamics.Contact.Components;

namespace Noname.AvatarTools.Analysis
{
    // A receiver can have several independent consumers: Animator parameters and
    // per-renderer shader buffers. All consumers participate in the dependency graph.
    // SDK suffixes/limits and AAO 1.9.21 ComponentInfos.VRCSDK.cs are behavior
    // references; shader-consumer analysis and merge remapping are independent.
    internal static class ContactOutputs
    {
        private static readonly (ContactReceiver.AdvancedParameter Flag, string[] Suffixes)[] Groups = {
            (ContactReceiver.AdvancedParameter.SenderType, new[] { ContactReceiver.PARAM_SENDER_TYPE }),
            (ContactReceiver.AdvancedParameter.SenderLocalPosition, new[] { ContactReceiver.PARAM_SENDER_LOCAL_POSITION_X, ContactReceiver.PARAM_SENDER_LOCAL_POSITION_Y, ContactReceiver.PARAM_SENDER_LOCAL_POSITION_Z }),
            (ContactReceiver.AdvancedParameter.SenderLossyScale, new[] { ContactReceiver.PARAM_SENDER_LOSSY_SCALE_X, ContactReceiver.PARAM_SENDER_LOSSY_SCALE_Y, ContactReceiver.PARAM_SENDER_LOSSY_SCALE_Z }),
            (ContactReceiver.AdvancedParameter.SenderVelocity, new[] { ContactReceiver.PARAM_SENDER_VELOCITY_X, ContactReceiver.PARAM_SENDER_VELOCITY_Y, ContactReceiver.PARAM_SENDER_VELOCITY_Z, ContactReceiver.PARAM_SENDER_VELOCITY_MAGNITUDE }),
            (ContactReceiver.AdvancedParameter.PointSeparation, new[] { ContactReceiver.PARAM_POINT_SEPARATION }),
            (ContactReceiver.AdvancedParameter.ProximitySpeed, new[] { ContactReceiver.PARAM_PROXIMITY_SPEED })
        };

        internal static IEnumerable<string> Parameters(ContactReceiver receiver)
        {
            if (string.IsNullOrEmpty(receiver.parameter)) yield break;
            yield return receiver.parameter;
            if (receiver.receiverType != ContactReceiver.ReceiverType.Proximity) yield break;
            foreach (var group in Groups)
                if ((receiver.advancedParameters & group.Flag) != 0)
                    foreach (string suffix in group.Suffixes) yield return receiver.parameter + suffix;
            if ((receiver.advancedParameters & ContactReceiver.AdvancedParameter.CollisionTags) != 0)
                foreach (string tag in (receiver.collisionTags ?? new()).Take(16).Where(t => !string.IsNullOrEmpty(t)).Distinct())
                    yield return receiver.parameter + ContactReceiver.PARAM_COLLISION_TAG_PREFIX + tag;
        }

        internal static IEnumerable<Renderer> Targets(ContactReceiver receiver)
            => receiver.writeToMaterials && (!string.IsNullOrEmpty(receiver.separateShaderParameter) || !string.IsNullOrEmpty(receiver.parameter))
                ? (receiver.shaderTargetRenderers ?? Array.Empty<Renderer>()).Take(ContactReceiver.SHADER_MAX_UPDATED_RENDERERS).Where(r => r != null).Distinct()
                : Enumerable.Empty<Renderer>();

        internal static string[] Properties(ContactReceiver receiver)
            => new[] { receiver.ShaderPropertyNameTransforms, receiver.ShaderPropertyNameData };

        internal static bool MaterialReads(ContactReceiver receiver, Renderer renderer, ShaderPropertyUsage shaders,
            AvatarAnimationIndex animations = null)
        {
            var materials = renderer.sharedMaterials.AsEnumerable();
            if (animations != null)
            {
                var swaps = animations.ForObject(renderer).Where(u => u.Binding.isPPtrCurve &&
                    u.Binding.propertyName.StartsWith("m_Materials", StringComparison.Ordinal)).ToArray();
                if (swaps.Any(u => !u.Complete)) return true;
                materials = materials.Concat(swaps.SelectMany(u => u.ObjectKeys ?? Array.Empty<UnityEditor.ObjectReferenceKeyframe>())
                    .Select(k => k.value).OfType<Material>());
            }
            return materials.Where(m => m != null).Any(m => Properties(receiver).Any(shaders.Get(m.shader).MayUse));
        }

        internal static IEnumerable<VRCContactReceiver> Writers(GameObject avatar, Renderer renderer)
            => avatar.GetComponentsInChildren<VRCContactReceiver>(true).Where(r => Targets(r).Contains(renderer));

        internal static void Remap(GameObject avatar, IEnumerable<Renderer> sources, Renderer destination)
        {
            var replaced = new HashSet<Renderer>(sources);
            foreach (var receiver in avatar.GetComponentsInChildren<VRCContactReceiver>(true))
            {
                // Preserve slot count and ordering: entries after the SDK limit must
                // never become active merely because duplicate targets were removed.
                if (receiver.shaderTargetRenderers == null) continue;
                receiver.shaderTargetRenderers = receiver.shaderTargetRenderers.Select(r => r != null && replaced.Contains(r) ? destination : r).ToArray();
            }
        }
    }
}
