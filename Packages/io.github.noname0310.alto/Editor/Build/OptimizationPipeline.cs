using nadena.dev.ndmf;
using Noname.AvatarTools.AnimatorOptimization;
using Noname.AvatarTools.BlendShapeOptimization;
using Noname.AvatarTools.Expressions;
using Noname.AvatarTools.MaterialAtlas;
using Noname.AvatarTools.Materials;
using Noname.AvatarTools.Physics;
using Noname.AvatarTools.MeshReduction;
using Noname.AvatarTools.Meshes;
using Noname.AvatarTools.ObjectOptimization;
using Noname.AvatarTools.ParameterPolicy;
using Noname.AvatarTools.Occlusion;

namespace Noname.AvatarTools.Build
{
    // Staging was reviewed against AAO 1.9.20 OptimizerPlugin. The analyses and
    // mutation contracts are owned by this package; AAO remains optional. See CREDITS.md.
    internal static class OptimizationPipeline
    {
        internal static void BeforeAtlas(BuildContext context) => OptimizationSession.RunBoundary(context, "before atlas", () =>
        {
            using var shapeBoundary = context.GetState<OptimizationState>().Provenance.BeginBoundary();
            OptimizationSession.Step(context, "BlendShape input provenance", AnalysisDomain.None, ALTOPlugin.PrepareInputs);
            OptimizationSession.Step(context, "converted material provenance", AnalysisDomain.None, MaterialAtlasPass.RegisterEarlyCopies);
            OptimizationSession.Step(context, "generated audio exclusions", AnalysisDomain.Hierarchy | AnalysisDomain.Animation, AudioOutputFilterPass.Apply);
            OptimizationSession.Step(context, "explicit BlendShape region exclusions", AnalysisDomain.Meshes, BlendShapeExclusionPass.Apply);
            OptimizationSession.Step(context, "rendering exclusions", AnalysisDomain.Hierarchy | AnalysisDomain.Meshes | AnalysisDomain.Materials | AnalysisDomain.Animation,
                MaterialFilterPass.Apply);
            OptimizationSession.Step(context, "mesh renderer settings", AnalysisDomain.Hierarchy | AnalysisDomain.Meshes, MeshRendererSettingsOverridePass.Apply);
            OptimizationSession.Step(context, "parameter specialization", AnalysisDomain.Animation, ParameterFreezePass.Apply);
            OptimizationSession.Step(context, "physics filtering", AnalysisDomain.Physics | AnalysisDomain.Hierarchy | AnalysisDomain.Animation, PhysBoneFilterPass.Apply);
            OptimizationSession.Step(context, "Contact filtering", AnalysisDomain.ExternalInputs | AnalysisDomain.Hierarchy | AnalysisDomain.Animation, ContactFilterPass.Apply);
            OptimizationFixedPoint.PrepareInputs(context);
            OcclusionPass.ChooseStage(context);
            if (!context.GetState<OcclusionBuildState>().AfterMaterialConversion)
            {
                OptimizationSession.Step(context, "occluded region removal", AnalysisDomain.Meshes, OcclusionPass.Apply);
                OptimizationSession.Step(context, "BlendShape simplification", AnalysisDomain.Animation | AnalysisDomain.Meshes | AnalysisDomain.Hierarchy, ALTOPlugin.Apply);
            }
            OptimizationSession.Step(context, "static material texture baking", AnalysisDomain.Materials | AnalysisDomain.Animation, MaterialOptimizationPass.BakeBeforeAtlas);
        });

        internal static void AfterAtlas(BuildContext context) => OptimizationSession.RunBoundary(context, "after atlas", () =>
        {
            using var shapeBoundary = context.GetState<OptimizationState>().Provenance.BeginBoundary();
            OptimizationSession.Step(context, "material conversion after generators", AnalysisDomain.Materials | AnalysisDomain.Animation, MaterialAtlasPass.ConvertAfterGenerators);
            OptimizationSession.Step(context, "converted BlendShape input provenance", AnalysisDomain.None, ALTOPlugin.PrepareInputs);
            if (context.GetState<MaterialAtlasState>().Pending)
            {
                OptimizationFixedPoint.PrepareInputs(context, inspectMaterials: true);
            }
            if (context.GetState<OcclusionBuildState>().AfterMaterialConversion)
            {
                // Inspect actual converted shader/animation output. Do not assume
                // that a requested conversion will succeed or remove an alpha pass.
                // Keep region morphs until this point; atlas vertex splitting is
                // handled by extracting masks from the current generated mesh.
                OptimizationSession.Step(context, "occluded regions after material conversion", AnalysisDomain.Meshes, OcclusionPass.Apply);
                OptimizationSession.Step(context, "BlendShapes after occlusion", AnalysisDomain.Animation | AnalysisDomain.Meshes | AnalysisDomain.Hierarchy, ALTOPlugin.Apply);
            }
            OptimizationSession.Step(context, "reduction input capture", AnalysisDomain.None, MeshReductionPass.Prepare);
            OptimizationSession.Step(context, "mesh merge input capture", AnalysisDomain.None, MeshOptimizationPass.Prepare);
            OptimizationSession.Step(context, "material optimization input capture", AnalysisDomain.None, MaterialOptimizationPass.Prepare);
            OptimizationSession.Step(context, "physics optimization input capture", AnalysisDomain.None, PhysBoneOptimizationPass.Prepare);
            OptimizationSession.Step(context, "atlas selection input capture", AnalysisDomain.None, MaterialAtlasPass.CaptureInputs);
        });

        internal static void AfterExternalOptimizers(BuildContext context) => OptimizationSession.RunBoundary(context, "after external optimizers", () =>
        {
            MaterialAtlasPass.CaptureMappedInputs(context);
            OptimizationSession.Step(context, "late generated output filtering", AnalysisDomain.ExternalInputs | AnalysisDomain.Hierarchy |
                AnalysisDomain.Animation | AnalysisDomain.Physics | AnalysisDomain.Meshes | AnalysisDomain.Materials, GeneratedOutputFilterPass.Apply);
            OptimizationSession.Step(context, "material data simplification", AnalysisDomain.Materials | AnalysisDomain.Animation, MaterialOptimizationPass.Apply);
            OptimizationSession.Step(context, "combined parameter validation", AnalysisDomain.Animation, ParameterFreezePass.Apply);
            OptimizationSession.Step(context, "final initial visibility policy", AnalysisDomain.Animation | AnalysisDomain.Hierarchy, InitialVisibilityPolicy.Apply);
            OptimizationFixedPoint.Apply(context);
            MaterialAtlasPass.ValidateOmittedInputs(context);
        });

        internal static void AfterMeshes(BuildContext context) => OptimizationSession.RunBoundary(context, "after meshes", () =>
        {
            if (context.GetState<MeshOptimizationState>().Enabled || StaticTransformOptimizationPass.Enabled(context) ||
                context.GetState<OptimizationState>().Scopes.Count != 0)
            {
                OptimizationFixedPoint.Apply(context, includeMeshes: true);
            }
            // Run expression cleanup once, against the final surviving consumers. The SDK
            // finalizer still rechecks late generated/compressed parameter declarations.
            MaterialAtlasPass.ValidateOmittedInputs(context);
            AvatarOutputSdkFinalizer.Capture(context);
            OptimizationSession.Step(context, "expression cleanup", AnalysisDomain.ExternalInputs, ExpressionOptimizationPass.Apply);
            if (!AvatarOutputSdkFinalizer.HasSession(context)) PhysBoneFilterPass.CheckBudget(context);
        });
    }
}
