using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.ParameterPolicy;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase;

namespace Noname.AvatarTools.ObjectOptimization
{
    internal static class SensorParameterPruning
    {
        internal static HashSet<string> Referenced(VirtualAnimatorController[] controllers)
        {
            var nodes = controllers.SelectMany(c => c.AllReachableNodes()).Distinct().ToArray();
            var result = new HashSet<string>(ParameterSpecialization.Reads(nodes).Where(n => !string.IsNullOrEmpty(n)), StringComparer.Ordinal);
            foreach (var clip in nodes.OfType<VirtualClip>())
                result.UnionWith(clip.GetFloatCurveBindings().Concat(clip.GetObjectCurveBindings())
                    .Where(b => b.type == typeof(Animator)).Select(b => b.propertyName));
            foreach (var behaviour in ParameterSpecialization.Behaviours(nodes))
            {
                if (behaviour == null) continue;
                if (behaviour is VRC_AvatarParameterDriver driver)
                {
                    result.UnionWith((driver.parameters ?? new()).Where(p => p != null).Select(p => p.name));
                    continue;
                }
                using var serialized = new SerializedObject(behaviour);
                var property = serialized.GetIterator();
                while (property.Next(true))
                    if (property.propertyType == SerializedPropertyType.String) result.Add(property.stringValue);
            }
            return result;
        }

        internal static int Apply(GameObject avatar, VirtualAnimatorController[] controllers, ObjectOptimizationState state,
            ISet<string> preserved, ExternalParameterAccess external, ICollection<string> messages,
            ISet<string> removableLocals = null, ISet<string> frozenInputs = null)
        {
            var used = Referenced(controllers);
            used.UnionWith(preserved);
            used.UnionWith(AnimatorReachability.BuiltIns);
            used.UnionWith(ParameterFreezeSelection.MenuInputs(avatar));
            var expressions = avatar.GetComponent<VRCAvatarDescriptor>()?.expressionParameters;
            if (expressions?.parameters != null) used.UnionWith(expressions.parameters
                .Where(p => p != null && (p.networkSynced || removableLocals == null || !removableLocals.Contains(p.name))).Select(p => p.name));
            int declarations = 0, names = 0, proofs = 0;
            foreach (var group in AvatarParameterInputs.Sensors(avatar).GroupBy(s => s.Name))
            {
                string name = group.Key;
                // Require a removed local consumer or an explicit input-freeze policy.
                // Undeclared external-only sensors otherwise remain conservative roots.
                if ((!state.InitialReaders.Contains(name) && frozenInputs?.Contains(name) != true) ||
                    used.Contains(name) || external.IsRead(name) || external.IsWritten(name) ||
                    group.Any(s => !state.Scope.Contains(s.Source.transform) || state.Protected(s.Source)))
                {
                    // A late builder may add an observer after the earlier proof. If the
                    // producer still exists, collection must use the current graph.
                    state.PrunedSensorInputs.Remove(name);
                    continue;
                }
                int count = 0;
                foreach (var controller in controllers)
                    if (controller.Parameters.ContainsKey(name))
                    {
                        controller.Parameters = controller.Parameters.Remove(name); count++;
                    }
                // Earlier graph optimization may already have removed the declaration. A
                // proven frozen input can still make a multi-output sensor collectible.
                bool newProof = state.PrunedSensorInputs.Add(name);
                if (newProof) proofs++;
                if (count == 0 && !newProof) continue;
                names++; declarations += count;
                messages.Add("Unobserved sensor input: " + name + " (removed " + count + " controller declarations; no reader, writer, network, menu or external observer).");
            }
            messages.Add("Pruned " + declarations + " sensor parameter declarations for " + names + " unobserved inputs.");
            return declarations + proofs;
        }
    }
}
