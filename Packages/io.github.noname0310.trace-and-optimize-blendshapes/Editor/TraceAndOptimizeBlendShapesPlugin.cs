using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using Object = UnityEngine.Object;

[assembly: ExportsPlugin(typeof(Noname.AvatarTools.BlendShapeOptimization.TraceAndOptimizeBlendShapesPlugin))]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Noname.TraceAndOptimizeBlendShapes.Tests")]

namespace Noname.AvatarTools.BlendShapeOptimization
{
    internal sealed class ScopeSettings
    {
        internal Transform Root;
        internal HashSet<SkinnedMeshRenderer> OriginalRenderers;
        internal bool Mmd, ZeroReset;
        internal int Depth;
    }

    internal sealed class OptimizationState
    {
        internal readonly List<ScopeSettings> Scopes = new();
    }

    [RunsOnAllPlatforms]
    public sealed class TraceAndOptimizeBlendShapesPlugin : Plugin<TraceAndOptimizeBlendShapesPlugin>
    {
        public override string QualifiedName => "noname.trace-and-optimize-blendshapes";
        public override string DisplayName => "Noname Trace And Optimize BlendShapes";

        protected override void Configure()
        {
            InPhase(BuildPhase.Resolving).Run("Capture BlendShape optimization scopes", Capture);
            InPhase(BuildPhase.Optimizing)
                .BeforePlugin("com.anatawa12.avatar-optimizer")
                .Run("Optimize BlendShapes and zero-reset curves", Apply);
            InPhase(BuildPhase.Optimizing).AfterPlugin("com.anatawa12.avatar-optimizer").Run("Remove BlendShape optimization markers", context =>
            {
                foreach (var marker in context.AvatarRootObject.GetComponentsInChildren<BlendShapeOptimizationExclude>(true))
                    Object.DestroyImmediate(marker);
                foreach (var component in context.AvatarRootObject.GetComponentsInChildren<TraceAndOptimizeBlendShapes>(true))
                    Object.DestroyImmediate(component);
            });
        }

        internal static bool IsEditorOnly(Transform transform)
        {
            for (; transform != null; transform = transform.parent)
                if (transform.CompareTag("EditorOnly")) return true;
            return false;
        }

        internal static void Capture(BuildContext context)
        {
            var state = context.GetState<OptimizationState>();
            foreach (var component in context.AvatarRootObject.GetComponentsInChildren<TraceAndOptimizeBlendShapes>(true))
            {
                if (!component.enabled || IsEditorOnly(component.transform)) continue;
                var descriptor = component.FindDescriptor();
                var root = component.ScopeRoot != null ? component.ScopeRoot : component.transform;
                if (descriptor == null || descriptor.gameObject != context.AvatarRootObject ||
                    !root.IsChildOf(context.AvatarRootTransform))
                    throw new InvalidOperationException("Trace And Optimize BlendShapes requires a scope inside its avatar.");
                int depth = 0;
                for (var parent = root; parent != context.AvatarRootTransform; parent = parent.parent) depth++;
                state.Scopes.Add(new ScopeSettings { Root = root, Depth = depth,
                    OriginalRenderers = new HashSet<SkinnedMeshRenderer>(root.GetComponentsInChildren<SkinnedMeshRenderer>(true)),
                    Mmd = component.PreserveMmdBlendShapes, ZeroReset = component.OptimizeZeroResetAnimations });
            }
            // An equally specific overlap has no meaningful priority. Require matching settings instead of build-order dependence.
            foreach (var group in state.Scopes.GroupBy(s => s.Root))
                if (group.Select(s => (s.Mmd, s.ZeroReset)).Distinct().Count() > 1)
                    throw new InvalidOperationException("BlendShape optimization components with the same scope must use matching settings.");
        }

