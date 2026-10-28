using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.Occlusion
{
    internal sealed class OcclusionPoseSnapshot : IDisposable
    {
        private Scene scene;
        private GameObject holder;
        private readonly Dictionary<Renderer, Renderer> copies = new();
        private readonly List<Mesh> meshes = new();
        private readonly List<Object> assets = new();
        private readonly Dictionary<Material, Material> materials = new();
        private readonly GameObject original;
        private readonly Matrix4x4 originalRootMatrix;
        internal readonly GameObject Root;

        internal OcclusionPoseSnapshot(GameObject avatar, OcclusionPose pose, IReadOnlyDictionary<string, string> paths = null)
        {
            original = avatar; originalRootMatrix = avatar.transform.localToWorldMatrix;
            scene = EditorSceneManager.NewPreviewScene();
            try
            {
                holder = new GameObject("Occlusion pose snapshot"); holder.SetActive(false);
                SceneManager.MoveGameObjectToScene(holder, scene);
                Root = Object.Instantiate(avatar, holder.transform, false);
                Root.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity); Root.transform.localScale = Vector3.one;
                var sources = avatar.GetComponentsInChildren<Renderer>(true); var targets = Root.GetComponentsInChildren<Renderer>(true);
                if (sources.Length != targets.Length) throw new InvalidOperationException("The pose copy changed renderer membership.");
                for (int i = 0; i < sources.Length; i++)
                {
                    if (sources[i].GetType() != targets[i].GetType()) throw new InvalidOperationException("The pose copy changed renderer order.");
                    copies.Add(sources[i], targets[i]);
                    targets[i].sharedMaterials = sources[i].sharedMaterials.Select(CopyMaterial).ToArray();
                    targets[i].forceRenderingOff = sources[i].forceRenderingOff;
                    if (sources[i].HasPropertyBlock())
                    {
                        var block = new MaterialPropertyBlock();
                        sources[i].GetPropertyBlock(block); targets[i].SetPropertyBlock(block);
                        for (int slot = 0; slot < sources[i].sharedMaterials.Length; slot++)
                        {
                            block.Clear(); sources[i].GetPropertyBlock(block, slot); targets[i].SetPropertyBlock(block, slot);
                        }
                    }
                }
                if (pose?.Clip != null) CopyClip(pose.Clip, paths).SampleAnimation(Root, pose.Time);
            }
            catch { Dispose(); throw; }
        }

        private Material CopyMaterial(Material source)
        {
            if (source == null) return null;
            if (!materials.TryGetValue(source, out var copy))
            { copy = Object.Instantiate(source); materials.Add(source, copy); assets.Add(copy); }
            return copy;
        }

        private AnimationClip CopyClip(AnimationClip source, IReadOnlyDictionary<string, string> paths)
        {
            var clip = Object.Instantiate(source); assets.Add(clip);
            var floats = AnimationUtility.GetCurveBindings(source);
            var objects = AnimationUtility.GetObjectReferenceCurveBindings(source);
            clip.ClearCurves(); AnimationUtility.SetAnimationEvents(clip, Array.Empty<AnimationEvent>());
            var bindings = new HashSet<EditorCurveBinding>();
            EditorCurveBinding Map(EditorCurveBinding binding)
            {
                if (binding.type != typeof(Transform) && binding.type != typeof(GameObject) && binding.type != typeof(Animator) &&
                    (binding.type == null || !typeof(Renderer).IsAssignableFrom(binding.type)) && binding.type != typeof(MeshFilter))
                    throw new InvalidOperationException("Occlusion pose clips support transforms, renderers, mesh filters, active state and humanoid pose curves: " + source.name);
                if (paths != null && paths.TryGetValue(binding.path, out string mapped))
                {
                    if (mapped == null) throw new InvalidOperationException("An occlusion pose path was removed during the build: " + binding.path);
                    binding.path = mapped;
                }
                if (!bindings.Add(binding)) throw new InvalidOperationException("Occlusion pose bindings collided after path remapping: " + source.name);
                if (AnimationUtility.GetAnimatedObject(Root, binding) == null)
                    throw new InvalidOperationException("An occlusion pose binding cannot be resolved from the avatar root: " + binding.path + "/" + binding.propertyName);
                return binding;
            }
            foreach (var binding in floats) AnimationUtility.SetEditorCurve(clip, Map(binding), AnimationUtility.GetEditorCurve(source, binding));
            foreach (var binding in objects)
            {
                var keys = AnimationUtility.GetObjectReferenceCurve(source, binding);
                for (int i = 0; i < keys.Length; i++)
                    if (keys[i].value is Material material) keys[i].value = CopyMaterial(material);
                AnimationUtility.SetObjectReferenceCurve(clip, Map(binding), keys);
            }
            return clip;
        }

        internal Renderer Copy(Renderer source) => copies.TryGetValue(source, out var target) ? target : null;
        internal bool Active(Renderer source)
        {
            var copy = Copy(source);
            if (copy == null || !copy.enabled || copy.forceRenderingOff || copy.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly) return false;
            for (var current = copy.transform; current != null && current != Root.transform; current = current.parent)
                if (!current.gameObject.activeSelf) return false;
            return true;
        }

        internal Mesh WorldMesh(Renderer source, IEnumerable<string> maskShapes = null)
        {
            var copy = Copy(source); if (copy == null) return null;
            Mesh mesh;
            if (copy is SkinnedMeshRenderer skin)
            {
                var originalSkin = (SkinnedMeshRenderer)source;
                if (skin.sharedMesh == null || originalSkin.bones.Any(b => b != null && !b.IsChildOf(original.transform)) ||
                    originalSkin.rootBone != null && !originalSkin.rootBone.IsChildOf(original.transform)) return null;
                if (maskShapes != null && skin.sharedMesh != originalSkin.sharedMesh) return null;
                foreach (string name in maskShapes ?? Array.Empty<string>())
                {
                    int index = skin.sharedMesh.GetBlendShapeIndex(name); if (index >= 0) skin.SetBlendShapeWeight(index, 0);
                }
                mesh = new Mesh(); meshes.Add(mesh);
                // BakeMesh(false) can already incorporate the renderer scale.
                // true returns the local positions expected by the matrix below.
                skin.BakeMesh(mesh, useScale: true);
            }
            else if (copy is MeshRenderer)
            {
                var filter = copy.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null) return null;
                mesh = Object.Instantiate(filter.sharedMesh); meshes.Add(mesh);
            }
            else return null;
            var matrix = originalRootMatrix * copy.transform.localToWorldMatrix;
            mesh.vertices = mesh.vertices.Select(matrix.MultiplyPoint3x4).ToArray(); mesh.RecalculateBounds();
            // Native renderers compensate front-face winding for a mirrored
            // rendering matrix. A world-space proxy has an identity matrix, so
            // carry that compensation into its indices. Candidates are tested
            // double-sided; only occluder culling depends on these indices.
            var windingTransform = copy is SkinnedMeshRenderer renderer && renderer.rootBone != null ? renderer.rootBone : copy.transform;
            if ((originalRootMatrix * windingTransform.localToWorldMatrix).determinant < 0)
                for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
                {
                    if (mesh.GetTopology(submesh) != MeshTopology.Triangles) continue;
                    var indices = mesh.GetIndices(submesh, applyBaseVertex: true);
                    for (int i = 0; i < indices.Length; i += 3) (indices[i], indices[i + 1]) = (indices[i + 1], indices[i]);
                    mesh.SetIndices(indices, MeshTopology.Triangles, submesh, calculateBounds: false, baseVertex: 0);
                }
            return mesh;
        }

        internal Mesh Proxy(Mesh worldMesh, int[] indices)
        {
            var mesh = new Mesh { indexFormat = worldMesh.indexFormat }; meshes.Add(mesh);
            mesh.vertices = worldMesh.vertices; mesh.SetIndices(indices, UnityEngine.MeshTopology.Triangles, 0); return mesh;
        }

        public void Dispose()
        {
            foreach (var mesh in meshes) if (mesh != null) Object.DestroyImmediate(mesh);
            meshes.Clear();
            if (holder != null) Object.DestroyImmediate(holder);
            foreach (var asset in assets) if (asset != null) Object.DestroyImmediate(asset);
            assets.Clear();
            if (scene.IsValid()) { EditorSceneManager.ClosePreviewScene(scene); scene = default; }
        }
    }
}
