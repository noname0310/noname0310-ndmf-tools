using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.Analysis;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.Contact.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;

namespace Noname.AvatarTools.ParameterPolicy
{
    internal static class ParameterFreezePass
    {
        internal static IEnumerable<string> ExternalNames(GameObject avatar) => AvatarParameterInputs.ExternalNames(avatar);

        internal static void Apply(BuildContext context)
        {
            var state = context.GetState<ParameterPolicyState>();
            if (!state.Enabled || (state.Frozen.Count == 0 && state.DefaultFrozen.Count == 0 &&
                state.FreezeMode == AnimatorParameterFreezeMode.SelectedParameters)) return;
            var watch = Stopwatch.StartNew();
            var animationLease = Noname.AvatarTools.Build.OptimizationSession.Acquire(context);
            var services = animationLease.Services;
            try
            {
                var entries = services.ControllerContext.Controllers
                    .Where(e => !(e.Key is UnityEngine.Object owner) || owner != null).ToDictionary(e => e.Key, e => e.Value);
                var controllers = entries.Values.Distinct().ToArray();
                ObjectOptimization.ObjectOptimizationPass.CaptureConsumers(context, entries);
                var policy = ParameterPolicyPass.Resolve(context, entries);
                if (policy.Uncertain)
                    throw new InvalidOperationException("Parameter freezing requires verified Preserve mappings. Fix the parameter-policy diagnostics first.");
                var report = context.GetState<OptimizationBuildReport>();
                if (!state.FreezeResolved)
                {
                    if (state.FreezeMode == AnimatorParameterFreezeMode.AllExternalInputsExceptPreserved)
                    {
                        var automatic = ParameterFreezeSelection.AutomaticInputs(context.AvatarRootObject, controllers, policy.Preserved);
                        state.DefaultFrozen.UnionWith(automatic.Except(state.Frozen.Keys));
                        report.Messages.Add("Auto-freeze selected " + automatic.Count + " external inputs, excluding built-ins and resolved Preserve dependencies.");
                    }
                    var selected = new Dictionary<string, float>(state.Frozen, StringComparer.Ordinal);
                    foreach (var name in state.DefaultFrozen)
                        selected[name] = ParameterFreezeSelection.DefaultValue(context.AvatarRootObject, controllers, name);
                    foreach (var pair in ParameterSpecialization.Prepare(controllers, selected, policy.Preserved, ExternalNames(context.AvatarRootObject),
                        ParameterFreezeSelection.ExternalTypes(context.AvatarRootObject)))
                        state.Constants.Add(pair.Key, pair.Value);
                    state.FreezeResolved = true;
                }
                if (state.Constants.Count == 0) return;
                ValidateEnvironment(context.AvatarRootObject, state.Constants, policy);
                ParameterFreezeScope.Validate(context.AvatarRootObject, state.Scope, entries, state.Constants.Keys,
                    services.ObjectPathRemapper.GetVirtualToRealPathMap());
                var changes = ParameterSpecialization.Apply(controllers, state.Constants);
                ParameterFreezeSdkFinalizer.Capture(context);
                report.FrozenParameters.Clear();
                report.FrozenParameters.AddRange(state.Constants.Values.OrderBy(c => c.Name, StringComparer.Ordinal).Select(c => new FrozenParameterReport
                {
                    Name = c.Name, InternalName = c.InternalName, Type = c.TypeLabel, Value = c.Value,
                    FromBuildDefault = state.DefaultFrozen.Contains(c.Name)
                }));
                foreach (var constant in state.Constants.Values)
                {
                    var mapping = "Frozen parameter: " + constant.Name + " = " + constant.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
                        " (" + constant.TypeLabel + "; internal " + constant.InternalName +
                        (state.DefaultFrozen.Contains(constant.Name) ? "; build default" : "; explicit") + ")";
                    if (state.Reported.Add(mapping)) report.Messages.Add(mapping);
                }
                var message = "Parameter specialization: " + changes + "; " + watch.ElapsedMilliseconds + " ms.";
                report.Messages.Add(message);
                UnityEngine.Debug.Log("[ALTO] " + message, context.AvatarRootObject);
            }
            finally
            {
                animationLease.Dispose();
            }
        }

        internal static void ValidateEnvironment(GameObject avatar, IReadOnlyDictionary<string, ParameterConstant> constants,
            ResolvedParameterPolicy policy)
        {
            if (policy.Uncertain)
                throw new InvalidOperationException("Parameter freezing requires verified Preserve mappings. Fix the parameter-policy diagnostics first.");
            var external = new HashSet<string>(ExternalNames(avatar), StringComparer.Ordinal);
            var externalAccess = ExternalParameterAccess.Collect();
            if (externalAccess.Uncertain)
                throw new InvalidOperationException("Parameter freezing requires verified external parameter declarations: " + string.Join("; ", externalAccess.Diagnostics));
            foreach (var constant in constants.Values)
            {
                if (external.Contains(constant.InternalName) || externalAccess.IsWritten(constant.InternalName))
                    throw new InvalidOperationException("A later build pass exposed an internal parameter constant: " + constant.InternalName);
                if (policy.Preserved.Contains(constant.Name) || policy.Preserved.Contains(constant.InternalName))
                    throw new InvalidOperationException("Frozen parameter conflicts with a preserved dependency: " + constant.Name);
            }
        }
    }
}
