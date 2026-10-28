using Anatawa12.AvatarOptimizer.API;

namespace Noname.AvatarTools.Physics
{
    [ComponentInformation(typeof(ALTOTraceAndOptimizePhysBones))]
    internal sealed class PhysBoneOptimizationInformation : ComponentInformation<ALTOTraceAndOptimizePhysBones>
    {
        protected override void CollectDependency(ALTOTraceAndOptimizePhysBones component, ComponentDependencyCollector collector)
        {
            var descriptor = component.FindDescriptor();
            if (descriptor != null) collector.AddDependency(descriptor.transform, component).EvenIfDependantDisabled();
        }
        protected override void ApplySpecialMapping(ALTOTraceAndOptimizePhysBones component, MappingSource mappingSource)
        {
            for (int i = 0; i < component.BuildInputs.Length; i++)
            {
                var value = component.BuildInputs[i];
                if (!ReferenceEquals(value, null)) component.BuildInputs[i] = mappingSource.GetMappedComponent(value).MappedComponent;
            }
        }
    }
}
