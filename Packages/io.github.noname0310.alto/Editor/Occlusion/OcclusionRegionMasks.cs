using System;
using System.Collections.Generic;
using System.Linq;
using Noname.AvatarTools.Meshes;
using UnityEngine;

namespace Noname.AvatarTools.Occlusion
{
    internal sealed class OcclusionRegionMasks : BlendShapeRegionMasks
    {
        internal new static OcclusionRegionMasks Create(Mesh mesh, IEnumerable<string> names, float displacementThreshold)
        {
            var masks = BlendShapeRegionMasks.Create(mesh, names, displacementThreshold);
            return new OcclusionRegionMasks { Indices = masks.Indices, SubMeshTriangleCounts = masks.SubMeshTriangleCounts, Regions = masks.Regions };
        }

        internal bool[] RemovalMask(IReadOnlyList<uint[]> poses, out string[] removedRegions)
        {
            if (poses == null || poses.Count == 0) throw new ArgumentException("At least one visibility pose is required.", nameof(poses));
            int count = Indices.Length / 3;
            if (poses.Any(p => p == null || p.Length != count)) throw new ArgumentException("Visibility results do not match the triangle masks.", nameof(poses));
            // Bit 1: at least one covered pixel. Bit 2: at least one visible pixel.
            // Missing raster coverage in any pose is uncertainty, never occlusion.
            removedRegions = Regions.Where(r => r.Triangles.Length > 0 &&
                r.Triangles.All(t => poses.All(p => p[t] == 1))).Select(r => r.Name).ToArray();
            var selected = new HashSet<string>(removedRegions, StringComparer.Ordinal); var remove = new bool[count];
            foreach (var region in Regions.Where(r => selected.Contains(r.Name)))
                foreach (int triangle in region.Triangles) remove[triangle] = true;
            return remove;
        }
    }
}
