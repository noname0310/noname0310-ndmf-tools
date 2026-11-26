using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Rendering;
using VRC.SDK3.Avatars.Components;

namespace Noname.AvatarTools.Analysis
{
    // AAO AutoMergeSkinnedMesh's default-value/animation-location comparison is a
    // design reference. This implementation uses Unity's native binding defaults
    // and ALTO's shared writer index. Materials and their unanimated values remain
    // separate; only equivalent renderer-wide animation outputs are coalesced.
    internal sealed class MaterialAnimationInputs
    {
        private static readonly Regex Property = new(@"^material\.([_A-Za-z][_A-Za-z0-9]*)(?:\.([rgbaxyzw]))?$", RegexOptions.CultureInvariant);
        private readonly AvatarAnimationIndex index;
        private readonly Dictionary<(Renderer, string), Defaults> defaults = new();
        private readonly Dictionary<(Renderer, Material, ColorSpace), IReadOnlyDictionary<string, float>> uniforms = new();
        private readonly Dictionary<(Renderer, string), bool> fullOutputs = new();
        private readonly Dictionary<VirtualMotion, HashSet<EditorCurveBinding>> motionBindings = new();
        private AnimatorReachability reachability;
        private FxPropertyOutput layerFacts;

        private sealed class Defaults
        {
            internal readonly Dictionary<string, float> Values = new(StringComparer.Ordinal);
            internal readonly Dictionary<string, string> Types = new(StringComparer.Ordinal);
            internal readonly Dictionary<string, string[]> Groups = new(StringComparer.Ordinal);
            internal string Uncertain;
        }

        internal MaterialAnimationInputs(AvatarAnimationIndex index) { this.index = index; }

        internal static string PropertyName(string binding)
        {
            var match = Property.Match(binding);
            return match.Success ? match.Groups[1].Value : null;
        }

        private Defaults Read(Renderer renderer) => Read(renderer, index.ForObject(renderer)
            .Where(u => u.Binding.propertyName.StartsWith("material.", StringComparison.Ordinal)).Select(u => u.Binding.propertyName));

        private Defaults Read(Renderer renderer, IEnumerable<string> bindings)
        {
            var requested = bindings.Distinct().OrderBy(n => n, StringComparer.Ordinal).ToArray();
            var key = (renderer, string.Join(";", requested));
            if (defaults.TryGetValue(key, out var cached)) return cached;
            var result = new Defaults(); defaults.Add(key, result);
            var material = renderer.sharedMaterial;
            if (material == null || material.shader == null || renderer.HasPropertyBlock())
            { result.Uncertain = "unverified material defaults"; return result; }
            var declared = new HashSet<string>(StringComparer.Ordinal);
            var ambiguous = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < material.shader.GetPropertyCount(); i++)
            {
                string propertyName = material.shader.GetPropertyName(i);
                if (!declared.Add(propertyName)) ambiguous.Add(propertyName);
            }
            var groups = requested.GroupBy(PropertyName);
            foreach (var group in groups)
            {
                if (group.Key == null) { result.Uncertain = "unsupported material animation binding"; return result; }
                string name = group.Key;
                if (ambiguous.Contains(name)) { result.Uncertain = "ambiguous material property declaration: " + name; return result; }
                int property = material.shader.FindPropertyIndex(name);
                string kind = property < 0 ? "undeclared" : material.shader.GetPropertyType(property) + ":" +
                    (material.shader.GetPropertyFlags(property) & (ShaderPropertyFlags.Gamma | ShaderPropertyFlags.HDR));
                var channels = group.ToArray();
                bool scalar = channels.Any(n => n == "material." + name);
                // Undeclared scalar bindings and ordinary Float/Range bindings
                // use the same native float encoding. Their defaults are still
                // read and compared; color, gamma and integer contracts stay distinct.
                if (scalar && (property < 0 ||
                    (material.shader.GetPropertyType(property) == ShaderPropertyType.Float || material.shader.GetPropertyType(property) == ShaderPropertyType.Range) &&
                    (material.shader.GetPropertyFlags(property) & (ShaderPropertyFlags.Gamma | ShaderPropertyFlags.HDR)) == 0)) kind = "float";
                result.Types[name] = kind;
                var suffixes = channels.Where(n => n.Length == "material.".Length + name.Length + 2).Select(n => n[n.Length - 1]).ToArray();
                if (scalar && suffixes.Length != 0 || suffixes.Any("rgba".Contains) && suffixes.Any("xyzw".Contains))
                { result.Uncertain = "aliased material animation channels: " + name; return result; }
                result.Groups[name] = channels;
                // A vector/color is one native property-block output. Check its
                // implicit channels as well, including Write Defaults restoration.
                var read = new HashSet<string>(channels, StringComparer.Ordinal);
                foreach (string binding in channels.Where(n => n.Length == "material.".Length + name.Length + 2))
                    foreach (char channel in "rgba".Contains(binding[binding.Length - 1]) ? "rgba" : "xyzw")
                        read.Add("material." + name + "." + channel);
                foreach (string binding in read)
                {
                    var native = EditorCurveBinding.FloatCurve("", renderer.GetType(), binding);
                    if (!AnimationUtility.GetFloatValue(renderer.gameObject, native, out float value) || !MotionAnalysis.Finite(value))
                    { result.Uncertain = "unverified material default: " + binding; return result; }
                    result.Values[binding] = value;
                }
            }
            return result;
        }

