using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.Analysis
{
    // Read-only evidence for sampling the serialized scene. A proven constant that
    // differs from that scene is NOT invariant here: its authorized bake must run first.
    internal sealed class InvariantRenderInputs
    {
        private readonly AvatarAnimationIndex index;
        private readonly Dictionary<(Object, string), bool> cache = new();
        internal InvariantRenderInputs(AvatarAnimationIndex index) { this.index = index; }

        internal bool Unchanged(Object target, string property)
        {
            if (index == null) return true;
            if (target == null || index.UnsafeReason != null || index.Reachability.FallbackReason != null) return false;
            var key = (target, property);
            if (!cache.TryGetValue(key, out bool value)) cache[key] = value = Evaluate(target, property);
            return value;
        }

        private bool Evaluate(Object target, string property)
        {
            var uses = index.ForObject(target).Where(u => u.Binding.propertyName == property).ToArray();
            if (uses.Length == 0) return true;
            // Material property blocks and their implicit vector channels need a
            // dedicated rewrite; the normal constant-material pass handles those.
            if (property.StartsWith("material.", StringComparison.Ordinal)) return false;
            using var serialized = new SerializedObject(target);
            if (!ConstantOutputValue.TryRead(serialized.FindProperty(property == "sharedMesh" ? "m_Mesh" : property), out var initial)) return false;
            if (initial.Kind != ConstantOutputKind.ObjectReference && target is Component component &&
                index.ConstantOutputs.MatchesInitialValue(component, uses, initial.Number, false, initial.Kind)) return true;
            return index.ConstantOutputs.TryProve(target, property, initial, out var proof, out _) && !proof.ChangesInitialValue;
        }
    }
}
