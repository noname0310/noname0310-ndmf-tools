using System.Linq;
using Anatawa12.AvatarOptimizer.API;
using UnityEngine;

namespace Noname.AvatarTools.BlendShapeOptimization
{
    // Keep the marker until after AAO so it sees the preservation contract during its own analysis.
    [ComponentInformation(typeof(BlendShapeOptimizationExclude))]
    internal sealed class ExclusionInformation : ComponentInformation<BlendShapeOptimizationExclude>
    {
        protected override void CollectDependency(BlendShapeOptimizationExclude component, ComponentDependencyCollector collector)
        {
            collector.AddDependency(component.GetComponent<SkinnedMeshRenderer>(), component);
        }

        protected override void CollectMutations(BlendShapeOptimizationExclude component, ComponentMutationsCollector collector)
        {
            if (!component.enabled) return;
            var renderer = component.GetComponent<SkinnedMeshRenderer>();
            if (renderer != null)
                collector.ModifyProperties(renderer, component.BlendShapes.Select(n => "blendShape." + n));
        }
    }
}
