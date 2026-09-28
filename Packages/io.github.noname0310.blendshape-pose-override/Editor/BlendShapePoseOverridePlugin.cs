using System;
using System.Collections.Generic;
using nadena.dev.ndmf;
using UnityEngine;
using Object = UnityEngine.Object;

[assembly: ExportsPlugin(typeof(Noname.AvatarTools.Editor.BlendShapePoseOverridePlugin))]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Noname.BlendShapePoseOverride.Tests")]

namespace Noname.AvatarTools.Editor
{
    [RunsOnAllPlatforms]
    public sealed class BlendShapePoseOverridePlugin : Plugin<BlendShapePoseOverridePlugin>
    {
        public override string QualifiedName => "noname.blendshape-pose-override";
        public override string DisplayName => "Noname BlendShape Pose Override";

        protected override void Configure()
        {
            // Sample intact shapes and original animation paths before MA bakes, deletes, or merges meshes.
            InPhase(BuildPhase.Transforming)
                .AfterPlugin("com.erenoa.eremorph")
                .BeforePlugin("nadena.dev.modular-avatar")
                .Run("Override blendshapes from animation poses", Apply)
                .PreviewingWith(new PoseOverridePreview());
        }

        internal static void Apply(BuildContext context)
        {
            var components = context.AvatarRootObject.GetComponentsInChildren<BlendShapePoseOverride>(true);
            var plans = new List<PoseOverridePlan>();
            foreach (var component in components)
            {
                if (!component.enabled || PoseOverrideConfiguration.IsEditorOnly(component.transform)) continue;
                if (!PoseOverrideConfiguration.TryResolve(component, out var plan, out var error))
                    throw new InvalidOperationException($"BlendShape Pose Override on '{component.name}': {error}");
                if (plan.Poses.Count > 0) plans.Add(plan);
            }
            // Generate every output before changing any renderer, so one component cannot change another's basis.
            var outputs = new List<(SkinnedMeshRenderer renderer, PoseOverrideResult result)>();
            try
            {
                foreach (var plan in plans) outputs.Add((plan.Renderer, PoseOverrideProcessor.Generate(plan.Renderer, plan.Poses)));
            }
            catch
            {
                foreach (var output in outputs) output.result.Dispose();
                throw;
            }
            foreach (var output in outputs)
            {
                ObjectRegistry.RegisterReplacedObject(output.result.Source, output.result.Mesh);
                context.AssetSaver.SaveAsset(output.result.Mesh);
                output.result.Apply(output.renderer);
            }
            foreach (var component in components) Object.DestroyImmediate(component);
        }
    }
}
