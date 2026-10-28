using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.BlendShapeOptimization;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using VRC.SDK3.Avatars.Components;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.Meshes
{
    // Design reference: AAO 1.9.20 AutoMergeSkinnedMesh. Independently implemented
    // grouping using the shared animation/reference index; see CREDITS.md.
    internal sealed class MeshMergeCandidate
    {
        internal SkinnedMeshRenderer Renderer;
        internal bool Pinned;
        internal readonly HashSet<string> PinReasons = new(StringComparer.Ordinal);
        internal void Pin(string reason) { Pinned = true; PinReasons.Add(reason); }
        internal Bounds Bounds;
        internal string MaskCoverage;
        internal readonly List<MeshAnimationSignal> Visibility = new();
        internal readonly Dictionary<string, MeshAnimationSignal> Properties = new(StringComparer.Ordinal);
        internal AvatarAnimationIndex.BindingUse[] Bindings;
        internal readonly HashSet<string> ReservedShapeNames = new(StringComparer.Ordinal);
    }

    internal sealed class MeshAnimationSignal : IEquatable<MeshAnimationSignal>
    {
        internal bool Initial;
        internal string MaskCoverage;
        internal AvatarAnimationIndex.BindingUse[] Writers;
        internal bool SameWriter(AvatarAnimationIndex.BindingUse a, AvatarAnimationIndex.BindingUse b) =>
            a.Clip == b.Clip && a.Binding.isDiscreteCurve == b.Binding.isDiscreteCurve &&
            SameCurve(a.Curve, b.Curve);
        internal static bool SameCurve(AnimationCurve a, AnimationCurve b) => a != null && b != null &&
            a.preWrapMode == b.preWrapMode && a.postWrapMode == b.postWrapMode && a.keys.SequenceEqual(b.keys);
        public bool Equals(MeshAnimationSignal other) => other != null && Initial == other.Initial && MaskCoverage == other.MaskCoverage &&
            Writers.Length == other.Writers.Length && Writers.All(a => other.Writers.Any(b => SameWriter(a, b)));
        public override bool Equals(object obj) => obj is MeshAnimationSignal other && Equals(other);
        public override int GetHashCode() => Initial.GetHashCode();
    }

    internal static class MeshMergeAnalysis
    {
        internal static MeshMergeCandidate Inspect(SkinnedMeshRenderer renderer, GameObject avatar,
            AvatarAnimationIndex animations, AvatarDependencyGraph references, ShaderPropertyUsage shaders, out string reason,
            MeshMergePolicy policy = null)
        {
            bool makeRigid = renderer != null && policy?.ConvertRigidMeshes == true && RigidMeshConversion.Unweighted(renderer.sharedMesh);
            reason = makeRigid ? SkinnedMeshAssembler.ValidateGeometry(renderer, renderer.sharedMesh) : SkinnedMeshAssembler.Validate(renderer);
            if (reason != null) return null;
            var result = new MeshMergeCandidate { Renderer = renderer, Bounds = StoredBounds(renderer),
                MaskCoverage = MaskCoverage(avatar, renderer.transform, animations, typeof(Renderer)) };
            string Unsupported()
            {
                if (renderer.rootBone == null && policy?.AllowCoordinateSpaceChanges != true) return "no explicit root bone";
                if (renderer.updateWhenOffscreen && policy?.ExpandBounds != true) return "per-frame bounds depend on the original skinned vertex set";
                if (renderer.HasPropertyBlock()) return "a material property block requires separate drawing";
                if (renderer.GetComponent<Cloth>() != null) return "cloth simulation owns this mesh";
                if (renderer.GetComponents<Component>().Any(c => c != null && c != renderer && Build.BuildFilterPass.Requires(c, renderer)))
                    return "a component requires this renderer";
                if (renderer.lightProbeUsage != LightProbeUsage.Off || renderer.reflectionProbeUsage != ReflectionProbeUsage.Off)
                    if (renderer.probeAnchor == null) return "implicit probe sampling depends on renderer bounds";
                if (renderer.lightProbeUsage == LightProbeUsage.UseProxyVolume && renderer.lightProbeProxyVolumeOverride == null)
                    return "implicit light probe proxy volume";
                if (renderer.lightmapIndex >= 0 || renderer.realtimeLightmapIndex >= 0) return "lightmapped renderer";
                if (references.ReferenceOwners.TryGetValue(renderer, out var readers))
                {
                    if (readers.Any(r => !(r is VRCAvatarDescriptor))) return "renderer readers: " + string.Join(", ",
                        readers.Where(r => !(r is VRCAvatarDescriptor)).Select(r => r.GetType().Name + " on '" + r.name + "'"));
                    if (readers.Count > 0) result.Pin("Descriptor renderer reference");
                    foreach (var descriptor in readers.OfType<VRCAvatarDescriptor>())
                    {
                        if (descriptor.VisemeSkinnedMesh == renderer)
                            foreach (var name in (descriptor.VisemeBlendShapes ?? Array.Empty<string>()).Append(descriptor.MouthOpenBlendShapeName))
                                if (!string.IsNullOrEmpty(name) && renderer.sharedMesh.GetBlendShapeIndex(name) < 0) result.ReservedShapeNames.Add(name);
                        if (descriptor.customEyeLookSettings.eyelidsSkinnedMesh == renderer &&
                            (descriptor.customEyeLookSettings.eyelidsBlendshapes ?? Array.Empty<int>()).Any(i => i >= renderer.sharedMesh.blendShapeCount && i >= 0))
                            return "invalid Descriptor eyelid indices could become valid after appending morphs";
                    }
                }
                // External MMD clips address the original morph names. Keep this renderer
                // as the destination; two independent external interfaces do not merge.
                var mesh = renderer.sharedMesh;
                var externalNames = Enumerable.Range(0, mesh.blendShapeCount).Select(mesh.GetBlendShapeName).Where(MmdMorphNames.All.Contains).ToArray();
                if (externalNames.Length > 0) result.Pin("MMD names: " + string.Join(", ", externalNames.Take(5)));
                string shaderIssue = ShaderIssue(renderer.sharedMaterials, shaders, addSkinning: makeRigid,
                    allowCoordinateSpaceChanges: policy?.AllowCoordinateSpaceChanges == true);
                if (shaderIssue != null) return shaderIssue;
                result.Bindings = animations.ForObject(renderer).ToArray();
                foreach (var use in result.Bindings)
                {
                    if (!use.Complete || use.Targets.Length != 1 || use.Objects().Count() != 1) return "ambiguous renderer animation binding";
                    bool shape = use.Binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal);
                    if (use.Immutable)
                    {
                        if (!shape) return "immutable renderer animation";
                        result.Pin("immutable animation: " + use.Clip.Name + " / " + use.Binding.propertyName);
                    }
                    if (use.Binding.isPPtrCurve || !shape && use.Binding.propertyName != "m_Enabled" &&
                        !use.Binding.propertyName.StartsWith("material.", StringComparison.Ordinal)) return "unsupported renderer animation: " + use.Binding.propertyName;
                    if (!use.Immutable && (use.Roots.Length != 1 || use.Roots[0] != avatar.transform))
                        return "renderer animation has a relative or shared controller root";
                    // Reserve missing names on the destination so appended morphs cannot
                    // activate a formerly dead track. A mutable source track can instead
                    // be removed when its renderer is merged. Mesh swaps and external
                    // renderer readers are rejected above; immutable clips still pin paths.
                    if (shape && mesh.GetBlendShapeIndex(use.Binding.propertyName.Substring("blendShape.".Length)) < 0)
                    {
                        result.ReservedShapeNames.Add(use.Binding.propertyName.Substring("blendShape.".Length));
                    }
                }
                for (var transform = renderer.transform; transform != null; transform = transform.parent)
                {
                    var issue = AddVisibility(result, transform.gameObject, "m_IsActive", transform.gameObject.activeSelf, avatar, animations);
                    if (issue != null) return issue;
                    if (transform == avatar.transform) break;
                }
                var enabledIssue = AddVisibility(result, renderer, "m_Enabled", renderer.enabled, avatar, animations);
                if (enabledIssue != null) return enabledIssue;
                foreach (var group in result.Bindings.Where(u => u.Binding.propertyName.StartsWith("material.", StringComparison.Ordinal))
                    .GroupBy(u => u.Binding.propertyName))
                    result.Properties.Add(group.Key, new MeshAnimationSignal { Writers = group.ToArray() });
                return null;
            }
            reason = Unsupported();
            return reason == null ? result : null;
        }

        internal static string ShaderIssue(IEnumerable<Material> materials, ShaderPropertyUsage shaders, bool addSkinning = false,
            bool allowCoordinateSpaceChanges = false)
        {
            foreach (var material in materials.Distinct())
            {
                if (material == null || material.shader == null) return "missing material or shader";
                if (material.renderQueue > 2500) return "transparent sorting requires separate renderers";
                if (!allowCoordinateSpaceChanges && !string.Equals(material.GetTag("DisableBatching", false, "False"), "False", StringComparison.OrdinalIgnoreCase))
                    return "shader disables batching";
                var proof = shaders.Get(material.shader);
                if (proof.Uncertain != null) return "unverified shader inputs: " + proof.Uncertain;
                var semantics = new[] { "SV_VertexID", "SV_PrimitiveID", "BLENDINDICES" }.Concat(addSkinning ? new[] { "BLENDWEIGHT" } : Array.Empty<string>());
                if (semantics.Any(semantic => proof.Tokens.Any(t => string.Equals(t, semantic, StringComparison.OrdinalIgnoreCase)) || proof.MayUse(semantic)))
                    return "shader may depend on vertex, primitive or skinning indices/weights";
            }
            return null;
        }

        private static string AddVisibility(MeshMergeCandidate candidate, Object obj, string property, bool initial, GameObject avatar, AvatarAnimationIndex animations)
        {
            var writers = animations.ForObject(obj).Where(u => u.Binding.propertyName == property).ToArray();
            // A shared clip evaluated by independent Animator roots can carry the
            // same curve with different weights or states at each resolved target.
            if (writers.Any(u => !u.Complete || u.Targets.Length != 1 || u.Objects().Count() != 1 ||
                u.Immutable || u.Curve == null)) return "unverified activation animation";
            if (initial && writers.Length == 0) return null;
            var transform = obj is GameObject gameObject ? gameObject.transform : ((Component)obj).transform;
            var signal = new MeshAnimationSignal { Initial = initial, Writers = writers,
                MaskCoverage = writers.Length == 0 ? null : MaskCoverage(avatar, transform, animations,
                    obj is GameObject ? typeof(GameObject) : typeof(Renderer)) };
            if (!candidate.Visibility.Contains(signal)) candidate.Visibility.Add(signal);
            return null;
        }

        internal static bool Compatible(MeshMergeCandidate a, MeshMergeCandidate b, MeshMergePolicy policy = null)
            => Incompatibility(a, b, policy) == null;

        internal static string Incompatibility(MeshMergeCandidate a, MeshMergeCandidate b, MeshMergePolicy policy = null)
        {
            var x = a.Renderer; var y = b.Renderer;
            if (a.Pinned && b.Pinned) return "independent external morph interfaces";
            if (a.MaskCoverage != b.MaskCoverage) return "different Animator mask coverage";
            if (a.Visibility.Count != b.Visibility.Count || !a.Visibility.All(s => b.Visibility.Contains(s))) return "different activation or enabled signals";
            if (a.Properties.Count != b.Properties.Count || !a.Properties.All(p => b.Properties.TryGetValue(p.Key, out var signal) && p.Value.Equals(signal)))
                return "different material-property animation signals";
            if (a.Properties.Count != 0 && !MaterialDefaultsMatch(x.sharedMaterials, y.sharedMaterials, policy?.AllowMaterialSlotReordering == true))
                return "different animated material defaults";
            if (!(policy?.CoordinatesMatch(a, b) ?? MeshMergePolicy.CoordinateRoot(x) == MeshMergePolicy.CoordinateRoot(y)))
                return "different coordinate systems or independent animated scale signs";
            if (policy?.ExpandBounds != true && !a.Bounds.Equals(b.Bounds)) return "different bounds with expansion disabled";
            if (x.sharedMesh.HasVertexAttribute(VertexAttribute.Normal) != y.sharedMesh.HasVertexAttribute(VertexAttribute.Normal)) return "different normal attribute presence";
            if (x.gameObject.layer != y.gameObject.layer || x.renderingLayerMask != y.renderingLayerMask) return "different rendering layers";
            if (x.shadowCastingMode != y.shadowCastingMode || x.receiveShadows != y.receiveShadows || x.staticShadowCaster != y.staticShadowCaster)
                return "different shadow settings";
            if (x.lightProbeUsage != y.lightProbeUsage || x.reflectionProbeUsage != y.reflectionProbeUsage ||
                x.lightProbeUsage == LightProbeUsage.UseProxyVolume && x.lightProbeProxyVolumeOverride != y.lightProbeProxyVolumeOverride)
                return "different probe modes or proxy volumes";
            if (!(policy?.ProbeAnchorsMatch(x, y) ?? (x.probeAnchor == y.probeAnchor ||
                !MeshMergePolicy.UsesProbeAnchor(x) && !MeshMergePolicy.UsesProbeAnchor(y)))) return "different lighting anchors";
            if (x.allowOcclusionWhenDynamic != y.allowOcclusionWhenDynamic || x.updateWhenOffscreen != y.updateWhenOffscreen || x.forceRenderingOff != y.forceRenderingOff)
                return "different dynamic culling settings";
            if (x.quality != y.quality) return "different skinning quality";
            if (x.skinnedMotionVectors != y.skinnedMotionVectors || x.motionVectorGenerationMode != y.motionVectorGenerationMode) return "different motion-vector settings";
            if (x.sortingLayerID != y.sortingLayerID || x.sortingOrder != y.sortingOrder || x.rendererPriority != y.rendererPriority) return "different sorting settings";
            return null;
        }

        private static bool MaterialDefaultsMatch(Material[] first, Material[] second, bool reorder)
        {
            if (!reorder) return MaterialRuns(first).SequenceEqual(MaterialRuns(second));
            // Keep the default-material interface even under the explicit draw-order
            // relaxation. Different material sets still require a separate proof.
            return first.Length > 0 && second.Length > 0 && first[0] == second[0] && new HashSet<Material>(first).SetEquals(second);
        }

        private static IEnumerable<Material> MaterialRuns(IReadOnlyList<Material> materials)
        {
            // Repeating the same material in adjacent slots does not introduce a
            // different animation default. Keep the order of distinct materials;
            // the assembler still preserves each submesh's geometry and draw order.
            for (int i = 0; i < materials.Count; i++)
                if (i == 0 || materials[i] != materials[i - 1]) yield return materials[i];
        }

        internal static Bounds StoredBounds(SkinnedMeshRenderer renderer)
        {
            // With updateWhenOffscreen enabled, localBounds may return the current posed
            // bounds. Group by the serialized culling volume, not this frame's geometry.
            using var serialized = new SerializedObject(renderer);
            var property = serialized.FindProperty("m_AABB");
            return property != null && property.propertyType == SerializedPropertyType.Bounds ? property.boundsValue : renderer.localBounds;
        }

        private static string MaskCoverage(GameObject avatar, Transform target, AvatarAnimationIndex animations, Type bindingType)
        {
            var profiles = new List<string>();
            foreach (var entry in animations.ControllerEntries)
            {
                // Transform-only controllers cannot filter renderer curves supplied by
                // another controller. Keep every mask within a relevant controller:
                // Write Defaults can affect bindings absent from the current layer.
                if (!animations.MayAnimateType(entry.Value, bindingType)) continue;
                var owner = entry.Key is Animator animator ? animator.transform : avatar.transform;
                foreach (var mask in entry.Value.Layers.Select(l => l.AvatarMask).Where(m => m != null).Distinct())
                {
                    if (!target.IsChildOf(owner)) { profiles.Add("outside"); continue; }
                    Transform nearest = null; float weight = 0; bool ambiguous = false;
                    foreach (var element in mask.Elements)
                    {
                        var resolved = AnimationUsage.Resolve(avatar.transform, owner, element.Key, animations.Paths);
                        foreach (var ancestor in resolved.Where(target.IsChildOf))
                        {
                            ambiguous |= resolved.Count != 1;
                            if (nearest == null || ancestor.IsChildOf(nearest)) { nearest = ancestor; weight = element.Value; }
                        }
                    }
                    // Equal resolved weights in every layer have identical filtering,
                    // even when inherited from different ancestors. Ambiguous paths
                    // never make two different targets equivalent.
                    profiles.Add(ambiguous ? "ambiguous:" + target.GetInstanceID() : nearest == null ? "absent" :
                        weight.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                }
            }
            return string.Join(";", profiles);
        }

        internal static List<List<MeshMergeCandidate>> Group(IEnumerable<MeshMergeCandidate> candidates, bool mergeRenderers, MeshMergePolicy policy = null)
        {
            var groups = new List<List<MeshMergeCandidate>>();
            // Pin the external interface first. Its morph names, indices and object remain.
            foreach (var candidate in candidates.OrderByDescending(c => c.Pinned))
            {
                var group = mergeRenderers ? groups.FirstOrDefault(g => g.All(c => Compatible(c, candidate, policy))) : null;
                if (group == null) groups.Add(group = new());
                group.Add(candidate);
            }
            return groups;
        }
    }
}
