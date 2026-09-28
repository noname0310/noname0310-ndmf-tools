using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using nadena.dev.ndmf.preview;
using UnityEngine;

namespace Noname.AvatarTools.Editor
{
    internal sealed class NormalFieldOffsetPreview : IRenderFilter
    {
        private static readonly TogglablePreviewNode EnableNode = TogglablePreviewNode.Create(
            () => "Normal Field Offset", "noname.normal-field-offset/preview", true);

        // The body can be hidden and still be required to calculate a visible garment's field.
        public bool StrictRenderGroup => true;
        public bool IsEnabled(ComputeContext context) => context.Observe(EnableNode.IsEnabled);
        public IEnumerable<TogglablePreviewNode> GetPreviewControlNodes() { yield return EnableNode; }

        public ImmutableList<RenderGroup> GetTargetGroups(ComputeContext context)
        {
            var groups = ImmutableList.CreateBuilder<RenderGroup>();
            foreach (var root in context.GetAvatarRoots())
            {
                var components = new List<NormalFieldOffset>();
                var renderers = new HashSet<Renderer>();
                foreach (var component in context.GetComponentsInChildren<NormalFieldOffset>(root, true))
                {
                    context.Observe(component);
                    ObserveTransform(context, component.transform);
                    if (!component.enabled || NormalFieldOffsetPlugin.IsEditorOnly(component.transform)) continue;
                    var clothing = context.GetComponent<SkinnedMeshRenderer>(component.gameObject);
                    var body = component.TargetBody;
                    if (clothing != null) context.Observe(clothing);
                    if (body != null) context.Observe(body);
                    if (NormalFieldProcessor.Validate(new OffsetRequest(clothing, body, component.Offset, component.MaxDistance)) != null) continue;
                    ObserveTransform(context, body.transform);
                    if (context.GetAvatarRoot(body.gameObject) != root || NormalFieldOffsetPlugin.IsEditorOnly(body.transform)) continue;
                    components.Add(component);
                    renderers.Add(clothing);
                    renderers.Add(body);
                }
                if (components.Count > 0)
                    groups.Add(RenderGroup.For(renderers).WithData(components.ToArray(), (a, b) => a.SequenceEqual(b)));
            }
            return groups.ToImmutable();
        }

        public Task<IRenderFilterNode> Instantiate(RenderGroup group, IEnumerable<(Renderer, Renderer)> proxyPairs, ComputeContext context)
        {
            var pairs = proxyPairs.ToDictionary(p => p.Item1, p => (SkinnedMeshRenderer)p.Item2);
            foreach (var original in pairs.Keys.Cast<SkinnedMeshRenderer>())
            {
                context.Observe(original);
                if (original.sharedMesh != null) context.Observe(original.sharedMesh);
                ObserveTransform(context, original.transform);
                foreach (var bone in original.bones)
                    if (bone != null) ObserveTransform(context, bone);
            }
            var requests = new List<OffsetRequest>();
            foreach (var component in group.GetData<NormalFieldOffset[]>())
            {
                context.Observe(component);
                if (!component.enabled || component.TargetBody == null) continue;
                var original = component.GetComponent<SkinnedMeshRenderer>();
                if (!pairs.TryGetValue(original, out var clothing) || !pairs.TryGetValue(component.TargetBody, out var body)) continue;
                requests.Add(new OffsetRequest(clothing, body, component.Offset, component.MaxDistance));
            }
            var results = NormalFieldProcessor.Generate(requests);
            var reverse = pairs.ToDictionary(p => p.Value, p => p.Key);
            var outputs = results.ToDictionary(r => reverse[r.Renderer], r => r);
            // NDMF allows mesh assignments during Instantiate; bounds are applied in OnFrame.
            foreach (var result in results) result.Renderer.sharedMesh = result.Mesh;
            return Task.FromResult<IRenderFilterNode>(new Node(outputs));
        }

        private static void ObserveTransform(ComputeContext context, Transform transform)
        {
            foreach (var ancestor in context.ObservePath(transform))
            {
                context.Observe(ancestor, t => t.localToWorldMatrix);
                context.Observe(ancestor.gameObject, go => go.tag);
            }
        }

        private sealed class Node : IRenderFilterNode
        {
            private readonly Dictionary<Renderer, OffsetResult> outputs;
            internal Node(Dictionary<Renderer, OffsetResult> outputs) { this.outputs = outputs; }
            public RenderAspects WhatChanged => RenderAspects.Mesh;

            // Rebuild on every invalidation, including an upstream mesh/shape change or an edited bone.
            public Task<IRenderFilterNode> Refresh(IEnumerable<(Renderer, Renderer)> pairs, ComputeContext context, RenderAspects updatedAspects)
                => Task.FromResult<IRenderFilterNode>(null);

            public void OnFrame(Renderer original, Renderer proxy)
            {
                if (proxy is SkinnedMeshRenderer renderer && outputs.TryGetValue(original, out var result) && result.Mesh != null)
                {
                    renderer.sharedMesh = result.Mesh;
                    renderer.localBounds = result.LocalBounds;
                }
            }

            public void Dispose()
            {
                foreach (var result in outputs.Values) result.Dispose();
            }
        }
    }
}
