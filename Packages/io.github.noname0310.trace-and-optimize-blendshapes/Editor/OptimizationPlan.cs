using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.BlendShapeOptimization
{
    internal sealed class ShapeInput
    {
        internal int Index;
        internal string Name;
        internal float Weight;
        internal IReadOnlyList<CurveUse> Uses;
    }

    internal sealed class ShapeGroup
    {
        internal string Name;
        internal readonly List<ShapeInput> Inputs = new();
        internal float DefaultWeight => Inputs.Any(s => s.Weight != 0) ? 100 : 0;
    }

    internal sealed class OptimizationPlan
    {
        internal SkinnedMeshRenderer Renderer;
        internal Mesh Source;
        internal readonly List<ShapeInput> Frozen = new();
        internal readonly List<ShapeGroup> Groups = new();
        internal readonly List<string> Skipped = new();
        internal bool HasChanges => Frozen.Count != 0 || Groups.Count != 0;
        internal IEnumerable<ShapeInput> Removed => Frozen.Concat(Groups.SelectMany(g => g.Inputs));

        internal static OptimizationPlan Create(SkinnedMeshRenderer renderer, AnimationUsage usage,
            bool zeroReset, ISet<string> preserve)
        {
            var plan = new OptimizationPlan { Renderer = renderer, Source = renderer.sharedMesh };
            if (plan.Source == null) return plan;
            if (renderer.GetComponent<Cloth>() != null || usage.MeshSwaps.Contains(renderer))
            {
                plan.Skipped.Add("Renderer uses Cloth or an animated mesh reference.");
                return plan;
            }
            var groups = new Dictionary<string, ShapeGroup>(StringComparer.Ordinal);
            for (int i = 0; i < plan.Source.blendShapeCount; i++)
            {
                string name = plan.Source.GetBlendShapeName(i);
                if (preserve.Contains(name)) continue;
                int frames = plan.Source.GetBlendShapeFrameCount(i);
                if (frames == 0 || Enumerable.Range(0, frames).Any(f =>
                        !AnimationUsage.Finite(plan.Source.GetBlendShapeFrameWeight(i, f)) ||
                        plan.Source.GetBlendShapeFrameWeight(i, f) <= 0))
                {
                    plan.Skipped.Add(name + ": unsupported frame weights.");
                    continue;
                }
                float weight = renderer.GetBlendShapeWeight(i);
                if (!AnimationUsage.Finite(weight) || weight < 0 || weight > 100)
                {
                    plan.Skipped.Add(name + ": default weight is outside the supported 0..100 range.");
                    continue;
                }
                var shape = new ShapeInput { Index = i, Name = name, Weight = weight, Uses = usage.Get(renderer, name) };
                if (shape.Uses.Count == 0) { plan.Frozen.Add(shape); continue; }
                if (!zeroReset || shape.Uses.Any(u => !AnimationUsage.IsConstantZero(u.Curve))) continue;
                if (shape.Uses.Any(u => u.ReadOnly || u.Additive))
                {
                    plan.Skipped.Add(name + ": immutable/shared binding or additive animation context.");
                    continue;
                }
                // A single frame at 100 has a linear deformation over the entire runtime range.
                // A zero default never contributes geometry and can be grouped regardless of frame layout.
                if (weight != 0 && (plan.Source.GetBlendShapeFrameCount(i) != 1 ||
                                    plan.Source.GetBlendShapeFrameWeight(i, 0) != 100))
                {
                    plan.Skipped.Add(name + ": zero-reset grouping requires a single frame at weight 100.");
                    continue;
                }
                var locations = shape.Uses.Select(u => (u.ClipId, u.Binding.path)).Distinct()
                    .OrderBy(x => x.ClipId).ThenBy(x => x.path, StringComparer.Ordinal).ToArray();
                string signature = ContentHash.Compute(writer =>
                {
                    writer.Write(locations.Length);
                    foreach (var location in locations) { writer.Write(location.ClipId); writer.Write(location.path); }
                });
                if (!groups.TryGetValue(signature, out var group)) groups.Add(signature, group = new ShapeGroup());
                group.Inputs.Add(shape);
            }
            foreach (var group in groups.Values.OrderBy(g => g.Inputs.Min(s => s.Index)))
            {
                group.Inputs.Sort((a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));
                group.Name = "__noname_zero_" + ContentHash.Text(string.Join("\n", group.Inputs.Select(s => s.Name))).Substring(0, 16);
                while (plan.Source.GetBlendShapeIndex(group.Name) >= 0 || plan.Groups.Any(g => g.Name == group.Name)) group.Name += "_";
                plan.Groups.Add(group);
            }
            return plan;
        }

        internal void RewriteCurves()
        {
            foreach (var group in Groups)
            {
                foreach (var clipGroup in group.Inputs.SelectMany(s => s.Uses).GroupBy(u => (u.Clip, u.Binding.path)))
                {
                    var uses = clipGroup.ToArray();
                    float first = uses.Min(u => u.Curve.keys[0].time);
                    float last = uses.Max(u => u.Curve.keys[u.Curve.length - 1].time);
                    var binding = uses[0].Binding;
                    binding.propertyName = "blendShape." + group.Name;
                    // Retain the removed curves' time range, including zero-only clips with exit-time transitions.
                    var curve = first == last ? new AnimationCurve(new Keyframe(first, 0)) :
                        new AnimationCurve(new Keyframe(first, 0), new Keyframe(last, 0));
                    curve.preWrapMode = WrapMode.ClampForever;
                    curve.postWrapMode = WrapMode.ClampForever;
                    foreach (var use in uses) use.Clip.SetFloatCurve(use.Binding, null);
                    clipGroup.Key.Clip.SetFloatCurve(binding, curve);
                }
            }
        }
    }
}
