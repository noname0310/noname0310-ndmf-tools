using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
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

        internal static IEnumerable<VRCContactReceiver> Writers(GameObject avatar, Renderer renderer,
            AvatarAnimationIndex animations = null)
            => animations == null
                ? avatar.GetComponentsInChildren<VRCContactReceiver>(true).Where(r => Targets(r).Contains(renderer))
                : PotentialWriters(avatar, renderer, animations);

        private static bool MaterialConfiguration(string property) => property != null &&
            (property.StartsWith("writeToMaterials", StringComparison.Ordinal) ||
                property.StartsWith("shaderTargetRenderers", StringComparison.Ordinal));

        private static IEnumerable<VRCContactReceiver> PotentialWriters(GameObject avatar, Renderer renderer,
            AvatarAnimationIndex animations)
        {
            // Count the iterator's work without timing across yield boundaries,
            // which would accidentally include its consumer's processing time.
            AnalysisCost.Count("Contact outputs/potential writer enumeration");
            var native = new NativeAnimationBindings(avatar);
            bool unresolved = UnresolvedConfiguration(animations);
            foreach (var receiver in avatar.GetComponentsInChildren<VRCContactReceiver>(true))
            {
                if (string.IsNullOrEmpty(receiver.separateShaderParameter) && string.IsNullOrEmpty(receiver.parameter)) continue;
                if (unresolved) { yield return receiver; continue; }
                bool possible;
                try { possible = MayTarget(receiver, renderer, animations, native); }
                catch (Exception) { possible = true; }
                if (possible) yield return receiver;
            }
        }

        private static bool MayTarget(VRCContactReceiver receiver, Renderer renderer, AvatarAnimationIndex animations,
            NativeAnimationBindings native)
        {
            using var cost = AnalysisCost.Measure("Contact outputs/receiver target query");
            var uses = animations.ForObject(receiver).Where(u => MaterialConfiguration(u.Binding.propertyName)).ToArray();
            var enabled = uses.Where(u => u.Binding.propertyName.StartsWith("writeToMaterials", StringComparison.Ordinal)).ToArray();
            bool unknownEnable = enabled.Any(u => u.Binding.propertyName != "writeToMaterials" || u.Binding.isPPtrCurve ||
                !u.Complete || u.Objects().Count() != 1);
            if (unknownEnable) return true;
            // These exact SDK fields are serialized but current native Animator
            // sampling leaves them unchanged when no binding is advertised.
            // Reconsider their writes if a future SDK exposes native bindings.
            if (!receiver.writeToMaterials && (enabled.Length == 0 || !native.Supports(receiver, enabled, out _) ||
                animations.ConstantOutputs.TryProve(receiver, "writeToMaterials", ConstantOutputValue.Discrete(0), out var proof, out _) &&
                    proof.Value.Number == 0)) return false;

            // Keep initial targets even when animation always chooses another:
            // contact output can be consumed before Animator starts evaluating.
            var targets = (receiver.shaderTargetRenderers ?? Array.Empty<Renderer>())
                .Take(ContactReceiver.SHADER_MAX_UPDATED_RENDERERS).Where(r => r != null).ToHashSet();
            foreach (var use in uses.Where(u => u.Binding.propertyName.StartsWith("shaderTargetRenderers", StringComparison.Ordinal)))
            {
                if (!TargetSlot(use.Binding.propertyName, out int slot)) return true;
                if (slot >= ContactReceiver.SHADER_MAX_UPDATED_RENDERERS) continue;
                if (!use.Complete || !use.Binding.isPPtrCurve || use.ObjectKeys == null || use.ObjectKeys.Length == 0 ||
                    use.Objects().Count() != 1 ||
                    use.ObjectKeys.Any(k => !MotionAnalysis.Finite(k.time) || k.value != null && !(k.value is Renderer))) return true;
                if (!native.Supports(receiver, new[] { use }, out _)) continue;
                targets.UnionWith(use.ObjectKeys.Select(k => k.value).OfType<Renderer>());
            }
            return targets.Contains(renderer);
        }

        private static bool UnresolvedConfiguration(AvatarAnimationIndex animations)
        {
            using var cost = AnalysisCost.Measure("Contact outputs/all-binding configuration scan");
            return animations.UnsafeReason != null || animations.Bindings.Any(use => MaterialConfiguration(use.Binding.propertyName) &&
                (use.Binding.type == null || use.Binding.type.IsAssignableFrom(typeof(VRCContactReceiver)) ||
                    typeof(ContactReceiver).IsAssignableFrom(use.Binding.type)) &&
                (!use.Complete || use.Objects().OfType<ContactReceiver>().Count() != 1));
        }

        private static bool TargetSlot(string property, out int slot)
        {
            slot = -1; const string prefix = "shaderTargetRenderers.Array.data[";
            return property.StartsWith(prefix, StringComparison.Ordinal) && property.EndsWith("]", StringComparison.Ordinal) &&
                int.TryParse(property.Substring(prefix.Length, property.Length - prefix.Length - 1),
                    NumberStyles.None, CultureInfo.InvariantCulture, out slot);
        }

        // A possible writer set is an exclusion proof, not a temporal equivalence
        // proof. Mesh merging requires stable target coverage; alternating A/B
        // targets must not become one renderer receiving both outputs together.
        internal static bool FixedMaterialConfiguration(VRCContactReceiver receiver, AvatarAnimationIndex animations)
        {
            using var cost = AnalysisCost.Measure("Contact outputs/fixed material configuration query");
            if (UnresolvedConfiguration(animations)) return false;
            var native = new NativeAnimationBindings(animations.Avatar);
            foreach (var use in animations.ForObject(receiver).Where(u => MaterialConfiguration(u.Binding.propertyName)))
            {
                if (!use.Complete || use.Objects().Count() != 1) return false;
                bool enable = use.Binding.propertyName == "writeToMaterials" && !use.Binding.isPPtrCurve;
                bool target = TargetSlot(use.Binding.propertyName, out int slot) && use.Binding.isPPtrCurve;
                if (!enable && !target) return false;
                if (target && slot >= ContactReceiver.SHADER_MAX_UPDATED_RENDERERS) continue;
                if (native.Supports(receiver, new[] { use }, out _)) return false;
            }
            return true;
        }

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
