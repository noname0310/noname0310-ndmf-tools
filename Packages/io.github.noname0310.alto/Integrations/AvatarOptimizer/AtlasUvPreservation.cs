using System;
using System.Collections.Generic;
using Anatawa12.AvatarOptimizer.API;
using nadena.dev.ndmf;
using Noname.AvatarTools.MaterialAtlas;
using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.ObjectOptimization
{
    [InitializeOnLoad]
    internal static class AtlasUvPreservation
    {
        static AtlasUvPreservation() => MaterialAtlasBackend.PreserveOriginalUv = Evacuate;

        private static void Evacuate(BuildContext context, SkinnedMeshRenderer renderer)
        {
            if (!UVUsageCompabilityAPI.IsTexCoordUsed(renderer, 0)) return;
            var source = renderer.sharedMesh;
            int channel = 7;
            while (channel > 0 && source.HasVertexAttribute((UnityEngine.Rendering.VertexAttribute)((int)UnityEngine.Rendering.VertexAttribute.TexCoord0 + channel))) channel--;
            if (channel == 0) throw new InvalidOperationException("No free UV channel can preserve AAO mesh-removal masks: " + renderer.name);
            var mesh = UnityEngine.Object.Instantiate(source); mesh.name = source.name + " (Preserved UV0)";
            var uv = new List<Vector4>(); mesh.GetUVs(0, uv); mesh.SetUVs(channel, uv);
            renderer.sharedMesh = mesh; context.AssetSaver.SaveAsset(mesh); ObjectRegistry.RegisterReplacedObject(source, mesh);
            UVUsageCompabilityAPI.RegisterTexCoordEvacuation(renderer, 0, channel);
        }
    }
}
