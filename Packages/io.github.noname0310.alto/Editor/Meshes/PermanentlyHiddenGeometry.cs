using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.BlendShapeOptimization;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.ObjectOptimization;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.Meshes
{
    internal static class PermanentlyHiddenGeometry
    {
        internal static int Apply(GameObject avatar, IEnumerable<SkinnedMeshRenderer> renderers,
            MeshOptimizationState settings, ObjectOptimizationState objects, AvatarAnimationIndex animations,
            AvatarDependencyGraph references, ShaderPropertyUsage shaders, BuildTarget target,
            Action<Mesh> save, ICollection<string> messages, BlendShapeProvenance shapeProvenance = null,
            Func<Transform, bool> allowInitialChanges = null)
        {
            if (!settings.RemoveHiddenGeometry || animations.UnsafeReason != null || references.UnsafeReason != null) return 0;
            int removed = 0;
            NonFiniteSkinningProof nonFinite = null;
            bool animatedNaN = animations.Bindings.Any(u => u.Binding.type == typeof(Transform) &&
                u.Binding.propertyName.StartsWith("m_LocalScale.", StringComparison.Ordinal) && u.Curve != null &&
                u.Curve.length == 1 && float.IsNaN(u.Curve.keys[0].value));
            var fullReaders = BlendShapeEffectAnalysis.FindFullVertexReaders(avatar, animations);
            bool visibilityAffectsAnimation = avatar.GetComponentsInChildren<Animator>(true).Any(a =>
                a.cullingMode != AnimatorCullingMode.AlwaysAnimate || animations.Writes(a, "m_CullingMode"));
            foreach (var renderer in renderers)
            {
                if (renderer == null || !settings.Scope.Contains(renderer.transform) || objects.Protected(renderer) ||
                    settings.Preserved.Contains(ObjectRegistry.GetReference(renderer)) ||
                    MeshOptimizationPass.InputProtected(settings, renderer, out _) || fullReaders.Contains(renderer) ||
                    renderer.GetComponent<Cloth>() != null || renderer.HasPropertyBlock()) continue;
                var mesh = renderer.sharedMesh;
                if (mesh == null || !mesh.isReadable || mesh.subMeshCount == 0) continue;
                if (references.ReferenceOwners.TryGetValue(mesh, out var meshReaders) && meshReaders.Any(r => !(r is Renderer))) continue;
                var swaps = new Dictionary<int, HashSet<Material>>(); bool unsupported = false;
                foreach (var use in animations.ForObject(renderer))
                {
                    if (!use.Complete || use.Targets.Length != 1 || use.Objects().Count() != 1) { unsupported = true; break; }
                    if (!use.Binding.isPPtrCurve) continue;
                    if (!MaterialFilterPass.TrySlot(use.Binding.propertyName, out int slot) || use.ObjectKeys == null)
                    { unsupported = true; break; }
                    if (!swaps.TryGetValue(slot, out var materials)) swaps.Add(slot, materials = new());
                    foreach (var key in use.ObjectKeys)
                        if (key.value is Material material) materials.Add(material); else unsupported = true;
                }
                if (unsupported) continue;
                HiddenTriangleProof proof;
                try { proof = new HiddenTriangleProof(mesh); }
                catch (Exception) { continue; }
                var masks = proof.Candidates.Select(c => c == null ? null : (bool[])c.Clone()).ToArray();
                var poisoned = new bool[mesh.vertexCount];
                try
                {
                    if (animatedNaN || renderer.bones.Any(b => b != null &&
                        float.IsNaN(b.localScale.x) && float.IsNaN(b.localScale.y) && float.IsNaN(b.localScale.z)))
                    {
                        nonFinite ??= new NonFiniteSkinningProof(avatar, animations);
                        poisoned = nonFinite.Vertices(renderer, allowInitialChanges?.Invoke(renderer.transform) == true);
                    }
                }
                catch (Exception) { /* An unsupported native skinning layout has no nonfinite certificate. */ }
                bool hasNonFinite = false;
                var nonFiniteTriangles = masks.Select(c => c == null ? null : new bool[c.Length]).ToArray();
                for (int slot = 0; slot < masks.Length; slot++)
                {
                    if (masks[slot] == null) continue;
                    for (int triangle = 0; triangle < masks[slot].Length; triangle++)
                    {
                        bool hidden = true;
                        for (int corner = 0; corner < 3; corner++) hidden &= poisoned[proof.Indices[slot][triangle * 3 + corner]];
                        if (!hidden) continue;
                        masks[slot][triangle] = nonFiniteTriangles[slot][triangle] = hasNonFinite = true;
                    }
                }
                if (proof.CandidateCount == 0 && !hasNonFinite) continue;
                var initial = renderer.sharedMaterials;
                for (int slot = 0; slot < Math.Max(mesh.subMeshCount, initial.Length); slot++)
                {
                    int submesh = Math.Min(slot, mesh.subMeshCount - 1);
                    if (masks[submesh] == null || !masks[submesh].Any(v => v)) continue;
                    if (!swaps.TryGetValue(slot, out var materials)) materials = new();
                    materials.Add(slot < initial.Length ? initial[slot] : null);
                    foreach (var material in materials)
                    {
                        // Vertex numbering is retained. Primitive numbering changes
                        // in the edited submesh and therefore needs an absence proof.
                        if (material == null || shaders.MeshInputs.Issue(material, renderer, animations,
                            new ShaderInputQuery(new ShaderInputQuery.Semantic("SV_PrimitiveID")), target) != null)
                        { Array.Clear(masks[submesh], 0, masks[submesh].Length); break; }
                        var inputs = shaders.MeshInputs.PositionInputs(material, renderer, animations, target, hasNonFinite);
                        if (inputs.Issue != null)
                        {
                            Array.Clear(masks[submesh], 0, masks[submesh].Length);
                            messages.Add("Hidden geometry retained on '" + renderer.name + "': " + inputs.Issue + "."); break;
                        }
                        for (int triangle = 0; triangle < masks[submesh].Length; triangle++)
                            if (masks[submesh][triangle] && !proof.Equivalent(submesh, triangle, inputs) &&
                                !(nonFiniteTriangles[submesh][triangle] && inputs.NonFinitePositionIsHidden)) masks[submesh][triangle] = false;
                        if (hasNonFinite && inputs.NonFiniteIssue != null)
                            messages.Add("Nonfinite hidden geometry retained on '" + renderer.name + "': " + inputs.NonFiniteIssue + ".");
                    }
                }
                int count = masks.Sum(mask => mask?.Count(v => v) ?? 0);
                if (count > 0 && visibilityAffectsAnimation)
                {
                    // An empty draw can stop participating in native visibility
                    // tracking. Materials may differ in shadow/depth participation,
                    // so retain one primitive in each submesh that would be emptied.
                    int retained = 0;
                    foreach (var mask in masks)
                    {
                        if (mask == null || mask.Length == 0 || !mask.All(value => value)) continue;
                        mask[0] = false; count--; retained++;
                    }
                    if (retained != 0) messages.Add("Hidden geometry retained " + retained + " submesh primitives on '" +
                        renderer.name + "' for Animator visibility culling.");
                }
                if (count == 0) continue;
                var copy = Object.Instantiate(mesh); copy.name = mesh.name + " (Hidden geometry removed)";
                try
                {
                    for (int slot = 0; slot < masks.Length; slot++)
                    {
                        var mask = masks[slot]; if (mask == null || !mask.Any(v => v)) continue;
                        var kept = new List<int>(); int baseVertex = checked((int)mesh.GetBaseVertex(slot));
                        for (int triangle = 0; triangle < mask.Length; triangle++)
                            if (!mask[triangle]) for (int corner = 0; corner < 3; corner++)
                                kept.Add(proof.Indices[slot][triangle * 3 + corner] - baseVertex);
                        copy.SetIndices(kept, MeshTopology.Triangles, slot, false, baseVertex);
                    }
                    save?.Invoke(copy); shapeProvenance?.RecordMeshRewrite(renderer, mesh, copy);
                    renderer.sharedMesh = copy; removed += count;
                }
                catch { Object.DestroyImmediate(copy); throw; }
            }
            if (removed > 0) messages.Add("Removed " + removed + " permanently nondrawing triangles with all-pass vertex-output proofs; original vertex IDs and bounds retained.");
            return removed;
        }
    }
}
