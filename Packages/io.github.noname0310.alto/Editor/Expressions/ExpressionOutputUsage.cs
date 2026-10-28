using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.BlendShapeOptimization;
using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.Expressions
{
    // Ignore proven absent hierarchy/component bindings, including duration-preservation curves.
    // Shader Properties are not a complete uniform inventory; unknown material outputs stay live.
    internal sealed class ExpressionOutputUsage
    {
        private readonly Transform avatar;
        private readonly IReadOnlyDictionary<string, string> paths;
        private readonly Dictionary<VirtualClip, HashSet<Transform>> roots = new();
        private readonly Dictionary<(VirtualClip, EditorCurveBinding), bool> cache = new();

        internal ExpressionOutputUsage(GameObject avatar, IDictionary<object, VirtualAnimatorController> controllers,
            IReadOnlyDictionary<string, string> paths)
        {
            this.avatar = avatar.transform; this.paths = paths;
            foreach (var entry in controllers)
                foreach (var clip in entry.Value.AllReachableNodes().OfType<VirtualClip>())
                {
                    if (!roots.TryGetValue(clip, out var owners)) roots.Add(clip, owners = new());
                    owners.Add(entry.Key is Animator animator ? animator.transform : avatar.transform);
                }
        }

        internal bool IsObservable(VirtualClip clip, EditorCurveBinding binding)
        {
            var key = (clip, binding);
            if (cache.TryGetValue(key, out bool result)) return result;
            if (binding.type == null || !roots.TryGetValue(clip, out var owners)) return cache[key] = true;
            if (binding.type != typeof(GameObject) && !typeof(Component).IsAssignableFrom(binding.type)) return cache[key] = true;
            foreach (var root in owners)
            {
                if (root == null) return cache[key] = true;
                foreach (var target in AnimationUsage.Resolve(avatar, root, binding.path, paths))
                    if (binding.type == typeof(GameObject) || target.GetComponents(binding.type).Length > 0 ||
                        typeof(Renderer).IsAssignableFrom(binding.type) && target.GetComponent<Renderer>() != null) return cache[key] = true;
            }
            return cache[key] = false;
        }
    }
}
