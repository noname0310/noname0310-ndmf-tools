using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.ParameterPolicy;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.Contact.Components;
using VRC.SDK3.Dynamics.Constraint.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;
using VRC.SDKBase;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.Analysis
{
    // Native/humanoid writer handling was reviewed against AAO 1.9.20
    // AnimatorParserV2/AnimatorParser and ConfigureMergeBone. Independently
    // implemented shared analysis; the original transforms are never reparented.
    internal sealed class TransformMutationAnalysis
    {
        internal readonly HashSet<Transform> Mutable = new();
        internal string Uncertain;
        private readonly Dictionary<Transform, HashSet<Object>> writers = new();
        private readonly Dictionary<Transform, HashSet<Object>> scaleWriters = new();
        private readonly HashSet<Object> nonReversingScaleWriters = new();

        internal bool MayChangeScale(Transform target) => target != null && scaleWriters.ContainsKey(target);
        internal bool MayReverseScale(Transform target) => target != null &&
            scaleWriters.TryGetValue(target, out var sources) && sources.Any(s => !nonReversingScaleWriters.Contains(s));

        // Keep writer provenance: a simulation's own output is not an external
        // animation of its rest pose. Build this index once for all simulations.
        internal bool WrittenByOther(Transform target, Object owner, bool scaleOnly = false) =>
            target != null && (scaleOnly ? scaleWriters : writers).TryGetValue(target, out var sources) && sources.Any(s => s != owner);

        private void Add(Transform target, Object source, bool scale = true)
        {
            if (target == null) return;
            Mutable.Add(target);
            if (!writers.TryGetValue(target, out var all)) writers.Add(target, all = new());
            all.Add(source);
            if (!scale) return;
            if (!scaleWriters.TryGetValue(target, out var scales)) scaleWriters.Add(target, scales = new());
            scales.Add(source);
        }

        private static bool MayWriteScale(string property) =>
            property.StartsWith("m_LocalScale", StringComparison.Ordinal) || property.StartsWith("localScale", StringComparison.Ordinal) ||
            !(property.StartsWith("m_LocalPosition", StringComparison.Ordinal) || property.StartsWith("localPosition", StringComparison.Ordinal) ||
              property.StartsWith("m_LocalRotation", StringComparison.Ordinal) || property.StartsWith("localRotation", StringComparison.Ordinal) ||
              property.StartsWith("localEulerAngles", StringComparison.Ordinal));

        internal static TransformMutationAnalysis Create(GameObject avatar, AvatarAnimationIndex animations)
        {
            var result = NativeWriterIndex(avatar); result.Uncertain = animations.UnsafeReason;
            foreach (var use in animations.Bindings)
            {
                var source = MotionAnalysis.Source(use.Clip);
                if (use.Binding.type != null && typeof(Transform).IsAssignableFrom(use.Binding.type))
                    foreach (var target in use.Targets) result.Add(target, source, MayWriteScale(use.Binding.propertyName));
                foreach (var key in use.ObjectKeys ?? Array.Empty<ObjectReferenceKeyframe>())
                    if (AsTransform(key.value) is Transform target) result.Add(target, source);
            }
            foreach (var behaviour in ParameterSpecialization.Behaviours(animations.Controllers.SelectMany(c => c.AllReachableNodes())))
            {
                if (behaviour == null) continue;
                if (!(behaviour is VRC_AvatarParameterDriver) && !(behaviour is VRC_AnimatorLayerControl) &&
                    !(behaviour is VRC_PlayableLayerControl) && !(behaviour is VRC_AnimatorTrackingControl) &&
                    !(behaviour is VRC_AnimatorLocomotionControl) && !(behaviour is VRC_AnimatorTemporaryPoseSpace) &&
                    !(behaviour is VRC_AnimatorPlayAudio))
                    result.Uncertain ??= "unverified StateMachineBehaviour: " + behaviour.GetType().FullName;
                ReadReferences(behaviour, t => result.Add(t, behaviour), new HashSet<Object>());
            }
            if (result.Uncertain == null)
                result.nonReversingScaleWriters.UnionWith(TransformScaleSignAnalysis.NonReversingNativeWriters(avatar, animations));
            return result;
        }

        internal static HashSet<Transform> NativeWriters(GameObject avatar) => NativeWriterIndex(avatar).Mutable;

        private static TransformMutationAnalysis NativeWriterIndex(GameObject avatar)
        {
            var result = new TransformMutationAnalysis();
            result.Add(avatar.transform, avatar, false);
            foreach (var animator in avatar.GetComponentsInChildren<Animator>(true))
            {
                result.Add(animator.transform, animator, false);
                if (animator.isHuman)
                    for (var bone = HumanBodyBones.Hips; bone < HumanBodyBones.LastBone; bone++)
                        // Humanoid retargeting can depend on intermediate skeleton transforms.
                        for (var target = animator.GetBoneTransform(bone); target != null && target != animator.transform; target = target.parent)
                            result.Add(target, animator, false);
                if (animator.applyRootMotion && !animator.isHuman)
                    foreach (var target in animator.GetComponentsInChildren<Transform>(true)) result.Add(target, animator);
            }
            foreach (var bone in avatar.GetComponentsInChildren<VRCPhysBone>(true))
                foreach (var target in (bone.rootTransform != null ? bone.rootTransform : bone.transform).GetComponentsInChildren<Transform>(true)) result.Add(target, bone);
            foreach (var component in avatar.GetComponentsInChildren<Component>(true))
            {
                if (component == null || component is Transform || component is Renderer || component is MeshFilter ||
                    component is Animator || component is INDMFEditorOnly || component is IEditorOnly) continue;
                if (KnownConstraintWriter(component, out var constraintTarget, out bool writesScale))
                {
                    // Sources and world-up transforms are readers. SDK constraints
                    // may write a separate TargetTransform instead of their owner.
                    result.Add(constraintTarget, component, writesScale);
                    continue;
                }
                if (component is IConstraint || component is Rigidbody || component is ArticulationBody ||
                    component.GetType().Assembly.GetName().Name == "VRC.SDK3.Dynamics.Constraint") result.Add(component.transform, component);
                // Keep separate traversals per writer so shared assets retain every
                // reader's provenance, including an overlapping PhysBone.
                // Contacts/colliders consume transform frames; Descriptor eye/jaw
                // controls use rotations. Their references do not write local scale.
                // Keep conservative transform retention, but do not invent independent
                // scale-sign changes. Parameter-driven scale animation is indexed above.
                // Exact SDK types only: custom subclasses keep the unknown-writer rule.
                var type = component.GetType();
                bool mayWriteReferencedScale = type != typeof(VRCAvatarDescriptor) &&
                    type != typeof(VRCContactSender) && type != typeof(VRCContactReceiver) &&
                    type != typeof(VRCPhysBoneCollider);
                ReadReferences(component, t => result.Add(t, component, mayWriteReferencedScale), new HashSet<Object>());
            }
            return result;
        }

        private static bool KnownConstraintWriter(Component component, out Transform target, out bool scale)
        {
            var type = component.GetType();
            scale = type == typeof(ScaleConstraint) || type == typeof(VRCScaleConstraint);
            target = component.transform;
            if (type == typeof(AimConstraint) || type == typeof(LookAtConstraint) || type == typeof(ParentConstraint) ||
                type == typeof(PositionConstraint) || type == typeof(RotationConstraint) || type == typeof(ScaleConstraint)) return true;
            if (type != typeof(VRCAimConstraint) && type != typeof(VRCLookAtConstraint) && type != typeof(VRCParentConstraint) &&
                type != typeof(VRCPositionConstraint) && type != typeof(VRCRotationConstraint) && type != typeof(VRCScaleConstraint)) return false;
            var configuredTarget = ((VRC.Dynamics.VRCConstraintBase)component).TargetTransform;
            // Serialized empty or destroyed Unity references can have a managed wrapper.
            // The SDK then writes its own Transform; CLR null coalescing misses that case.
            target = configuredTarget != null ? configuredTarget : component.transform;
            return true;
        }

        private static Transform AsTransform(Object value) => value is GameObject obj ? obj.transform : (value as Component)?.transform;

        private static void ReadReferences(Object owner, Action<Transform> add, ISet<Object> inspected)
        {
            if (owner == null || !inspected.Add(owner)) return;
            using var serialized = new SerializedObject(owner);
            var property = serialized.GetIterator();
            while (property.Next(true))
            {
                if (property.propertyType != SerializedPropertyType.ObjectReference || property.propertyPath == "m_GameObject" || property.propertyPath == "m_Script") continue;
                var reference = property.objectReferenceValue;
                if (AsTransform(reference) is Transform target) add(target);
                else if (reference is ScriptableObject) ReadReferences(reference, add, inspected);
            }
        }
    }
}
