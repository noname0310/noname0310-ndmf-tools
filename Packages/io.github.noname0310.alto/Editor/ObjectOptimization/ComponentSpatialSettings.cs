using System;
using System.Linq;
using Noname.AvatarTools.Analysis;
using UnityEngine;
using VRC.Dynamics;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.Contact.Components;
using VRC.SDK3.Dynamics.Constraint.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;
using VRC.SDKBase;

namespace Noname.AvatarTools.ObjectOptimization
{
    internal static class ComponentSpatialSettings
    {
        // Returns a mutation to apply only after the normal ownership, activation,
        // multiplicity, reference and animation relocation proofs have succeeded.
        internal static bool TryPlan(Component source, Transform avatar, AvatarAnimationIndex animations,
            StaticTransformProof proof, out Action<Component> apply)
        {
            apply = null;
            var frame = source.transform;
            var parent = frame.parent;
            bool allowRounding = proof.AllowsRounding(NumericalOptimization.ComponentHoisting, frame, parent);
            bool fixedSettings = !animations.ForObject(source).Any(u => !u.Complete ||
                u.Binding.isPPtrCurve || u.Binding.propertyName != "m_Enabled");
            if (source is VRCContactSender sender)
            {
                // Sender matrices and velocity are public Contact outputs. Moreover,
                // OnEnter computes a closest point using the component's own origin.
                // Retain the original root, and move only co-located owners.
                if (!frame.localPosition.Equals(Vector3.zero) || !fixedSettings) return false;
                var originalRoot = sender.GetRootTransform();
                apply = c => ((VRCContactSender)c).rootTransform = originalRoot;
                return true;
            }
            if (source is VRCContactReceiver receiver)
            {
                if (!fixedSettings) return false;
                var originalRoot = receiver.GetRootTransform();
                bool geometryOnly = (receiver.receiverType == ContactReceiver.ReceiverType.Constant ||
                    receiver.receiverType == ContactReceiver.ReceiverType.Proximity) && !receiver.HasAnyAdvancedParameters && !receiver.writeToMaterials;
                if (!geometryOnly && !frame.localPosition.Equals(Vector3.zero)) return false;
                apply = c => ((VRCContactReceiver)c).rootTransform = originalRoot;
                if (originalRoot != frame || !geometryOnly || !allowRounding ||
                    !SpatialFrameProof.UniformShapeScale(frame, avatar, proof.Mutations, out float scale, allowRounding)) return true;
                var position = StaticTransformProof.Local(frame).MultiplyPoint3x4(receiver.position);
                var rotation = frame.localRotation * receiver.rotation;
                float radius = receiver.radius * scale, height = receiver.height * scale;
                var size = receiver.size * scale;
                if (!StaticTransformProof.Finite(position) || !StaticTransformProof.Finite(rotation) ||
                    !StaticTransformProof.Finite(radius) || !StaticTransformProof.Finite(height) || !StaticTransformProof.Finite(size)) return true;
                apply = c =>
                {
                    var target = (VRCContactReceiver)c;
                    target.rootTransform = parent; target.position = position; target.rotation = rotation;
                    target.radius = radius; target.height = height; target.size = size;
                };
                return true;
            }
            if (source is VRCPhysBoneCollider collider)
            {
                if (!fixedSettings) return false;
                var originalRoot = collider.GetRootTransform();
                apply = c => ((VRCPhysBoneCollider)c).rootTransform = originalRoot;
                if (originalRoot != frame || !allowRounding ||
                    !SpatialFrameProof.UniformShapeScale(frame, avatar, proof.Mutations, out float scale, allowRounding)) return true;
                var position = StaticTransformProof.Local(frame).MultiplyPoint3x4(collider.position);
                var rotation = frame.localRotation * collider.rotation;
                float radius = collider.radius * scale, height = collider.height * scale;
                if (!StaticTransformProof.Finite(position) || !StaticTransformProof.Finite(rotation) ||
                    !StaticTransformProof.Finite(radius) || !StaticTransformProof.Finite(height)) return true;
                apply = c =>
                {
                    var target = (VRCPhysBoneCollider)c;
                    target.rootTransform = parent; target.position = position; target.rotation = rotation;
                    target.radius = radius; target.height = height;
                };
                return true;
            }
            // These contracts already retain their explicit targets in RetainTargets.
            // A simulation's root is not replaced by its parent's coordinate system.
            if (source is VRCPhysBone || source is VRCConstraintBase || source is VRCHeadChop) return true;
            // Native Unity colliders have no separate root field. Strict mode
            // retains a nonidentity frame rather than regrouping its arithmetic.
            if (!allowRounding || !fixedSettings || !(source is Collider) ||
                !SpatialFrameProof.UniformShapeScale(frame, avatar, proof.Mutations, out float unityScale, allowRounding)) return false;
            // Unity boxes/capsules have no independent rotation field. Translation
            // and uniform scale are representable in center/size. Spheres have no
            // orientation; mesh colliders have no independent center/size fields.
            var matrix = StaticTransformProof.Local(frame);
            if (source is SphereCollider sphere)
            {
                var center = matrix.MultiplyPoint3x4(sphere.center); float radius = sphere.radius * unityScale;
                if (!StaticTransformProof.Finite(center) || !StaticTransformProof.Finite(radius)) return false;
                apply = c => { ((SphereCollider)c).center = center; ((SphereCollider)c).radius = radius; }; return true;
            }
            if (!StaticTransformProof.Near(Matrix4x4.Rotate(frame.localRotation), Matrix4x4.identity, allowRounding)) return false;
            if (source is BoxCollider box)
            {
                var center = matrix.MultiplyPoint3x4(box.center); var size = box.size * unityScale;
                if (!StaticTransformProof.Finite(center) || !StaticTransformProof.Finite(size)) return false;
                apply = c => { ((BoxCollider)c).center = center; ((BoxCollider)c).size = size; }; return true;
            }
            if (source is CapsuleCollider capsule)
            {
                var center = matrix.MultiplyPoint3x4(capsule.center); float radius = capsule.radius * unityScale, height = capsule.height * unityScale;
                if (!StaticTransformProof.Finite(center) || !StaticTransformProof.Finite(radius) || !StaticTransformProof.Finite(height)) return false;
                apply = c => { var target = (CapsuleCollider)c; target.center = center; target.radius = radius; target.height = height; }; return true;
            }
            return false;
        }
    }
}
