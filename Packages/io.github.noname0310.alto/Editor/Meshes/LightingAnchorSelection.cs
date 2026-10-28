using System;
using System.Collections.Generic;
using System.Linq;
using Noname.AvatarTools.BlendShapeOptimization;
using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.Meshes
{
    internal static class LightingAnchorSelection
    {
        internal static Transform Resolve(GameObject avatar, IReadOnlyList<Renderer> inputs, Transform explicitAnchor)
        {
            if (explicitAnchor != null)
            {
                if (!explicitAnchor.IsChildOf(avatar.transform) || ALTOPlugin.IsEditorOnly(explicitAnchor))
                    throw new InvalidOperationException("The lighting anchor override must be a retained transform inside this avatar.");
                return explicitAnchor;
            }

            var common = inputs.Select(r => r.probeAnchor)
                .Where(a => a != null && a.IsChildOf(avatar.transform) && !ALTOPlugin.IsEditorOnly(a))
                .GroupBy(a => a).OrderByDescending(g => g.Count())
                .ThenBy(g => AnimationUtility.CalculateTransformPath(g.Key, avatar.transform), StringComparer.Ordinal)
                .Select(g => g.Key).FirstOrDefault();
            if (common != null) return common;
            if (inputs.Count == 0) return null;

            // A generated anchor deliberately replaces bounds-dependent sampling.
            // Keep it attached to the avatar rather than to a removable renderer.
            var bounds = inputs[0].bounds;
            foreach (var input in inputs.Skip(1)) bounds.Encapsulate(input.bounds);
            var center = bounds.center;
            if (!Analysis.MotionAnalysis.Finite(center.x) || !Analysis.MotionAnalysis.Finite(center.y) || !Analysis.MotionAnalysis.Finite(center.z))
                throw new InvalidOperationException("Cannot select a lighting anchor from non-finite renderer bounds.");
            var anchor = new GameObject(GameObjectUtility.GetUniqueNameForSibling(avatar.transform, "Merged Lighting Anchor")).transform;
            anchor.SetParent(avatar.transform, false);
            anchor.position = center;
            return anchor;
        }
    }
}
