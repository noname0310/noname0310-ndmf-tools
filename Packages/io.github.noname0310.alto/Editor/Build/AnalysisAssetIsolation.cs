using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.util;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.Build
{
    // Instantiate clones scene objects but shares their temporary meshes and other asset references.
    // NDMF serializes these assets into its output folder. Isolate them before preprocessing so
    // discarding that folder cannot invalidate an authoring tool's live preview in the source scene.
    internal sealed class AnalysisAssetIsolation : IDisposable
    {
        private readonly List<Object> owned = new();

        internal static AnalysisAssetIsolation Capture(GameObject clone)
        {
            var isolation = new AnalysisAssetIsolation();
            try
            {
                var referenced = clone.ReferencedAssets(traverseSaved: false).Distinct().ToArray();
                var replacements = new Dictionary<Object, Object>();
                foreach (var asset in referenced)
                {
                    if (asset == null || asset is GameObject || asset is Component || asset is MonoScript ||
                        EditorUtility.IsPersistent(asset)) continue;
                    // Unity cannot Instantiate native Animator graph objects reliably. Copy their
                    // serialized data into new native instances, then remap the shared graph below.
                    Object copy = asset switch
                    {
                        AnimatorController => new AnimatorController(),
                        AnimatorStateMachine => new AnimatorStateMachine(),
                        AnimatorState => new AnimatorState(),
                        AnimatorStateTransition => new AnimatorStateTransition(),
                        AnimatorTransition => new AnimatorTransition(),
                        BlendTree => new BlendTree(),
                        _ => null
                    };
                    if (copy != null) EditorUtility.CopySerialized(asset, copy);
                    else copy = Object.Instantiate(asset);
                    isolation.owned.Add(copy);
                    copy.name = asset.name;
                    replacements.Add(asset, copy);
                }
                if (replacements.Count == 0) return isolation;
                foreach (var target in referenced.Where(o => o is Component).Concat(isolation.owned))
                {
                    if (target == null) continue;
                    using var serialized = new SerializedObject(target);
                    bool changed = false;
                    foreach (var property in serialized.ObjectProperties())
                        if (property.objectReferenceValue != null &&
                            replacements.TryGetValue(property.objectReferenceValue, out var replacement))
                        {
                            property.objectReferenceValue = replacement;
                            changed = true;
                        }
                    if (changed) serialized.ApplyModifiedPropertiesWithoutUndo();
                }
                return isolation;
            }
            catch
            {
                isolation.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            // Persisted copies belong to the unique build folder and are removed with that folder.
            foreach (var asset in owned)
                if (asset != null && !EditorUtility.IsPersistent(asset)) Object.DestroyImmediate(asset);
            owned.Clear();
        }
    }
}
