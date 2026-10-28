using System;
using System.Collections.Generic;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using nadena.dev.ndmf.util;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.Build
{
    internal static class AnimatorAssetPersistence
    {
        internal static void Commit(BuildContext context, VirtualControllerContext controllers, Action<Object> save = null)
        {
            controllers.OnDeactivate(context);
            int count = SaveReferencedAssets(context.AvatarRootObject, save ?? context.AssetSaver.SaveAsset);
            if (count != 0) context.GetState<OptimizationBuildReport>().Messages.Add("Controller dependency persistence: " + count + " generated assets saved.");
        }

        // NDMF's controller commit cache excludes StateMachineBehaviours, including
        // behaviours cloned from existing controllers. Its ordinary build later calls
        // BuildContext.Serialize to persist the complete referenced-asset closure.
        // Late SDK commits run after that sweep, so they must establish the same
        // persistence invariant before the owning saver flushes its assets.
        internal static int SaveReferencedAssets(GameObject avatar, Action<Object> save)
        {
            var visited = new HashSet<Object>();
            int count = 0;
            foreach (var (_, controller, _) in VRChatPlatformAnimatorBindings.Instance.GetInnateControllers(avatar))
            {
                if (controller == null || visited.Contains(controller)) continue;
                foreach (var asset in controller.ReferencedAssets(traverseSaved: true, includeScene: false,
                             traversalFilter: value => value is not MonoScript && !visited.Contains(value)))
                {
                    visited.Add(asset);
                    if (asset == null || asset is MonoScript || EditorUtility.IsPersistent(asset)) continue;
                    // Match NDMF's ordinary behaviour serialization: cloned runtime
                    // behaviours must not inherit a generator's DontSave flags.
                    if (asset is StateMachineBehaviour) asset.hideFlags = HideFlags.HideInHierarchy;
                    else asset.hideFlags &= ~HideFlags.DontSave;
                    save(asset); count++;
                }
            }
            return count;
        }
    }
}
