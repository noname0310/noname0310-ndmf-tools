using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.Build
{
    internal sealed class MaterialSlotPlan
    {
        internal Renderer Renderer;
        internal Mesh SourceMesh;
        internal Material[] Materials;
        internal int[] SlotMap, KeptSubMeshes;
        internal readonly HashSet<MaterialExclusion> MatchedRules = new();
        internal bool HasChanges => SlotMap.Any(i => i < 0);
        internal bool RemovesGeometry => SourceMesh != null && KeptSubMeshes.Length != SourceMesh.subMeshCount;

        internal static bool SameMaterial(Material current, Material selected) => current != null && selected != null &&
            (current == selected || ObjectRegistry.GetReference(current).Object == ObjectRegistry.GetReference(selected).Object);

        internal static MaterialSlotPlan Create(Renderer renderer, IEnumerable<MaterialExclusion> rules)
        {
            Mesh mesh = renderer is SkinnedMeshRenderer skin ? skin.sharedMesh :
                renderer is MeshRenderer && renderer.TryGetComponent<MeshFilter>(out var filter) ? filter.sharedMesh : null;
            var materials = renderer.sharedMaterials;
            int submeshes = mesh != null ? mesh.subMeshCount : 0;
            var passes = new HashSet<int>();
            var geometry = new HashSet<int>();
            var matched = new HashSet<MaterialExclusion>();
            foreach (var rule in rules)
            {
                if (rule.Renderer != null && rule.Renderer != renderer) continue;
                for (int slot = 0; slot < materials.Length; slot++)
                {
                    if (rule.Slot >= 0 && rule.Slot != slot) continue;
                    if (rule.Material != null && !SameMaterial(materials[slot], rule.Material)) continue;
                    matched.Add(rule);
                    if (rule.Mode == MaterialRemovalMode.RemoveMaterialPass) passes.Add(slot);
                    else
                    {
                        if (submeshes == 0) throw new InvalidOperationException("Cannot remove submesh geometry from a renderer without a mesh: " + renderer.name);
                        geometry.Add(Math.Min(slot, submeshes - 1));
                    }
                }
            }
            if (passes.Count > 0 || geometry.Count > 0)
            {
                if (!(renderer is SkinnedMeshRenderer) && !(renderer is MeshRenderer))
                    throw new InvalidOperationException("Material exclusions support SkinnedMeshRenderer and MeshRenderer: " + renderer.name);
                if (materials.Length < submeshes)
                    throw new InvalidOperationException("Material exclusions require at least one material slot per submesh: " + renderer.name);
                if (passes.Any(slot => slot < submeshes && !geometry.Contains(slot)))
                    throw new InvalidOperationException("Remove Material Pass only removes extra slots beyond the mesh's submesh count. " +
                        "Use Remove SubMesh Geometry to remove primary geometry: " + renderer.name);
            }
            int next = 0;
            var plan = new MaterialSlotPlan
            {
                Renderer = renderer, SourceMesh = mesh, Materials = materials,
                KeptSubMeshes = Enumerable.Range(0, submeshes).Where(i => !geometry.Contains(i)).ToArray(),
                SlotMap = Enumerable.Range(0, materials.Length).Select(slot => passes.Contains(slot) ||
                    geometry.Contains(Math.Min(slot, submeshes - 1)) ? -1 : next++).ToArray()
            };
            plan.MatchedRules.UnionWith(matched);
            return plan;
        }

        internal void Apply(BuildContext context)
        {
            if (RemovesGeometry)
            {
                var mesh = Object.Instantiate(SourceMesh);
                mesh.name = SourceMesh.name + " (Filtered)";
                mesh.SetSubMeshes(KeptSubMeshes.Select(SourceMesh.GetSubMesh).ToArray(), MeshUpdateFlags.DontRecalculateBounds);
                ObjectRegistry.RegisterReplacedObject(SourceMesh, mesh);
                context.AssetSaver.SaveAsset(mesh);
                if (Renderer is SkinnedMeshRenderer skin) skin.sharedMesh = mesh;
                else Renderer.GetComponent<MeshFilter>().sharedMesh = mesh;
            }
            Renderer.sharedMaterials = Materials.Where((_, index) => SlotMap[index] >= 0).ToArray();
        }
    }
}
