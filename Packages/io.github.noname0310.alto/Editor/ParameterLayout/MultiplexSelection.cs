using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.Expressions;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDKBase;
using ControlType = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control.ControlType;
using Parameter = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters.Parameter;

namespace Noname.AvatarTools.ParameterLayout
{
    [Flags]
    internal enum MultiplexExclusion
    {
        None = 0, Preserved = 1, BuiltIn = 2, MomentaryControl = 4,
        DynamicInput = 8, ExternalAccess = 16, NoPersistentMenu = 32, UncertainExternalAccess = 64
    }

    internal sealed class MultiplexSelectionResult
    {
        internal readonly HashSet<string> Eligible = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, MultiplexExclusion> Excluded = new(StringComparer.Ordinal);

        internal string Summary(IEnumerable<Parameter> parameters)
        {
            var groups = parameters.Where(p => p.networkSynced).GroupBy(p =>
                Eligible.Contains(p.name) ? "eligible" : Primary(Excluded.TryGetValue(p.name, out var reason) ? reason : MultiplexExclusion.NoPersistentMenu));
            return string.Join("; ", groups.OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => g.Key + "=" + g.Sum(p => MultiplexEncoding.Cost(p.valueType)) + " bits (" + g.Count() + " inputs)"));
        }

        private static string Primary(MultiplexExclusion reason)
        {
            foreach (MultiplexExclusion flag in Enum.GetValues(typeof(MultiplexExclusion)))
                if (flag != MultiplexExclusion.None && (reason & flag) != 0) return flag.ToString();
            return "unknown";
        }
    }

    internal static class MultiplexSelection
    {
        internal static HashSet<string> Automatic(GameObject avatar, VirtualAnimatorController[] controllers,
            ISet<string> preserved, ICollection<string> diagnostics)
            => Analyze(avatar, controllers, preserved, diagnostics).Eligible;

        internal static MultiplexSelectionResult Analyze(GameObject avatar, VirtualAnimatorController[] controllers,
            ISet<string> preserved, ICollection<string> diagnostics, IDictionary<object, VirtualAnimatorController> entries = null)
        {
            var eligible = new HashSet<string>(StringComparer.Ordinal);
            var momentary = new HashSet<string>(StringComparer.Ordinal);
            foreach (var menu in ExpressionMenuOptimizer.Menus(avatar.GetComponent<VRCAvatarDescriptor>().expressionsMenu))
                foreach (var control in menu.controls ?? new())
                {
                    if (control == null) continue;
                    if (control.type == ControlType.Toggle) Add(eligible, control.parameter?.name);
                    else if (control.type == ControlType.RadialPuppet)
                    {
                        Add(eligible, control.subParameters?.FirstOrDefault()?.name);
                        Add(momentary, control.parameter?.name);
                    }
                    else foreach (var name in ExpressionMenuOptimizer.Parameters(control)) Add(momentary, name);
                }
            var graph = AnimatorParameterDependencies.Collect(avatar, controllers, controllerEntries: entries);
            IEnumerable<string> ClipSources(VirtualClip clip) => clip.GetFloatCurveBindings()
                .Where(b => b.type == typeof(Animator) && b.path == "").Select(b => b.propertyName);
            IEnumerable<string> BehaviourSources(StateMachineBehaviour behaviour) => behaviour is VRC_AvatarParameterDriver driver
                ? (driver.parameters ?? new()).Where(p => p != null &&
                    p.type is VRC_AvatarParameterDriver.ChangeType.Add or VRC_AvatarParameterDriver.ChangeType.Random).Select(p => p.name)
                : Array.Empty<string>();
            var influenced = graph.AffectedByWriters(AvatarParameterInputs.Sensors(avatar).Select(s => s.Name),
                ClipSources, BehaviourSources);
            var external = ExternalParameterAccess.Collect();
            if (external.Uncertain)
                diagnostics.Add("Automatic multiplexing skipped: " + string.Join("; ", external.Diagnostics));
            var result = new MultiplexSelectionResult();
            var declared = ParameterLayoutModel.Read(avatar.GetComponent<VRCAvatarDescriptor>().expressionParameters?.parameters);
            foreach (string name in eligible.Concat(declared.Select(p => p.name)).Distinct())
            {
                MultiplexExclusion reason = MultiplexExclusion.None;
                if (preserved.Contains(name)) reason |= MultiplexExclusion.Preserved;
                if (AnimatorReachability.BuiltIns.Contains(name)) reason |= MultiplexExclusion.BuiltIn;
                if (momentary.Contains(name)) reason |= MultiplexExclusion.MomentaryControl;
                if (influenced.Contains(name)) reason |= MultiplexExclusion.DynamicInput;
                if (!eligible.Contains(name)) reason |= MultiplexExclusion.NoPersistentMenu;
                if (external.Uncertain) reason |= MultiplexExclusion.UncertainExternalAccess;
                else if (external.IsRead(name) || external.IsWritten(name)) reason |= MultiplexExclusion.ExternalAccess;
                if (reason == MultiplexExclusion.None) result.Eligible.Add(name);
                else result.Excluded.Add(name, reason);
            }
            diagnostics.Add("Automatic multiplexing selection: " + result.Summary(declared));
            foreach (var parameter in declared.Where(p => p.networkSynced && result.Excluded.ContainsKey(p.name)).OrderBy(p => p.name, StringComparer.Ordinal))
                diagnostics.Add("Automatic multiplexing kept native: " + parameter.name + " (" + result.Excluded[parameter.name] + ")");
            return result;
        }

        private static void Add(ISet<string> set, string name)
        { if (!string.IsNullOrEmpty(name)) set.Add(name); }
    }
}
