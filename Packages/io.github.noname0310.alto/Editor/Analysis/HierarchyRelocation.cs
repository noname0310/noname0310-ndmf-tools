using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Meshes;
using Noname.AvatarTools.ParameterPolicy;
using Noname.AvatarTools.ObjectOptimization;
using Noname.AvatarTools.BlendShapeOptimization;
using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.Analysis
{
    // Relocation is a binding operation, separate from the proof that a frame is
    // removable. Existing descendant writers are relocated, not treated as readers.
    internal sealed class HierarchyRelocation
    {
        internal sealed class FlattenPlan
        {
            internal readonly Dictionary<string, string> Paths = new(StringComparer.Ordinal);
            internal readonly Dictionary<Transform, string> Names = new();
        }

        private readonly AvatarAnimationIndex animations;
        private readonly HashSet<UnityEngine.Object> nativeVisibility;
        private HashSet<string> observedPaths;
        internal HierarchyRelocation(AvatarAnimationIndex animations)
        { this.animations = animations; nativeVisibility = VisibilityMutationAnalysis.NativeWriters(animations.Avatar); }

        internal bool RedundantGate(Transform frame, Transform retained)
        {
            if (nativeVisibility.Contains(frame.gameObject)) return false;
            var writers = animations.ForObject(frame.gameObject);
            if (frame.gameObject.activeSelf && writers.Count == 0) return true;
            if (writers.Any(u => u.Binding.propertyName != "m_IsActive")) return false;
            var signal = Visibility(frame);
            if (signal == null) return false;
            for (var ancestor = retained; ancestor != null; ancestor = ancestor.parent)
            {
                if (nativeVisibility.Contains(ancestor.gameObject)) return false;
                if (signal.SameVisibility(Visibility(ancestor))) return true;
                if (ancestor == animations.Avatar.transform) break;
            }
            return false;
        }

        private MeshAnimationSignal Visibility(Transform target)
        {
            var writers = animations.ForObject(target.gameObject).Where(u => u.Binding.propertyName == "m_IsActive").ToArray();
            if (writers.Any(u => !u.Complete || u.Immutable || u.Targets.Length != 1 || u.Curve == null)) return null;
            return new MeshAnimationSignal { Initial = target.gameObject.activeSelf, Writers = writers,
                AnalyzeBoolean = writers.Length == 0 ? null : () => animations.BooleanSignals.For(target.gameObject, "m_IsActive"),
                MaskCoverage = MeshMergeAnalysis.MaskCoverage(animations.Avatar, target, animations, typeof(GameObject)) };
        }

        // Reparenting at the SDK boundary has no active NDMF path remapper. Prove
        // a complete native-path transaction before changing the hierarchy. Mask
        // inheritance needs a separate proof when the parent changes; retain it.
        internal bool CanReparent(Transform frame, Transform parent, out Dictionary<string, string> map)
        {
            map = new Dictionary<string, string>(StringComparer.Ordinal);
            var avatar = animations.Avatar.transform;
            if (animations.Paths != null || frame == null || parent == null || frame == avatar ||
                !frame.IsChildOf(avatar) || !parent.IsChildOf(avatar) || parent.IsChildOf(frame)) return false;
            if (animations.ControllerEntries.Keys.OfType<Animator>().Any(a => a != null && a.transform != avatar) ||
                frame.GetComponentsInChildren<Animator>(true).Length != 0 ||
                animations.Controllers.SelectMany(c => c.Layers).Any(l => l.AvatarMask != null)) return false;
            var moved = frame.GetComponentsInChildren<Transform>(true).ToHashSet();
            if (moved.SelectMany(t => t.GetComponents<SkinnedMeshRenderer>()).Any(ComponentHoisting.HasExternalMorphInterface)) return false;
            if (parent.Cast<Transform>().Any(t => t != frame && t.name == frame.name)) return false;
            string oldPrefix = AnimationUtility.CalculateTransformPath(frame, avatar);
            string parentPath = AnimationUtility.CalculateTransformPath(parent, avatar);
            var parentTargets = AnimationUsage.Resolve(avatar, avatar, parentPath, null);
            if (parentTargets.Count != 1 || parentTargets[0] != parent) return false;
            string newPrefix = (parentPath.Length == 0 ? "" : parentPath + "/") + frame.name;
            var destinations = new HashSet<string>(StringComparer.Ordinal);
            foreach (var target in moved)
            {
                string from = AnimationUtility.CalculateTransformPath(target, avatar);
                var sources = AnimationUsage.Resolve(avatar, avatar, from, null);
                if (sources.Count != 1 || sources[0] != target) return false;
                string to = newPrefix + from.Substring(oldPrefix.Length);
                if (!map.TryAdd(from, to) || !destinations.Add(to)) return false;
            }
            foreach (var use in animations.Bindings)
            {
                bool relocates = use.Targets.Any(moved.Contains);
                if (relocates && (!use.Complete || use.Immutable || use.Roots.Length != 1 || use.Roots[0] != avatar)) return false;
                // A previously unresolved destination must not acquire a writer,
                // including an otherwise empty Write Defaults binding.
                if (destinations.Contains(use.Binding.path) && !relocates) return false;
            }
            bool safe = true;
            foreach (var behaviour in ParameterSpecialization.Behaviours(animations.Controllers.SelectMany(c => c.AllReachableNodes())).Distinct())
                VRChatPlatformAnimatorBindings.Instance.RemapPathsInStateBehaviour(behaviour, path =>
                {
                    var targets = AnimationUsage.Resolve(avatar, avatar, path ?? "", null);
                    bool relocates = targets.Any(moved.Contains);
                    if (relocates && targets.Count != 1 || path != null && destinations.Contains(path) && !relocates) safe = false;
                    return path;
                });
            return safe;
        }

        // The NDMF remapper owns virtual paths. The late SDK boundary has native
        // paths and uses this same checked relocation with an explicit commit.
        internal bool CanFlatten(Transform frame, Transform[] children, bool removeFrame, out FlattenPlan plan)
        {
            plan = new FlattenPlan();
            var nativeMap = plan.Paths;
            var avatar = animations.Avatar.transform;
            // Independent controller roots may share clips with different relative
            // interpretations. Retain these frames until a per-root clone is proved.
            if (animations.ControllerEntries.Keys.OfType<Animator>().Any(a => a != null && a.transform != avatar)) return false;
            if (children.Length == 0 || children.Any(c => c == null || c.parent != frame)) return false;
            // Source ambiguity cannot be fixed by choosing a unique destination:
            // one original path may already refer to multiple different objects.
            if (children.Any(c => frame.Cast<Transform>().Count(s => s.name == c.name) != 1)) return false;
            var moved = new HashSet<Transform>(children.SelectMany(c => c.GetComponentsInChildren<Transform>(true)));
            if (moved.SelectMany(t => t.GetComponents<SkinnedMeshRenderer>()).Any(ComponentHoisting.HasExternalMorphInterface)) return false;
            var destinationPaths = new HashSet<string>(StringComparer.Ordinal);
            string oldPrefix = AnimationUtility.CalculateTransformPath(frame, avatar);
            string newPrefix = AnimationUtility.CalculateTransformPath(frame.parent, avatar);
            string prefix = newPrefix.Length == 0 ? "" : newPrefix + "/";
            // Include the removed frame's name: a new child must not capture its
            // old animation or behaviour interface during the same relocation.
            var occupied = frame.parent.Cast<Transform>().Select(t => t.name).ToHashSet(StringComparer.Ordinal);
            foreach (var child in children)
            {
                string original = AnimationUtility.CalculateTransformPath(child, avatar);
                string name = child.name;
                if (occupied.Contains(name))
                {
                    if (HasHumanoidNameInterface(child)) return false;
                    string stem = name + "$ALTO$" + PathHash(original);
                    name = stem;
                    for (int suffix = 1; occupied.Contains(name) || ObservesSubtree(prefix + name); suffix++) name = stem + "$" + suffix;
                    plan.Names.Add(child, name);
                }
                occupied.Add(name);
                foreach (var target in child.GetComponentsInChildren<Transform>(true))
                {
                    string from = AnimationUtility.CalculateTransformPath(target, avatar);
                    string to = prefix + name + from.Substring(original.Length);
                    if (!nativeMap.TryAdd(from, to) || !destinationPaths.Add(to)) return false;
                }
            }
            if (removeFrame) nativeMap[oldPrefix] = null;
            foreach (var use in animations.Bindings)
            {
                if (use.Targets.Any(moved.Contains) && (!use.Complete || use.Immutable)) return false;
                // An unresolved destination track must not acquire a target through
                // relocation. This also protects Write Defaults binding ownership.
                string actual = animations.Paths != null && animations.Paths.TryGetValue(use.Binding.path, out var mapped) ? mapped : use.Binding.path;
                if (actual != null && destinationPaths.Contains(actual) && !use.Targets.Any(moved.Contains)) return false;
            }
            foreach (var mask in animations.Controllers.SelectMany(c => c.Layers).Select(l => l.AvatarMask).Where(m => m != null).Distinct())
                foreach (var element in mask.Elements)
                {
                    var targets = AnimationUsage.Resolve(avatar, avatar, element.Key, animations.Paths);
                    // A group-level mask owns inherited weights. Do not erase that
                    // ownership merely because descendant paths can be rewritten.
                    if (targets.Contains(frame) || targets.Any(moved.Contains) && targets.Count != 1) return false;
                    string actual = animations.Paths != null && animations.Paths.TryGetValue(element.Key, out var mapped) ? mapped : element.Key;
                    if (actual != null && destinationPaths.Contains(actual) && !targets.Any(moved.Contains)) return false;
                }
            // SDK behaviours can contain path strings (for example Play Audio).
            // Use the public platform binding contract for both inspection and edits.
            bool safe = true;
            foreach (var behaviour in ParameterSpecialization.Behaviours(animations.Controllers.SelectMany(c => c.AllReachableNodes())).Distinct())
                VRChatPlatformAnimatorBindings.Instance.RemapPathsInStateBehaviour(behaviour, path =>
                {
                    var targets = AnimationUsage.Resolve(avatar, avatar, path ?? "", animations.Paths);
                    if (targets.Contains(frame) || targets.Any(moved.Contains) && targets.Count != 1) safe = false;
                    string actual = path != null && animations.Paths != null && animations.Paths.TryGetValue(path, out var mapped) ? mapped : path;
                    if (actual != null && destinationPaths.Contains(actual) && !targets.Any(moved.Contains)) safe = false;
                    return path;
                });
            return safe;
        }

        private bool ObservesSubtree(string prefix)
        {
            if (observedPaths == null)
            {
                observedPaths = new HashSet<string>(StringComparer.Ordinal);
                void Observe(string path)
                {
                    if (path != null && animations.Paths != null && animations.Paths.TryGetValue(path, out var mapped)) path = mapped;
                    if (path != null) observedPaths.Add(path);
                }
                foreach (var use in animations.Bindings) Observe(use.Binding.path);
                foreach (var mask in animations.Controllers.SelectMany(c => c.Layers).Select(l => l.AvatarMask).Where(m => m != null).Distinct())
                    foreach (var element in mask.Elements) Observe(element.Key);
                foreach (var behaviour in ParameterSpecialization.Behaviours(animations.Controllers.SelectMany(c => c.AllReachableNodes())).Distinct())
                    VRChatPlatformAnimatorBindings.Instance.RemapPathsInStateBehaviour(behaviour, path => { Observe(path); return path; });
            }
            return observedPaths.Any(p => p == prefix || p.StartsWith(prefix + "/", StringComparison.Ordinal));
        }

        private bool HasHumanoidNameInterface(Transform target)
        {
            foreach (var animator in animations.Avatar.GetComponentsInChildren<Animator>(true))
                if (animator.isHuman && animator.avatar != null && animator.avatar.humanDescription.skeleton.Any(b => b.name == target.name)) return true;
            return false;
        }

        // Design reference: AAO MergeBoneProcessor uses a parent/name/counter
        // suffix. Independently use a stable original-path hash for reproducible
        // output, then resolve actual and unresolved-interface collisions.
        private static string PathHash(string path)
        {
            uint hash = 2166136261;
            unchecked { foreach (char value in path) hash = (hash ^ value) * 16777619; }
            return hash.ToString("x8", System.Globalization.CultureInfo.InvariantCulture);
        }

        internal static void CommitNativePaths(AvatarAnimationIndex animations, Dictionary<string, string> map)
        {
            new AnimationIndex(animations.Controllers.Cast<VirtualNode>()).RewritePaths(map);
            foreach (var behaviour in ParameterSpecialization.Behaviours(animations.Controllers.SelectMany(c => c.AllReachableNodes())).Distinct())
                VRChatPlatformAnimatorBindings.Instance.RemapPathsInStateBehaviour(behaviour,
                    path => path != null && map.TryGetValue(path, out var relocated) ? relocated : path);
        }
    }
}
