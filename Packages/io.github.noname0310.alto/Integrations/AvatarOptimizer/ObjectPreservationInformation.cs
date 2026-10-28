using Anatawa12.AvatarOptimizer.API;
using UnityEngine;

namespace Noname.AvatarTools.ObjectOptimization
{
    [ComponentInformation(typeof(ALTOTraceAndOptimizeObjects))]
    internal sealed class ObjectPreservationInformation : ComponentInformation<ALTOTraceAndOptimizeObjects>
    {
        protected override void CollectDependency(ALTOTraceAndOptimizeObjects component, ComponentDependencyCollector collector)
        {
            var descriptor = component.FindDescriptor();
            if (descriptor == null) return;
            foreach (var target in component.BuildPreservedComponents)
                if (target != null) collector.AddDependency(descriptor.transform, target).EvenIfDependantDisabled();
        }
    }
}
