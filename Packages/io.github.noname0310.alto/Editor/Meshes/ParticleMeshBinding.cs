using System.Linq;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.ObjectOptimization;
using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.Meshes
{
    // A material-scoped triangle emitter observes one surface, not the whole
    // renderer. Keep that draw range isolated and relocate its native reference.
    internal sealed class ParticleMeshBinding
    {
        internal ParticleSystem Particles;
        internal int MaterialSlot;

        internal static bool TryCapture(ParticleSystem particles, SkinnedMeshRenderer source,
            AvatarAnimationIndex animations, out ParticleMeshBinding binding)
        {
            binding = null;
            var shape = particles.shape;
            if (shape.shapeType != ParticleSystemShapeType.SkinnedMeshRenderer || shape.skinnedMeshRenderer != source ||
                shape.meshShapeType != ParticleSystemMeshShapeType.Triangle || !shape.useMeshMaterialIndex ||
                shape.meshMaterialIndex < 0 || shape.meshMaterialIndex >= source.sharedMesh.subMeshCount ||
                shape.meshMaterialIndex >= source.sharedMaterials.Length) return false;
            if (animations != null && animations.ForObject(particles).Any(u => !u.Complete ||
                u.Binding.propertyName.StartsWith("ShapeModule.", System.StringComparison.Ordinal))) return false;
            using var serialized = new SerializedObject(particles);
            if (SerializedReferences.Properties(serialized).Where(p => p.objectReferenceValue == source)
                .Any(p => p.propertyPath != "ShapeModule.m_SkinnedMeshRenderer")) return false;
            binding = new ParticleMeshBinding { Particles = particles, MaterialSlot = shape.meshMaterialIndex };
            return true;
        }

        internal void Apply(SkinnedMeshRenderer destination, int[] materialSlots)
        {
            var shape = Particles.shape;
            shape.skinnedMeshRenderer = destination;
            shape.meshMaterialIndex = materialSlots[MaterialSlot];
        }
    }
}