        internal string Incompatibility(Renderer first, Renderer second, bool allowAnimatedDefaults = false)
        {
            var a = Read(first); var b = Read(second);
            if (a.Uncertain != null || b.Uncertain != null) return a.Uncertain ?? b.Uncertain;
            if (a.Groups.Count != b.Groups.Count) return "different material animation interfaces";
            foreach (var group in a.Groups)
            {
                if (!b.Groups.TryGetValue(group.Key, out var channels) || !channels.SequenceEqual(group.Value) ||
                    a.Types[group.Key] != b.Types[group.Key]) return "different material animation interfaces: " + group.Key;
                var names = a.Values.Keys.Where(n => PropertyName(n) == group.Key).ToArray();
                var changed = names.Where(n => !b.Values.TryGetValue(n, out float other) || !SameBits(a.Values[n], other)).ToArray();
                if (changed.Length == 0) continue;
                if (FullyDriven(first, group.Key, group.Value) && FullyDriven(second, group.Key, channels)) continue;
                // The caller must still prove identical writers and evaluation.
                // Only explicit animated channels may lose their starting values;
                // implicit vector/color channels remain part of the native output.
                if (allowAnimatedDefaults && changed.All(group.Value.Contains)) continue;
                return "different observable animated material defaults: " + group.Key;
            }
            return null;
        }

        internal string RelocationIncompatibility(Renderer destination, Renderer source, string property, string[] channels,
            bool matchingDestinationWriters, bool allowAnimatedDefaults)
        {
            var a = Read(destination, channels); var b = Read(source, channels);
            if (a.Uncertain != null || b.Uncertain != null) return a.Uncertain ?? b.Uncertain;
            if (a.Types[property] != b.Types[property]) return "different material animation interfaces: " + property;
            var changed = b.Values.Keys.Where(n => !SameBits(a.Values[n], b.Values[n])).ToArray();
            if (changed.Length == 0) return null;
            // The chosen producer retains its original controller/layer/state
            // coverage after path relocation. An unused destination writer is
            // removed, so it cannot supply coverage for this proof.
            if (FullyDriven(source, property, channels) &&
                (!matchingDestinationWriters || FullyDriven(destination, property, channels))) return null;
            if (allowAnimatedDefaults && changed.All(channels.Contains)) return null;
            return "different observable animated material defaults: " + property;
        }

        internal IEnumerable<(string Property, float Previous, float Retained)> RelocationDefaultChanges(
            Renderer destination, Renderer source, string property, string[] channels, bool matchingDestinationWriters)
        {
            var a = Read(destination, channels); var b = Read(source, channels);
            if (a.Uncertain != null || b.Uncertain != null) yield break;
            if (FullyDriven(source, property, channels) &&
                (!matchingDestinationWriters || FullyDriven(destination, property, channels))) yield break;
            foreach (string binding in channels)
                if (!SameBits(a.Values[binding], b.Values[binding])) yield return (binding, b.Values[binding], a.Values[binding]);
        }

