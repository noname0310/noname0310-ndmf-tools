using System.Collections.Generic;
using System.Linq;
using Noname.AvatarTools.Analysis;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.Contact.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;
using Noname.AvatarTools.ObjectOptimization;

namespace Noname.AvatarTools.BlendShapeOptimization
{
    // Region filtering removes indices before unused vertex data is compacted.
    // Only vertices still indexed by a submesh contribute rendered geometry.
    internal sealed class BlendShapeEffectAnalysis
    {
        private readonly Mesh mesh;
        private readonly int[] vertices;
        private readonly ShapeDelta buffer;
        private readonly Dictionary<int, bool> effects = new();

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

        internal static HashSet<SkinnedMeshRenderer> FindFullVertexReaders(GameObject avatar, AvatarAnimationIndex animations = null,
            IDictionary<SkinnedMeshRenderer, string> reasons = null)
        {
            var result = new HashSet<SkinnedMeshRenderer>();
            var references = new SerializedReferenceSnapshot();
            var inspected = new HashSet<Object>();
            var referenced = new HashSet<Object>();
            string reader = "animated object reference";
            void Add(SkinnedMeshRenderer skin)
            {
                if (result.Add(skin) && reasons != null) reasons[skin] = reader;
            }
            void Reference(Object value)
            {
                if (value == null || !referenced.Add(value)) return;
                if (value is SkinnedMeshRenderer renderer) { Add(renderer); return; }
                if (value is GameObject go)
                { foreach (var skin in go.GetComponentsInChildren<SkinnedMeshRenderer>(true)) Add(skin); return; }
                if (value is Transform transform) { Reference(transform.gameObject); return; }
                if (value is Component || value is ScriptableObject) Read(value);
            }
            void Read(Object owner)
            {
                if (owner == null || !inspected.Add(owner)) return;
                foreach (var reference in references.Get(owner)) Reference(reference.Value);
            }
            // A renderer reader can consume unindexed vertices (for example a particle
            // emitter). Unknown readers keep the entire vertex buffer observable.
            // Descriptor morph names/indices are protected separately; our settings
            // describe transformations rather than consume deformed vertex output.
            // A renderer's own bones/root/probe anchors are rendering inputs, not
            // external readers of every vertex. Actual consumers can still refer
            // directly to that renderer through Reference above.
            foreach (var component in avatar.GetComponentsInChildren<Component>(true))
                if (component != null && !(component is Transform) && !(component is Renderer) && !(component is VRCAvatarDescriptor) &&
                    !(component is ALTOOptimizationComponent) && !(component is ALTOTraceAndOptimizeBlendShapes) &&
                    !(component is BlendShapeOptimizationExclude) && !ObjectOptimizationSafety.IsBuildOnly(component) &&
                    !TransformReferencesOnly(component))
                {
                    reader = component.GetType().FullName + " on '" + AnimationUtility.CalculateTransformPath(component.transform, avatar.transform) + "'";
                    if (component is ParticleSystem)
                    {
                        // Custom simulation space and collision planes consume a
                        // frame, not every descendant renderer's vertices. Keep
                        // actual renderer/mesh readers and unknown indirect paths.
                        foreach (var reference in references.Get(component))
                            if (!(reference.Value is Transform)) Reference(reference.Value);
                    }
                    else Read(component);
                }
            // An emitter or other consumer can select a renderer only after an
            // animation evaluates. Its serialized initial reference is not the
            // complete set of mesh readers, even when this binding is immutable.
            // Static and animated references share the same transitive closure,
            // including readers that locate a renderer through a hierarchy root.
            if (animations != null) foreach (var binding in animations.Bindings)
            {
                if (binding.ObjectKeys == null) continue;
                var owners = binding.Objects().ToArray();
                if (binding.Complete && owners.Length != 0 && owners.All(owner => owner is Renderer ||
                    owner is Component component && (TransformReferencesOnly(component) || ObjectOptimizationSafety.IsBuildOnly(component)))) continue;
                if (binding.Complete && owners.Length != 0 && owners.All(owner => owner is ParticleSystem) &&
                    binding.ObjectKeys.All(key => key.value == null || key.value is Transform)) continue;
                reader = "animation binding '" + binding.Binding.path + ":" + binding.Binding.propertyName + "'";
                foreach (var key in binding.ObjectKeys)
                    Reference(key.value);
            }
            return result;
        }

        // These native contracts use referenced frames, not a search for vertex
        // buffers below those frames. Unknown scripts still retain the transitive
        // hierarchy-reader rule, including scripts that reference these components.
        private static bool TransformReferencesOnly(Component component) => component is Animator || component is MeshFilter ||
            component is Light || component is VRCPhysBone || component is VRCPhysBoneCollider ||
            component is VRCContactSender || component is VRCContactReceiver ||
            component is UnityEngine.Animations.IConstraint || component is VRC.Dynamics.VRCConstraintBase ||
            ObjectOptimizationSafety.IsParentChangeDetector(component);

        internal bool HasEffect(int shape)
        {
            if (effects.TryGetValue(shape, out bool result)) return result;
            for (int frame = 0; frame < mesh.GetBlendShapeFrameCount(shape); frame++)
            {
                if (!AnimationUsage.Finite(mesh.GetBlendShapeFrameWeight(shape, frame))) return effects[shape] = true;
                buffer.Read(mesh, shape, frame);
                if (HasEffect(buffer)) return effects[shape] = true;
            }
            return effects[shape] = false;
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
