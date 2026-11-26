using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.BlendShapeOptimization;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.ObjectOptimization;
using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.Analysis
{
    // Permission for a continuous animation output, not for a parameter or a
    // material uniform. Resolve every owner of a shared clip in one snapshot.
    // Construct lazily so ordinary exact tree rewrites do not pay for readers.
    internal sealed class AnimationOutputRounding
    {
        private static readonly HashSet<string> LightFields = new(StringComparer.Ordinal) {
            "m_Intensity", "m_Range", "m_SpotAngle", "m_Color.r", "m_Color.g", "m_Color.b", "m_Color.a", "m_Shadows.m_Strength"
        };
        private static readonly HashSet<string> TransformFields = new(StringComparer.Ordinal) {
            "m_LocalPosition.x", "m_LocalPosition.y", "m_LocalPosition.z",
            "m_LocalScale.x", "m_LocalScale.y", "m_LocalScale.z",
            "m_LocalRotation.x", "m_LocalRotation.y", "m_LocalRotation.z", "m_LocalRotation.w"
        };
        private readonly GameObject avatar;
        private readonly IDictionary<object, VirtualAnimatorController> entries;
        private readonly IReadOnlyDictionary<string, string> paths;
        private readonly IEnumerable<string> preserved;
        private readonly OptimizationPolicy policy;
        private readonly Dictionary<(VirtualClip, EditorCurveBinding), bool> allowed = new();
        private Dictionary<(VirtualClip, EditorCurveBinding), AvatarAnimationIndex.BindingUse> uses;
        private AvatarAnimationIndex index;
        private InitialPoseReaders.Inputs readers;
        private HashSet<SkinnedMeshRenderer> vertexReaders;
        private bool initialized, supported;

        internal AnimationOutputRounding(GameObject avatar, IDictionary<object, VirtualAnimatorController> entries,
            IReadOnlyDictionary<string, string> paths, OptimizationPolicy policy, IEnumerable<string> preserved = null)
        { this.avatar = avatar; this.entries = entries; this.paths = paths; this.policy = policy; this.preserved = preserved; }

        internal bool Allows(VirtualClip clip, EditorCurveBinding binding)
        {
            if (allowed.TryGetValue((clip, binding), out bool value)) return value;
            return allowed[(clip, binding)] = Inspect(clip, binding);
        }

        private bool Inspect(VirtualClip clip, EditorCurveBinding binding)
        {
            if (policy == null || binding.isPPtrCurve || binding.isDiscreteCurve || string.IsNullOrEmpty(binding.propertyName) ||
                !(binding.type == typeof(Transform) && TransformFields.Contains(binding.propertyName) ||
                  binding.type == typeof(Light) && LightFields.Contains(binding.propertyName) ||
                  binding.type == typeof(SkinnedMeshRenderer) && binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal))) return false;
            if (!initialized)
            {
                initialized = true;
                if (avatar == null || entries == null || ObjectOptimizationSafety.UnsupportedRuntime(avatar) != null) return false;
                index = AvatarAnimationIndex.Create(avatar, entries, paths, preserved);
                if (index.UnsafeReason != null) return false;
                uses = index.Bindings.ToDictionary(u => (u.Clip, u.Binding));
                supported = true;
            }
            if (!supported || !uses.TryGetValue((clip, binding), out var use) || !use.Complete || use.Protected ||
                use.Targets.Length == 0) return false;
            // A shared output meets every resolved root and every inherited
            // policy. A missing or ambiguous target is not an empty permission.
            if (use.Targets.Any(t => t == null ||
                !policy.AllowsSmallNumericalDifferences(t, NumericalOptimization.AnimationOutputs))) return false;
            readers ??= InitialPoseReaders.Analyze(avatar, animations: index);
            foreach (var target in use.Targets)
            {
                if (binding.type == typeof(Transform))
                {
                    if (target is RectTransform || readers.Frames.Contains(target) || readers.CullingFrames.Contains(target) ||
                        !StaticTransformProof.AllowsSubtreeRounding(policy, target, NumericalOptimization.AnimationOutputs)) return false;
                }
                else
                {
                    var components = target.GetComponents(binding.type);
                    if (components.Length != 1 || readers.Components.Contains(components[0]) ||
                        readers.CullingComponents.Contains(components[0])) return false;
                    if (components[0] is SkinnedMeshRenderer skin)
                    {
                        vertexReaders ??= BlendShapeEffectAnalysis.FindFullVertexReaders(avatar, index);
                        if (skin.sharedMesh == null || skin.sharedMesh.GetBlendShapeIndex(binding.propertyName.Substring(11)) < 0 ||
                            vertexReaders.Contains(skin)) return false;
                    }
                }
            }
            return true;
        }
    }
}
