using System;
using System.Collections.Generic;
using System.Linq;
using Meshia.MeshSimplification;
using Meshia.MeshSimplification.Ndmf;
using UnityEditor;
using UnityEngine;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Noname.ALTO.Meshia.Tests")]

namespace Noname.AvatarTools.MeshReduction
{
    [InitializeOnLoad]
    internal static class MeshiaReductionBackend
    {
        static MeshiaReductionBackend()
        {
            MeshReductionBackend.Simplify = Simplify;
            MeshReductionBackend.Validate = Validate;
            MeshReductionBackend.OtherReductionTargets.Add(OtherTargets);
        }

        internal static IEnumerable<Renderer> OtherTargets(GameObject avatar)
        {
            foreach (var component in avatar.GetComponentsInChildren<MeshiaMeshSimplifier>(true))
                if (component.enabled && component.TryGetComponent<Renderer>(out var renderer)) yield return renderer;
        }

        internal static string Validate(Renderer renderer, Mesh mesh)
        {
            if (!mesh.isReadable) return "mesh is not readable by Meshia";
            if (renderer.GetComponent<Cloth>() != null) return "Cloth requires the original topology";
            if (renderer is MeshRenderer basic && basic.additionalVertexStreams != null) return "additional vertex streams require the original topology";
            if (mesh.subMeshCount > 32) return "Meshia supports at most 32 submeshes";
            if (Enumerable.Range(0, mesh.subMeshCount).Any(s => mesh.GetTopology(s) != MeshTopology.Triangles)) return "non-triangle topology";
            // Meshia 3.x copies vertex-stream bone data; variable-length skinning above four influences
            // is stored separately by Unity and would otherwise be truncated.
            using (var counts = mesh.GetBonesPerVertex())
                for (int i = 0; i < counts.Length; i++) if (counts[i] > 4) return "more than four skinning influences per vertex";
            return null;
        }

        internal static void Simplify(IReadOnlyList<ReductionRequest> requests, bool preserveBorders)
        {
            var batch = new List<(Mesh, MeshSimplificationTarget, MeshSimplifierOptions, Mesh)>();
            foreach (var request in requests)
            {
                var options = MeshSimplifierOptions.Default;
                options.PreserveBorderEdges = preserveBorders;
                // Meshia's Smart Link compares undeformed positions and surface attributes,
                // but not skinning palettes/weights or morph deltas. Coincident islands can
                // belong to opposite sides of an avatar, even with the same bone Transform
                // and different bind poses. Restrict deformation to existing mesh edges.
                if (request.Renderer is SkinnedMeshRenderer || request.Source.blendShapeCount > 0)
                    options.EnableSmartLink = false;
                request.Result = new Mesh { name = request.Source.name + " (Reduced)" };
                var target = new MeshSimplificationTarget { Kind = MeshSimplificationTargetKind.AbsoluteTriangleCount, Value = request.Target };
                // In 3.2.0 only the single-mesh API guards against adding BlendShapes to a zero-vertex result.
                // Use that path for morphs; meshes without morphs can safely use the parallel batch API.
                if (request.Source.blendShapeCount > 0) MeshSimplifier.Simplify(request.Source, target, options, request.Result);
                else batch.Add((request.Source, target, options, request.Result));
            }
            if (batch.Count > 0) MeshSimplifier.SimplifyBatch(batch);
        }
    }
}
