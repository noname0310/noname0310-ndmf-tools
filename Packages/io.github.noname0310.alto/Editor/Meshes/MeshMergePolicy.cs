using System;
using System.Collections.Generic;
using System.Linq;
using Noname.AvatarTools.Analysis;
using UnityEngine;
using UnityEngine.Rendering;

namespace Noname.AvatarTools.Meshes
{
    internal sealed class MeshMergePolicy
    {
        internal bool ExpandBounds, AllowCoordinateSpaceChanges, ConvertRigidMeshes, AllowMaterialSlotReordering;
        internal TransformMutationAnalysis Mutations;

        internal static Transform CoordinateRoot(SkinnedMeshRenderer renderer) => renderer.rootBone != null ? renderer.rootBone : renderer.transform;

        internal static bool UsesProbeAnchor(Renderer renderer) =>
            renderer.lightProbeUsage != LightProbeUsage.Off || renderer.reflectionProbeUsage != ReflectionProbeUsage.Off;

        internal bool ProbeAnchorsMatch(Renderer first, Renderer second)
        {
            if (!UsesProbeAnchor(first) && !UsesProbeAnchor(second)) return true;
            if (first.probeAnchor == second.probeAnchor) return true;
            if (first.probeAnchor == null || second.probeAnchor == null || Mutations == null || Mutations.Uncertain != null) return false;
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
            // Distinct helper roots can still describe the same coordinate system.
            // Prove this through fixed local transforms, not a one-frame world-matrix comparison.
            var first = Canonical(x); var second = Canonical(y);
            if (first.Root == second.Root && first.Offset.Equals(second.Offset)) return true;
            if (!AllowCoordinateSpaceChanges || !ExpandBounds) return false;
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

        internal static HashSet<SkinnedMeshRenderer> ReversedInputs(IReadOnlyList<MeshMergeCandidate> group)
        {
            bool destination = CoordinateRoot(group[0].Renderer).localToWorldMatrix.determinant < 0;
            return group.Where(c => (CoordinateRoot(c.Renderer).localToWorldMatrix.determinant < 0) != destination)
                .Select(c => c.Renderer).ToHashSet();
        }

        internal static Bounds UnionBounds(IReadOnlyList<MeshMergeCandidate> group)
        {
            var root = CoordinateRoot(group[0].Renderer); var result = group[0].Bounds;
            foreach (var candidate in group.Skip(1))
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
