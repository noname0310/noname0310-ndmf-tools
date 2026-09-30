using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Noname.AvatarTools.Editor
{
    internal sealed class EyePresetAssignmentReport
    {
        internal int Assigned;
        internal int Preserved;
        internal readonly List<string> Warnings = new List<string>();
    }

    internal static class EyePresetAutoAssignment
    {
        internal static EyePresetAssignmentReport FillMissing(BlendShapePoseOverride component, IReadOnlyDictionary<string, EyePose> requests)
        {
            var report = new EyePresetAssignmentReport();
            var pending = new List<(BlendShapePoseEntry entry, List<EyeClipCandidate> candidates)>();
            var empty = new List<(BlendShapePoseEntry entry, EyePose pose)>();
            foreach (var entry in component.Overrides)
            {
                if (entry == null || entry.BlendShapeName == null || !requests.TryGetValue(entry.BlendShapeName, out var pose)) continue;
                if (entry.GetAnimations().Any(clip => clip != null)) report.Preserved++;
                else empty.Add((entry, pose));
            }
            if (empty.Count == 0) return report;
            var found = EyeAnimationDiscovery.Find(component);
            foreach (var item in empty)
            {
                var candidates = found.Candidates[item.pose];
                if (candidates.Count == 0)
                    report.Warnings.Add($"'{item.entry.BlendShapeName}': No compatible pose clips were found under the recognized eye parameters. Assign animations manually.");
                else if (candidates.Any(c => !c.SameClips(candidates[0])))
                    report.Warnings.Add($"'{item.entry.BlendShapeName}': Multiple pose alternatives match across the avatar's controllers. Assign animations manually.");
                else pending.Add((item.entry, candidates));
            }
            if (pending.Count == 0) return report;

            var descriptor = component.FindDescriptor();
            var currentRoot = component.PathMode == PoseAnimationPathMode.Absolute ? descriptor.transform
                : component.RelativePathRoot != null ? component.RelativePathRoot : component.transform;
            var clips = component.Overrides.Where(e => e != null).SelectMany(e => e.GetAnimations()).Where(clip => clip != null)
                .Concat(pending.SelectMany(p => p.candidates[0].Clips)).Distinct().ToArray();
            var roots = new[] { currentRoot }.Concat(pending.SelectMany(p => p.candidates.Select(c => c.Root))).Distinct();
            // Keep the current path settings whenever they already resolve every clip. Otherwise use a common
            // source root only if all existing manual assignments will still resolve to the selected renderer.
            var commonRoot = roots.FirstOrDefault(root => clips.All(clip => EyeAnimationDiscovery.ClipMatchesTarget(clip, root, component.TargetMesh)));
            var selectedRoot = commonRoot != null ? commonRoot : currentRoot;
            foreach (var item in pending)
            {
                var animations = item.candidates[0].Clips;
                if (!PoseOverrideConfiguration.TryReadAnimations(animations, selectedRoot, component.TargetMesh, out _, out _, out _))
                {
                    report.Warnings.Add($"'{item.entry.BlendShapeName}': The discovered animations need a different animation root or have conflicting keyed values. Check the clips, Path Mode, and Relative Path Root before assigning them.");
                    continue;
                }
                item.entry.Animation = animations[0];
                item.entry.AdditionalAnimations = animations.Skip(1).ToList();
                report.Assigned++;
            }
            if (report.Assigned > 0 && selectedRoot != currentRoot)
            {
                component.PathMode = selectedRoot == descriptor.transform ? PoseAnimationPathMode.Absolute : PoseAnimationPathMode.Relative;
                component.RelativePathRoot = component.PathMode == PoseAnimationPathMode.Relative ? selectedRoot : null;
            }
            return report;
        }
    }
}
