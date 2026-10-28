using Anatawa12.AvatarOptimizer.API;
using UnityEngine;

namespace Noname.AvatarTools.MeshReduction
{
    [ComponentInformation(typeof(ALTOMeshReduction))]
    internal sealed class MeshReductionInformation : ComponentInformation<ALTOMeshReduction>
    {
        protected override void CollectDependency(ALTOMeshReduction component, ComponentDependencyCollector collector)
        {
            var descriptor = component.FindDescriptor();
            if (descriptor != null) collector.AddDependency(descriptor.transform, component).EvenIfDependantDisabled();
            // Selection references are authoring metadata, not runtime consumers. Do not retain dead meshes.
        }

        protected override void ApplySpecialMapping(ALTOMeshReduction component, MappingSource mappingSource)
        {
            for (int i = 0; i < component.BuildInputRenderers.Length; i++)
            {
                var renderer = component.BuildInputRenderers[i];
                // AAO may have destroyed the source Unity object, but its instance identity still maps to the result.
                if (!ReferenceEquals(renderer, null))
                    component.BuildInputRenderers[i] = mappingSource.GetMappedComponent(renderer).MappedComponent;
            }
        }
    }
}
