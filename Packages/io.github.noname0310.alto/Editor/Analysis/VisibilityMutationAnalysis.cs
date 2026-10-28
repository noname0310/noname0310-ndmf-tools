using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Noname.AvatarTools.Analysis
{
    internal static class VisibilityMutationAnalysis
    {
        internal static HashSet<Object> NativeWriters(GameObject avatar)
        {
            var result = new HashSet<Object>();
            foreach (var lod in avatar.GetComponentsInChildren<LODGroup>(true))
                foreach (var renderer in lod.GetLODs().SelectMany(l => l.renderers))
                    if (renderer != null) result.Add(renderer);
            foreach (var particles in avatar.GetComponentsInChildren<ParticleSystem>(true))
                if (particles.main.stopAction != ParticleSystemStopAction.None) result.Add(particles.gameObject);
            foreach (var trail in avatar.GetComponentsInChildren<TrailRenderer>(true))
                if (trail.autodestruct) result.Add(trail.gameObject);
            return result;
        }
    }
}
