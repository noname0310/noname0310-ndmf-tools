using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase;

namespace Noname.AvatarTools.Analysis
{
    internal static class AnimatorInstanceContexts
    {
        internal sealed class Context
        {
            internal bool LocalInstance;
            internal AnimatorReachability Avatar, Other;
            internal HashSet<VirtualNode> AvatarNodes, OtherNodes;
            internal HashSet<StateMachineBehaviour> OtherBehaviours;

            internal bool IsReachable(VirtualState state) =>
                AvatarNodes.Contains(state) && Avatar.IsReachable(state) || OtherNodes.Contains(state) && Other.IsReachable(state);
            internal bool IsReachable(VirtualStateMachine machine) =>
                AvatarNodes.Contains(machine) && Avatar.IsReachable(machine) || OtherNodes.Contains(machine) && Other.IsReachable(machine);
            internal bool CanTake(VirtualTransitionBase transition) =>
                AvatarNodes.Contains(transition) && Avatar.CanTake(transition) || OtherNodes.Contains(transition) && Other.CanTake(transition);
            internal bool Executes(StateMachineBehaviour behaviour) => LocalInstance ||
                OtherBehaviours.Contains(behaviour) || !(behaviour is VRC_AvatarParameterDriver driver) || !driver.localOnly;
        }

        // IsLocal is a runtime-owned identity, not a user parameter assumed constant
        // from its default. No other runtime input is specialized by these contexts.
        // https://creators.vrchat.com/avatars/animator-parameters/#built-in-parameters
        internal static Context[] Analyze(GameObject avatar, VirtualAnimatorController[] controllers,
            IDictionary<object, VirtualAnimatorController> entries = null)
            => AnalyzeWithDiagnostics(avatar, controllers, entries, null);

