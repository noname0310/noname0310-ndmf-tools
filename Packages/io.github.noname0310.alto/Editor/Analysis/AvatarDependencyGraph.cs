using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.ObjectOptimization;
using Noname.AvatarTools.ParameterPolicy;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using VRC.Dynamics;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.Contact.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;
using VRC.SDKBase;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.Analysis
{
    // Design reference: AAO 1.9.20 dependency collection and FindUnusedObjectsProcessor.
    // This independently implemented graph consumes our animation and parameter analyses.
    // See CREDITS.md and ThirdPartyNotices.txt for attribution and license distinctions.
    internal sealed class AvatarDependencyGraph
    {
        private readonly GameObject avatar;
        private readonly AvatarAnimationIndex animations;
        private readonly Dictionary<Object, HashSet<Object>> edges = new();
        private readonly HashSet<Object> roots = new();
        private readonly HashSet<Object> live = new();
        internal readonly Dictionary<Component, Transform> Constraints = new();
        internal readonly HashSet<Component> Components;
        // Explicit serialized readers, separate from structural membership/dependency edges.
        // Mesh rewrites must not confuse a renderer's GameObject with an external reader.
        internal readonly Dictionary<Object, HashSet<Object>> ReferenceOwners = new();
        internal string UnsafeReason;

        private AvatarDependencyGraph(GameObject avatar, AvatarAnimationIndex animations)
        {
            this.avatar = avatar; this.animations = animations;
            Components = new(avatar.GetComponentsInChildren<Component>(true).Where(c => c != null));
        }

        internal bool IsLive(Object value) => value != null && live.Contains(value);
        internal bool DependsOn(Object owner, Object dependency)
        {
            var seen = new HashSet<Object>(); var pending = new Queue<Object>(); pending.Enqueue(owner);
            while (pending.Count > 0)
            {
                var value = pending.Dequeue();
                if (value == null || !seen.Add(value)) continue;
                if (value == dependency) return true;
                if (edges.TryGetValue(value, out var references)) foreach (var reference in references) pending.Enqueue(reference);
                // Include future serialized references from object animation keys,
                // not only the reference currently stored on the component.
                foreach (var use in animations.ForObject(value))
                    foreach (var key in use.ObjectKeys ?? Array.Empty<ObjectReferenceKeyframe>()) pending.Enqueue(key.value);
            }
            return false;
        }
        internal bool IsHierarchyObject(Object value) => value is Component component && component.transform.IsChildOf(avatar.transform) ||
            value is GameObject obj && obj.transform.IsChildOf(avatar.transform);

        private void Edge(Object owner, Object target)
        {
            if (owner == null || target == null) return;
            if (!edges.TryGetValue(owner, out var dependencies)) edges.Add(owner, dependencies = new());
            dependencies.Add(target);
        }

        private void Root(Object value) { if (value != null) roots.Add(value); }

        internal static AvatarDependencyGraph Create(GameObject avatar, AvatarAnimationIndex animations,
            ObjectOptimizationState settings, bool collectObjects)
        {
            var result = new AvatarDependencyGraph(avatar, animations) { UnsafeReason = animations.UnsafeReason };
            result.Build(settings, collectObjects);
            return result;
        }

        private bool CanActivate(Transform transform)
        {
            // An animation can turn an initially disabled object on. Treat every write as
            // potentially active, including additive/direct trees and ambiguous bindings.
            // The invariant-curve pass can remove proven redundant writes beforehand.
            for (var current = transform; current != null && current != avatar.transform; current = current.parent)
                if (!current.gameObject.activeSelf && !animations.Writes(current.gameObject, "m_IsActive")) return false;
            return true;
        }

        private bool CanEnable(Component component)
        {
            if (!CanActivate(component.transform)) return false;
            if (animations.Writes(component, "m_Enabled")) return true;
            return component is Renderer renderer ? renderer.enabled : component is Behaviour behaviour ? behaviour.enabled : true;
        }

        private void Build(ObjectOptimizationState settings, bool collectObjects)
        {
            Root(avatar.transform);
            var animatedGlobals = new HashSet<Component>(animations.Bindings.Where(u => u.Binding.type != null &&
                typeof(VRCPhysBoneColliderBase).IsAssignableFrom(u.Binding.type) &&
                (u.Binding.propertyName.StartsWith("globalCollision", StringComparison.Ordinal) || u.Binding.propertyName == "isGlobalCollider"))
                .SelectMany(u => u.Objects()).OfType<Component>());
            var byObject = Components.GroupBy(c => c.gameObject).ToDictionary(g => g.Key, g => g.ToArray());
            foreach (var pair in byObject)
            {
                foreach (var component in pair.Value) Edge(pair.Key, component);
                foreach (var owner in pair.Value)
                    foreach (var required in pair.Value)
                        if (owner != required && BuildFilterPass.Requires(owner, required)) Edge(owner, required);
            }
            foreach (var component in Components)
            {
                if (component is Transform transform) Edge(transform, transform.parent);
                else Edge(component, component.transform);
                if (TryConstraintTarget(component, out var target))
                {
                    Constraints.Add(component, target);
                    if (target != null) Edge(target, component);
                }
                // A raycast writes its result transform as well as optional Animator
                // parameters. The result's consumers keep the producer alive, just
                // like constraint consumers; a reference from the producer alone
                // must not turn an otherwise unused result into a root.
                if (component.GetType() == typeof(VRCRaycast))
                {
                    var result = ((VRCRaycast)component).ResultTransform;
                    if (result != null) Edge(result, component);
                }
                // Head Chop scales its configured bones in first-person views. A live
                // target (or descendant) keeps this writer, just like a constraint;
                // the writer alone must not keep an unused target hierarchy alive.
                // https://creators.vrchat.com/avatars/avatar-components/vrc-headchop/
                if (component.GetType() == typeof(VRCHeadChop))
                    foreach (var bone in ((VRCHeadChop)component).targetBones ?? Array.Empty<VRCHeadChop.HeadChopBone>())
                        if (bone?.transform != null) Edge(bone.transform, component);
                if (component is MeshRenderer) Edge(component, component.GetComponent<MeshFilter>());
                if (component is ParticleSystemRenderer) Edge(component, component.GetComponent<ParticleSystem>());
                bool audioModifier = AudioOutputDependencies.IsModifier(component);
                if (audioModifier)
                    foreach (var output in AudioOutputDependencies.Outputs(component)) Edge(output, component);
                if (component is Animator animator)
                {
                    if (animator.isHuman)
                        for (var bone = HumanBodyBones.Hips; bone < HumanBodyBones.LastBone; bone++) Edge(component, animator.GetBoneTransform(bone));
                    if (animator.applyRootMotion && !animator.isHuman)
                        foreach (var child in animator.GetComponentsInChildren<Transform>(true)) Edge(component, child);
                }
                // PhysBones also consume descendants that are not serialized individually.
                // Keep ignored branches conservatively until the PhysBone specialization pass.
                if (component is VRCPhysBone physBone)
                    foreach (var child in (physBone.rootTransform != null ? physBone.rootTransform : physBone.transform).GetComponentsInChildren<Transform>(true))
                        Edge(component, child);

                bool authoring = component is INDMFEditorOnly || component is IEditorOnly;
                if (!settings.Scope.Contains(component.transform) || settings.Protected(component)) Root(component);
                else if (authoring)
                {
                    // Captured settings are metadata. Keep external editor components and
                    // their explicit references for later validation, but their placement
                    // alone does not make the entire subtree a runtime consumer. Generators
                    // must finish before optimization or declare the required pass ordering.
                    if (!settings.RuntimeDependenciesOnly && !(component is ALTOOptimizationComponent) &&
                        !(component is ALTOTraceAndOptimizeAnimator) && !(component is ALTOTraceAndOptimizeExpressions) &&
                        !(component is ALTOTraceAndOptimizeBlendShapes) && !(component is BlendShapeOptimizationExclude) &&
                        !(component is nadena.dev.ndmf.runtime.components.NDMFAvatarRoot))
                    {
                        Root(component);
                    }
                    else if (!settings.RuntimeDependenciesOnly && collectObjects) Root(component.transform);
                }
                else if (component is Transform || component is MeshFilter || Constraints.ContainsKey(component) || audioModifier) { }
                else if (!collectObjects) Root(component);
                else if (component is Animator childAnimator)
                {
                    if (childAnimator.transform == avatar.transform || childAnimator.runtimeAnimatorController != null ||
                        childAnimator.hasBoundPlayables || animations.ControllerEntries.ContainsKey(childAnimator)) Root(component);
                    else
                        // Preserve the nested animation boundary whenever any descendant is
                        // observed. An otherwise unused controller-less subtree can be collected.
                        // References, object curves and explicit preservation still root it.
                        Edge(childAnimator.transform, childAnimator);
                }
                else if (component is Renderer || component is ParticleSystem || component is VRCContactSender)
                {
                    if (CanEnable(component)) Root(component);
                }
                else if (component is VRCPhysBoneCollider collider)
                {
                    // Global colliders affect simulations without an explicit local reference.
                    if (collider.globalCollision != VRCPhysBoneBase.AdvancedBool.False || animatedGlobals.Contains(collider)) Root(component);
                }
                else if (component is VRCContactReceiver receiver)
                {
                    // Disabled receivers may reset their parameter. Removal requires proof
                    // that the output itself became unobservable, not just inactive drawing.
                    if (!settings.PrunedSensorInputs.Contains(receiver.parameter ?? "")) Root(component);
                }
                else if (component.GetType() == typeof(VRCRaycast))
                {
                    var raycast = (VRCRaycast)component;
                    bool parametersObserved = !string.IsNullOrEmpty(raycast.Parameter) &&
                        AvatarParameterInputs.RaycastSuffixes.Any(suffix => !settings.PrunedSensorInputs.Contains(raycast.Parameter + suffix));
                    if (parametersObserved || raycast.ResultTransform != null && !IsHierarchyObject(raycast.ResultTransform)) Root(component);
                }
                else if (component.GetType() == typeof(VRCHeadChop))
                {
                    // An external target is observed outside this avatar's graph.
                    if ((((VRCHeadChop)component).targetBones ?? Array.Empty<VRCHeadChop.HeadChopBone>())
                        .Any(b => b?.transform != null && !IsHierarchyObject(b.transform))) Root(component);
                }
                else Root(component); // Unknown native outputs and runtime behaviours stay observable.

                if (Constraints.TryGetValue(component, out var constraintTarget) &&
                    (constraintTarget == null || !constraintTarget.IsChildOf(avatar.transform))) Root(component);
                if (!(component is Transform) && !authoring) ReadReferences(component);
                else if (authoring && roots.Contains(component)) ReadReferences(component);
            }

            foreach (var behaviour in ParameterSpecialization.Behaviours(animations.Controllers.SelectMany(c => c.AllReachableNodes()).Distinct()))
            {
                Root(behaviour);
                ReadReferences(behaviour);
            }
            foreach (var use in animations.Bindings)
            {
                if (use.Binding.isPPtrCurve)
                {
                    // Retargeting can change the graph at runtime. Both the property owner
                    // and every possible object key remain valid, even in an inactive branch.
                    foreach (var key in use.ObjectKeys ?? Array.Empty<ObjectReferenceKeyframe>()) Root(key.value);
                    foreach (var target in use.Objects()) Root(target);
                }
                if (use.Immutable || !use.Complete)
                    foreach (var target in use.Targets) Root(target.gameObject);
                // A preserved parameter remains dynamic and retains its live outputs.
                // Its animation writers do not create consumers for otherwise unused
                // hierarchy. Object preservation and runtime references root those
                // consumers independently, including inactive skinning/constraint inputs.
            }
            var pending = new Queue<Object>(roots);
            while (pending.Count > 0)
            {
                var value = pending.Dequeue();
                if (value == null || !live.Add(value)) continue;
                // A runtime object key or a preserved owner can reach metadata indirectly.
                // Its references matter even when it was not an independent output root.
                if (value is Component metadata && (metadata is INDMFEditorOnly || metadata is IEditorOnly))
                    ReadReferences(metadata);
                if (edges.TryGetValue(value, out var dependencies)) foreach (var dependency in dependencies) pending.Enqueue(dependency);
            }
        }

        private readonly HashSet<Object> inspected = new();
        private void ReadReferences(Object owner)
        {
            if (owner == null || !inspected.Add(owner)) return;
            using var serialized = new SerializedObject(owner);
            var property = serialized.GetIterator();
            while (property.Next(true))
            {
                if (property.propertyType != SerializedPropertyType.ObjectReference || property.propertyPath == "m_GameObject" ||
                    property.propertyPath == "m_Script") continue;
                var reference = property.objectReferenceValue;
                if (reference == null) continue;
                Edge(owner, reference);
                if (!ReferenceOwners.TryGetValue(reference, out var readers)) ReferenceOwners.Add(reference, readers = new());
                readers.Add(owner);
                // Traverse assets which can hold indirect scene references, without walking
                // large mesh/texture payloads or the controller graph indexed separately.
                if (reference is ScriptableObject) ReadReferences(reference);
            }
        }

        internal static bool TryConstraintTarget(Component component, out Transform target)
        {
            target = null;
            if (component is IConstraint) { target = component.transform; return true; }
            var type = component.GetType();
            if (type.Assembly.GetName().Name != "VRC.SDK3.Dynamics.Constraint") return false;
            var field = type.GetField("TargetTransform");
            if (field?.FieldType != typeof(Transform)) return false;
            var configured = field.GetValue(component) as Transform;
            // Empty serialized references can be Unity missing-object wrappers.
            // The SDK treats these as self-targets, just like an ordinary null.
            target = configured != null ? configured : component.transform;
            return true;
        }
    }
}
