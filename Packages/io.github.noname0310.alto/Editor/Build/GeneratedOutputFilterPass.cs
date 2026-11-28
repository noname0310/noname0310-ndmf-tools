using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using UnityEngine;

namespace Noname.AvatarTools.Build
{
    // Current-component rules are repeatable. Authored topology selections use
    // source-interface provenance, never raw indices on arbitrary merged output.
    internal static class GeneratedOutputFilterPass
    {
        internal static bool HasPendingRemovals(BuildContext context) =>
            AudioOutputFilterPass.HasPendingRemovals(context) || ContactFilterPass.HasPendingRemovals(context) ||
            PhysBoneFilterPass.HasPendingRemovals(context) || HasRendererCandidates(context) ||
            Meshes.MeshRendererSettingsOverridePass.HasPendingOutputs(context) || HasAuthoredRenderingRules(context);

        private static bool HasAuthoredRenderingRules(BuildContext context)
        {
            var state = context.GetState<RenderingFilterState>();
            return state.Scope != null && (state.Materials.Count != 0 || state.BlendShapes.Count != 0);
        }

        private static bool HasRendererCandidates(BuildContext context)
        {
            var state = context.GetState<RenderingFilterState>();
            return state.Scope != null && (state.ExcludedRendererTypes != RendererFilterTypes.None || state.RemoveAdditionalMaterialPasses) &&
                context.AvatarRootObject.GetComponentsInChildren<Renderer>(true).Any(r =>
                    AutomaticRenderingRules.RemovalCandidate(state, r) || AutomaticRenderingRules.AdditionalPassCandidate(state, r));
        }

        internal static void Apply(BuildContext context)
        {
            if (!HasPendingRemovals(context) && context.GetState<PhysBoneFilterState>().Scope == null) return;
            using var lease = OptimizationSession.Acquire(context);
            Apply(context, lease.Services.ControllerContext.Controllers
                .Where(e => !(e.Key is Object owner) || owner != null).ToDictionary(e => e.Key, e => e.Value),
                lease.Services.ObjectPathRemapper.GetVirtualToRealPathMap());
        }

        internal static void Apply(BuildContext context, IDictionary<object, VirtualAnimatorController> controllers,
            IReadOnlyDictionary<string, string> paths = null)
        {
            try
            {
                if (Meshes.MeshRendererSettingsOverridePass.HasPendingOutputs(context))
                    Meshes.MeshRendererSettingsOverridePass.Apply(context);
                AudioOutputFilterPass.Apply(context, controllers, paths);
                ContactFilterPass.Apply(context, controllers, paths);
                PhysBoneFilterPass.Apply(context, controllers, paths);
                BlendShapeExclusionPass.Apply(context, controllers, paths);
                if (HasRendererCandidates(context) || HasAuthoredRenderingRules(context)) MaterialFilterPass.Apply(context, controllers, paths);
            }
            finally
            {
                context.GetState<OptimizationAnalysis>().Invalidate(AnalysisDomain.Hierarchy | AnalysisDomain.Physics |
                    AnalysisDomain.ExternalInputs | AnalysisDomain.Animation | AnalysisDomain.Materials | AnalysisDomain.Meshes);
            }
        }
    }
}
