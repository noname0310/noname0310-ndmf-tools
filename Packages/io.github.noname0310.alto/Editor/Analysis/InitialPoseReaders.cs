using System.Collections.Generic;
using nadena.dev.ndmf;
using UnityEngine;
using VRC.SDKBase;

namespace Noname.AvatarTools.Analysis
{
    // Initialization is observable beyond rendering when a simulation or sensor
    // consumes a pose. Protect its input ancestors and referenced skinning frames.
    // This is intentionally a reader analysis, separate from transform writers.
    internal static class InitialPoseReaders
    {
        internal sealed class Inputs
        {
            internal readonly HashSet<Transform> Frames = new();
            internal readonly HashSet<Component> Components = new();
            // Changing visibility before the first evaluation can start or stop
            // Animator state machines. These readers only block changed defaults:
            // removing a curve equal to the authored value cannot change culling.
            internal readonly HashSet<Transform> CullingFrames = new();
            internal readonly HashSet<Component> CullingComponents = new();
            // A fixed skinned AABB is independent of morph arithmetic. Keep the
            // wider visibility set for enabled/shadow/transform changes, but do
            // not make it an implicit strict policy for every vertex operation.
            internal readonly HashSet<SkinnedMeshRenderer> DeformedCullingBounds = new();
        }

        internal static HashSet<Transform> Collect(GameObject avatar, SerializedReferenceSnapshot references = null)
            => Analyze(avatar, references).Frames;

        internal static Inputs Analyze(GameObject avatar, SerializedReferenceSnapshot references = null,
            AvatarAnimationIndex animations = null, bool reuseNativeReferences = false)
        {
            using var cost = AnalysisCost.Measure("Initial pose readers");
            AnalysisCost.Count(references == null ? "Initial pose readers/own references" : "Initial pose readers/shared references");
            // A caller-supplied reader snapshot can predate an in-batch edit.
            // Preserve the native writer scan's fresh-read contract unless the
            // caller owns an unchanged transaction, or these records are new.
            reuseNativeReferences |= references == null;
            references ??= new SerializedReferenceSnapshot();
            var result = new Inputs(); var visited = new HashSet<Object>();
            void Pose(Transform target, HashSet<Transform> frames)
            {
                for (var current = target; current != null && current.IsChildOf(avatar.transform); current = current.parent)
                    if (!frames.Add(current)) break;
            }
            void Skin(SkinnedMeshRenderer skin, HashSet<Transform> frames)
            {
                Pose(skin.rootBone, frames);
                foreach (var bone in skin.bones) Pose(bone, frames);
            }
            void Read(Object owner)
            {
                if (owner == null || !visited.Add(owner)) return;
                if (owner is Transform transform) { Pose(transform, result.Frames); return; }
                if (owner is GameObject go) { Pose(go.transform, result.Frames); return; }
                if (owner is Component component)
                {
                    result.Components.Add(component);
                    Pose(component.transform, result.Frames);
                    if (component is SkinnedMeshRenderer skin) Skin(skin, result.Frames);
                }
                foreach (var entry in references.Get(owner))
                    if (entry.Value is Component || entry.Value is GameObject || entry.Value is ScriptableObject) Read(entry.Value);
            }
            foreach (var target in TransformMutationAnalysis.NativeWriters(avatar, reuseNativeReferences ? references : null)) Pose(target, result.Frames);
            foreach (var component in avatar.GetComponentsInChildren<Component>(true))
            {
                if (component == null || component is Transform || component is Renderer || component is MeshFilter ||
                    component is Light || component is INDMFEditorOnly || component is IEditorOnly ||
                    ObjectOptimization.ObjectOptimizationSafety.IsParentChangeDetector(component)) continue;
                Read(component);
            }
            if (animations != null)
                foreach (var binding in animations.Bindings)
                foreach (var key in binding.ObjectKeys ?? System.Array.Empty<UnityEditor.ObjectReferenceKeyframe>())
                {
                    // A native reader may select another root or component through
                    // animation. Its serialized starting reference is not a complete
                    // reader set; root readers can consume an entire hierarchy.
                    var frame = key.value is GameObject go ? go.transform : key.value as Transform;
                    if (frame != null)
                        foreach (var child in frame.GetComponentsInChildren<Transform>(true)) Pose(child, result.Frames);
                    if (key.value is Component || key.value is GameObject || key.value is ScriptableObject) Read(key.value);
                }
            bool hasCulledAnimator = false;
            foreach (var animator in avatar.GetComponentsInChildren<Animator>(true))
            {
                if (animator.cullingMode == AnimatorCullingMode.AlwaysAnimate) continue;
                hasCulledAnimator = true;
                foreach (var renderer in animator.GetComponentsInChildren<Renderer>(true))
                {
                    result.CullingComponents.Add(renderer);
                    Pose(renderer.transform, result.CullingFrames);
                    if (renderer is SkinnedMeshRenderer skin)
                    {
                        Skin(skin, result.CullingFrames);
                        if (skin.updateWhenOffscreen || animations == null ||
                            animations.Writes(skin, "m_UpdateWhenOffscreen"))
                            result.DeformedCullingBounds.Add(skin);
                    }
                }
            }
            if (hasCulledAnimator)
                foreach (var light in avatar.GetComponentsInChildren<Light>(true))
                {
                    // Shadows count as renderer visibility. An avatar light can
                    // affect culling outside its own subtree, including world
                    // shadow receivers, so do not infer a local-only relationship.
                    result.CullingComponents.Add(light);
                    Pose(light.transform, result.CullingFrames);
                }
            return result;
        }
    }
}
