using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.ObjectOptimization;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.ParameterPolicy
{
    // SDK generators may add consumers after the NDMF passes. The resolved logical
    // inputs keep their existing policy; generated transport names are not new inputs
    // to auto-freeze. No external generator or transport protocol is recognized here.
    internal static class ParameterFreezeSdkFinalizer
    {
        private sealed class Session
        {
            internal BuildContext Context;
            internal IObjectRegistry Registry;
        }

        private static readonly ConditionalWeakTable<GameObject, Session> Sessions = new();

        internal static void Begin(GameObject avatar)
        {
            Sessions.Remove(avatar);
            Sessions.Add(avatar, new Session());
        }

        internal static void Capture(BuildContext context)
        {
            if (!Sessions.TryGetValue(context.AvatarRootObject, out var session)) return;
            session.Context = context;
            session.Registry = ObjectRegistry.ActiveRegistry;
        }

        internal static void Complete(GameObject avatar, bool allowTransientAssetsForTests = false)
        {
            if (!Sessions.TryGetValue(avatar, out var session)) return;
            Sessions.Remove(avatar);
            var context = session.Context;
            if (context == null) return;
            var state = context.GetState<ParameterPolicyState>();
            if (!state.Enabled || !state.FreezeResolved || state.Constants.Count == 0) return;
            var report = context.GetState<OptimizationBuildReport>();
            using var measurement = OptimizationMeasurement.Begin(report, "SDK finalizer", "parameter specialization");
            try
            {
                if (!allowTransientAssetsForTests && (context.AssetSaver.CurrentContainer == null ||
                    !EditorUtility.IsPersistent(context.AssetSaver.CurrentContainer)))
                    throw new InvalidOperationException("Final parameter specialization requires the generated NDMF asset container.");
                var unsupported = ObjectOptimizationSafety.UnsupportedRuntime(avatar);
                if (unsupported != null)
                    throw new InvalidOperationException("Cannot finalize parameter specialization: " + unsupported);
                using var registry = new ObjectRegistryScope(session.Registry);
                ValidateNativeBehaviours(avatar);
                // A fresh context includes late controllers and current hierarchy paths.
                // The earlier NDMF virtual graph is deliberately not reopened or replayed.
                var current = new VirtualControllerContext();
                current.OnActivate(context);
                var entries = current.Controllers.Where(e => !(e.Key is Object owner) || owner != null)
                    .ToDictionary(e => e.Key, e => e.Value);
                var policy = ParameterPolicyPass.Resolve(context, entries);
                ParameterFreezePass.ValidateEnvironment(avatar, state.Constants, policy);
                ParameterFreezeScope.Validate(avatar, state.Scope, entries, state.Constants.Keys);
                var changes = ParameterSpecialization.Apply(entries.Values.Distinct().ToArray(), state.Constants);
                if (changes.Changes != 0)
                {
                    // Commit only after every scope/preservation/constant check succeeds.
                    current.OnDeactivate(context);
                    if (!allowTransientAssetsForTests) context.AssetSaver.Dispose();
                }
                report.Messages.Add("Final parameter specialization: " + changes + ".");
                measurement?.Complete();
            }
            catch (Exception exception)
            {
                report.Messages.Add("Final parameter specialization failed: " + exception.Message);
                throw;
            }
            finally { AvatarBuildAnalysis.PublishReport(avatar, report); }
        }

        internal static void ValidateNativeBehaviours(GameObject avatar)
        {
            // Native state cloning can omit unsupported script behaviours. Check the
            // actual SDK output before virtualization, including machine behaviours.
            var controllers = new HashSet<RuntimeAnimatorController>();
            var machines = new HashSet<AnimatorStateMachine>();
            foreach (var (_, runtime, _) in VRChatPlatformAnimatorBindings.Instance.GetInnateControllers(avatar))
            {
                var source = runtime;
                var wrappers = new HashSet<RuntimeAnimatorController>();
                while (source is AnimatorOverrideController wrapper && wrappers.Add(source)) source = wrapper.runtimeAnimatorController;
                if (source == null || !controllers.Add(source)) continue;
                if (source is not AnimatorController controller)
                    throw new InvalidOperationException("Cannot verify the final runtime Animator controller: " + source.name);
                var pending = new Stack<AnimatorStateMachine>(controller.layers.Select(l => l.stateMachine));
                while (pending.Count > 0)
                {
                    var machine = pending.Pop();
                    if (machine == null || !machines.Add(machine)) continue;
                    foreach (var behaviour in machine.behaviours.Concat(machine.states.Where(s => s.state != null)
                                 .SelectMany(s => s.state.behaviours)))
                        if (!ParameterSpecialization.SupportsBehaviour(behaviour))
                            throw new InvalidOperationException("Parameter freezing cannot prove the inputs of StateMachineBehaviour: " + behaviour.GetType().FullName);
                    foreach (var child in machine.stateMachines) pending.Push(child.stateMachine);
                }
            }
        }
    }
}
