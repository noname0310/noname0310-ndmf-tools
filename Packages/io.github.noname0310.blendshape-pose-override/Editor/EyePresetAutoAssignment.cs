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
                if (entry.Animation != null) report.Preserved++;
                else empty.Add((entry, pose));
            }
            if (empty.Count == 0) return report;
            var found = EyeAnimationDiscovery.Find(component);
            foreach (var item in empty)
            {
                var candidates = found.Candidates[item.pose];
                int distinctClips = candidates.Select(c => c.Clip).Distinct().Count();
                if (distinctClips == 0)
                    report.Warnings.Add($"'{item.entry.BlendShapeName}': No matching pose clip was found under the v2 eye parameters. Assign it manually.");
                else if (distinctClips > 1)
                    report.Warnings.Add($"'{item.entry.BlendShapeName}': Multiple pose clips match across the avatar's controllers. Assign one manually.");
                else pending.Add((item.entry, candidates));
            }
            if (pending.Count == 0) return report;

            var descriptor = component.FindDescriptor();
            var currentRoot = component.PathMode == PoseAnimationPathMode.Absolute ? descriptor.transform
                : component.RelativePathRoot != null ? component.RelativePathRoot : component.transform;
            var clips = component.Overrides.Where(e => e?.Animation != null).Select(e => e.Animation)
                .Concat(pending.Select(p => p.candidates[0].Clip)).Distinct().ToArray();
            var roots = new[] { currentRoot }.Concat(pending.SelectMany(p => p.candidates.Select(c => c.Root))).Distinct();
            // Keep the current path settings whenever they already resolve every clip. Otherwise use a common
            // source root only if all existing manual assignments will still resolve to the selected renderer.
            var commonRoot = roots.FirstOrDefault(root => clips.All(clip => EyeAnimationDiscovery.ClipMatchesTarget(clip, root, component.TargetMesh)));
            var selectedRoot = commonRoot != null ? commonRoot : currentRoot;
            foreach (var item in pending)
            {
                var clip = item.candidates[0].Clip;
                if (!EyeAnimationDiscovery.ClipMatchesTarget(clip, selectedRoot, component.TargetMesh))
                {
                    report.Warnings.Add($"'{item.entry.BlendShapeName}': The discovered clip needs a different animation root. Check Path Mode and Relative Path Root before assigning it.");
                    continue;
                }
                item.entry.Animation = clip;
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