        internal static void Apply(BuildContext context)
        {
            var scopes = context.GetState<OptimizationState>().Scopes;
            foreach (var component in context.AvatarRootObject.GetComponentsInChildren<TraceAndOptimizeBlendShapes>(true))
                Object.DestroyImmediate(component);
            if (scopes.Count == 0) return;
            var watch = Stopwatch.StartNew();
            // Activate lazily: avatars without this component must not pay for cloning/indexing controllers.
            var services = context.ActivateExtensionContextRecursive<AnimatorServicesContext>();
            try
            {
                var descriptor = context.AvatarRootObject.GetComponent<VRCAvatarDescriptor>();
                var usage = AnimationUsage.Collect(context.AvatarRootObject, services.ControllerContext,
                    services.ObjectPathRemapper.GetVirtualToRealPathMap());
                var plans = new List<OptimizationPlan>();
                foreach (var renderer in context.AvatarRootObject.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (renderer.sharedMesh == null || IsEditorOnly(renderer.transform)) continue;
                    var scope = scopes.Where(s => s.OriginalRenderers.Contains(renderer) ||
                        (s.Root != null && renderer.transform.IsChildOf(s.Root))).OrderByDescending(s => s.Depth).FirstOrDefault();
                    if (scope == null) continue;
                    var preserve = ProtectedNames(renderer, descriptor, scope.Mmd);
                    PreserveForOtherOptimizers(renderer, preserve);
                    plans.Add(OptimizationPlan.Create(renderer, usage, scope.ZeroReset, preserve));
                }
                var eyelidNames = CaptureEyelids(descriptor);
                var outputs = new List<(OptimizationPlan plan, Mesh mesh)>();
                try
                {
                    foreach (var plan in plans.Where(p => p.HasChanges))
                        outputs.Add((plan, MeshOptimization.Generate(plan, MeshOptimization.CreateRecipe(plan))));
                }
                catch
                {
                    foreach (var output in outputs) Object.DestroyImmediate(output.mesh);
                    throw;
                }
                foreach (var (plan, mesh) in outputs)
                {
                    ObjectRegistry.RegisterReplacedObject(plan.Source, mesh);
                    context.AssetSaver.SaveAsset(mesh);
                    MeshOptimization.Apply(plan, mesh);
                    plan.RewriteCurves();
                }
                if (descriptor != null && outputs.Any(o => o.plan.Renderer == descriptor.customEyeLookSettings.eyelidsSkinnedMesh))
                    RestoreEyelids(descriptor, eyelidNames);
                UnityEngine.Debug.Log($"[Noname BlendShapes] {outputs.Count} meshes, {usage.ClipCount} clips: " +
                    $"froze {plans.Sum(p => p.Frozen.Count)} shapes; merged {plans.Sum(p => p.Groups.Sum(g => g.Inputs.Count))} " +
                    $"zero-reset shapes into {plans.Sum(p => p.Groups.Count)}; " +
                    $"{watch.ElapsedMilliseconds} ms.", context.AvatarRootObject);
                foreach (var plan in plans.Where(p => p.Skipped.Count > 0))
                    UnityEngine.Debug.Log($"[Noname BlendShapes] Preserved {plan.Skipped.Count} unsupported inputs on '{plan.Renderer.name}': " +
                        string.Join("; ", plan.Skipped.Take(5)), plan.Renderer);
            }
            finally
            {
                context.DeactivateExtensionContext<AnimatorServicesContext>();
                context.DeactivateExtensionContext<VirtualControllerContext>();
            }
        }

        internal static HashSet<string> ProtectedNames(SkinnedMeshRenderer renderer, VRCAvatarDescriptor descriptor, bool mmd)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            var marker = renderer.GetComponent<BlendShapeOptimizationExclude>();
            if (marker != null && marker.enabled) names.UnionWith(marker.BlendShapes);
            if (mmd) names.UnionWith(MmdMorphNames.All);
            if (descriptor != null)
            {
                if (descriptor.VisemeSkinnedMesh == renderer)
                {
                    if (descriptor.VisemeBlendShapes != null) names.UnionWith(descriptor.VisemeBlendShapes.Where(n => n != null));
                    if (!string.IsNullOrEmpty(descriptor.MouthOpenBlendShapeName)) names.Add(descriptor.MouthOpenBlendShapeName);
                }
                if (descriptor.customEyeLookSettings.eyelidsSkinnedMesh == renderer)
                    names.UnionWith(CaptureEyelids(descriptor).Where(n => n != null));
            }
            return names;
        }

        private static void PreserveForOtherOptimizers(SkinnedMeshRenderer renderer, HashSet<string> names)
        {
            var existing = names.Where(n => renderer.sharedMesh.GetBlendShapeIndex(n) >= 0).ToList();
            if (existing.Count == 0) return;
            var marker = renderer.GetComponent<BlendShapeOptimizationExclude>();
            if (marker == null) marker = renderer.gameObject.AddComponent<BlendShapeOptimizationExclude>();
            marker.enabled = true;
            marker.BlendShapes = existing;
        }

        internal static string[] CaptureEyelids(VRCAvatarDescriptor descriptor)
        {
            if (descriptor == null) return Array.Empty<string>();
            var settings = descriptor.customEyeLookSettings;
            var mesh = settings.eyelidsSkinnedMesh != null ? settings.eyelidsSkinnedMesh.sharedMesh : null;
            return (settings.eyelidsBlendshapes ?? Array.Empty<int>()).Select(i =>
                mesh != null && i >= 0 && i < mesh.blendShapeCount ? mesh.GetBlendShapeName(i) : null).ToArray();
        }

        internal static void RestoreEyelids(VRCAvatarDescriptor descriptor, string[] names)
        {
            if (descriptor == null) return;
            var settings = descriptor.customEyeLookSettings;
            if (settings.eyelidsSkinnedMesh == null || settings.eyelidsSkinnedMesh.sharedMesh == null) return;
            var mesh = settings.eyelidsSkinnedMesh.sharedMesh;
            settings.eyelidsBlendshapes = names.Select(n => n == null ? -1 : mesh.GetBlendShapeIndex(n)).ToArray();
            descriptor.customEyeLookSettings = settings;
        }
    }
}
