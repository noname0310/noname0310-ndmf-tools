using Anatawa12.AvatarOptimizer.API;

namespace Noname.AvatarTools.Materials
{
    [ComponentInformation(typeof(ALTOTraceAndOptimizeMaterials))]
    internal sealed class MaterialOptimizationInformation : ComponentInformation<ALTOTraceAndOptimizeMaterials>
    {
        protected override void CollectDependency(ALTOTraceAndOptimizeMaterials component, ComponentDependencyCollector collector)
        {
            var descriptor = component.FindDescriptor();
            if (descriptor != null) collector.AddDependency(descriptor.transform, component).EvenIfDependantDisabled();
        }
        protected override void ApplySpecialMapping(ALTOTraceAndOptimizeMaterials component, MappingSource mappingSource)
        {
            for (int i = 0; i < component.BuildInputRenderers.Length; i++)
            {
                var renderer = component.BuildInputRenderers[i];
                if (!ReferenceEquals(renderer, null)) component.BuildInputRenderers[i] = mappingSource.GetMappedComponent(renderer).MappedComponent;
            }
        }
    }
}
