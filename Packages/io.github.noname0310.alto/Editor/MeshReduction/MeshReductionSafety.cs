using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.BlendShapeOptimization;
using Noname.AvatarTools.ObjectOptimization;
using UnityEngine;

namespace Noname.AvatarTools.MeshReduction
{
    internal static class MeshReductionSafety
    {
        internal static Dictionary<Renderer, string> Find(GameObject avatar, VirtualControllerContext controllers,
            IReadOnlyDictionary<string, string> paths)
        {
            var hazards = new Dictionary<Renderer, string>();
            string unknown = ObjectOptimizationSafety.UnsupportedRuntime(avatar);
            if (unknown != null)
            {
                foreach (var renderer in avatar.GetComponentsInChildren<Renderer>(true)) hazards[renderer] = unknown;
                return hazards;
            }
            foreach (var entry in controllers.Controllers.Where(e => !(e.Key is Object owner) || owner != null))
            {
                var root = entry.Key is Animator animator ? animator.transform : avatar.transform;
                foreach (var clip in entry.Value.AllReachableNodes().OfType<VirtualClip>().Distinct())
                foreach (var binding in clip.GetObjectCurveBindings())
                {
                    if (binding.propertyName != "m_Mesh" ||
                        binding.type != typeof(SkinnedMeshRenderer) && binding.type != typeof(MeshFilter)) continue;
                    foreach (var target in AnimationUsage.Resolve(avatar.transform, root, binding.path, paths))
                        foreach (var renderer in target.GetComponents<Renderer>())
                            hazards[renderer] = "animated mesh reference in " + clip.Name;
                }
            }
            return hazards;
        }
    }
}
