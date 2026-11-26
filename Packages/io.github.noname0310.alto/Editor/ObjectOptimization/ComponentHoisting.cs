using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.BlendShapeOptimization;
using Noname.AvatarTools.Meshes;
using Noname.AvatarTools.ParameterPolicy;
using Noname.AvatarTools.Physics;
using UnityEditor;
using UnityEngine;
using VRC.Dynamics;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.Contact.Components;
using VRC.SDK3.Dynamics.Constraint.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;
using VRC.SDKBase;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.ObjectOptimization
{
    // Known component contracts, not asset/prefab names. A contract distinguishes
    // co-location, explicit runtime targets and animation's type-based addressing.
    // Unknown subclasses never inherit relocation permission from a known base.
    internal static class ComponentHoisting
    {
        private sealed class Rule
        {
            internal Func<ComponentHoistingOptions, bool> Enabled;
            internal bool Multiple;
            internal Action<Component, Component> RetainTargets;
            internal Rule(Func<ComponentHoistingOptions, bool> enabled, bool multiple = false,
                Action<Component, Component> retainTargets = null)
            { Enabled = enabled; Multiple = multiple; RetainTargets = retainTargets; }
        }

        private static readonly Dictionary<Type, Rule> Rules = CreateRules();
        private static Dictionary<Type, Rule> CreateRules()
        {
            var rules = new Dictionary<Type, Rule>
            {
                [typeof(SkinnedMeshRenderer)] = new(o => o.SkinnedMeshRenderers, retainTargets: (a, b) =>
                {
                    var source = (SkinnedMeshRenderer)a; var target = (SkinnedMeshRenderer)b;
                    if (source.rootBone == null || source.rootBone == source.transform) target.rootBone = target.transform;
                    target.bones = source.bones.Select(bone => bone == source.transform ? target.transform : bone).ToArray();
                    for (int i = 0; source.sharedMesh != null && i < source.sharedMesh.blendShapeCount; i++)
                        target.SetBlendShapeWeight(i, source.GetBlendShapeWeight(i));
                }),
                [typeof(MeshRenderer)] = new(o => o.MeshRenderers),
                [typeof(VRCPhysBone)] = new(o => o.PhysBones, true, (a, b) => ((VRCPhysBone)b).rootTransform = ((VRCPhysBone)a).GetRootTransform()),
                [typeof(VRCPhysBoneCollider)] = new(o => o.PhysBoneColliders, true, (a, b) => ((VRCPhysBoneCollider)b).rootTransform = RelocatedRoot(((VRCPhysBoneCollider)a).rootTransform, a, b)),
                [typeof(VRCContactSender)] = new(o => o.ContactSenders, true, (a, b) => ((VRCContactSender)b).rootTransform = RelocatedRoot(((VRCContactSender)a).rootTransform, a, b)),
                [typeof(VRCContactReceiver)] = new(o => o.ContactReceivers, true, (a, b) => ((VRCContactReceiver)b).rootTransform = RelocatedRoot(((VRCContactReceiver)a).rootTransform, a, b)),
                [typeof(VRCRaycast)] = new(o => o.Raycasts, true),
                [typeof(VRCHeadChop)] = new(o => o.HeadChop, true),
                [typeof(AudioSource)] = new(o => o.AudioSources),
                [typeof(VRCSpatialAudioSource)] = new(o => o.AudioSources),
                [typeof(Light)] = new(o => o.Lights),
                [typeof(BoxCollider)] = new(o => o.UnityColliders, true),
                [typeof(SphereCollider)] = new(o => o.UnityColliders, true),
                [typeof(CapsuleCollider)] = new(o => o.UnityColliders, true),
                [typeof(MeshCollider)] = new(o => o.UnityColliders, true)
            };
            foreach (var type in new[] { typeof(VRCAimConstraint), typeof(VRCLookAtConstraint), typeof(VRCParentConstraint),
                typeof(VRCPositionConstraint), typeof(VRCRotationConstraint), typeof(VRCScaleConstraint) })
                rules.Add(type, new Rule(o => o.VRCConstraints, true, (a, b) =>
                {
                    var old = (VRCConstraintBase)a;
                    ((VRCConstraintBase)b).TargetTransform = old.TargetTransform != null ? old.TargetTransform : a.transform;
                }));
            return rules;
        }

        private static Transform RelocatedRoot(Transform root, Component source, Component destination) =>
            root == null || root == source.transform ? destination.transform : root;

        internal static bool CanCoexist(Type type, IEnumerable<Component> existing, bool animated, AvatarAnimationIndex animations)
        {
            if (!Rules.TryGetValue(type, out var rule)) return false;
            var components = existing.Where(c => c != null).ToArray();
            if (typeof(Renderer).IsAssignableFrom(type) && components.Any(c => c is Renderer)) return false;
            // Unity's native uniqueness rules are not all represented by managed
            // attributes. The closed contract table opts into multiplicity explicitly.
            if ((!rule.Multiple || animated) && components.Any(c => type.IsAssignableFrom(c.GetType()) || c.GetType().IsAssignableFrom(type))) return false;
            for (var current = type; current != null && typeof(Component).IsAssignableFrom(current); current = current.BaseType)
                if (Attribute.IsDefined(current, typeof(DisallowMultipleComponent), false) && components.Any(c => current.IsInstanceOfType(c))) return false;
            return !components.Any(c => (type.IsAssignableFrom(c.GetType()) || c.GetType().IsAssignableFrom(type)) && animations.ForObject(c).Count != 0);
        }

        internal static int Apply(BuildContext context, AvatarAnimationIndex animations, AvatarDependencyGraph references,
            StaticTransformProof proof, Func<Transform, string> pathFor)
        {
            var settings = context.GetState<ObjectOptimizationState>();
            if (!settings.HoistComponents || settings.Scope == null || references.UnsafeReason != null) return 0;
            var options = settings.ComponentHoisting ?? new ComponentHoistingOptions();
            var avatar = context.AvatarRootObject;
            if (animations.ControllerEntries.Keys.OfType<Animator>().Any(a => a != null && a.gameObject != avatar)) return 0;
            var relocation = new HierarchyRelocation(animations);
            var sharedOutputs = SharedSensorWriters(avatar);
            var movedTo = new HashSet<Transform>();
            var relocatedBindingTypes = new Dictionary<Transform, HashSet<Type>>();
            int changed = 0;
            foreach (var source in avatar.GetComponentsInChildren<Component>(true))
            {
                if (source == null || !Rules.TryGetValue(source.GetType(), out var rule) || !rule.Enabled(options) ||
                    settings.Protected(source) || sharedOutputs.Contains(source) || HasCompetingTransformWriter(source, proof.Mutations)) continue;
                if (source is VRCSpatialAudioSource) continue; // Moves with its AudioSource.
                var frame = source.transform; var parent = frame.parent;
                if (frame == avatar.transform || parent == null || !settings.Scope.Contains(frame) || !settings.Scope.Contains(parent) ||
                    settings.Protected(parent) || movedTo.Contains(frame) ||
                    !proof.CanSubstitute(frame, settings.Scope) ||
                    !relocation.RedundantGate(frame, parent) || frame.gameObject.layer != parent.gameObject.layer || frame.tag != parent.tag ||
                    GameObjectUtility.GetStaticEditorFlags(frame.gameObject) != GameObjectUtility.GetStaticEditorFlags(parent.gameObject)) continue;
                if (references.ReferenceOwners.ContainsKey(frame.gameObject)) continue;
                Action<Component> spatialSettings = null;
                // Contact callbacks and velocity outputs observe the owner's
                // actual origin. A near-identity frame must still use its contract.
                bool allowRounding = proof.AllowsRounding(NumericalOptimization.ComponentHoisting, frame, parent);
                bool identity = allowRounding ? StaticTransformProof.Near(StaticTransformProof.Local(frame), Matrix4x4.identity,
                    !(source is ContactBase)) : StaticTransformProof.ExactLocalIdentity(frame);
                if (!identity &&
                    (!settings.FoldStaticTransformsIntoComponents ||
                     !ComponentSpatialSettings.TryPlan(source, avatar.transform, animations, proof, out spatialSettings))) continue;
                // Native writer hosts (PhysBones and constraints targeting themselves)
                // are intentionally retained; explicit targets can still be hoisted.
                var companions = source is MeshRenderer ? frame.GetComponents<MeshFilter>().Cast<Component>().ToArray() :
                    source is AudioSource ? frame.GetComponents<VRCSpatialAudioSource>().Cast<Component>().ToArray() : Array.Empty<Component>();
                if (source is MeshRenderer && (companions.Length != 1 || parent.GetComponent<MeshFilter>() != null)) continue;
                var group = source is MeshRenderer ? companions.Append(source).ToArray() : companions.Prepend(source).ToArray();
                if (source is AudioSource && (frame.GetComponents<AudioSource>().Length != 1 ||
                    frame.GetComponents<Component>().Any(c => c != null && AudioOutputDependencies.IsModifier(c) && !group.Contains(c)) ||
                    parent.GetComponents<Component>().Any(c => c != null && AudioOutputDependencies.IsModifier(c)) ||
                    HasBehaviourPath(animations, parent))) continue;
                if (group.Any(settings.Protected) || frame.GetComponents<Component>().Any(c => c == null ||
                    !group.Contains(c) && group.Any(part => BuildFilterPass.Requires(c, part)))) continue;
                if (source is Renderer renderer)
                {
                    var meshSettings = context.GetState<MeshOptimizationState>();
                    if (renderer.HasPropertyBlock() || renderer.GetComponent<Cloth>() != null ||
                        meshSettings.Preserved.Contains(ObjectRegistry.GetReference(renderer)) || MeshOptimizationPass.InputProtected(meshSettings, renderer, out _) ||
                        meshSettings.Scope != null && meshSettings.Scope.Contains(frame) != meshSettings.Scope.Contains(parent)) continue;
                    if (renderer is SkinnedMeshRenderer skinned && HasExternalMorphInterface(skinned)) continue;
                }
                if ((source is VRCPhysBone || source is VRCPhysBoneCollider) && context.GetState<PhysBoneOptimizationState>().Scope != null &&
                    PhysBoneOptimizationPass.Protected(context.GetState<PhysBoneOptimizationState>(), source, settings)) continue;
                if (source is Collider && frame.GetComponent<Rigidbody>() != null) continue;
                var uses = group.SelectMany(animations.ForObject).Distinct().ToArray();
                if (uses.Any(u => !u.Complete || u.Immutable || u.Targets.Length != 1 || u.Objects().Count() != 1 ||
                    u.Roots.Length != 1 || u.Roots[0] != avatar.transform)) continue;
                if (!CanCoexist(source.GetType(), parent.GetComponents<Component>(), uses.Length != 0, animations)) continue;
                if (uses.Any(u => parent.GetComponents<Component>().Any(c => c != null && u.Binding.type.IsInstanceOfType(c))) ||
                    relocatedBindingTypes.TryGetValue(parent, out var writtenTypes) &&
                    writtenTypes.Any(t => group.Any(c => t.IsInstanceOfType(c)))) continue;
                if (group.Any(part => !SafeReaders(part, references, settings, group))) continue;
                bool unsafeKeys = animations.Bindings.Any(u => u.Immutable && (u.ObjectKeys ?? Array.Empty<ObjectReferenceKeyframe>()).Any(k => group.Contains(k.value)));
                if (unsafeKeys) continue;
                if (group.Any(part => MeshMergeAnalysis.MaskCoverage(avatar, frame, animations, part.GetType()) !=
                    MeshMergeAnalysis.MaskCoverage(avatar, parent, animations, part.GetType()))) continue;
                // Missing destination bindings are also occupied interfaces: adding a
                // component must not make one of them start writing at runtime.
                if (animations.Bindings.Any(u => u.Targets.Contains(parent) && u.Binding.type != null &&
                    group.Any(part => u.Binding.type.IsAssignableFrom(part.GetType()) || part.GetType().IsAssignableFrom(u.Binding.type)))) continue;
                string destinationPath = pathFor(parent);
                var replacements = new Dictionary<Object, Object>();
                foreach (var old in group)
                {
                    var replacement = parent.gameObject.AddComponent(old.GetType());
                    EditorUtility.CopySerialized(old, replacement);
                    if (Rules.TryGetValue(old.GetType(), out var contract)) contract.RetainTargets?.Invoke(old, replacement);
                    if (old == source) spatialSettings?.Invoke(replacement);
                    replacements.Add(old, replacement);
                }
                // Only build-owned scene components are edited. Unknown readers and
                // persistent ScriptableObjects were rejected by SafeReaders.
                foreach (var owner in group.SelectMany(c => references.ReferenceOwners.TryGetValue(c, out var readers) ? readers : Enumerable.Empty<Object>())
                    .Concat(replacements.Values).Distinct()) RemapReferences(owner, replacements);
                foreach (var use in animations.Bindings)
                {
                    if (use.ObjectKeys == null || !use.ObjectKeys.Any(k => k.value != null && replacements.ContainsKey(k.value))) continue;
                    use.Clip.SetObjectCurve(use.Binding, use.ObjectKeys.Select(k =>
                    { if (k.value != null && replacements.TryGetValue(k.value, out var mapped)) k.value = mapped; return k; }).ToArray());
                }
                foreach (var use in uses)
                {
                    var binding = use.Binding; binding.path = destinationPath;
                    if (binding.isPPtrCurve) { var keys = use.Clip.GetObjectCurve(use.Binding); use.Clip.SetObjectCurve(use.Binding, null); use.Clip.SetObjectCurve(binding, keys); }
                    else { use.Clip.SetFloatCurve(use.Binding, null); use.Clip.SetFloatCurve(binding, use.Curve); }
                }
                if (!relocatedBindingTypes.TryGetValue(parent, out var newTypes)) relocatedBindingTypes.Add(parent, newTypes = new());
                newTypes.UnionWith(uses.Select(u => u.Binding.type));
                if (source is AudioSource)
                    foreach (var behaviour in ParameterSpecialization.Behaviours(animations.Controllers.SelectMany(c => c.AllReachableNodes())).Distinct())
                        nadena.dev.ndmf.animator.VRChatPlatformAnimatorBindings.Instance.RemapPathsInStateBehaviour(behaviour, p =>
                            AnimationUsage.Resolve(avatar.transform, avatar.transform, p ?? "", animations.Paths).Contains(frame) ? destinationPath : p);
                string componentName = source.GetType().Name;
                // Other moves in this batch may read the component just moved.
                // Retarget the snapshot's owner identities as well as serialized
                // references, without rebuilding the entire avatar index per component.
                foreach (var readers in references.ReferenceOwners.Values)
                    foreach (var replacement in replacements)
                        if (readers.Remove(replacement.Key)) readers.Add(replacement.Value);
                foreach (var replacement in replacements)
                    if (references.ReferenceOwners.TryGetValue(replacement.Key, out var readers))
                    { references.ReferenceOwners.Remove(replacement.Key); references.ReferenceOwners[replacement.Value] = readers; }
                proof.NumericalPolicy?.InheritNumericalConstraints(parent, new[] { frame });
                foreach (var old in group.Reverse())
                {
                    ObjectRegistry.RegisterReplacedObject(old, replacements[old]);
                    if (old is Renderer r) context.GetState<MeshOptimizationState>().RecordPermissions((Renderer)replacements[old], context.GetState<MeshOptimizationState>().PermissionsFor(r));
                    Object.DestroyImmediate(old);
                }
                context.GetState<OptimizationBuildReport>().Messages.Add("Hoisted " + componentName + " from '" + frame.name + "' to '" + parent.name + "'; runtime targets and bindings retained.");
                movedTo.Add(parent); changed += group.Length;
            }
            return changed;
        }

        internal static bool HasExternalMorphInterface(SkinnedMeshRenderer renderer) => renderer.sharedMesh != null &&
            Enumerable.Range(0, renderer.sharedMesh.blendShapeCount).Any(i => MmdMorphNames.All.Contains(renderer.sharedMesh.GetBlendShapeName(i)));

        private static HashSet<Component> SharedSensorWriters(GameObject avatar)
        {
            // Native outputs are assignments, not necessarily commutative blends.
            // Reordering two writers of the same output can change the last value.
            var shared = AvatarParameterInputs.Sensors(avatar).GroupBy(s => s.Name, StringComparer.Ordinal)
                .Where(g => g.Select(s => s.Source).Distinct().Skip(1).Any()).SelectMany(g => g.Select(s => s.Source)).ToHashSet();
            var materialOutputs = avatar.GetComponentsInChildren<VRCContactReceiver>(true)
                .SelectMany(c => ContactOutputs.Targets(c).SelectMany(r => ContactOutputs.Properties(c)
                    .Select(p => (Receiver: c, Renderer: r, Property: p))));
            foreach (var output in materialOutputs.GroupBy(o => (o.Renderer, o.Property)))
                if (output.Select(o => o.Receiver).Distinct().Skip(1).Any()) shared.UnionWith(output.Select(o => o.Receiver));
            return shared;
        }

        private static bool HasCompetingTransformWriter(Component component, TransformMutationAnalysis mutations)
        {
            if (component is VRCConstraintBase constraint)
            {
                var target = constraint.TargetTransform != null ? constraint.TargetTransform : component.transform;
                return mutations.WrittenByOther(target, component);
            }
            if (component is VRCRaycast raycast) return mutations.WrittenByOther(raycast.ResultTransform, component);
            if (component is VRCHeadChop chop)
                return (chop.targetBones ?? Array.Empty<VRCHeadChop.HeadChopBone>())
                    .Any(b => b?.transform != null && mutations.WrittenByOther(b.transform, component));
            return false;
        }

        private static bool HasBehaviourPath(AvatarAnimationIndex animations, Transform destination)
        {
            // A Play Audio path can currently point at an object without an audio
            // source. Relocation must not accidentally make that missing interface live.
            bool observed = false;
            foreach (var behaviour in ParameterSpecialization.Behaviours(animations.Controllers.SelectMany(c => c.AllReachableNodes())).Distinct())
                nadena.dev.ndmf.animator.VRChatPlatformAnimatorBindings.Instance.RemapPathsInStateBehaviour(behaviour, path =>
                {
                    observed |= AnimationUsage.Resolve(animations.Avatar.transform, animations.Avatar.transform, path ?? "", animations.Paths).Contains(destination);
                    return path;
                });
            return observed;
        }

        private static bool SafeReaders(Component component, AvatarDependencyGraph graph, ObjectOptimizationState settings, Component[] group)
        {
            if (!graph.ReferenceOwners.TryGetValue(component, out var readers)) return true;
            return readers.All(r => group.Contains(r) || r is Component c && !(c is INDMFEditorOnly) && !(c is VRC.SDKBase.IEditorOnly) &&
                !settings.Protected(c) && (Rules.ContainsKey(c.GetType()) || c is VRCAvatarDescriptor || c is LODGroup));
        }

        private static void RemapReferences(Object owner, IReadOnlyDictionary<Object, Object> replacements)
        {
            using var serialized = new SerializedObject(owner);
            var property = serialized.GetIterator(); bool changed = false;
            while (property.Next(true))
                if (property.propertyType == SerializedPropertyType.ObjectReference && property.propertyPath != "m_GameObject" &&
                    property.objectReferenceValue != null && replacements.TryGetValue(property.objectReferenceValue, out var replacement))
                { property.objectReferenceValue = replacement; changed = true; }
            if (changed) serialized.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
