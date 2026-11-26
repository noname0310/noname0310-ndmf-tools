using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.ObjectOptimization;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.Contact.Components;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.Meshes
{
    internal static class StaticSkinningOptimization
    {
        // Unlike mesh assembly, this changes neither vertex/index/weight buffers,
        // renderer identity, morph indices, material slots nor drawing coordinates.
        // Shader source availability and batching eligibility are not prerequisites.
        internal static int Apply(GameObject avatar, MeshOptimizationState settings, ObjectOptimizationState objects,
            AvatarAnimationIndex animations, AvatarDependencyGraph references, StaticTransformProof proof, Action<Mesh> save,
            BlendShapeOptimization.BlendShapeProvenance shapeProvenance = null)
        {
            if (!settings.RebindStaticBones || settings.Scope == null || proof.Mutations.Uncertain != null) return 0;
            int changed = 0;
            foreach (var renderer in avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (!settings.Scope.Contains(renderer.transform) || renderer.sharedMesh == null || !renderer.sharedMesh.isReadable ||
                    renderer.GetComponent<Cloth>() != null || settings.Preserved.Contains(ObjectRegistry.GetReference(renderer)) ||
                    objects.Protected(renderer) || MeshOptimizationPass.InputProtected(settings, renderer, out _)) continue;
                if (references.ReferenceOwners.TryGetValue(renderer, out var readers) &&
                    readers.Any(r => !(r is VRCAvatarDescriptor) && !(r is VRCContactReceiver) &&
                        !IsSurfaceParticleReader(r, renderer))) continue;
                if (animations.ForObject(renderer).Any(u => !u.Complete ||
                    u.Binding.propertyName == "m_Mesh" || u.Binding.propertyName.StartsWith("m_Bones", StringComparison.Ordinal))) continue;
                var original = renderer.sharedMesh;
                if (renderer.bones.Length != original.bindposes.Length) continue;
                var contributors = new HashSet<Transform> { renderer.transform };
                var plan = StaticSkinningPlan.Create(renderer, bone => proof.CanSubstitute(bone, settings.Scope), bone =>
                {
                    contributors.Add(bone); contributors.Add(bone.parent);
                    return proof.AllowsRounding(NumericalOptimization.Skinning, renderer.transform, bone, bone.parent);
                });
                if (plan.ReboundEntries == 0) continue;
                proof.NumericalPolicy?.InheritNumericalConstraints(renderer.transform, contributors);
                var mesh = Object.Instantiate(original);
                mesh.name = original.name;
                mesh.bindposes = plan.Poses;
                // A copied mesh retains all vertex streams, all blendshape frames,
                // submesh descriptors and bone weights. Do not run palette compaction.
                save?.Invoke(mesh);
                ObjectRegistry.RegisterReplacedObject(original, mesh);
                shapeProvenance?.RecordMeshRewrite(renderer, original, mesh);
                var weights = Enumerable.Range(0, original.blendShapeCount).Select(renderer.GetBlendShapeWeight).ToArray();
                renderer.sharedMesh = null;
                renderer.sharedMesh = mesh;
                renderer.bones = plan.Bones;
                for (int i = 0; i < weights.Length; i++) renderer.SetBlendShapeWeight(i, weights[i]);
                changed += plan.ReboundEntries;
                settings.Changed = true;
            }
            return changed;
        }

        private static bool IsSurfaceParticleReader(Object reader, SkinnedMeshRenderer renderer)
        {
            if (!(reader is ParticleSystem particles) || particles.shape.skinnedMeshRenderer != renderer) return false;
            // Skin rebinding preserves every vertex stream, index, weight and sampled
            // surface position. Other uses of a Renderer need a separate contract.
            using var serialized = new UnityEditor.SerializedObject(particles);
            return SerializedReferences.Properties(serialized).Where(p => p.objectReferenceValue == renderer)
                .All(p => p.propertyPath == "ShapeModule.m_SkinnedMeshRenderer");
        }
    }
}
