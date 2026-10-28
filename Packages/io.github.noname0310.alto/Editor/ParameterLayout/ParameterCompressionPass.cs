using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.BlendShapeOptimization;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.Expressions;
using Noname.AvatarTools.ObjectOptimization;
using Noname.AvatarTools.ParameterPolicy;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.ParameterLayout
{
    internal sealed class ParameterCompressionSettings
    {
        internal bool RemoveUnused;
        internal ParameterMultiplexingMode Multiplexing;
        internal int Budget = 256;
        internal float HoldSeconds = .15f;
        internal readonly HashSet<string> MultiplexNames = new(StringComparer.Ordinal);
        internal readonly List<string> MultiplexPrefixes = new();
        internal readonly List<CompressedIntegerParameter> Integers = new();
        internal readonly HashSet<string> KeepNative = new(StringComparer.Ordinal);
        internal readonly List<string> KeepPrefixes = new();
    }

    internal sealed class ParameterCompressionResult
    {
        internal List<IntegerEncoding> Integers = new();
        internal MultiplexEncoding Multiplexing;
    }

    internal sealed class TransportPolicyPreservation
    {
        internal readonly HashSet<string> Names = new(StringComparer.Ordinal);
        internal readonly List<string> Prefixes = new();
    }

    internal static class ParameterCompressionPass
    {
        // Network representation is shared across platforms. A policy whose freezing
        // only runs on Android can still require native inputs in the PC contract.
        // Capture declarations still present here before policy markers are removed.
        // Platform filters and generators that remove whole declarations remain a
        // visibility boundary; replay still rejects incompatible preservation.
        internal static void CapturePolicyPreservation(BuildContext context)
        {
            var avatar = context.AvatarRootObject;
            if (!avatar.GetComponentsInChildren<ALTOParameterCompression>(true).Any(c => c.enabled &&
                c.TimeMultiplexing == ParameterMultiplexingMode.AutomaticByPriority && BuildPlatformFilters.Includes(c.transform, context))) return;
            var state = context.GetState<TransportPolicyPreservation>();
            foreach (var policy in avatar.GetComponentsInChildren<ALTOAnimatorParameterPolicy>(true).Where(p => p.enabled &&
                p.ApplyOnPlatforms != OptimizationPlatforms.None && BuildPlatformFilters.Includes(p.transform, context)))
                CaptureSelection(context, policy.PreserveParameters, state.Names, state.Prefixes);
        }

        internal static ParameterCompressionSettings Capture(BuildContext context, bool hasLayout)
        {
            var all = context.AvatarRootObject.GetComponentsInChildren<ALTOParameterCompression>(true);
            var enabled = all.Where(c => c.enabled && !ALTOPlugin.IsEditorOnly(c.transform) &&
                BuildPlatformFilters.Includes(c.transform, context)).ToArray();
            if (enabled.Length > 1) throw new InvalidOperationException("Use one enabled ALTO Parameter Compression per avatar.");
            if (enabled.Length != 0 && !hasLayout)
                throw new InvalidOperationException("ALTO Parameter Compression requires ALTO Parameter Layout on the same avatar.");
            ParameterCompressionSettings settings = null;
            if (enabled.Length == 1)
            {
                var component = enabled[0];
                if (!Enum.IsDefined(typeof(ParameterMultiplexingMode), component.TimeMultiplexing) ||
                    (component.TimeMultiplexing != ParameterMultiplexingMode.Disabled &&
                    (component.TargetBitBudget < 16 || component.TargetBitBudget > 256 ||
                    !MotionAnalysis.Finite(component.PacketHoldSeconds) || component.PacketHoldSeconds < .1f || component.PacketHoldSeconds > 1)))
                    throw new InvalidOperationException("Choose valid time multiplexing settings and budget.");
                settings = new ParameterCompressionSettings { RemoveUnused = component.RemoveUnusedSynchronization,
                    Multiplexing = component.TimeMultiplexing, Budget = component.TargetBitBudget, HoldSeconds = component.PacketHoldSeconds };
                CaptureSelection(context, component.KeepNativeParameters, settings.KeepNative, settings.KeepPrefixes);
                if (settings.Multiplexing == ParameterMultiplexingMode.AutomaticByPriority)
                {
                    var shared = context.GetState<TransportPolicyPreservation>();
                    settings.KeepNative.UnionWith(shared.Names);
                    settings.KeepPrefixes.AddRange(shared.Prefixes);
                    if (shared.Names.Count != 0 || shared.Prefixes.Count != 0)
                        context.GetState<OptimizationBuildReport>().Messages.Add("Automatic priority compression also retains native inputs required by visible " +
                            "parameter policies on other platforms. Their freeze and object policies still run only on their selected platforms.");
                }
                if (settings.Multiplexing == ParameterMultiplexingMode.SelectedInputs)
                    CaptureSelection(context, component.MultiplexedParameters, settings.MultiplexNames, settings.MultiplexPrefixes);
                foreach (var rule in component.Integers ?? new())
                {
                    if (rule == null || string.IsNullOrWhiteSpace(rule.Name))
                        throw new InvalidOperationException("Each compressed integer needs a name. Remove unused rows.");
                    IntegerEncoding.Width(rule.Minimum, rule.Maximum);
                    string name = rule.Name;
                    if (!ReferenceEquals(rule.Source, null) && rule.Source == null)
                        throw new InvalidOperationException("The compression source was removed: " + name);
                    if (rule.Source != null)
                    {
                        var resolved = ParameterSourceResolver.Resolve(new AnimatorParameterSource { Source = rule.Source },
                            context.AvatarRootObject, context);
                        var candidates = resolved.Parameters.Where(p => p.Name == name).ToArray();
                        var names = candidates.Select(p => ParameterPolicyPass.ResolveName(context, p)).Distinct().ToArray();
                        if (names.Length != 1 || candidates.Any(p => p.Owner == null) || resolved.Diagnostics.Count != 0)
                            throw new InvalidOperationException("Cannot uniquely resolve compressed integer: " + name);
                        name = names[0];
                    }
                    if (settings.Integers.Any(p => p.Name == name))
                        throw new InvalidOperationException("Duplicate compressed integer: " + name);
                    settings.Integers.Add(new CompressedIntegerParameter { Name = name, Minimum = rule.Minimum, Maximum = rule.Maximum });
                }
            }
            foreach (var component in all) Object.DestroyImmediate(component);
            return settings;
        }

        private static void CaptureSelection(BuildContext context, AnimatorParameterSelection selection, ISet<string> names, ICollection<string> prefixes)
        {
            selection ??= new();
            names.UnionWith((selection.Names ?? new()).Where(n => !string.IsNullOrWhiteSpace(n)));
            foreach (var prefix in (selection.Prefixes ?? new()).Where(n => !string.IsNullOrEmpty(n))) prefixes.Add(prefix);
            foreach (var source in selection.Sources ?? new())
            {
                if (source?.Source == null) continue;
                var resolved = ParameterSourceResolver.Resolve(source, context.AvatarRootObject, context);
                if (resolved.Diagnostics.Count > 0 || resolved.Parameters.Any(p => p.Owner == null))
                    throw new InvalidOperationException("Cannot resolve compression parameter source: " + source.Source.name);
                names.UnionWith(resolved.Parameters.Where(p => !(source.ExcludedNames ?? new()).Contains(p.Name))
                    .Select(p => ParameterPolicyPass.ResolveName(context, p)));
            }
        }

        internal static HashSet<string> FindUnused(GameObject avatar, IDictionary<object, VirtualAnimatorController> entries,
            ISet<string> preserved, OptimizationBuildReport report)
        {
            var external = ExternalParameterAccess.Collect();
            string reason = ObjectOptimizationSafety.UnsupportedRuntime(avatar);
            if (external.Uncertain) reason = string.Join("; ", external.Diagnostics);
            var controllers = entries.Values.Distinct().ToArray();
            var analysis = reason == null ? AnimatorReachability.Analyze(avatar, controllers, preserved) : null;
            reason ??= analysis?.FallbackReason;
            if (controllers.SelectMany(c => c.AllReachableNodes()).OfType<VirtualClip>()
                .Any(c => MotionAnalysis.Source(c)?.events.Length > 0)) reason = "animation events may observe parameters";
            if (reason != null)
            { report.Messages.Add("Unused synchronized parameter analysis skipped: " + reason); return new(StringComparer.Ordinal); }
            var output = new ExpressionOutputUsage(avatar, entries, null);
            var graph = AnimatorParameterDependencies.Collect(avatar, controllers, true, output.IsObservable, analysis, entries);
            var roots = new HashSet<string>(StringComparer.Ordinal);
            roots.UnionWith(preserved);
            // Retained sensors can supply consumers not represented by animation curves.
            roots.UnionWith(AvatarParameterInputs.Sensors(avatar).Select(s => s.Name));
            var parameters = ParameterLayoutModel.Read(avatar.GetComponent<VRCAvatarDescriptor>().expressionParameters?.parameters);
            roots.UnionWith(parameters.Where(p => external.IsRead(p.name) || external.IsWritten(p.name)).Select(p => p.name));
            var live = graph.ExpandObservable(roots);
            return parameters.Where(p => p.networkSynced && !live.Contains(p.name)).Select(p => p.name).ToHashSet(StringComparer.Ordinal);
        }

        internal static ParameterCompressionResult Apply(BuildContext context, ParameterCompressionSettings settings,
            ParameterLayoutRecord reference, OptimizationBuildReport report, Action<Object> save)
        {
            if (settings == null)
            {
                if (reference?.IntegerEncodings?.Count > 0 || reference?.Multiplexing != null)
                    throw new InvalidOperationException("The reference layout uses compression. Enable ALTO Parameter Compression on this platform too.");
                return new();
            }
            if (context == null) throw new InvalidOperationException("Parameter compression requires the NDMF build context.");
            var avatar = context.AvatarRootObject;
            var descriptor = avatar.GetComponent<VRCAvatarDescriptor>();
            var original = descriptor.expressionParameters;
            var input = ParameterLayoutModel.Read(original?.parameters);
            var clone = new CloneContext(VRChatPlatformAnimatorBindings.Instance);
            var entries = new Dictionary<object, VirtualAnimatorController>();
            foreach (var (key, controller, _) in VRChatPlatformAnimatorBindings.Instance.GetInnateControllers(avatar))
                if (controller != null) entries[key] = clone.Clone(controller, key);
            var controllers = entries.Values.Distinct().ToArray();
            var policy = ParameterPolicyPass.Resolve(context, entries);
            if (policy.Uncertain) throw new InvalidOperationException("Cannot compress an uncertain parameter preservation mapping.");
            var protectionRoots = new HashSet<string>(policy.Roots, StringComparer.Ordinal);
            protectionRoots.UnionWith(settings.KeepNative);
            protectionRoots.UnionWith(input.Where(p => settings.KeepPrefixes.Any(prefix => p.name.StartsWith(prefix, StringComparison.Ordinal))).Select(p => p.name));
            var preserve = AnimatorParameterDependencies.Collect(avatar, controllers, controllerEntries: entries).Expand(protectionRoots);
            preserve.UnionWith(AnimatorReachability.BuiltIns);
            var declared = input.ToDictionary(p => p.name, StringComparer.Ordinal);
            var occupied = new HashSet<string>(input.Select(p => p.name).Concat(controllers.SelectMany(c => c.Parameters.Keys)), StringComparer.Ordinal);
            occupied.UnionWith(AvatarParameterInputs.Sensors(avatar).Select(p => p.Name));
            occupied.UnionWith(ExpressionMenuOptimizer.Menus(descriptor.expressionsMenu).SelectMany(m => m.controls ?? new())
                .Where(c => c != null).SelectMany(ExpressionMenuOptimizer.Parameters));
            var encodings = reference?.IntegerEncodings ?? settings.Integers.Select(rule =>
                IntegerEncoding.Create(rule.Name, rule.Minimum, rule.Maximum, occupied)).ToList();
            var active = new List<IntegerEncoding>();
            foreach (var encoding in encodings)
            {
                encoding.Validate();
                if (!declared.TryGetValue(encoding.Name, out var parameter) || !parameter.networkSynced)
                {
                    if (reference == null) throw new InvalidOperationException("Compressed integer is absent or not synchronized: " + encoding.Name);
                    continue; // A removed/frozen feature gets anonymous wire padding, never a decoder.
                }
                if (preserve.Contains(encoding.Name)) throw new InvalidOperationException("Preserved parameter conflicts with integer compression: " + encoding.Name);
                if (reference != null && encoding.Bits.Any(occupied.Contains))
                    throw new InvalidOperationException("Recorded integer wire name collides with a parameter on this platform: " + encoding.Name);
                if (controllers.Any(c => c.Parameters.TryGetValue(encoding.Name, out var p) && p.type != AnimatorControllerParameterType.Int))
                    throw new InvalidOperationException("Compressed integer has conflicting controller types: " + encoding.Name);
                encoding.Declarations(parameter); // Validate all contracts before modifying controllers or assets.
                active.Add(encoding);
            }
            var selected = new HashSet<string>(protectionRoots, StringComparer.Ordinal);
            selected.UnionWith(active.Select(e => e.Name));
            var unused = settings.RemoveUnused ? FindUnused(avatar, entries, selected, report) : new HashSet<string>();
            var replacements = active.ToDictionary(e => e.Name, e => e.Declarations(declared[e.Name]), StringComparer.Ordinal);
            var output = input.SelectMany(parameter =>
            {
                if (replacements.TryGetValue(parameter.name, out var replacement)) return replacement;
                var copy = ParameterLayoutModel.Copy(parameter);
                if (unused.Contains(copy.name)) copy.networkSynced = false;
                return new[] { copy };
            }).ToArray();
            MultiplexEncoding multiplex = reference?.Multiplexing;
            if (multiplex != null && settings.Multiplexing == ParameterMultiplexingMode.Disabled)
                throw new InvalidOperationException("The reference upload uses time multiplexing. Enable it on this platform too.");
            if (reference == null && settings.Multiplexing == ParameterMultiplexingMode.AutomaticByPriority)
            {
                var warnings = new List<string>();
                multiplex = MultiplexPriorities.Analyze(avatar, controllers, preserve, entries)
                    .Plan(output, settings.Budget, settings.HoldSeconds, occupied, report.Messages, warnings);
                foreach (string warning in warnings)
                {
                    report.Messages.Add("Warning: " + warning);
                    Debug.LogWarning("[ALTO] " + warning, avatar);
                }
            }
            else if (reference == null && settings.Multiplexing != ParameterMultiplexingMode.Disabled)
            {
                var selection = settings.Multiplexing == ParameterMultiplexingMode.AutomaticMenuInputs
                    ? MultiplexSelection.Analyze(avatar, controllers, preserve, report.Messages, entries) : null;
                var eligible = settings.Multiplexing == ParameterMultiplexingMode.AutomaticMenuInputs
                    ? selection.Eligible
                    : settings.Multiplexing == ParameterMultiplexingMode.AllSynchronizedInputs
                    ? input.Where(p => p.networkSynced).Select(p => p.name).ToHashSet(StringComparer.Ordinal)
                    : new HashSet<string>(settings.MultiplexNames, StringComparer.Ordinal);
                if (settings.Multiplexing == ParameterMultiplexingMode.SelectedInputs)
                    eligible.UnionWith(input.Where(p => settings.MultiplexPrefixes.Any(prefix => p.name.StartsWith(prefix, StringComparison.Ordinal))).Select(p => p.name));
                eligible.ExceptWith(preserve);
                eligible.ExceptWith(encodings.SelectMany(e => e.Bits));
                if (settings.Multiplexing == ParameterMultiplexingMode.AllSynchronizedInputs)
                    report.Messages.Add("All Synchronized Inputs explicitly permits snapshot delay for continuous and momentary inputs. " +
                        "Keep Native and policy preservation still apply; brief changes between snapshots can be missed.");
                try { multiplex = MultiplexEncoding.Plan(output, eligible, settings.Budget, settings.HoldSeconds, occupied); }
                catch (InvalidOperationException exception) when (selection != null)
                { throw new InvalidOperationException(exception.Message + " Selection: " + selection.Summary(output), exception); }
            }
            var multiplexActive = new HashSet<string>(StringComparer.Ordinal);
            var fallback = new Dictionary<string, float>(StringComparer.Ordinal);
            if (multiplex != null)
            {
                multiplex.Validate();
                if (reference != null && multiplex.WireDeclarations().Any(p => occupied.Contains(p.name)))
                    throw new InvalidOperationException("Recorded multiplexing wire names collide with current parameters.");
                var outputByName = output.ToDictionary(p => p.name, StringComparer.Ordinal);
                var frozen = context.GetState<ParameterPolicyState>().Constants;
                foreach (var member in multiplex.Inputs)
                {
                    outputByName.TryGetValue(member.Name, out var logical);
                    if (logical != null && logical.valueType != member.Type)
                        throw new InvalidOperationException("Multiplexed parameter type differs from the reference upload: " + member.Name);
                    if (logical?.networkSynced == true)
                    {
                        if (preserve.Contains(member.Name)) throw new InvalidOperationException("Preservation conflicts with recorded multiplexing: " + member.Name);
                        if (controllers.Any(c => c.Parameters.TryGetValue(member.Name, out var p) && p.type == AnimatorControllerParameterType.Trigger))
                            throw new InvalidOperationException("Multiplexing does not support Trigger parameters: " + member.Name);
                        multiplexActive.Add(member.Name); logical.networkSynced = false;
                    }
                    fallback[member.Name] = frozen.TryGetValue(member.Name, out var constant) ? constant.Value : logical?.defaultValue ?? 0;
                }
                output = output.Concat(multiplex.WireDeclarations()).ToArray();
                occupied.UnionWith(multiplex.WireDeclarations().Select(p => p.name));
            }
            if (active.Count != 0 || multiplex != null)
            {
                if (entries.TryGetValue(VRCAvatarDescriptor.AnimLayerType.FX, out var existingFx))
                    ParameterTransportIdentity.Read(existingFx);
                // Use NDMF's public serialization lifecycle, including layer-index remapping
                // and generated-asset ownership. Reopen after all late SDK processors finish.
                var current = new VirtualControllerContext(); current.OnActivate(context);
                try
                {
                    var key = VRCAvatarDescriptor.AnimLayerType.FX;
                    if (!current.Controllers.TryGetValue(key, out var fx))
                        current.Controllers[key] = fx = VirtualAnimatorController.Create(current.CloneContext, "ALTO Parameter Transport");
                    foreach (var encoding in active)
                        IntegerEncodingAnimator.Append(current.CloneContext, fx, encoding, (int)declared[encoding.Name].defaultValue, save);
                    if (multiplex != null) MultiplexAnimator.Append(current.CloneContext, fx, multiplex,
                        declared, multiplexActive, fallback, occupied, save);
                }
                finally { current.OnDeactivate(context); }
            }
            if (active.Count != 0 || unused.Count != 0 || multiplex != null)
            {
                var copy = original != null ? Object.Instantiate(original) : ScriptableObject.CreateInstance<VRCExpressionParameters>();
                copy.name = "Compressed Expression Parameters"; copy.parameters = output; save(copy);
                if (original != null) ObjectRegistry.RegisterReplacedObject(original, copy);
                descriptor.expressionParameters = copy;
            }
            int before = input.Where(p => p.networkSynced).Sum(p => p.valueType == VRCExpressionParameters.ValueType.Bool ? 1 : 8);
            int after = output.Where(p => p.networkSynced).Sum(p => p.valueType == VRCExpressionParameters.ValueType.Bool ? 1 : 8);
            report.Messages.Add("Parameter transport: " + before + " -> " + after + " bits; " + active.Count +
                " Ints encoded; " + unused.Count + " unused synchronized inputs kept local. Logical names and Saved values retained.");
            foreach (var name in unused.OrderBy(n => n, StringComparer.Ordinal)) report.Messages.Add("Unused synchronized input kept local: " + name);
            if (multiplex != null)
                report.Messages.Add("Time multiplexing: " + multiplex.Inputs.Count + " inputs, " + multiplex.Slots.Count +
                    " typed payload slots, " + multiplex.Batches + " batches; nominal hold time per cycle " +
                    multiplex.NominalCycleSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) +
                    " seconds plus Animator frames and network delay. Only complete received snapshots are applied.");
            return new ParameterCompressionResult { Integers = encodings, Multiplexing = multiplex };
        }
    }
}
