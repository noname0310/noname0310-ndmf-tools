using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Noname.AvatarTools.Analysis
{
    internal static class TransformScaleSignAnalysis
    {
        // Head Chop applies scale factors to hide bones in first-person views.
        // A proven nonnegative multiplier can collapse a bone without reversing
        // its winding. It still writes scale and must not make that bone static.
        // https://creators.vrchat.com/avatars/avatar-components/vrc-headchop/
        internal static IEnumerable<Object> NonReversingNativeWriters(GameObject avatar, AvatarAnimationIndex index)
        {
            var candidates = avatar.GetComponentsInChildren<VRCHeadChop>(true)
                .Where(c => c.GetType() == typeof(VRCHeadChop) && Nonnegative(c.globalScaleFactor) &&
                    c.targetBones != null && c.targetBones.All(b => b != null && Nonnegative(b.scaleFactor) &&
                        (b.transform == null || Positive(b.transform.localScale)))).ToArray();
            if (candidates.Length == 0) yield break;
            var analysis = index.Reachability;
            if (analysis.FallbackReason != null) yield break;
            var proof = new InvariantComponentSettings(index, analysis, index.ControllerEntries);
            foreach (var candidate in candidates)
                if (proof.IsConstant(candidate) || NonnegativeScaleOutputs(candidate, index, proof)) yield return candidate;
        }

        private static bool NonnegativeScaleOutputs(VRCHeadChop component, AvatarAnimationIndex index,
            InvariantComponentSettings invariant)
        {
            // This is a sign fact, not permission to bake a factor or remove its
            // curves. Finite normalized interpolation of identical nonnegative
            // contributors can change magnitude bits without reversing winding.
            // Keep all lifecycle/reference fields exact, and reuse the bounded
            // mixer premises (including retained defaults and stable weights).
            using var serialized = new SerializedObject(component);
            var factors = new HashSet<string> { nameof(component.globalScaleFactor) };
            var bones = serialized.FindProperty(nameof(component.targetBones));
            if (bones?.isArray == true)
                for (int i = 0; i < bones.arraySize; i++)
                {
                    var factor = bones.GetArrayElementAtIndex(i).FindPropertyRelative("scaleFactor");
                    if (factor != null) factors.Add(factor.propertyPath);
                }
            foreach (var group in index.ForObject(component).GroupBy(u => u.Binding.propertyName))
            {
                if (invariant.IsConstantProperty(component, group.Key)) continue;
                var property = serialized.FindProperty(group.Key);
                if (!factors.Contains(group.Key) || property?.propertyType != SerializedPropertyType.Float ||
                    !Nonnegative(property.floatValue) ||
                    !index.ConstantOutputs.MatchesInitialValue(component, group.ToArray(), property.floatValue, true,
                        ConstantOutputKind.Scalar, allowSmallNumericalDifferences: true)) return false;
            }
            return true;
        }

        private static bool Nonnegative(float value) => MotionAnalysis.Finite(value) && value >= 0;
        private static bool Positive(Vector3 value) =>
            Nonnegative(value.x) && Nonnegative(value.y) && Nonnegative(value.z) && value.x > 0 && value.y > 0 && value.z > 0;
    }
}
