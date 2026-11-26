using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEngine;
using Noname.AvatarTools.Analysis;

namespace Noname.AvatarTools.BlendShapeOptimization
{
    internal sealed class ShapeInput
    {
        internal int Index;
        internal string Name;
        internal float Weight;
        internal bool ChangedInitialValue;
        internal NumericalOptimization ConsumedNumericalPermission;
        internal IReadOnlyList<CurveUse> Uses;
        internal IReadOnlyList<CurveUse> IgnoredUses = Array.Empty<CurveUse>();
        internal IReadOnlyList<CurveUse> UnreachableUses = Array.Empty<CurveUse>();
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
        internal BlendShapeEffectAnalysis Effects;
        internal readonly List<ShapeInput> Frozen = new();
        internal readonly List<ShapeInput> Ineffective = new();
        internal readonly List<ShapeGroup> Groups = new();
        internal readonly List<string> Skipped = new();
        internal bool HasChanges => Frozen.Count != 0 || Groups.Count != 0 || Ineffective.Count != 0;
        internal IEnumerable<ShapeInput> Removed => Frozen.Concat(Groups.SelectMany(g => g.Inputs)).Concat(Ineffective);

        internal static OptimizationPlan Create(SkinnedMeshRenderer renderer, AnimationUsage usage,
            bool zeroReset, ISet<string> preserve, Func<VirtualClip, bool> excludeAnimation = null,
            bool ignoreUnreachableStates = false, bool removeIneffective = false, bool observeAllVertices = false,
            ConstantPropertyAnalysis constants = null, bool allowInitialValueChanges = false, BlendShapeEffectAnalysis effects = null,
            bool allowSmallNumericalDifferences = true, bool allowAnimationOutputRounding = false)
        {
            using var cost = AnalysisCost.Measure("BlendShape output planning");
            allowSmallNumericalDifferences &= !observeAllVertices;
            allowAnimationOutputRounding &= !observeAllVertices;
            var plan = new OptimizationPlan { Renderer = renderer, Source = renderer.sharedMesh, Effects = removeIneffective ? effects : null };
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
                var allUses = usage.Get(renderer, name);
                float weight = renderer.GetBlendShapeWeight(i);
                if (removeIneffective && plan.Source.isReadable && AnimationUsage.Finite(weight) &&
                    !(plan.Effects ??= new BlendShapeEffectAnalysis(plan.Source, observeAllVertices)).HasEffect(i))
                {
                    if (allUses.Any(u => u.ReadOnly))
                        plan.Skipped.Add(name + ": ineffective shape has an immutable or shared binding.");
                    else
                        plan.Ineffective.Add(new ShapeInput { Index = i, Name = name, Weight = weight, Uses = allUses });
                    continue;
                }
                int frames = plan.Source.GetBlendShapeFrameCount(i);
                if (frames == 0 || Enumerable.Range(0, frames).Any(f =>
                        !AnimationUsage.Finite(plan.Source.GetBlendShapeFrameWeight(i, f)) ||
                        plan.Source.GetBlendShapeFrameWeight(i, f) <= 0))
                {
                    plan.Skipped.Add(name + ": unsupported frame weights.");
                    continue;
                }
                if (!AnimationUsage.Finite(weight) || weight < 0 || weight > 100)
                {
                    plan.Skipped.Add(name + ": default weight is outside the supported 0..100 range.");
                    continue;
                }
                var ignored = allUses.Where(u => excludeAnimation?.Invoke(u.Clip) == true).ToArray();
                var unreachable = allUses.Except(ignored).Where(u => ignoreUnreachableStates && u.Unreachable).ToArray();
                if (ignored.Concat(unreachable).Any(u => u.ReadOnly))
                {
                    plan.Skipped.Add(name + ": ignored animation has an immutable or shared binding.");
                    continue;
                }
                if (unreachable.Any(u => u.Additive))
                {
                    plan.Skipped.Add(name + ": unreachable binding participates in an additive animation context.");
                    continue;
                }
                var shape = new ShapeInput { Index = i, Name = name, Weight = weight,
                    Uses = allUses.Except(ignored).Except(unreachable).ToArray(), IgnoredUses = ignored,
                    UnreachableUses = unreachable };
                if (shape.Uses.Count == 0)
                {
                    // CPU baking changes the order of vertex/morph arithmetic.
                    // A zero-weight removal needs no such numerical permission.
                    if (allowSmallNumericalDifferences || shape.Weight == 0)
                    {
                        if (shape.Weight != 0) shape.ConsumedNumericalPermission = NumericalOptimization.BlendShapes;
                        plan.Frozen.Add(shape);
                    }
                    else plan.Skipped.Add(name + ": strict numerical policy retains nonzero morph accumulation.");
                    continue;
                }
                if (constants != null && !allUses.Any(u => u.ReadOnly) &&
                    constants.TryProve(renderer, "blendShape." + name, ConstantOutputValue.Scalar(weight), out var proof, out _,
                        includeUnreachableOutputs: !ignoreUnreachableStates,
                        allowSmallNumericalDifferences: allowAnimationOutputRounding) &&
                    proof.Value.Number >= 0 && proof.Value.Number <= 100 &&
                    (allowSmallNumericalDifferences || proof.Value.Number == 0) &&
                    (!proof.ChangesInitialValue || allowInitialValueChanges && !observeAllVertices && !proof.Writers.Any(w => w.PreservedInput)))
                {
                    shape.Weight = proof.Value.Number; shape.ChangedInitialValue = proof.ChangesInitialValue;
                    if (shape.Weight != 0) shape.ConsumedNumericalPermission |= NumericalOptimization.BlendShapes;
                    if (constants.RequiresNumericalPermission(renderer, "blendShape." + name, ConstantOutputValue.Scalar(weight), proof,
                        includeUnreachableOutputs: !ignoreUnreachableStates))
                        shape.ConsumedNumericalPermission |= NumericalOptimization.AnimationOutputs;
                    plan.Frozen.Add(shape); continue;
                }
                if (!zeroReset || shape.Uses.Any(u => !AnimationUsage.IsConstantZero(u.Curve))) continue;
                if (!allowSmallNumericalDifferences && shape.Weight != 0)
                {
                    plan.Skipped.Add(name + ": strict numerical policy retains nonzero combined morph arithmetic.");
                    continue;
                }
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
                    if (ignoreUnreachableStates)
                    {
                        // Dead clips still register properties for Write Defaults. Only combine shapes
                        // with identical layer coverage, and retain that coverage when remapping curves.
                        var contexts = shape.Uses.Concat(shape.UnreachableUses).SelectMany(u => u.DefaultContexts).Distinct().OrderBy(c => c).ToArray();
                        writer.Write(contexts.Length);
                        foreach (int context in contexts) writer.Write(context);
                    }
                });
                if (!groups.TryGetValue(signature, out var group)) groups.Add(signature, group = new ShapeGroup());
                if (shape.Weight != 0) shape.ConsumedNumericalPermission |= NumericalOptimization.BlendShapes;
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
            foreach (var clipUses in Removed.SelectMany(s => s.IgnoredUses)
                         .Concat(Frozen.SelectMany(s => s.Uses))
                         .Concat(Frozen.SelectMany(s => s.UnreachableUses))
                         .Concat(Ineffective.SelectMany(s => s.Uses.Concat(s.UnreachableUses))).GroupBy(u => u.Clip))
            {
                var clip = clipUses.Key;
                float end = EndTime(clip);
                foreach (var use in clipUses) clip.SetFloatCurve(use.Binding, null);
                if (EndTime(clip) < end || !clip.GetFloatCurveBindings().Any() && !clip.GetObjectCurveBindings().Any())
                {
                    // An unbound timing curve preserves exit times even if every real track was removed.
                    // Keep a zero-length curve too: Unity serializes a clip with no curves
                    // as one second long, which would introduce new timing dependencies.
                    var timing = EditorCurveBinding.FloatCurve("__noname_clip_duration_" + Guid.NewGuid().ToString("N"),
                        typeof(GameObject), "m_IsActive");
                    clip.SetFloatCurve(timing, end == 0 ? new AnimationCurve(new Keyframe(0, 0)) : AnimationCurve.Constant(0, end, 0));
                }
            }
            foreach (var group in Groups)
            {
                foreach (var clipGroup in group.Inputs.SelectMany(s => s.Uses.Concat(s.UnreachableUses)).GroupBy(u => (u.Clip, u.Binding.path)))
                {
                    var uses = clipGroup.ToArray();
                    var times = uses.SelectMany(u => u.Curve?.keys ?? Array.Empty<Keyframe>()).Select(k => k.time).DefaultIfEmpty(0).ToArray();
                    float first = times.Min();
                    float last = times.Max();
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

        private static float EndTime(VirtualClip clip)
            => clip.GetFloatCurveBindings().SelectMany(b => clip.GetFloatCurve(b)?.keys ?? Array.Empty<Keyframe>()).Select(k => k.time)
                .Concat(clip.GetObjectCurveBindings().SelectMany(b => clip.GetObjectCurve(b) ?? Array.Empty<ObjectReferenceKeyframe>()).Select(k => k.time))
                .DefaultIfEmpty(0).Max();
    }
}
