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
        private readonly SerializedReferenceSnapshot references;
        private readonly Dictionary<Object, HashSet<Object>> edges = new();
        private readonly HashSet<Object> roots = new();
        private readonly HashSet<Object> live = new();
        private readonly Dictionary<Transform, bool> activation = new();
        private InvariantComponentSettings invariantSettings;
        internal readonly Dictionary<Component, Transform> Constraints = new();
        internal readonly HashSet<Component> Components;
        // Explicit serialized readers, separate from structural membership/dependency edges.
        // Mesh rewrites must not confuse a renderer's GameObject with an external reader.
        internal readonly Dictionary<Object, HashSet<Object>> ReferenceOwners = new();
        internal string UnsafeReason;

        private AvatarDependencyGraph(GameObject avatar, AvatarAnimationIndex animations, SerializedReferenceSnapshot references)
        {
            this.avatar = avatar; this.animations = animations; this.references = references;
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
            ObjectOptimizationState settings, bool collectObjects, ShaderPropertyUsage shaders = null, SerializedReferenceSnapshot references = null)
        {
            using var cost = AnalysisCost.Measure("Object dependencies");
            var result = new AvatarDependencyGraph(avatar, animations, references) { UnsafeReason = animations.UnsafeReason };
            result.Build(settings, collectObjects, shaders ?? new ShaderPropertyUsage());
            return result;
        }

        private bool CanActivate(Transform transform)
        {
            // An animation can turn an initially disabled object on. Treat every write as
            // potentially active, including additive/direct trees and ambiguous bindings.
            // Share ancestor results only during this immutable graph construction.
            if (transform == null) return true;
            if (activation.TryGetValue(transform, out bool known)) return known;
            var pending = new List<Transform>();
            bool active = true;
            var root = avatar.transform;
            for (var current = transform; current != null && current != root; current = current.parent)
            {
                if (activation.TryGetValue(current, out active)) break;
                pending.Add(current);
                var obj = current.gameObject;
                if (!obj.activeSelf && !animations.Writes(obj, "m_IsActive")) { active = false; break; }
                active = true;
            }
            foreach (var current in pending) activation.Add(current, active);
            return active;
        }

        private bool CanEnable(Component component)
        {
            if (!CanActivate(component.transform)) return false;
            bool enabled = component is Renderer renderer ? renderer.enabled : component is Behaviour behaviour ? behaviour.enabled : true;
            if (enabled || !animations.Writes(component, "m_Enabled")) return enabled;
            // A binding kept for Write Defaults does not necessarily enable the output.
            // Only fold a disabled component when all reachable evaluations preserve it.
            invariantSettings ??= new InvariantComponentSettings(animations,
                animations.Reachability, animations.ControllerEntries);
            return !invariantSettings.IsConstantProperty(component, "m_Enabled");
        }

        private void Build(ObjectOptimizationState settings, bool collectObjects, ShaderPropertyUsage shaderUsage)
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
                if (component is VRCContactReceiver materialContact)
                    foreach (var renderer in ContactOutputs.Targets(materialContact))
                        if (ContactOutputs.MaterialReads(materialContact, renderer, shaderUsage, animations))
                        {
                            if (CanEnable(renderer)) Edge(renderer, component);
                            if (!IsHierarchyObject(renderer)) Root(component);
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
                // PhysBones consume their included simulation chain, including endpoints.
                if (component is VRCPhysBone physBone)
                {
                    var physBoneTarget = physBone.rootTransform != null ? physBone.rootTransform : physBone.transform;
                    // Match the SDK's non-serialized hierarchy listener dependency. Keep it
                    // for surviving simulations, and collect it when its PhysBone is removed.
                    foreach (var helper in physBoneTarget.GetComponents<Component>().Where(ObjectOptimizationSafety.IsParentChangeDetector))
                        Edge(component, helper);
                    foreach (var child in PhysBoneHierarchy.Included(physBone, animations))
                        Edge(component, child);
                }

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
                else if (component is Transform || component is MeshFilter || Constraints.ContainsKey(component) || audioModifier ||
                    ObjectOptimizationSafety.IsParentChangeDetector(component)) { }
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
                    if ((PhysBonePermissions.MayAllowGlobalCollision(collider) || animatedGlobals.Contains(collider)) && CanEnable(collider)) Root(component);
                }
                else if (component is VRCContactReceiver receiver)
                {
                    // Disabled receivers may reset their parameter. Removal requires proof
                    // that the output itself became unobservable, not just inactive drawing.
                    if (ContactOutputs.Parameters(receiver).Any(p => !settings.PrunedSensorInputs.Contains(p))) Root(component);
                }
                else if (component.GetType() == typeof(VRCRaycast))
                {
                    var raycast = (VRCRaycast)component;
                    bool parametersObserved = AvatarParameterInputs.RaycastProduces(raycast) && !string.IsNullOrEmpty(raycast.Parameter) &&
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
            using var cost = AnalysisCost.Measure("Object serialized references");
            using var typedCost = AnalysisCost.Measure("Object references by type", owner.GetType());
            bool fixedExclusions = owner is VRCPhysBone bone && PhysBoneHierarchy.FixedExclusions(bone, animations);
            foreach (var property in references == null ? SerializedReferenceSnapshot.Read(owner) : references.Get(owner))
            {
                var reference = property.Value;
                if (reference == null) continue;
                // Native FT mesh references are output destinations, not input
                // dependencies. A hidden, otherwise unused output must remain collectible.
                bool nativeFaceOutput = owner is VRCAvatarDescriptor && property.Path.StartsWith("faceTrackingMeshes.", StringComparison.Ordinal);
                bool materialContactOutput = owner is VRCContactReceiver && property.Path.StartsWith("shaderTargetRenderers.", StringComparison.Ordinal);
                // An exclusion is a traversal boundary, not a consumer of the excluded
                // subtree. Retain it in ReferenceOwners so flattening cannot move a
                // live child back into the simulation; an entirely dead branch can die.
                bool ignoredBranch = fixedExclusions && property.Path.StartsWith("ignoreTransforms.", StringComparison.Ordinal);
                if (!nativeFaceOutput && !materialContactOutput && !ignoredBranch) Edge(owner, reference);
                if (!ReferenceOwners.TryGetValue(reference, out var readers)) ReferenceOwners.Add(reference, readers = new());
                readers.Add(owner);
                // Traverse assets which can hold indirect scene references, without walking
                // large mesh/texture payloads or the controller graph indexed separately.
                if (reference is ScriptableObject) ReadReferences(reference);
            }
        }

        private static readonly Dictionary<Type, System.Reflection.FieldInfo> ConstraintTargetFields = new();

        internal static bool TryConstraintTarget(Component component, out Transform target)
        {
            target = null;
            if (component is IConstraint) { target = component.transform; return true; }
            var type = component.GetType();
            if (!ConstraintTargetFields.TryGetValue(type, out var field))
            {
                if (ObjectOptimizationSafety.AssemblyName(type) == "VRC.SDK3.Dynamics.Constraint")
                {
                    field = type.GetField("TargetTransform");
                    if (field?.FieldType != typeof(Transform)) field = null;
                }
                ConstraintTargetFields.Add(type, field);
            }
            if (field == null) return false;
            var configured = field.GetValue(component) as Transform;
            // Empty serialized references can be Unity missing-object wrappers.
            // The SDK treats these as self-targets, just like an ordinary null.
            target = configured != null ? configured : component.transform;
            return true;
        }
    }
}
