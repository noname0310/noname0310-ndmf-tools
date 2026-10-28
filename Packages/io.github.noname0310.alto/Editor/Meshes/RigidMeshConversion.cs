using System;
using System.Linq;
using nadena.dev.ndmf;
using Noname.AvatarTools.Analysis;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.Meshes
{
    // AAO 1.9.20 MeshInfo2.MakeBoned is a design reference. Independently
    // implemented rigid conversion. Unity's skinned object space can differ from
    // MeshRenderer object space under scale, even when world geometry is preserved.
    // The optimizer only invokes conversion under its explicit coordinate-space
    // policy. Default equivalence-preserving optimization leaves these inputs alone.
    internal static class RigidMeshConversion
    {
        internal static bool Unweighted(Mesh mesh)
        {
            if (mesh == null || !mesh.isReadable) return false;
            using var weights = mesh.GetAllBoneWeights();
            return weights.Length == 0;
        }

        internal static string ValidateExplicitConversion(MeshRenderer renderer, AvatarAnimationIndex animations,
            AvatarDependencyGraph references, ShaderPropertyUsage shaders, bool allowCoordinateSpaceChanges = false)
        {
            var filter = renderer.GetComponent<MeshFilter>(); var mesh = filter != null ? filter.sharedMesh : null;
            string reason = SkinnedMeshAssembler.ValidateGeometry(renderer, mesh) ?? MeshMergeAnalysis.ShaderIssue(renderer.sharedMaterials, shaders, true, allowCoordinateSpaceChanges);
            if (reason != null) return reason;
            if (renderer.isPartOfStaticBatch || renderer.additionalVertexStreams != null) return "static batching or additional vertex streams";
            if (renderer.HasPropertyBlock()) return "a material property block requires the original renderer";
            if (renderer.lightmapIndex >= 0 || renderer.realtimeLightmapIndex >= 0) return "lightmapped renderer";
            if (references.ReferenceOwners.TryGetValue(renderer, out var readers) && readers.Count > 0) return "external renderer reference";
            if (renderer.GetComponents<Component>().Any(c => c != null && c != renderer && Build.BuildFilterPass.Requires(c, renderer)))
                return "a component requires the original renderer type";
            if (animations.ForObject(filter).Count > 0) return "animated MeshFilter data";
            foreach (var use in animations.ForObject(renderer))
            {
                if (!use.Complete || use.Targets.Length != 1 || use.Objects().Count() != 1 || use.Immutable || use.Binding.isPPtrCurve)
                    return "ambiguous, immutable or object-reference renderer animation";
                if (use.Binding.propertyName != "m_Enabled" && !use.Binding.propertyName.StartsWith("material.", StringComparison.Ordinal))
                    return "unsupported renderer animation: " + use.Binding.propertyName;
            }
            // A previously missing SMR track must not begin affecting the new component.
            if (animations.Bindings.Any(u => u.Targets.Contains(renderer.transform) && u.Binding.type != null &&
                typeof(SkinnedMeshRenderer).IsAssignableFrom(u.Binding.type))) return "a missing skinned-renderer binding would become active";
            return null;
        }

        internal static Mesh RigidMesh(Mesh source, bool keepShapes)
        {
            var result = Object.Instantiate(source); result.name = source.name + " (Rigid Skinning)";
            if (!keepShapes) result.ClearBlendShapes(); // MeshRenderer never evaluates morphs.
            result.bindposes = new[] { Matrix4x4.identity };
            using var counts = new NativeArray<byte>(Enumerable.Repeat((byte)1, result.vertexCount).ToArray(), Allocator.Temp);
            using var weights = new NativeArray<BoneWeight1>(Enumerable.Repeat(new BoneWeight1 { boneIndex = 0, weight = 1 }, result.vertexCount).ToArray(), Allocator.Temp);
            result.SetBoneWeights(counts, weights);
            return result;
        }

        internal static SkinnedMeshRenderer Convert(MeshRenderer source, AvatarAnimationIndex animations, Action<Mesh> save)
        {
            var state = new RendererState(source); var obj = source.gameObject; var filter = source.GetComponent<MeshFilter>();
            var mesh = RigidMesh(filter.sharedMesh, false);
            var reference = ObjectRegistry.GetReference(source);
            var uses = animations.ForObject(source).ToArray();
            save?.Invoke(mesh);
            // Unity replaces an existing MeshRenderer when adding a SkinnedMeshRenderer.
            // Capture state and registry identity before that implicit removal occurs.
            var destination = obj.AddComponent<SkinnedMeshRenderer>();
            ObjectRegistry.RegisterReplacedObject(reference, destination);
            destination.sharedMesh = mesh; destination.bones = new[] { obj.transform }; destination.rootBone = null;
            // One full influence has the same result at every supported skinning
            // quality. Motion follows that bone after joining a different renderer.
            destination.localBounds = state.Bounds; destination.quality = SkinQuality.Auto;
            destination.skinnedMotionVectors = true; destination.updateWhenOffscreen = false;
            state.Apply(destination);
            foreach (var use in uses)
            {
                if (use.Binding.type != typeof(MeshRenderer)) continue;
                var binding = use.Binding; binding.type = typeof(SkinnedMeshRenderer);
                use.Clip.SetFloatCurve(use.Binding, null); use.Clip.SetFloatCurve(binding, use.Curve);
            }
            // Keep MeshFilter references for colliders/other consumers; the graph collector
            // removes the component only when no surviving dependency needs it.
            return destination;
        }

        internal static Mesh WeightUnweighted(SkinnedMeshRenderer renderer, Action<Mesh> save)
        {
            var source = renderer.sharedMesh;
            var weights = Enumerable.Range(0, source.blendShapeCount).Select(renderer.GetBlendShapeWeight).ToArray();
            var result = RigidMesh(source, true);
            ObjectRegistry.RegisterReplacedObject(source, result); save?.Invoke(result);
            renderer.sharedMesh = null; renderer.sharedMesh = result; renderer.bones = new[] { renderer.transform };
            for (int shape = 0; shape < weights.Length; shape++) renderer.SetBlendShapeWeight(shape, weights[shape]);
            return result;
        }

        private sealed class RendererState
        {
            internal readonly Bounds Bounds;
            private readonly Material[] materials;
            private readonly bool enabled, receiveShadows, occlusion, forceOff, staticShadow;
            private readonly ShadowCastingMode shadows;
            private readonly LightProbeUsage lightProbes;
            private readonly ReflectionProbeUsage reflections;
            private readonly Transform anchor;
            private readonly GameObject proxy;
            private readonly MotionVectorGenerationMode motion;
            private readonly int sortingLayer, sortingOrder, priority;
            private readonly uint layerMask;
            internal RendererState(Renderer source)
            {
                Bounds = source.localBounds; materials = source.sharedMaterials; enabled = source.enabled;
                shadows = source.shadowCastingMode; receiveShadows = source.receiveShadows;
                occlusion = source.allowOcclusionWhenDynamic; forceOff = source.forceRenderingOff; staticShadow = source.staticShadowCaster;
                lightProbes = source.lightProbeUsage; reflections = source.reflectionProbeUsage;
                anchor = source.probeAnchor; proxy = source.lightProbeProxyVolumeOverride; motion = source.motionVectorGenerationMode;
                sortingLayer = source.sortingLayerID; sortingOrder = source.sortingOrder; priority = source.rendererPriority; layerMask = source.renderingLayerMask;
            }
            internal void Apply(Renderer destination)
            {
                destination.sharedMaterials = materials; destination.enabled = enabled;
                destination.shadowCastingMode = shadows; destination.receiveShadows = receiveShadows;
                destination.allowOcclusionWhenDynamic = occlusion; destination.forceRenderingOff = forceOff; destination.staticShadowCaster = staticShadow;
                destination.lightProbeUsage = lightProbes; destination.reflectionProbeUsage = reflections;
                destination.probeAnchor = anchor; destination.lightProbeProxyVolumeOverride = proxy; destination.motionVectorGenerationMode = motion;
                destination.sortingLayerID = sortingLayer; destination.sortingOrder = sortingOrder; destination.rendererPriority = priority; destination.renderingLayerMask = layerMask;
            }
        }
    }
}