        internal static Context[] AnalyzeWithDiagnostics(GameObject avatar, VirtualAnimatorController[] controllers,
            IDictionary<object, VirtualAnimatorController> entries, System.Action<string> diagnostic)
        {
            Context[] Skip(string reason) { diagnostic?.Invoke(reason); return null; }
            if (avatar.GetComponent<VRCAvatarDescriptor>() == null) return Skip("No avatar descriptor.");
            var unsupported = ObjectOptimization.ObjectOptimizationSafety.UnsupportedRuntime(avatar);
            if (unsupported != null) return Skip(unsupported);
            var children = avatar.GetComponentsInChildren<Animator>(true).Where(a => a.gameObject != avatar).ToArray();
            VirtualAnimatorController[] owned;
            if (entries == null)
            {
                // Legacy array callers cannot establish ownership in a mixed hierarchy.
                if (children.Length != 0) return Skip("Child Animator ownership was not supplied.");
                owned = controllers;
            }
            else
            {
                if (controllers.Any(c => !entries.Values.Contains(c)) ||
                    children.Any(a => a.runtimeAnimatorController != null && !entries.ContainsKey(a)))
                    return Skip("Controller ownership entries are incomplete.");
                bool AvatarOwner(object owner) => owner is VRCAvatarDescriptor.AnimLayerType ||
                    owner is Animator a && a != null && a.gameObject == avatar;
                // A virtual controller shared with another Animator cannot have its
                // identity specialized, even if it also belongs to a playable layer.
                owned = controllers.Where(c => entries.Where(e => e.Value == c).All(e => AvatarOwner(e.Key))).ToArray();
            }
            if (owned.Length == 0) return Skip("No controller has verified avatar ownership.");
            var others = controllers.Except(owned).ToArray();
            var external = ExternalParameterAccess.Collect();
            if (external.Uncertain || external.IsWritten("IsLocal")) return Skip("External access may change runtime identity.");
            bool needed = false;
            foreach (var controller in controllers)
            {
                var nodes = controller.AllReachableNodes().ToArray();
                // Replacing a controller at runtime introduces an unindexed owner.
                if (nodes.OfType<VirtualClip>().Any(c => c.GetObjectCurveBindings().Any(b => b.type == typeof(Animator))))
                    return Skip("An Animator object-reference curve may replace a controller: " + controller.Name);
                var drivers = nodes.OfType<VirtualState>().SelectMany(s => s.Behaviours)
                    .Concat(nodes.OfType<VirtualStateMachine>().SelectMany(m => m.Behaviours))
                    .OfType<VRC_AvatarParameterDriver>().ToArray();
                bool runtimeOwned = owned.Contains(controller);
                needed |= runtimeOwned && drivers.Any(d => d.localOnly);
                if (drivers.Any(d => (d.parameters ?? new()).Any(p => p != null && p.name == "IsLocal")) ||
                    nodes.OfType<VirtualClip>().Any(c => c.GetFloatCurveBindings().Any(b =>
                        b.type == typeof(Animator) && b.propertyName == "IsLocal"))) return Skip("Runtime identity has an indexed writer: " + controller.Name);
                bool reads = nodes.OfType<VirtualTransitionBase>().Any(t => t.Conditions.Any(c => c.parameter == "IsLocal")) ||
                    nodes.OfType<VirtualState>().Any(s => new[] { s.TimeParameter, s.SpeedParameter, s.CycleOffsetParameter, s.MirrorParameter }.Contains("IsLocal")) ||
                    nodes.OfType<VirtualBlendTree>().Any(t => t.BlendParameter == "IsLocal" || t.BlendParameterY == "IsLocal" ||
                        t.Children.Any(c => c.DirectBlendParameter == "IsLocal")) ||
                    drivers.Any(d => (d.parameters ?? new()).Any(p => p != null &&
                        p.type == VRC_AvatarParameterDriver.ChangeType.Copy && p.source == "IsLocal"));
                // VRChat converts built-in Bool values to Int/Float 0 and 1. This is a
                // runtime contract, including when another optimizer changes the type.
                // https://creators.vrchat.com/avatars/animator-parameters/#mismatched-parameter-type-conversion
                if (runtimeOwned && reads && (!controller.Parameters.TryGetValue("IsLocal", out var parameter) ||
                    parameter.type is not (AnimatorControllerParameterType.Bool or AnimatorControllerParameterType.Int or AnimatorControllerParameterType.Float)))
                    return Skip("IsLocal is missing or has an unsupported type: " + controller.Name);
                needed |= runtimeOwned && reads;
            }
            if (!needed) return Skip("No avatar-owned reader uses runtime identity or local-only Drivers.");
            var local = AnimatorReachability.AnalyzeInstance(avatar, owned, true);
            var remote = AnimatorReachability.AnalyzeInstance(avatar, owned, false);
            // Child/custom-owner inputs remain variable, including a private IsLocal.
            // Same-named parameters across owners still over-approximate dependencies.
            var other = AnimatorReachability.Analyze(avatar, others, others.SelectMany(c => c.Parameters.Keys));
            if (local.FallbackReason != null || remote.FallbackReason != null || other.FallbackReason != null)
                return Skip(local.FallbackReason ?? remote.FallbackReason ?? other.FallbackReason);
            var avatarNodes = new HashSet<VirtualNode>(owned.SelectMany(c => c.AllReachableNodes()));
            var otherNodes = new HashSet<VirtualNode>(others.SelectMany(c => c.AllReachableNodes()));
            var otherBehaviours = new HashSet<StateMachineBehaviour>(otherNodes.OfType<VirtualState>().SelectMany(s => s.Behaviours)
                .Concat(otherNodes.OfType<VirtualStateMachine>().SelectMany(m => m.Behaviours)));
            return new[] { local, remote }.Select(a => new Context { LocalInstance = a.LocalInstance.Value, Avatar = a, Other = other,
                AvatarNodes = avatarNodes, OtherNodes = otherNodes, OtherBehaviours = otherBehaviours }).ToArray();
        }
    }
}
