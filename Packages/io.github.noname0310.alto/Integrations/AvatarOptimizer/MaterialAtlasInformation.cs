using Anatawa12.AvatarOptimizer.API;

namespace Noname.AvatarTools.MaterialAtlas
{
    [ComponentInformation(typeof(ALTOMaterialAtlas))]
    internal sealed class MaterialAtlasInformation : ComponentInformation<ALTOMaterialAtlas>
    {
        protected override void CollectDependency(ALTOMaterialAtlas component, ComponentDependencyCollector collector)
        {
            var descriptor = component.FindDescriptor();
            if (descriptor != null) collector.AddDependency(descriptor.transform, component).EvenIfDependantDisabled();
        }

        protected override void ApplySpecialMapping(ALTOMaterialAtlas component, MappingSource mappingSource)
        {
            for (int i = 0; i < component.BuildInputRenderers.Length; i++)
            {
                var renderer = component.BuildInputRenderers[i];
                if (!ReferenceEquals(renderer, null)) component.BuildInputRenderers[i] = mappingSource.GetMappedComponent(renderer).MappedComponent;
            }
        }
    }
}
