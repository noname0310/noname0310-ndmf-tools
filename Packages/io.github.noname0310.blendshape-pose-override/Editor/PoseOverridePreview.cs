using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using nadena.dev.ndmf.preview;
using UnityEngine;

namespace Noname.AvatarTools.Editor
{
    internal sealed class PoseOverridePreview : IRenderFilter
    {
        private static readonly TogglablePreviewNode EnableNode = TogglablePreviewNode.Create(
            () => "BlendShape Pose Override", "noname.blendshape-pose-override/preview", true);

        public bool StrictRenderGroup => true;
        public bool IsEnabled(ComputeContext context) => context.Observe(EnableNode.IsEnabled);
        public IEnumerable<TogglablePreviewNode> GetPreviewControlNodes() { yield return EnableNode; }

        public ImmutableList<RenderGroup> GetTargetGroups(ComputeContext context)
        {
            var groups = ImmutableList.CreateBuilder<RenderGroup>();
            foreach (var root in context.GetAvatarRoots())
            {
                var components = context.GetComponentsInChildren<BlendShapePoseOverride>(root, true);
                foreach (var component in components) ObserveConfiguration(context, component);
                foreach (var component in components)
                {
                    if (!component.enabled || PoseOverrideConfiguration.IsEditorOnly(component.transform)) continue;
                    if (!PoseOverrideConfiguration.TryResolve(component, out var plan, out _)) continue;
                    if (plan.Poses.Count == 0) continue;
                    groups.Add(RenderGroup.For(plan.Renderer).WithData(component, (a, b) => a == b));
                }
            }
            return groups.ToImmutable();
        }

        public Task<IRenderFilterNode> Instantiate(RenderGroup group, IEnumerable<(Renderer, Renderer)> proxyPairs, ComputeContext context)
        {
            var component = group.GetData<BlendShapePoseOverride>();
            ObserveConfiguration(context, component);
            if (!PoseOverrideConfiguration.TryResolve(component, out var plan, out _) || plan.Poses.Count == 0)
                return Task.FromResult<IRenderFilterNode>(new Node(component, null));
            var proxy = (SkinnedMeshRenderer)proxyPairs.Single(p => p.Item1 == plan.Renderer).Item2;
            var result = PoseOverrideProcessor.Generate(proxy, plan.Poses);
            result.Apply(proxy);
            return Task.FromResult<IRenderFilterNode>(new Node(component, result));
        }

        private static void ObserveConfiguration(ComputeContext context, BlendShapePoseOverride component)
        {
            // Slider changes only affect OnFrame; they do not require rebuilding every blendshape frame.
            context.Observe(component, c => (c.enabled, c.TargetMesh, c.PathMode, c.RelativePathRoot));
            context.Observe(component,
                c => c.Overrides?.Select(e => (Name: e?.BlendShapeName, Clips: e?.GetAnimations().ToArray())).ToArray(),
                (a, b) => a == null ? b == null : b != null && a.Length == b.Length && a.Zip(b, (x, y) =>
                    x.Name == y.Name && (x.Clips == null ? y.Clips == null : y.Clips != null && x.Clips.SequenceEqual(y.Clips))).All(equal => equal));
            ObservePath(context, component.transform);
            if (component.RelativePathRoot != null) ObservePath(context, component.RelativePathRoot);
            if (component.TargetMesh != null)
            {
                context.Observe(component.TargetMesh);
                ObservePath(context, component.TargetMesh.transform);
                if (component.TargetMesh.sharedMesh != null) context.Observe(component.TargetMesh.sharedMesh);
            }
            if (component.Overrides != null)
                foreach (var entry in component.Overrides)
                    if (entry != null)
                        foreach (var clip in entry.GetAnimations())
                            if (clip != null) context.Observe(clip);
        }

        private static void ObservePath(ComputeContext context, Transform transform)
        {
            foreach (var ancestor in context.ObservePath(transform))
                context.Observe(ancestor.gameObject, go => (go.name, go.tag));
        }

        private sealed class Node : IRenderFilterNode
        {
            private readonly BlendShapePoseOverride component;
            private readonly PoseOverrideResult result;

            internal Node(BlendShapePoseOverride component, PoseOverrideResult result)
            {
                this.component = component;
                this.result = result;
            }

            public RenderAspects WhatChanged => RenderAspects.Mesh | RenderAspects.Shapes;

            public Task<IRenderFilterNode> Refresh(IEnumerable<(Renderer, Renderer)> pairs, ComputeContext context, RenderAspects updatedAspects)
                => Task.FromResult<IRenderFilterNode>(null);

            public void OnFrame(Renderer original, Renderer proxy)
            {
                if (component == null || result?.Mesh == null || !(proxy is SkinnedMeshRenderer renderer)) return;
                renderer.sharedMesh = result.Mesh;
                foreach (int index in result.TargetIndices) renderer.SetBlendShapeWeight(index, 0);
                int selected = Mathf.Clamp(component.PreviewEntry, 0, result.TargetIndices.Length - 1);
                renderer.SetBlendShapeWeight(result.TargetIndices[selected], Mathf.Clamp(component.PreviewWeight, 0, 100));
            }

            public void Dispose() => result?.Dispose();
        }
    }
}
