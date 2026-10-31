using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Noname.AvatarTools.BlendShapeOptimization
{
    // Region filtering removes indices before unused vertex data is compacted.
    // Only vertices still indexed by a submesh contribute rendered geometry.
    internal sealed class BlendShapeEffectAnalysis
    {
        private readonly Mesh mesh;
        private readonly int[] vertices;
        private readonly ShapeDelta buffer;

        internal BlendShapeEffectAnalysis(Mesh mesh, bool observeAllVertices = false)
        {
            this.mesh = mesh;
            var used = new HashSet<int>();
            if (observeAllVertices) vertices = Enumerable.Range(0, mesh.vertexCount).ToArray();
            else
            {
                for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
                    used.UnionWith(mesh.GetIndices(subMesh, applyBaseVertex: true));
                vertices = used.OrderBy(v => v).ToArray();
            }
            buffer = new ShapeDelta(mesh.vertexCount);
        }

        internal static HashSet<SkinnedMeshRenderer> FindFullVertexReaders(GameObject avatar)
        {
            var result = new HashSet<SkinnedMeshRenderer>();
            var inspected = new HashSet<Object>();
            void Read(Object owner)
            {
                if (owner == null || !inspected.Add(owner)) return;
                using var serialized = new SerializedObject(owner);
                var property = serialized.GetIterator();
                while (property.Next(true))
                {
                    if (property.propertyType != SerializedPropertyType.ObjectReference ||
                        property.propertyPath == "m_GameObject" || property.propertyPath == "m_Script") continue;
                    var reference = property.objectReferenceValue;
                    if (reference is SkinnedMeshRenderer renderer) result.Add(renderer);
                    else if (reference is ScriptableObject asset) Read(asset);
                }
            }
            // A renderer reader can consume unindexed vertices (for example a particle
            // emitter). Unknown readers keep the entire vertex buffer observable.
            // Descriptor morph names/indices are protected separately; our settings
            // describe transformations rather than consume deformed vertex output.
            foreach (var component in avatar.GetComponentsInChildren<Component>(true))
                if (component != null && !(component is Transform) && !(component is VRCAvatarDescriptor) &&
                    !(component is ALTOOptimizationComponent) && !(component is ALTOTraceAndOptimizeBlendShapes) &&
                    !(component is BlendShapeOptimizationExclude)) Read(component);
            return result;
        }

        internal bool HasEffect(int shape)
        {
            for (int frame = 0; frame < mesh.GetBlendShapeFrameCount(shape); frame++)
            {
                if (!AnimationUsage.Finite(mesh.GetBlendShapeFrameWeight(shape, frame))) return true;
                buffer.Read(mesh, shape, frame);
                if (HasEffect(buffer)) return true;
            }
            return false;
        }

        internal bool HasEffect(ShapeDelta delta)
        {
            foreach (int vertex in vertices)
                if (Nonzero(delta.Vertices[vertex]) || Nonzero(delta.Normals[vertex]) || Nonzero(delta.Tangents[vertex]))
                    return true;
            return false;
        }

        // Vector3 equality and squared magnitudes use tolerances or can underflow.
        // Exact scalar comparison retains tiny changes and non-finite deltas.
        private static bool Nonzero(Vector3 delta) => delta.x != 0 || delta.y != 0 || delta.z != 0;
    }
}
