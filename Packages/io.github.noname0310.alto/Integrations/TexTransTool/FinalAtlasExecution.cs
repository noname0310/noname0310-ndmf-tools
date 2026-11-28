using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using nadena.dev.ndmf;
using net.rs64.TexTransTool.TextureAtlas;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.MaterialAtlas
{
    // TTT 1.0.2 exposes atlas settings publicly, but its execution domain is
    // internal. Isolate that versioned API here. A fresh renderer domain owns
    // only ALTO's private staging materials; no other TTT behaviour is replayed.
    internal static class FinalAtlasExecution
    {
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        internal static void Apply(BuildContext context, MaterialAtlasState state)
        {
            if (state.Plans.Count == 0) return;
            var editor = AppDomain.CurrentDomain.GetAssemblies().SingleOrDefault(a => a.GetName().Name == "net.rs64.tex-trans-tool.editor");
            var domainType = editor?.GetType("net.rs64.TexTransTool.RenderersDomain");
            var holderType = editor?.GetType("net.rs64.TexTransTool.TempAssetHolder");
            var apply = typeof(AtlasTexture).GetMethod("Apply", Instance);
            var transferred = holderType?.GetField("Transferred", Instance);
            var originEqual = domainType?.GetMethod("OriginEqual", Instance);
            if (domainType == null || holderType == null || apply == null || transferred == null || originEqual == null)
                throw new InvalidOperationException("ALTO final atlas execution requires the supported TexTransTool 1.0.2 domain API.");
            var holder = Activator.CreateInstance(holderType);
            object domain = null;
            var backend = context.GetState<TexTransAtlasState>();
            var renderers = state.Plans.Select(p => p.Renderer).Distinct().ToList();
            var sourceMeshes = renderers.ToDictionary(r => r, r => r is SkinnedMeshRenderer skin ? skin.sharedMesh : r.GetComponent<MeshFilter>().sharedMesh);
            bool success = false;
            try
            {
                domain = Activator.CreateInstance(domainType, new object[] { renderers, holder });
                try
                {
                    foreach (var atlas in backend.Atlases)
                        if (atlas.AtlasTargetMaterials.Count != 0) apply.Invoke(atlas, new[] { domain });
                }
                finally { ((IDisposable)domain).Dispose(); }
                // GPU readback and compression have finished. Transfer ownership
                // through the current build saver, including the SDK finalizer's.
                foreach (var asset in (IEnumerable<Object>)transferred.GetValue(holder))
                    if (asset != null) (state.SaveAsset ?? context.AssetSaver.SaveAsset)(asset);
                foreach (var plan in state.Plans)
                {
                    var materials = plan.Renderer.sharedMaterials;
                    if (materials.Length != plan.Groups.Length)
                        throw new InvalidOperationException("TexTransTool changed the final atlas material-slot interface.");
                    for (int slot = 0; slot < materials.Length; slot++)
                    {
                        int group = plan.Groups[slot];
                        if (group < 0) continue;
                        var carrier = backend.Carriers[group];
                        if (!(bool)originEqual.Invoke(domain, new object[] { carrier, materials[slot] }))
                            throw new InvalidOperationException("TexTransTool did not produce the requested final atlas on " + plan.Renderer.name);
                        backend.VerifiedOutputs.Add((materials[slot], group));
                        ObjectRegistry.TryRegisterReplacedObject(ObjectRegistry.GetReference(carrier), materials[slot]);
                    }
                    var source = sourceMeshes[plan.Renderer];
                    var output = plan.Renderer is SkinnedMeshRenderer skin ? skin.sharedMesh : plan.Renderer.GetComponent<MeshFilter>().sharedMesh;
                    if (source == output) continue;
                    ObjectRegistry.TryRegisterReplacedObject(ObjectRegistry.GetReference(source), output);
                    if (plan.Renderer is SkinnedMeshRenderer skinned)
                        context.GetState<BlendShapeOptimization.OptimizationState>().Provenance.RecordMeshRewrite(skinned, source, output);
                }
                success = true;
            }
            catch (TargetInvocationException exception) when (exception.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }
            finally
            {
                if (!success)
                    foreach (var asset in (IEnumerable<Object>)transferred.GetValue(holder))
                        if (asset != null && !UnityEditor.EditorUtility.IsPersistent(asset)) Object.DestroyImmediate(asset);
            }
        }
    }
}
