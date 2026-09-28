using System;
using System.Collections.Generic;
using nadena.dev.ndmf;
using UnityEngine;
using Object = UnityEngine.Object;

[assembly: ExportsPlugin(typeof(Noname.AvatarTools.Editor.NormalFieldOffsetPlugin))]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Noname.NormalFieldOffset.Tests")]

namespace Noname.AvatarTools.Editor
{
    [RunsOnAllPlatforms]
    public sealed class NormalFieldOffsetPlugin : Plugin<NormalFieldOffsetPlugin>
    {
        public override string QualifiedName => "noname.normal-field-offset";
        public override string DisplayName => "Noname Normal Field Offset";

        protected override void Configure()
        {
            // MA can delete covered body triangles and merge bones. Sample the intact setup body first.
            InPhase(BuildPhase.Transforming)
                .AfterPlugin("com.erenoa.eremorph")
                .BeforePlugin("nadena.dev.modular-avatar")
                .Run("Offset clothing along body normals", Apply)
                .PreviewingWith(new NormalFieldOffsetPreview());
        }

        internal static void Apply(BuildContext context)
        {
            var components = context.AvatarRootObject.GetComponentsInChildren<NormalFieldOffset>(true);
            var requests = new List<OffsetRequest>();
            foreach (var component in components)
            {
                // Inactive outfits still need their build-time geometry when enabled by a menu later.
                if (!component.enabled || IsEditorOnly(component.transform)) continue;
                var body = component.TargetBody;
                if (body != null && (!body.transform.IsChildOf(context.AvatarRootTransform) || IsEditorOnly(body.transform)))
                    throw new InvalidOperationException($"Normal Field Offset on '{component.name}': Target Body must belong to this avatar and must not be EditorOnly.");
                requests.Add(new OffsetRequest(component.GetComponent<SkinnedMeshRenderer>(), body, component.Offset, component.MaxDistance));
            }

            var results = NormalFieldProcessor.Generate(requests);
            foreach (var result in results)
            {
                ObjectRegistry.RegisterReplacedObject(result.Source, result.Mesh);
                context.AssetSaver.SaveAsset(result.Mesh);
                result.Apply();
            }
            foreach (var component in components) Object.DestroyImmediate(component);
        }

        internal static bool IsEditorOnly(Transform transform)
        {
            for (var current = transform; current != null; current = current.parent)
                if (current.CompareTag("EditorOnly")) return true;
            return false;
        }
    }
}
