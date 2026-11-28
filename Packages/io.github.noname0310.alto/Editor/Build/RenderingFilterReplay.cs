using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using Noname.AvatarTools.BlendShapeOptimization;
using UnityEngine;

namespace Noname.AvatarTools.Build
{
    // An authored index belongs to an input interface, not to a numeric slot on
    // every later mesh. Remember those interfaces and replay only on registered
    // replacements/copies that still expose them. Identity-based material rules
    // can be evaluated directly against current outputs at every boundary.
    internal sealed class RenderingFilterReplay
    {
        private sealed class Layout
        {
            internal ObjectReference Mesh;
            internal string Topology;
            internal ObjectReference[] Materials;
        }

        private readonly Dictionary<object, ObjectReference> targets = new();
        private readonly Dictionary<(object, ObjectReference), List<Layout>> inputs = new();
        private readonly Dictionary<Mesh, string> topology = new();
        internal bool MaterialsApplied, ShapesApplied;

        internal void Capture(object rule, Renderer target)
        {
            if (target == null) return;
            targets[rule] = ObjectRegistry.GetReference(target);
            // Retain the authored interface as well as the generated interface at
            // first application. A later generator can instantiate either source.
            if (rule is MaterialExclusion material && material.Slot < 0) return;
            Admit(rule, target, repeated: false, materials: rule is MaterialExclusion);
        }

        private bool MatchesTarget(object rule, Renderer renderer)
        {
            if (!targets.TryGetValue(rule, out var target))
            {
                var authored = rule is MaterialExclusion material ? material.Renderer : ((BlendShapeMeshExclusion)rule).Renderer;
                if (ReferenceEquals(authored, null)) return true;
                if (authored == null) return false;
                targets[rule] = target = ObjectRegistry.GetReference(authored);
            }
            return Equals(target, ObjectRegistry.GetReference(renderer));
        }

        internal void BeginBoundary() => topology.Clear();

        internal IEnumerable<MaterialExclusion> MaterialRules(Renderer renderer, IReadOnlyList<MaterialExclusion> rules)
        {
            foreach (var rule in rules)
            {
                if (!MatchesTarget(rule, renderer)) continue;
                if (rule.Slot >= 0 && !Admit(rule, renderer, MaterialsApplied, materials: true)) continue;
                yield return new MaterialExclusion { Renderer = renderer, Material = rule.Material, Slot = rule.Slot, Mode = rule.Mode };
            }
        }

        internal IEnumerable<BlendShapeMeshExclusion> ShapeRules(SkinnedMeshRenderer renderer,
            IReadOnlyList<BlendShapeMeshExclusion> rules)
        {
            foreach (var rule in rules)
            {
                if (!MatchesTarget(rule, renderer) || !Admit(rule, renderer, ShapesApplied, materials: false)) continue;
                if (ShapesApplied && (renderer.sharedMesh == null || rule.BlendShapes.Any(n => renderer.sharedMesh.GetBlendShapeIndex(n) < 0))) continue;
                yield return new BlendShapeMeshExclusion { Renderer = renderer, BlendShapes = rule.BlendShapes,
                    DisplacementThreshold = rule.DisplacementThreshold };
            }
        }

        private bool Admit(object rule, Renderer renderer, bool repeated, bool materials)
        {
            var key = (rule, ObjectRegistry.GetReference(renderer));
            var mesh = MeshReduction.MeshReductionPass.MeshOf(renderer);
            var currentMaterials = materials ? renderer.sharedMaterials.Select(m => m == null ? null : ObjectRegistry.GetReference(m)).ToArray() : null;
            if (!inputs.TryGetValue(key, out var known))
            {
                // A global source-slot rule can also select a newly generated
                // renderer's initial layout. A targeted rule requires its origin.
                inputs.Add(key, known = new());
                repeated = false;
            }
            if (!repeated)
            {
                known.Add(new Layout { Mesh = mesh == null ? null : ObjectRegistry.GetReference(mesh),
                    Topology = Signature(mesh), Materials = currentMaterials });
                return true;
            }
            foreach (var source in known)
            {
                if (!Equals(source.Mesh, mesh == null ? null : ObjectRegistry.GetReference(mesh))) continue;
                if (materials && !source.Materials.SequenceEqual(currentMaterials)) continue;
                if (source.Topology != null && source.Topology == Signature(mesh)) return true;
            }
            return false;
        }

        private string Signature(Mesh mesh)
        {
            if (mesh == null) return "no-mesh";
            if (topology.TryGetValue(mesh, out var signature)) return signature;
            if (!mesh.isReadable) return topology[mesh] = null;
            signature = ContentHash.Compute(writer =>
            {
                writer.Write(mesh.vertexCount); writer.Write(mesh.subMeshCount);
                for (int slot = 0; slot < mesh.subMeshCount; slot++)
                {
                    writer.Write((int)mesh.GetTopology(slot));
                    var indices = mesh.GetIndices(slot);
                    writer.Write(indices.Length);
                    foreach (int index in indices) writer.Write(index);
                }
            });
            return topology[mesh] = signature;
        }

        internal bool HasTarget(object rule, IEnumerable<Renderer> renderers) => renderers.Any(r => MatchesTarget(rule, r));
    }
}