        internal IEnumerable<(string Property, float Previous, float Retained)> DefaultChanges(Renderer destination, Renderer source)
        {
            var a = Read(destination); var b = Read(source);
            foreach (var group in a.Groups)
            {
                if (FullyDriven(destination, group.Key, group.Value) && FullyDriven(source, group.Key, b.Groups[group.Key])) continue;
                foreach (string binding in group.Value)
                    if (!SameBits(a.Values[binding], b.Values[binding]))
                        yield return (binding, b.Values[binding], a.Values[binding]);
            }
        }

        private static bool SameBits(float first, float second) =>
            ConstantOutputValue.Scalar(first).Equals(ConstantOutputValue.Scalar(second));

        // Material-slot baking uses the same coverage proof as renderer relocation:
        // both can change the defaults captured by the native material animator.
        internal bool FullyDriven(Renderer renderer, string name, string[] channels)
        {
            var key = (renderer, name);
            if (fullOutputs.TryGetValue(key, out bool cached)) return cached;
            bool Inspect()
            {
                // Require every channel of a compound output before discarding
                // its initial value. Partial channel writes may synthesize zeros.
                if (channels.Any(n => n.Length == "material.".Length + name.Length + 2))
                {
                    string suffixes = "rgba".Contains(channels[0][channels[0].Length - 1]) ? "rgba" : "xyzw";
                    if (channels.Length != 4 || suffixes.Any(c => !channels.Contains("material." + name + "." + c))) return false;
                }
                if (index.UnsafeReason != null) return false;
                var uses = index.ForObject(renderer).Where(u => PropertyName(u.Binding.propertyName) == name).ToArray();
                if (uses.Length == 0 || uses.Any(u => !u.Complete || u.Immutable || u.Roots.Length != 1 ||
                    u.Roots[0] != index.Avatar.transform || u.Curve == null || u.Binding.isDiscreteCurve)) return false;
                var bindings = uses.Select(u => u.Binding).Distinct().ToArray();
                if (bindings.Length != channels.Length) return false;
                var clips = uses.Select(u => u.Clip).ToHashSet();
                var owners = index.ControllerEntries.Where(e => e.Value.AllReachableNodes().OfType<VirtualClip>().Any(clips.Contains)).ToArray();
                if (owners.Length != 1 || !FxPropertyOutput.IsFx(owners[0].Key)) return false;
                var controller = owners[0].Value; var layers = controller.Layers.ToArray();
                var writer = layers.LastOrDefault(l => l.AllReachableNodes().OfType<VirtualClip>().Any(clips.Contains));
                if (writer == null || layers[0].AvatarMask != null || writer.AvatarMask != null || writer.SyncedLayerIndex != -1 ||
                    writer.BlendingMode != AnimatorLayerBlendingMode.Override || writer != layers[0] && writer.DefaultWeight != 1) return false;
                reachability ??= index.Reachability;
                if (reachability.FallbackReason != null) return false;
                layerFacts ??= new FxPropertyOutput(index.ControllerEntries, reachability);
                if (layerFacts.WeightCanChange(controller, writer)) return false;
                // A later Write Defaults layer may expose the original value even
                // without a matching explicit curve. Do not infer global coverage.
                if (layers.Skip(Array.IndexOf(layers, writer) + 1).Any(l =>
                    layerFacts.ReachableStates(l).Any(s => s.WriteDefaultValues))) return false;
                var states = FxPropertyOutput.OutputStates(writer, reachability);
                return states.Length > 0 && states.All(s => MotionCovers(s.Motion, bindings, name,
                    new HashSet<VirtualMotion>(), s.WriteDefaultValues));
            }
            return fullOutputs[key] = Inspect();
        }

