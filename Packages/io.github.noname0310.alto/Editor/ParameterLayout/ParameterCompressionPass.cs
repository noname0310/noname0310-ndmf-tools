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
        internal ParameterTransmissionMode Transmission;
        internal int Budget = 256;
        internal float HoldSeconds = .15f;
        internal readonly HashSet<string> MultiplexNames = new(StringComparer.Ordinal);
        internal readonly List<string> MultiplexPrefixes = new();
        internal readonly List<ResolvedParameterPrecisionRule> PrecisionRules = new();
        internal ParameterInputAssumption InputAssumption;
        internal bool AllowUnsignedBitReduction = true;
        internal readonly List<ResolvedParameterKeepRule> Keep = new();
    }

    internal sealed class ParameterCompressionResult
    {
        internal List<IntegerEncoding> Integers = new();
        internal MultiplexEncoding Multiplexing;
        internal List<FloatPrecisionEncoding> FloatPrecisions = new();
        internal List<ParameterLayoutEntry> AvatarDriven = new();
    }

    internal sealed class ResolvedParameterKeepRule
    {
        internal readonly HashSet<string> Names = new(StringComparer.Ordinal);
        internal readonly List<string> Prefixes = new();
        internal int Priority = 100;
    }

    internal sealed class SharedCompressionSelections
    {
        internal bool Captured;
        internal readonly List<ResolvedParameterKeepRule> Rules = new();
    }

    internal static class ParameterCompressionPass
    {
        // Capture before policy markers are removed. Only the component's own
        // Preserve list is exported, never lists imported by its Freeze Mode.
        // This keeps the two opt-in directions independent and avoids feedback.
        internal static void CapturePolicySelections(BuildContext context)
        {
            var state = context.GetState<SharedCompressionSelections>();
            state.Captured = true; state.Rules.Clear();
            bool Visible(Behaviour component) => component.enabled && !ALTOPlugin.IsEditorOnly(component.transform) &&
                BuildPlatformFilters.IncludesSettings(component.transform, context);
            if (!context.AvatarRootObject.GetComponentsInChildren<ALTOParameterCompression>(true).Any(Visible)) return;
            foreach (var policy in context.AvatarRootObject.GetComponentsInChildren<ALTOAnimatorParameterPolicy>(true)
                .Where(p => Visible(p) && p.UsePreserveForCompression))
            {
                if (policy.CompressionPriority < 1) throw new InvalidOperationException("Policy Compression priority must be positive.");
                var rule = new ResolvedParameterKeepRule { Priority = policy.CompressionPriority };
                CaptureSelection(context, policy.PreserveParameters, rule.Names, rule.Prefixes);
                state.Rules.Add(rule);
            }
        }

        internal static ParameterCompressionSettings Capture(BuildContext context, bool hasLayout)
        {
            var all = context.AvatarRootObject.GetComponentsInChildren<ALTOParameterCompression>(true);
            var enabled = all.Where(c => c.enabled && !ALTOPlugin.IsEditorOnly(c.transform) &&
                BuildPlatformFilters.IncludesSettings(c.transform, context)).ToArray();
            if (enabled.Length > 1) throw new InvalidOperationException("Use one enabled ALTO Parameter Compression per avatar.");
            if (enabled.Length != 0 && !hasLayout)
                throw new InvalidOperationException("ALTO Parameter Compression requires ALTO Parameter Layout on the same avatar.");
            ParameterCompressionSettings settings = null;
            if (enabled.Length == 1)
            {
                var component = enabled[0];
                if (!Enum.IsDefined(typeof(ParameterMultiplexingMode), component.TimeMultiplexing) ||
                    !Enum.IsDefined(typeof(ParameterInputAssumption), component.InputAssumption) ||
                    !Enum.IsDefined(typeof(ParameterTransmissionMode), component.TransmissionMode) ||
                    (component.TimeMultiplexing != ParameterMultiplexingMode.Disabled &&
                    (component.TargetBitBudget < 16 || component.TargetBitBudget > 256 ||
                    !MotionAnalysis.Finite(component.PacketHoldSeconds) || component.PacketHoldSeconds < .1f || component.PacketHoldSeconds > 1)))
                    throw new InvalidOperationException("Choose valid time multiplexing settings and budget.");
                settings = new ParameterCompressionSettings { RemoveUnused = component.RemoveUnusedSynchronization,
                    Multiplexing = component.TimeMultiplexing, Transmission = component.TransmissionMode,
                    Budget = component.TargetBitBudget, HoldSeconds = component.PacketHoldSeconds,
                    InputAssumption = component.InputAssumption, AllowUnsignedBitReduction = component.AllowUnsignedBitReduction };
                var shared = context.GetState<SharedCompressionSelections>();
                if (!shared.Captured) CapturePolicySelections(context);
                settings.Keep.AddRange(shared.Rules);
                foreach (var rule in component.KeepParameters ?? new())
                {
                    if (rule == null || rule.Priority < 1) throw new InvalidOperationException("Keep Parameters priorities must be positive.");
                    var resolved = new ResolvedParameterKeepRule { Priority = rule.Priority };
                    CaptureSelection(context, rule.Parameters, resolved.Names, resolved.Prefixes);
                    settings.Keep.Add(resolved);
                }
                if (settings.Multiplexing == ParameterMultiplexingMode.SelectedInputs)
                    CaptureSelection(context, component.MultiplexedParameters, settings.MultiplexNames, settings.MultiplexPrefixes);
                foreach (var source in component.PrecisionSourceOverrides ?? new())
                {
                    var rule = CapturePrecisionSettings(source);
                    CaptureSelection(context, source.Parameters, rule.Names, rule.Prefixes);
                    settings.PrecisionRules.Add(rule);
                }
                foreach (var individual in component.PrecisionOverrides ?? new())
                {
                    var rule = CapturePrecisionSettings(individual); rule.Individual = true; rule.Type = individual.Type;
                    rule.Names.Add(ResolveSingleName(context, individual.Source, individual.Name));
                    settings.PrecisionRules.Add(rule);
                }
            }
            foreach (var component in all) Object.DestroyImmediate(component);
            return settings;
        }

        private static ResolvedParameterPrecisionRule CapturePrecisionSettings(ParameterPrecisionSettings source)
        {
            if (source == null) throw new InvalidOperationException("Remove empty precision override rows.");
            if (source is ParameterPrecisionSourceOverride) ParameterPrecisionRules.Validate(source);
            return new ResolvedParameterPrecisionRule
            {
                Floats = source.Floats == null ? null : new FloatPrecisionSettings { Precision = source.Floats.Precision, Bits = source.Floats.Bits, Representation = source.Floats.Representation },
                Integers = source.Integers == null ? null : new IntegerRangeSettings { Range = source.Integers.Range, Minimum = source.Integers.Minimum, Maximum = source.Integers.Maximum }
            };
        }

        internal static Dictionary<string, int> ResolveKeep(ParameterCompressionSettings settings,
            IEnumerable<VRCExpressionParameters.Parameter> input, AnimatorParameterDependencies dependencies)
        {
            var priorities = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var rule in settings.Keep)
            {
                var roots = new HashSet<string>(rule.Names, StringComparer.Ordinal);
                roots.UnionWith(input.Where(p => rule.Prefixes.Any(prefix => p.name.StartsWith(prefix, StringComparison.Ordinal))).Select(p => p.name));
                foreach (var name in dependencies.Expand(roots).Concat(roots))
                    priorities[name] = priorities.TryGetValue(name, out int previous) ? Math.Max(previous, rule.Priority) : rule.Priority;
            }
            return priorities;
        }

        private static string ResolveSingleName(BuildContext context, Object source, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Each individual parameter override needs a name. Remove unused rows.");
            if (!ReferenceEquals(source, null) && source == null)
                throw new InvalidOperationException("The parameter override source was removed: " + name);
            if (source == null) return name;
            var resolved = ParameterSourceResolver.Resolve(new AnimatorParameterSource { Source = source }, context.AvatarRootObject, context);
            var candidates = resolved.Parameters.Where(p => p.Name == name).ToArray();
            var names = candidates.Select(p => ParameterPolicyPass.ResolveName(context, p)).Distinct().ToArray();
            if (names.Length != 1 || candidates.Any(p => p.Owner == null) || resolved.Diagnostics.Count != 0)
                throw new InvalidOperationException("Cannot uniquely resolve parameter override: " + name);
            return names[0];
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
                .Any(c => AnimationEventEffects.MayInvokeCallback(MotionAnalysis.Source(c)))) reason = "animation events may observe parameters";
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
            // Planning sees logical consumers only. Generated transport readers must
            // never feed back into dead-input analysis or allocation on this build.
            var plan = Plan(context, settings, reference, report);
            return Commit(plan, save);
        }

        private sealed class CompressionPlan
        {
            internal BuildContext Context;
            internal VRCExpressionParameters Original;
            internal string OriginalData;
            internal VRCExpressionParameters.Parameter[] Output;
            internal Dictionary<string, VRCExpressionParameters.Parameter> Declared;
            internal List<IntegerEncoding> Active;
            internal HashSet<string> MultiplexActive, Occupied;
            internal Dictionary<string, float> Fallback;
            internal bool WriteParameters;
            internal ParameterCompressionResult Result = new();
        }

        private static CompressionPlan Plan(BuildContext context, ParameterCompressionSettings settings,
            ParameterLayoutRecord reference, OptimizationBuildReport report)
        {
            if (settings == null)
            {
                if (reference?.IntegerEncodings?.Count > 0 || reference?.Multiplexing != null || reference?.FloatPrecisionOverrides?.Count > 0 ||
                    reference?.AvatarDrivenEncodings?.Count > 0)
                    throw new InvalidOperationException("The reference layout uses compression. Enable ALTO Parameter Compression on this platform too.");
                return new();
            }
            if (context == null) throw new InvalidOperationException("Parameter compression requires the NDMF build context.");
            var avatar = context.AvatarRootObject;
            var descriptor = avatar.GetComponent<VRCAvatarDescriptor>();
            var original = descriptor.expressionParameters;
            string originalData = original != null ? EditorJsonUtility.ToJson(original) : null;
            var input = ParameterLayoutModel.Read(original?.parameters);
            int before = MultiplexEncoding.Cost(input);
            var overrides = reference == null ? ParameterPrecisionRules.Resolve(settings.PrecisionRules, input) : new ResolvedParameterPrecisions();
            var precisions = reference != null ? reference.FloatPrecisionOverrides ?? new() :
                overrides.Floats;
            input = FloatPrecisionOverrides.Apply(input, precisions);
            var minimumPrecision = precisions.Where(p => p.MinimumPrecision).Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
            var clone = new CloneContext(VRChatPlatformAnimatorBindings.Instance);
            var entries = new Dictionary<object, VirtualAnimatorController>();
            foreach (var (key, controller, _) in VRChatPlatformAnimatorBindings.Instance.GetInnateControllers(avatar))
                if (controller != null) entries[key] = clone.Clone(controller, key);
            var controllers = entries.Values.Distinct().ToArray();
            // Transport inputs combine this component's Keep list with explicitly
            // shared Policy Preserve lists. Freeze values never define transport.
            var dependencies = AnimatorParameterDependencies.Collect(avatar, controllers, controllerEntries: entries);
            var keepPriorities = ResolveKeep(settings, input, dependencies);
            var protectionRoots = keepPriorities.Keys.ToHashSet(StringComparer.Ordinal);
            var preserve = settings.InputAssumption == ParameterInputAssumption.AvatarDrivenExceptListed
                ? new HashSet<string>(protectionRoots, StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal);
            preserve.UnionWith(AnimatorReachability.BuiltIns);
            var declared = input.ToDictionary(p => p.name, StringComparer.Ordinal);
            var occupied = new HashSet<string>(input.Select(p => p.name).Concat(controllers.SelectMany(c => c.Parameters.Keys)), StringComparer.Ordinal);
            occupied.UnionWith(AvatarParameterInputs.Sensors(avatar).Select(p => p.Name));
            occupied.UnionWith(new NativeFaceTracking(avatar).ForwardedParameters());
            occupied.UnionWith(ExpressionMenuOptimizer.Menus(descriptor.expressionsMenu).SelectMany(m => m.controls ?? new())
                .Where(c => c != null).SelectMany(ExpressionMenuOptimizer.Parameters));
            var encodings = reference?.IntegerEncodings ?? overrides.Integers.Select(rule =>
                declared.TryGetValue(rule.Name, out var parameter) ? IntegerEncoding.CreateForSdk(parameter, rule.Minimum, rule.Maximum, occupied)
                    : throw new InvalidOperationException("Compressed integer is absent: " + rule.Name)).ToList();
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
                if (reference != null && encoding.WireNames.Any(occupied.Contains))
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
            // Each explicitly encoded integer has one recorded transport contract.
            // Do not multiplex a native narrowed integer or its packed wire bits again.
            preserve.UnionWith(encodings.Select(e => e.Name));
            preserve.UnionWith(encodings.SelectMany(e => e.WireNames));
            MultiplexEncoding multiplex = reference?.Multiplexing;
            var avatarDriven = reference?.AvatarDrivenEncodings ?? new List<ParameterLayoutEntry>();
            if (multiplex != null && settings.Multiplexing == ParameterMultiplexingMode.Disabled)
                throw new InvalidOperationException("The reference upload uses time multiplexing. Enable it on this platform too.");
            if (reference == null)
            {
                var excluded = precisions.Select(p => p.Name).Concat(encodings.Select(e => e.Name)).ToHashSet(StringComparer.Ordinal);
                excluded.UnionWith(overrides.SelectedIntegers);
                var candidates = settings.InputAssumption == ParameterInputAssumption.AvatarDrivenExceptListed && settings.AllowUnsignedBitReduction
                    ? AvatarDrivenEncodingAnalysis.Analyze(avatar, controllers, output, preserve, excluded, report.Messages) : new List<ParameterLayoutEntry>();
                var planned = ParameterTransportPlanning.Choose(avatar, controllers, entries, settings, output, preserve, keepPriorities,
                    minimumPrecision, occupied, candidates, report);
                output = planned.Parameters; multiplex = planned.Multiplexing; avatarDriven = planned.AvatarDriven;
                report.Messages.AddRange(planned.Messages);
                foreach (string warning in planned.Warnings)
                {
                    report.Messages.Add("Warning: " + warning);
                    Debug.LogWarning("[ALTO] " + warning, avatar);
                }
            }
            else
            {
                if (avatarDriven.Any(p => preserve.Contains(p.Name) && output.Any(o => o.name == p.Name && o.networkSynced)))
                    throw new InvalidOperationException("External input protection conflicts with a recorded avatar-driven encoding. Rebuild the reference platform.");
                output = AvatarDrivenEncodingAnalysis.Apply(output, avatarDriven);
            }
            var multiplexActive = new HashSet<string>(StringComparer.Ordinal);
            var fallback = new Dictionary<string, float>(StringComparer.Ordinal);
            if (multiplex != null)
            {
                if (reference == null)
                    multiplex.Protocol = settings.Transmission == ParameterTransmissionMode.LowLatency ? 2 : 1;
                multiplex.Validate();
                if (reference != null && multiplex.WireDeclarations().Any(p => occupied.Contains(p.name)))
                    throw new InvalidOperationException("Recorded multiplexing wire names collide with current parameters.");
                var outputByName = output.ToDictionary(p => p.name, StringComparer.Ordinal);
                var frozen = context.GetState<ParameterPolicyState>().Constants;
                foreach (var member in multiplex.Inputs)
                {
                    outputByName.TryGetValue(member.Name, out var logical);
                    if (logical?.networkSynced == true)
                    {
                        // Local/frozen declarations are not codec inputs. Their
                        // original precision can differ from the recorded wire,
                        // which still carries the fallback value on this platform.
                        if (!member.SameEncoding(logical))
                            throw new InvalidOperationException("Multiplexed parameter encoding differs from the reference upload: " + member.Name);
                        if (!MultiplexEncoding.CanMultiplex(logical)) throw new InvalidOperationException("Custom interpolation conflicts with recorded multiplexing: " + member.Name + ". Rebuild the reference platform to keep this input native.");
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
            if ((active.Any(e => !e.IsNative) || multiplex != null) && entries.TryGetValue(VRCAvatarDescriptor.AnimLayerType.FX, out var existingFx))
                ParameterTransportIdentity.Read(existingFx);
            int after = MultiplexEncoding.Cost(output);
            report.Messages.Add("Parameter transport: " + before + " -> " + after + " bits; " + active.Count +
                " Ints encoded; " + unused.Count + " unused synchronized inputs kept local. Logical names and Saved values retained.");
            foreach (var name in unused.OrderBy(n => n, StringComparer.Ordinal)) report.Messages.Add("Unused synchronized input kept local: " + name);
            foreach (var encoding in active)
                report.Messages.Add("Integer encoding: " + encoding.Name + ": " + (encoding.IsNative
                    ? encoding.NativeBitWidth + "-bit native Int; no codec layer"
                    : encoding.IsRemapped ? encoding.NativeBitWidth + "-bit remapped Int" : encoding.Bits.Count + " Bool wire bits (recorded or two-value range)"));
            foreach (var precision in precisions)
            {
                var member = multiplex?.Inputs.FirstOrDefault(p => p.Name == precision.Name);
                int actualBits = member != null ? multiplex.Slots[member.Slot].Cost : precision.Cost;
                report.Messages.Add("Float precision override: " + precision.Name + ": " +
                    (precision.MinimumPrecision ? "minimum " : "exact ") + precision.Cost + " bits, " +
                    (precision.FlippedRepresentation ? "unsigned" : "signed") + "; " + actualBits + "-bit " +
                    (member != null ? "shared slot" : output.Any(p => p.name == precision.Name && p.networkSynced) ? "direct synchronization" : "local input"));
            }
            if (multiplex != null)
                report.Messages.Add("Time multiplexing: " + multiplex.Inputs.Count + " inputs, " + multiplex.Slots.Count +
                    " typed payload slots, " + multiplex.Batches + " batches; " + (multiplex.Protocol == 2 ? "Low Latency" : "Guarded") +
                    "; nominal average hold time per cycle " +
                    multiplex.NominalCycleSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) +
                    " seconds plus Animator frames and network delay. Only complete received snapshots are applied.");
            if (multiplex?.Protocol == 2)
                report.Messages.Add("Low Latency transport assumes coherent packet field delivery. Every " + Math.Min(4, multiplex.EpochCount) +
                    "th sweep holds publications longer for slow observers. This does not guarantee delivery under arbitrary loss or field skew.");
            var recordedPrecisions = reference != null ? precisions : precisions.Where(p =>
                output.Any(o => o.name == p.Name && o.networkSynced) || multiplex?.Inputs.Any(i => i.Name == p.Name) == true).ToList();
            return new CompressionPlan
            {
                Context = context, Original = original, OriginalData = originalData, Output = output, Declared = declared,
                Active = active, MultiplexActive = multiplexActive, Fallback = fallback, Occupied = occupied,
                WriteParameters = active.Count != 0 || unused.Count != 0 || multiplex != null || precisions.Count != 0 || avatarDriven.Count != 0,
                Result = new ParameterCompressionResult { Integers = encodings, Multiplexing = multiplex, FloatPrecisions = recordedPrecisions, AvatarDriven = avatarDriven }
            };
        }

        // A plan is private to the synchronous planning/commit interval. It is never
        // cached across generator boundaries, and a recorded wire contract is never
        // repacked after another platform has consumed it.
        private static ParameterCompressionResult Commit(CompressionPlan plan, Action<Object> save)
        {
            var context = plan.Context;
            if (context == null) return plan.Result;
            var descriptor = context.AvatarRootObject.GetComponent<VRCAvatarDescriptor>();
            var original = descriptor.expressionParameters;
            if (original != plan.Original || (original != null ? EditorJsonUtility.ToJson(original) : null) != plan.OriginalData)
                throw new InvalidOperationException("Parameter declarations changed while planning transport. No transport was emitted.");
            var multiplex = plan.Result.Multiplexing;
            if (plan.Active.Any(e => !e.IsNative) || multiplex != null)
            {
                var current = new VirtualControllerContext(); current.OnActivate(context);
                try
                {
                    var key = VRCAvatarDescriptor.AnimLayerType.FX;
                    if (!current.Controllers.TryGetValue(key, out var fx))
                        current.Controllers[key] = fx = VirtualAnimatorController.Create(current.CloneContext, "ALTO Parameter Transport");
                    foreach (var encoding in plan.Active)
                        IntegerEncodingAnimator.Append(current.CloneContext, fx, encoding, (int)plan.Declared[encoding.Name].defaultValue, save);
                    if (multiplex != null) MultiplexAnimator.Append(current.CloneContext, fx, multiplex,
                        plan.Declared, plan.MultiplexActive, plan.Fallback, plan.Occupied, save);
                }
                finally { AnimatorAssetPersistence.Commit(context, current, save); }
            }
            if (plan.WriteParameters)
            {
                var copy = original != null ? Object.Instantiate(original) : ScriptableObject.CreateInstance<VRCExpressionParameters>();
                copy.name = "Compressed Expression Parameters"; copy.parameters = plan.Output; save(copy);
                if (original != null) ObjectRegistry.RegisterReplacedObject(original, copy);
                descriptor.expressionParameters = copy;
            }
            return plan.Result;
        }
    }
}
