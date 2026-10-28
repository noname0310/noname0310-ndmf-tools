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
            OptimizationSession.Step(context, "converted material provenance", AnalysisDomain.None, MaterialAtlasPass.RegisterEarlyCopies);
            OptimizationSession.Step(context, "explicit BlendShape region exclusions", AnalysisDomain.Meshes, BlendShapeExclusionPass.Apply);
            OptimizationSession.Step(context, "rendering exclusions", AnalysisDomain.Hierarchy | AnalysisDomain.Meshes | AnalysisDomain.Materials | AnalysisDomain.Animation,
                MaterialFilterPass.Apply);
            OptimizationSession.Step(context, "mesh renderer settings", AnalysisDomain.Hierarchy | AnalysisDomain.Meshes, MeshRendererSettingsOverridePass.Apply);
            OptimizationSession.Step(context, "parameter specialization", AnalysisDomain.Animation, ParameterFreezePass.Apply);
            OptimizationSession.Step(context, "physics filtering", AnalysisDomain.Physics | AnalysisDomain.Hierarchy | AnalysisDomain.Animation, PhysBoneFilterPass.Apply);
            OptimizationSession.Step(context, "Contact filtering", AnalysisDomain.ExternalInputs | AnalysisDomain.Hierarchy | AnalysisDomain.Animation, ContactFilterPass.Apply);
            OptimizationSession.Step(context, "Animator simplification", AnalysisDomain.Animation | AnalysisDomain.Materials, AnimatorOptimizationPass.Apply);
            OptimizationSession.Step(context, "initial visibility policy", AnalysisDomain.Animation | AnalysisDomain.Hierarchy, InitialVisibilityPolicy.Apply);
            OptimizationSession.Step(context, "object simplification", AnalysisDomain.Animation | AnalysisDomain.Hierarchy | AnalysisDomain.Physics, ObjectOptimizationPass.Apply);
            OcclusionPass.ChooseStage(context);
            if (!context.GetState<OcclusionBuildState>().AfterMaterialConversion)
            {
                OptimizationSession.Step(context, "occluded region removal", AnalysisDomain.Meshes, OcclusionPass.Apply);
                OptimizationSession.Step(context, "BlendShape simplification", AnalysisDomain.Animation | AnalysisDomain.Meshes | AnalysisDomain.Hierarchy, ALTOPlugin.Apply);
            }
            OptimizationSession.Step(context, "static material texture baking", AnalysisDomain.Materials | AnalysisDomain.Animation, MaterialOptimizationPass.BakeBeforeAtlas);
            OptimizationSession.Step(context, "atlas preparation", AnalysisDomain.Hierarchy | AnalysisDomain.Materials | AnalysisDomain.Meshes, MaterialAtlasPass.Prepare);
        });

        internal static void AfterAtlas(BuildContext context) => OptimizationSession.RunBoundary(context, "after atlas", () =>
        {
            OptimizationSession.Step(context, "atlas material finalization", AnalysisDomain.Hierarchy | AnalysisDomain.Materials | AnalysisDomain.Meshes, MaterialAtlasPass.Finish);
            if (context.GetState<MaterialAtlasState>().Prepared)
            {
                OptimizationSession.Step(context, "material output simplification", AnalysisDomain.Animation | AnalysisDomain.Materials, AnimatorOptimizationPass.ApplyAfterMaterials);
                OptimizationSession.Step(context, "initial visibility after materials", AnalysisDomain.Animation | AnalysisDomain.Hierarchy, InitialVisibilityPolicy.Apply);
                OptimizationSession.Step(context, "object simplification after materials", AnalysisDomain.Animation | AnalysisDomain.Hierarchy | AnalysisDomain.Physics, ObjectOptimizationPass.Apply);
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
        });

        internal static void AfterExternalOptimizers(BuildContext context) => OptimizationSession.RunBoundary(context, "after external optimizers", () =>
        {
            OptimizationSession.Step(context, "late Contact filtering", AnalysisDomain.ExternalInputs | AnalysisDomain.Hierarchy | AnalysisDomain.Animation, ContactFilterPass.Apply);
            OptimizationSession.Step(context, "material data simplification", AnalysisDomain.Materials | AnalysisDomain.Animation, MaterialOptimizationPass.Apply);
            OptimizationSession.Step(context, "combined parameter validation", AnalysisDomain.Animation, ParameterFreezePass.Apply);
            OptimizationSession.Step(context, "physics data simplification", AnalysisDomain.Physics | AnalysisDomain.Hierarchy | AnalysisDomain.Animation, PhysBoneOptimizationPass.Apply);
            OptimizationSession.Step(context, "final initial visibility policy", AnalysisDomain.Animation | AnalysisDomain.Hierarchy, InitialVisibilityPolicy.Apply);
            OptimizationFixedPoint.Apply(context);
        });

        internal static void AfterMeshes(BuildContext context) => OptimizationSession.RunBoundary(context, "after meshes", () =>
        {
            if (context.GetState<MeshOptimizationState>().Changed)
            {
                OptimizationFixedPoint.Apply(context);
            }
            // Run expression cleanup once, against the final surviving consumers. The SDK
            // finalizer still rechecks late generated/compressed parameter declarations.
            AvatarOutputSdkFinalizer.Capture(context);
            OptimizationSession.Step(context, "expression cleanup", AnalysisDomain.ExternalInputs, ExpressionOptimizationPass.Apply);
        });
    }
}