        private bool MotionCovers(VirtualMotion motion, EditorCurveBinding[] bindings, string name, HashSet<VirtualMotion> stack,
            bool writeDefaults)
        {
            if (motion == null || !stack.Add(motion)) return false;
            bool result = false;
            if (motion is VirtualClip clip)
            {
                var source = MotionAnalysis.Source(clip);
                result = !clip.IsMarkerClip && !clip.Legacy && clip.AdditiveReferencePoseClip == null && source != null &&
                    !source.humanMotion && source.events.Length == 0 && bindings.All(b => clip.GetFloatCurve(b) is { } curve &&
                        curve.length > 0 && curve.keys.All(k => MotionAnalysis.Finite(k.time) && MotionAnalysis.Finite(k.value)));
            }
            else if (motion is VirtualBlendTree tree && tree.Children.Count > 0 && tree.Children.All(c =>
                !c.Mirror && MotionAnalysis.Finite(c.TimeScale) && MotionAnalysis.Finite(c.CycleOffset)))
            {
                if (tree.BlendType == BlendTreeType.Simple1D && tree.Children.All(c => MotionAnalysis.Finite(c.Threshold)) &&
                    tree.Children.Select(c => c.Threshold).Distinct().Count() == tree.Children.Count)
                    result = (MotionAnalysis.Constant1DChildren(tree, reachability) ?? tree.Children.ToArray())
                        .All(c => MotionCovers(c.Motion, bindings, name, stack, writeDefaults));
                else if (tree.BlendType == BlendTreeType.Direct && !tree.NormalizedBlendValues &&
                    (writeDefaults || !bindings.Any(binding => ConstantMotionOutput.DirectReadsRetainedValue(
                        tree, binding, reachability, motionBindings))))
                {
                    var writers = tree.Children.Where(c => c.Motion != null && MotionAnalysis.Bindings(c.Motion).Any(b =>
                        b.path == bindings[0].path && b.type == bindings[0].type && PropertyName(b.propertyName) == name) &&
                        (!reachability.Values(c.DirectBlendParameter).TryGetConstant(out float w) || w != 0)).ToArray();
                    result = writers.Length == 1 && reachability.Values(writers[0].DirectBlendParameter).TryGetConstant(out float weight) &&
                        weight == 1 && MotionCovers(writers[0].Motion, bindings, name, stack, writeDefaults);
                }
            }
            stack.Remove(motion); return result;
        }

        internal IReadOnlyDictionary<string, float> ConstantUniforms(Renderer renderer, Material material)
        {
            var colorSpace = PlayerSettings.colorSpace;
            var key = (renderer, material, colorSpace);
            if (uniforms.TryGetValue(key, out var cached)) return cached;
            var result = new Dictionary<string, float>(StringComparer.Ordinal); uniforms.Add(key, result);
            if (renderer == null || material == null || material.shader == null || renderer.HasPropertyBlock() || index.UnsafeReason != null) return result;
            var uses = index.ForObject(renderer);
            if (uses.Any(u => !u.Complete || u.Binding.isPPtrCurve ||
                u.Binding.propertyName.StartsWith("material.", StringComparison.Ordinal) && PropertyName(u.Binding.propertyName) == null)) return result;
            var changed = uses.Select(u => PropertyName(u.Binding.propertyName)).Where(n => n != null).ToHashSet();
            foreach (var receiver in ContactOutputs.Writers(index.Avatar, renderer, index)) changed.UnionWith(ContactOutputs.Properties(receiver));
            var declared = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < material.shader.GetPropertyCount(); i++)
            {
                string name = material.shader.GetPropertyName(i);
                // Shader generators can repeat editor-only placeholder properties.
                // Duplicate names are not a unique typed runtime declaration.
                if (!declared.Add(name)) { result.Remove(name); changed.Add(name); continue; }
                var type = material.shader.GetPropertyType(i);
                if (type != ShaderPropertyType.Float && type != ShaderPropertyType.Range && type != ShaderPropertyType.Int) continue;
                var flags = material.shader.GetPropertyFlags(i);
                if (changed.Contains(name) || (flags & ShaderPropertyFlags.PerRendererData) != 0) continue;
                // Gamma getters round-trip the native Linear upload and can lose
                // information (negative uploads read back as zero). Even getter
                // values 0/1 cannot prove an exact upload. Keep these uniforms
                // unknown; independent constants and compiled dead-code proofs
                // can still establish index independence without them.
                if (colorSpace == ColorSpace.Linear && (flags & ShaderPropertyFlags.Gamma) != 0) continue;
                float value = type == ShaderPropertyType.Int ? material.GetInteger(name) : material.GetFloat(name);
                // The restricted HLSL adapter has no typed uniform-upload ABI.
                // Integer zero has the same representation as float zero; other
                // integer properties must not be substituted into float globals.
                if (type == ShaderPropertyType.Int && value != 0) continue;
                if (MotionAnalysis.Finite(value)) result.Add(name, value);
            }
            return result;
        }
    }
}
