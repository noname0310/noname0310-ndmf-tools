using System.Collections.Generic;
using Meshia.MeshSimplification.Ndmf;
using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.MeshReduction
{
    [InitializeOnLoad]
    internal static class CascadingReductionConflicts
    {
        static CascadingReductionConflicts() => MeshReductionBackend.OtherReductionTargets.Add(Targets);

        private static IEnumerable<Renderer> Targets(GameObject avatar)
        {
            foreach (var component in avatar.GetComponentsInChildren<MeshiaCascadingAvatarMeshSimplifier>(true))
            foreach (var entry in component.Entries)
            {
                if (entry == null || !entry.Enabled) continue;
                var obj = entry.RendererObjectReference?.Get(component);
                var renderer = obj != null ? obj.GetComponent<Renderer>() : null;
                if (renderer != null) yield return renderer;
            }
        }
    }
}
