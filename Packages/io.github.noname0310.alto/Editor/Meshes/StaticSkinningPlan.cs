using System;
using UnityEngine;

namespace Noname.AvatarTools.Meshes
{
    // AAO 1.9.20 ConfigureMergeBone/MergeBoneProcessor are design references.
    // This independent implementation substitutes only skinning palette entries;
    // hierarchy, root bones, culling, vertex coordinates and morph deltas stay intact.
    internal sealed class StaticSkinningPlan
    {
        internal Transform[] Bones;
        internal Matrix4x4[] Poses;
        internal int ReboundEntries;

        internal static StaticSkinningPlan Create(SkinnedMeshRenderer renderer, Func<Transform, bool> canSubstitute)
        {
            var result = new StaticSkinningPlan { Bones = renderer.bones, Poses = renderer.sharedMesh.bindposes };
            int count = Math.Min(result.Bones.Length, result.Poses.Length);
            for (int i = 0; i < count; i++)
            {
                var original = result.Bones[i]; var bone = original; var pose = result.Poses[i];
                while (bone != null && bone.parent != null && canSubstitute(bone))
                {
                    // Multiplication works even for collapsed/nonuniformly scaled bones.
                    // Inverting the current world pose would lose their bind-time geometry.
                    var next = Matrix4x4.TRS(bone.localPosition, bone.localRotation, bone.localScale) * pose;
                    bool finite = true;
                    for (int element = 0; element < 16; element++)
                        finite &= !float.IsNaN(next[element]) && !float.IsInfinity(next[element]);
                    if (!finite) break;
                    pose = next; bone = bone.parent;
                }
                if (bone == original) continue;
                result.Bones[i] = bone; result.Poses[i] = pose; result.ReboundEntries++;
            }
            return result;
        }
    }
}
