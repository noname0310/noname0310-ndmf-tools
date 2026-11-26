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
    // implemented shared analysis; collecting these facts does not mutate the hierarchy.
    internal sealed class TransformMutationAnalysis
    {
        internal readonly HashSet<Transform> Mutable = new();
        internal string Uncertain;
        private readonly Dictionary<Transform, HashSet<Object>> writers = new();
        private readonly Dictionary<Transform, HashSet<Object>> scaleWriters = new();
        private readonly HashSet<Object> nonReversingScaleWriters = new();

        internal bool MayChangeScale(Transform target) => target != null && scaleWriters.ContainsKey(target);
        internal IEnumerable<Object> WritersOf(Transform target) => writers.TryGetValue(target, out var sources) ? sources : Enumerable.Empty<Object>();
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

        internal static TransformMutationAnalysis Create(GameObject avatar, AvatarAnimationIndex animations,
            bool assumeFixedHumanoidBoneScale = false, SerializedReferenceSnapshot references = null)
        {
            using var cost = AnalysisCost.Measure("Transform mutation analysis");
            var result = NativeWriterIndex(avatar, animations, assumeFixedHumanoidBoneScale, references); result.Uncertain = animations.UnsafeReason;
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
                    !(behaviour is VRC_AnimatorPlayAudio) && !(behaviour is VRC_AnimatorFaceTrackingBlendshapesSet))
                    result.Uncertain ??= "unverified StateMachineBehaviour: " + behaviour.GetType().FullName;
                ReadReferences(behaviour, result, behaviour, null, references);
            }
            if (result.Uncertain == null)
                result.nonReversingScaleWriters.UnionWith(TransformScaleSignAnalysis.NonReversingNativeWriters(avatar, animations));
            return result;
        }

        internal static HashSet<Transform> NativeWriters(GameObject avatar, SerializedReferenceSnapshot references = null) =>
            NativeWriterIndex(avatar, references: references).Mutable;

        private static TransformMutationAnalysis NativeWriterIndex(GameObject avatar, AvatarAnimationIndex animations = null,
            bool assumeFixedHumanoidBoneScale = false, SerializedReferenceSnapshot references = null)
        {
            using var cost = AnalysisCost.Measure("Native transform writer index");
            AnalysisCost.Count(references == null ? "Native transform writer index/own references" : "Native transform writer index/shared references");
            var result = new TransformMutationAnalysis();
            result.Add(avatar.transform, avatar, false);
            foreach (var animator in avatar.GetComponentsInChildren<Animator>(true))
            {
                result.Add(animator.transform, animator, false);
                if (animator.isHuman)
                    for (var bone = HumanBodyBones.Hips; bone < HumanBodyBones.LastBone; bone++)
                        // Humanoid retargeting can depend on intermediate skeleton transforms.
                        for (var target = animator.GetBoneTransform(bone); target != null && target != animator.transform; target = target.parent)
                            // External humanoid clips are absent from the build graph.
                            // An explicit opt-in may exclude their scale channel only;
                            // local clips, constraints and simulations add their own writers.
                            {
                                if (result.writers.TryGetValue(target, out var known) && known.Contains(animator)) break;
                                result.Add(target, animator, !assumeFixedHumanoidBoneScale);
                            }
                if (animator.applyRootMotion && !animator.isHuman)
                    foreach (var target in animator.GetComponentsInChildren<Transform>(true)) result.Add(target, animator);
            }
            foreach (var bone in avatar.GetComponentsInChildren<VRCPhysBone>(true))
                foreach (var target in PhysBoneHierarchy.Included(bone, animations)) result.Add(target, bone);
            foreach (var component in avatar.GetComponentsInChildren<Component>(true))
            {
                if (component == null || component is Transform || component is Renderer || component is MeshFilter ||
                    component is Animator || component is INDMFEditorOnly || component is IEditorOnly) continue;
                var type = component.GetType();
                if (type == typeof(VRCAvatarDescriptor))
                {
                    var descriptor = (VRCAvatarDescriptor)component;
                    if (descriptor.lipSync == VRC_AvatarDescriptor.LipSyncStyle.JawFlapBone)
                        result.Add(descriptor.lipSyncJawBone, descriptor, false);
                    if (descriptor.enableEyeLook)
                    {
                        var eyes = descriptor.customEyeLookSettings;
                        if (eyes.eyeGazeMode != VRCAvatarDescriptor.CustomEyeLookSettings.EyeGazeMode.FaceTracking)
                        { result.Add(eyes.leftEye, descriptor, false); result.Add(eyes.rightEye, descriptor, false); }
                        if (eyes.eyelidType == VRCAvatarDescriptor.EyelidType.Bones)
                            foreach (var lid in new[] { eyes.lowerLeftEyelid, eyes.upperLeftEyelid, eyes.lowerRightEyelid, eyes.upperRightEyelid })
                                result.Add(lid, descriptor, false);
                    }
                    // Native FT mesh references and shader Contact targets are outputs,
                    // not Transform writers. Their geometry/dependencies are indexed separately.
                    continue;
                }
                // PhysBone transform writes were indexed from its simulation root
                // above. Its collider references are readers, not writes to the
                // collider owners' transforms. Unknown subclasses stay conservative.
                if (type == typeof(VRCPhysBone) || type == typeof(VRCContactSender) || type == typeof(VRCContactReceiver) ||
                    type == typeof(VRCPhysBoneCollider)) continue;
                if (type == typeof(VRCRaycast))
                { result.Add(((VRCRaycast)component).ResultTransform, component, false); continue; }
                if (KnownConstraintWriter(component, out var constraintTarget, out bool writesScale))
                {
                    // Sources and world-up transforms are readers. SDK constraints
                    // may write a separate TargetTransform instead of their owner.
                    result.Add(constraintTarget, component, writesScale);
                    continue;
                }
                if (component is IConstraint || component is Rigidbody || component is ArticulationBody ||
                    ObjectOptimization.ObjectOptimizationSafety.AssemblyName(type) == "VRC.SDK3.Dynamics.Constraint") result.Add(component.transform, component);
                // Keep separate traversals per writer so shared assets retain every
                // reader's provenance, including an overlapping PhysBone.
                // Custom subclasses retain the conservative unknown-writer rule.
                ReadReferences(component, result, component, null, references);
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

        private static void ReadReferences(Object owner, TransformMutationAnalysis result, Object writer,
            ISet<Object> inspected, SerializedReferenceSnapshot references)
        {
            if (owner == null || inspected != null && !inspected.Add(owner)) return;
            using var cost = AnalysisCost.Measure("Mutation references by type", owner.GetType());
            foreach (var property in references == null ? SerializedReferenceSnapshot.Read(owner) : references.Get(owner))
            {
                var reference = property.Value;
                // Native shape sampling reads renderer geometry; it does not move
                // the renderer's Transform. Keep the dependency graph's reader
                // edge, but do not turn it into a transform/scale writer here.
                if (owner.GetType() == typeof(ParticleSystem) && reference is Renderer &&
                    (property.Path == "ShapeModule.m_MeshRenderer" ||
                     property.Path == "ShapeModule.m_SkinnedMeshRenderer" ||
                     property.Path == "ShapeModule.m_SpriteRenderer")) continue;
                if (AsTransform(reference) is Transform target) result.Add(target, writer);
                else if (reference is ScriptableObject)
                {
                    // Leaf references need neither a closure nor a recursion set.
                    // Keep separate visited sets once a writer enters an asset graph.
                    inspected ??= new HashSet<Object> { owner };
                    ReadReferences(reference, result, writer, inspected, references);
                }
            }
        }
    }
}
