using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.Analysis
{
    // Serialization exposes fields that Animator does not necessarily evaluate.
    // A changed-default bake must have a native animation binding as well as a
    // constant clip value. Cache only inside one immutable rewrite transaction.
    internal sealed class NativeAnimationBindings
    {
        private readonly GameObject avatar;
        private readonly Dictionary<GameObject, HashSet<(Type Type, string Property, bool ObjectReference)>> bindings = new();

        internal NativeAnimationBindings(GameObject avatar) { this.avatar = avatar; }

        internal bool Supports(Component target, IEnumerable<AvatarAnimationIndex.BindingUse> writers, out string reason)
        {
            reason = null;
            if (!bindings.TryGetValue(target.gameObject, out var available))
            {
                using var cost = AnalysisCost.Measure("Native animation binding inventory/cache miss");
                available = new();
                string path = AnimationUtility.CalculateTransformPath(target.transform, avatar.transform);
                foreach (var binding in AnimationUtility.GetAnimatableBindings(target.gameObject, avatar))
                    if (binding.path == path) available.Add((binding.type, binding.propertyName, binding.isPPtrCurve));
                bindings.Add(target.gameObject, available);
            }
            else AnalysisCost.Count("Native animation binding inventory/cache hit");
            foreach (var writer in writers)
                if (!available.Contains((writer.Binding.type, writer.Binding.propertyName, writer.Binding.isPPtrCurve)))
                {
                    reason = "Animator does not advertise a native binding for " + writer.Binding.type.Name + "." + writer.Binding.propertyName;
                    return false;
                }
            return true;
        }
    }
}
