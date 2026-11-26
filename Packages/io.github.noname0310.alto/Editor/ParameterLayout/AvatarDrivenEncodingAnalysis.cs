using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.Expressions;
using Noname.AvatarTools.ObjectOptimization;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using Parameter = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters.Parameter;
using ValueType = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters.ValueType;

namespace Noname.AvatarTools.ParameterLayout
{
    internal static class AvatarDrivenEncodingAnalysis
    {
        internal static List<ParameterLayoutEntry> Analyze(GameObject avatar, VirtualAnimatorController[] controllers,
            Parameter[] parameters, ISet<string> external, ISet<string> overridden, ICollection<string> messages)
        {
            var result = new List<ParameterLayoutEntry>();
            string reason = ObjectOptimizationSafety.UnsupportedRuntime(avatar);
            var access = ExternalParameterAccess.Collect();
            if (access.Uncertain) reason = string.Join("; ", access.Diagnostics);
            if (controllers.SelectMany(c => c.AllReachableNodes()).OfType<VirtualClip>()
                .Any(c => AnimationEventEffects.MayInvokeCallback(MotionAnalysis.Source(c))))
                reason ??= "animation events or unknown clips may write inputs";
            if (reason != null) { messages.Add("Avatar-driven range analysis skipped: " + reason); return result; }

            // Saved values may originate from an older avatar or OSC session.
            // An assumption about current writers does not prove restored values.
            var inputs = parameters.ToDictionary(p => p.name, p => p.saved ? ParameterValues.Unknown : ParameterValues.Constant(p.defaultValue), StringComparer.Ordinal);
            void Add(string name, ParameterValues values)
            { if (!string.IsNullOrEmpty(name) && inputs.TryGetValue(name, out var existing)) inputs[name] = existing.Union(values); }
            foreach (var menu in ExpressionMenuOptimizer.Menus(avatar.GetComponent<VRCAvatarDescriptor>()?.expressionsMenu))
            foreach (var control in menu.controls ?? new())
            {
                if (control == null) continue;
                if (!Enum.IsDefined(typeof(VRCExpressionsMenu.Control.ControlType), control.type))
                {
                    foreach (var name in ExpressionMenuOptimizer.Parameters(control)) Add(name, ParameterValues.Unknown);
                    continue;
                }
                Add(control.parameter?.name, ParameterValues.Constant(0).Union(ParameterValues.Constant(control.value)));
                var range = control.type == VRCExpressionsMenu.Control.ControlType.TwoAxisPuppet ? ParameterValues.Range(-1, 1) :
                    control.type is VRCExpressionsMenu.Control.ControlType.RadialPuppet or VRCExpressionsMenu.Control.ControlType.FourAxisPuppet
                        ? ParameterValues.Range(0, 1) : ParameterValues.Unknown;
                foreach (var sub in control.subParameters ?? Array.Empty<VRCExpressionsMenu.Control.Parameter>()) Add(sub?.name, range);
            }
            var analysis = AnimatorReachability.AnalyzeWithInputBounds(avatar, controllers, inputs, external);
            if (analysis.FallbackReason != null)
            { messages.Add("Avatar-driven range analysis skipped: " + analysis.FallbackReason); return result; }
            foreach (var parameter in parameters.Where(p => p.networkSynced && p.GetBitWidthByType() == 8 && !p.flippedRepresentation))
            {
                if (external.Contains(parameter.name) || overridden.Contains(parameter.name) || access.IsRead(parameter.name) ||
                    access.IsWritten(parameter.name) || AnimatorReachability.BuiltIns.Contains(parameter.name)) continue;
                if (parameter.valueType is not (ValueType.Float or ValueType.Int)) continue;
                var types = controllers.Where(c => c.Parameters.ContainsKey(parameter.name)).Select(c => c.Parameters[parameter.name].type).ToArray();
                var type = parameter.valueType == ValueType.Float ? AnimatorControllerParameterType.Float : AnimatorControllerParameterType.Int;
                if (types.Length == 0 || types.Any(t => t != type)) continue;
                var range = analysis.Values(parameter.name);
                if (range.Min < 0 || range.Max > (parameter.valueType == ValueType.Float ? 1 : 127)) continue;
                var encoding = ParameterLayoutEntry.Capture(parameter); encoding.BitWidth = 7;
                encoding.FlippedRepresentation = parameter.valueType == ValueType.Float;
                result.Add(encoding);
                messages.Add("Avatar-driven encoding candidate: " + parameter.name + " range [" + range.Min + ", " + range.Max + "] -> 7-bit unsigned " + parameter.valueType);
            }
            return result;
        }

        internal static Parameter[] Apply(Parameter[] parameters, IEnumerable<ParameterLayoutEntry> encodings)
        {
            var byName = encodings.ToDictionary(p => p.Name, StringComparer.Ordinal);
            return parameters.Select(source =>
            {
                var p = ParameterLayoutModel.Copy(source);
                if (p.networkSynced && byName.TryGetValue(p.name, out var encoding))
                {
                    if (p.valueType != encoding.Type) throw new InvalidOperationException("Avatar-driven encoding type differs from the reference upload: " + p.name);
                    p.bitWidth = (byte)encoding.Cost; p.flippedRepresentation = encoding.FlippedRepresentation;
                }
                return p;
            }).ToArray();
        }
    }
}
