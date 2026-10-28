using Anatawa12.AvatarOptimizer.API;

namespace Noname.AvatarTools.Meshes
{
    // Optional AAO public mapping interoperability. No internal optimization pass is called.
    [ComponentInformation(typeof(ALTOTraceAndOptimizeMeshes))]
    internal sealed class MeshOptimizationInformation : ComponentInformation<ALTOTraceAndOptimizeMeshes>
    {
        protected override void CollectDependency(ALTOTraceAndOptimizeMeshes component, ComponentDependencyCollector collector)
        {
            var descriptor = component.FindDescriptor();
            if (descriptor != null) collector.AddDependency(descriptor.transform, component).EvenIfDependantDisabled();
            // Renderer references are mapping metadata, not consumers of otherwise dead meshes.
            // Unsafe skinning inputs are consumed by our later substitution pass.
            foreach (var bone in component.BuildSkinningDependencies)
                if (bone != null) collector.AddDependency(component, bone).EvenIfDependantDisabled();
        }

        protected override void ApplySpecialMapping(ALTOTraceAndOptimizeMeshes component, MappingSource mappingSource)
        {
            for (int i = 0; i < component.BuildInputRenderers.Length; i++)
            {
                var renderer = component.BuildInputRenderers[i];
                if (!ReferenceEquals(renderer, null)) component.BuildInputRenderers[i] = mappingSource.GetMappedComponent(renderer).MappedComponent;
            }
        }
    }
}
