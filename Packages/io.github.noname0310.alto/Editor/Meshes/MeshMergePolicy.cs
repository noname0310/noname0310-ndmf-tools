using System;
using System.Collections.Generic;
using System.Linq;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.Build;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Noname.AvatarTools.Meshes
{
    internal sealed class MeshMergePolicy
    {
        internal bool ExpandBounds, AllowCoordinateSpaceChanges, ConvertRigidMeshes, AllowMaterialSlotReordering, AllowTransparentSortingChanges, AllowShaderIndexChanges, AllowAnimatedMaterialDefaultChanges;
        internal BuildTarget? Target;
        internal bool PreserveVertexIds;
        internal bool PreserveParticleRandomSequence;
        internal TransformMutationAnalysis Mutations;
        internal Func<Renderer, MeshOptimizationPermissions> PermissionsFor;
        internal OptimizationPolicy NumericalPolicy;

        internal bool AllowsRounding(params Transform[] targets) =>
            StaticTransformProof.AllowsRounding(NumericalPolicy, NumericalOptimization.MeshMerging, targets);

        internal bool AllowsCoordinates(Renderer renderer) => PermissionsFor != null
            ? (PermissionsFor(renderer) & MeshOptimizationPermissions.CoordinateSpaceChanges) != 0 : AllowCoordinateSpaceChanges;
        internal bool ConvertsRigid(Renderer renderer) => StaticTransformProof.AllowsRounding(NumericalPolicy, NumericalOptimization.Skinning, renderer.transform) && (PermissionsFor != null
            ? (PermissionsFor(renderer) & MeshOptimizationPermissions.RigidConversion) != 0 : ConvertRigidMeshes);
        internal bool ReordersSlots(Renderer renderer) => PermissionsFor != null
            ? (PermissionsFor(renderer) & MeshOptimizationPermissions.MaterialSlotReordering) != 0 : AllowMaterialSlotReordering;
        internal bool AllowsTransparentSorting(Renderer renderer) => PermissionsFor != null
            ? (PermissionsFor(renderer) & MeshOptimizationPermissions.TransparentSortingChanges) != 0 : AllowTransparentSortingChanges;
        internal bool AllowsShaderIndexChanges(Renderer renderer) => PermissionsFor != null
            ? (PermissionsFor(renderer) & MeshOptimizationPermissions.ShaderIndexChanges) != 0 : AllowShaderIndexChanges;

        internal bool AllowsAnimatedMaterialDefaultChanges(Renderer renderer) => PermissionsFor != null
            ? (PermissionsFor(renderer) & MeshOptimizationPermissions.AnimatedMaterialDefaultChanges) != 0 : AllowAnimatedMaterialDefaultChanges;

        internal static Transform CoordinateRoot(SkinnedMeshRenderer renderer) => renderer.rootBone != null ? renderer.rootBone : renderer.transform;

        internal static Transform DestinationCoordinateRoot(IReadOnlyList<MeshMergeCandidate> group) =>
            CoordinateRoot((group.FirstOrDefault(c => c.ParticleBindings.Count != 0) ?? group[0]).Renderer);

        internal bool ExactCoordinatesMatch(SkinnedMeshRenderer first, SkinnedMeshRenderer second)
        {
            var x = CoordinateRoot(first); var y = CoordinateRoot(second);
            if (x == y) return true;
            if (Mutations == null || Mutations.Uncertain != null) return false;
            if (!AllowsRounding(first.transform, second.transform, x, y)) return StructuralFramesMatch(x, y);
            var a = Canonical(x); var b = Canonical(y);
            return a.Root == b.Root && a.Offset.Equals(b.Offset);
        }

        internal bool ParticleDistributionsMatch(SkinnedMeshRenderer first, SkinnedMeshRenderer second)
        {
            if (ExactCoordinatesMatch(first, second)) return true;
            if (Mutations == null || Mutations.Uncertain != null) return false;
            var a = CoordinateRoot(first); var b = CoordinateRoot(second);
            var ancestors = new HashSet<Transform>();
            for (var current = a; current != null; current = current.parent) ancestors.Add(current);
            var common = b;
            while (common != null && !ancestors.Contains(common)) common = common.parent;
            if (common == null) return false;
            bool allowRounding = AllowsRounding(first.transform, second.transform, a, b);
            // Rotating an isotropic random direction changes its realization,
            // not its distribution. Fixed positive uniform scales cancel during
            // direction normalization. Retain uncertain/nonuniform scale paths;
            // common ancestor transforms act on the same distribution on both sides.
            foreach (var start in new[] { a, b })
                for (var current = start; current != common; current = current.parent)
                    if (Mutations.MayChangeScale(current) || !SpatialFrameProof.UniformPositiveScale(current.localScale,
                        allowRounding && AllowsRounding(current))) return false;
            return true;
        }

        internal static bool UsesProbeAnchor(Renderer renderer) =>
            renderer.lightProbeUsage != LightProbeUsage.Off || renderer.reflectionProbeUsage != ReflectionProbeUsage.Off;

        internal bool ProbeAnchorsMatch(Renderer first, Renderer second)
        {
            if (!UsesProbeAnchor(first) && !UsesProbeAnchor(second)) return true;
            if (first.probeAnchor == second.probeAnchor) return true;
            if (first.probeAnchor == null || second.probeAnchor == null || Mutations == null || Mutations.Uncertain != null) return false;
            if (!AllowsRounding(first.transform, second.transform, first.probeAnchor, second.probeAnchor))
                return StructuralFramesMatch(first.probeAnchor, second.probeAnchor);
            // Probe sampling uses position, not the anchor's rotation or scale.
            // Compare positions relative to the same runtime-driven ancestor;
            // equal positions in just the current pose are insufficient.
            var x = Canonical(first.probeAnchor); var y = Canonical(second.probeAnchor);
            var a = x.Offset.GetColumn(3); var b = y.Offset.GetColumn(3);
            return x.Root == y.Root && a.Equals(b) && MotionAnalysis.Finite(a.x) && MotionAnalysis.Finite(a.y) && MotionAnalysis.Finite(a.z);
        }

        internal bool CoordinatesMatch(MeshMergeCandidate a, MeshMergeCandidate b)
        {
            var x = CoordinateRoot(a.Renderer); var y = CoordinateRoot(b.Renderer);
            if (x == y) return true;
            if (Mutations == null || Mutations.Uncertain != null) return false;
            if (!Invertible(x.localToWorldMatrix) || !Invertible(y.localToWorldMatrix)) return false;
            if (!AllowsRounding(a.Renderer.transform, b.Renderer.transform, x, y)) return StructuralFramesMatch(x, y);
            // Distinct helper roots can still describe the same coordinate system.
            // Prove this through fixed local transforms, not a one-frame world-matrix comparison.
            var first = Canonical(x); var second = Canonical(y);
            if (first.Root == second.Root && first.Offset.Equals(second.Offset)) return true;
            if (!AllowsCoordinates(a.Renderer) || !AllowsCoordinates(b.Renderer) || !ExpandBounds) return false;
            // A fixed winding reversal can preserve mirrored culling. Independent
            // animated scale signs cannot be represented by one renderer's front face.
            return CoordinateScaleWriters(a.Renderer).SetEquals(CoordinateScaleWriters(b.Renderer));
        }

        private (Transform Root, Matrix4x4 Offset) Canonical(Transform root)
        {
            var offset = Matrix4x4.identity;
            while (root.parent != null && !Mutations.Mutable.Contains(root))
            {
                offset = Matrix4x4.TRS(root.localPosition, root.localRotation, root.localScale) * offset;
                root = root.parent;
            }
            return (root, offset);
        }

        // Equal rounded products do not prove equal native operation order.
        // Strict mode compares the fixed authored operations, skipping only
        // exact identity frames; runtime-driven nodes must be the same object.
        private bool StructuralFramesMatch(Transform first, Transform second)
        {
            while (first != second)
            {
                while (first != null && first.parent != null && !Mutations.Mutable.Contains(first) && StaticTransformProof.ExactLocalIdentity(first))
                    first = first.parent;
                while (second != null && second.parent != null && !Mutations.Mutable.Contains(second) && StaticTransformProof.ExactLocalIdentity(second))
                    second = second.parent;
                if (first == second) return true;
                if (first == null || second == null || Mutations.Mutable.Contains(first) || Mutations.Mutable.Contains(second) ||
                    !StaticTransformProof.ExactChannels(first.localPosition, second.localPosition) ||
                    !StaticTransformProof.ExactChannels(first.localRotation, second.localRotation) ||
                    !StaticTransformProof.ExactChannels(first.localScale, second.localScale)) return false;
                first = first.parent; second = second.parent;
            }
            return true;
        }

        internal bool RendererFramesMatch(Renderer first, Renderer second)
        {
            if (first.transform == second.transform) return true;
            if (Mutations == null || Mutations.Uncertain != null) return false;
            if (!AllowsRounding(first.transform, second.transform)) return StructuralFramesMatch(first.transform, second.transform);
            var a = Canonical(first.transform); var b = Canonical(second.transform);
            if (a.Root != b.Root) return false;
            if (a.Offset.Equals(b.Offset)) return true;
            // Import/parenting roundoff can produce distinct encodings of the
            // same frame. Compare relative transforms so very small scales or
            // large translations cannot hide a meaningful relative difference.
            return AllowsRounding(first.transform, second.transform) && Invertible(a.Offset) && Invertible(b.Offset) &&
                StaticTransformProof.Near(a.Offset.inverse * b.Offset, Matrix4x4.identity) &&
                StaticTransformProof.Near(b.Offset.inverse * a.Offset, Matrix4x4.identity);
        }

        private HashSet<Transform> CoordinateScaleWriters(SkinnedMeshRenderer renderer)
        {
            var writers = new HashSet<Transform>();
            var bones = renderer.bones;
            for (var current = CoordinateRoot(renderer); current != null; current = current.parent)
            {
                if (!Mutations.MayChangeScale(current)) continue;
                // A nonnegative native multiplier may still collapse the coordinate
                // root. Only disregard it when the entire input geometry collapses
                // with that frame; bones outside it require the original root.
                if (Mutations.MayReverseScale(current) || bones.Length == 0 ||
                    RigidMeshConversion.Unweighted(renderer.sharedMesh) || bones.Any(b => b == null || !b.IsChildOf(current)))
                    writers.Add(current);
            }
            return writers;
        }

        internal static bool Invertible(Matrix4x4 matrix)
        {
            if (matrix.determinant == 0) return false;
            var inverse = matrix.inverse;
            for (int i = 0; i < 16; i++) if (!MotionAnalysis.Finite(matrix[i]) || !MotionAnalysis.Finite(inverse[i])) return false;
            return true;
        }

        internal static HashSet<SkinnedMeshRenderer> ReversedInputs(IReadOnlyList<MeshMergeCandidate> group, Transform destinationRoot = null)
        {
            bool destination = (destinationRoot != null ? destinationRoot : CoordinateRoot(group[0].Renderer)).localToWorldMatrix.determinant < 0;
            return group.Where(c => (CoordinateRoot(c.Renderer).localToWorldMatrix.determinant < 0) != destination)
                .Select(c => c.Renderer).ToHashSet();
        }

        internal static Bounds UnionBounds(IReadOnlyList<MeshMergeCandidate> group, Transform destinationRoot = null)
        {
            var root = destinationRoot != null ? destinationRoot : CoordinateRoot(group[0].Renderer);
            // A native surface reader may require another input's root bone,
            // independently of the pinned destination object and morph interface.
            var initial = group.First(c => CoordinateRoot(c.Renderer) == root);
            var result = initial.Bounds;
            foreach (var candidate in group.Where(c => c != initial))
            {
                var source = CoordinateRoot(candidate.Renderer);
                if (source == root) { result.Encapsulate(candidate.Bounds); continue; }
                if (!Invertible(root.localToWorldMatrix)) throw new InvalidOperationException("Cannot expand bounds in a collapsed coordinate system.");
                var matrix = root.worldToLocalMatrix * source.localToWorldMatrix;
                var bounds = candidate.Bounds;
                for (int corner = 0; corner < 8; corner++)
                {
                    var local = bounds.center + Vector3.Scale(bounds.extents,
                        new Vector3((corner & 1) == 0 ? -1 : 1, (corner & 2) == 0 ? -1 : 1, (corner & 4) == 0 ? -1 : 1));
                    var point = matrix.MultiplyPoint3x4(local);
                    if (!MotionAnalysis.Finite(point.x) || !MotionAnalysis.Finite(point.y) || !MotionAnalysis.Finite(point.z))
                        throw new InvalidOperationException("A merged culling volume is non-finite.");
                    result.Encapsulate(point);
                }
            }
            return result;
        }
    }
}
