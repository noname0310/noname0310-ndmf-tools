using Noname.AvatarTools.ObjectOptimization;
using Noname.AvatarTools.ParameterPolicy;

namespace Noname.AvatarTools.Analysis
{
    // Reuse collection's proof without deleting authoring inputs. Expensive one-shot
    // consumers may omit dead inputs while their hierarchy is still needed by generators.
    internal static class RendererInputLiveness
    {
        internal static AvatarDependencyGraph Analyze(ObjectOptimizationState settings, AvatarAnimationIndex index, ShaderPropertyUsage shaders)
        {
            if (settings.Scope == null || !settings.RemoveUnusedObjects || index.UnsafeReason != null ||
                index.Reachability.FallbackReason != null || ObjectOptimizationSafety.UnsupportedRuntime(index.Avatar) != null ||
                ExternalParameterAccess.Collect().Uncertain) return null;
            var graph = AvatarDependencyGraph.Create(index.Avatar, index, settings, true, shaders);
            return graph.UnsafeReason == null ? graph : null;
        }
    }
}
