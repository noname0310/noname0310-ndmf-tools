using System;
using System.Collections.Generic;
using Noname.AvatarTools.Analysis;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Noname.AvatarTools.Materials
{
    internal static partial class SharedShaderConstants
    {
        // One read-only dependency-planning interval. Metadata is shader-owned;
        // writers and native defaults are renderer-owned; values are material-owned.
        // Keeping these identities separate avoids repeating the same native calls
        // without treating different material defaults as interchangeable.
        private sealed class InputCollection
        {
            private readonly AvatarAnimationIndex index;
            private readonly Func<Renderer, bool> allowInitialChanges;
            private readonly Dictionary<Shader, ShaderProperties> schemas = new();
            private readonly Dictionary<Renderer, RendererProperties> renderers = new();

            private readonly struct ScalarProperty
            {
                internal readonly string Name;
                internal readonly int Id;
                internal ScalarProperty(string name) { Name = name; Id = Shader.PropertyToID(name); }
            }

            private sealed class ShaderProperties
            {
                internal readonly Dictionary<string, ScalarProperty> Scalars = new(StringComparer.Ordinal);
                internal readonly List<ScalarProperty> Unique = new();
                internal ShaderProperties(Shader shader)
                {
                    using var cost = AnalysisCost.Measure("Shared shader constants/property metadata");
                    var declared = new HashSet<string>(StringComparer.Ordinal);
                    var duplicates = new HashSet<string>(StringComparer.Ordinal);
                    int count = shader.GetPropertyCount();
                    for (int i = 0; i < count; i++)
                    {
                        string name = shader.GetPropertyName(i);
                        if (!declared.Add(name)) { duplicates.Add(name); continue; }
                        if (!Scalar(shader, i)) continue;
                        var property = new ScalarProperty(name); Scalars.Add(name, property); Unique.Add(property);
                    }
                    Unique.RemoveAll(p => duplicates.Contains(p.Name));
                    // Native default lookup historically uses FindPropertyIndex.
                    // Preserve that behavior for duplicate authoring properties,
                    // while never treating them as unique rewrite candidates.
                    foreach (string name in duplicates)
                    {
                        if (Scalar(shader, shader.FindPropertyIndex(name))) Scalars[name] = new ScalarProperty(name);
                        else Scalars.Remove(name);
                    }
                }
                private static bool Scalar(Shader shader, int property)
                {
                    if (property < 0) return false;
                    var type = shader.GetPropertyType(property);
                    return (type == ShaderPropertyType.Float || type == ShaderPropertyType.Range) &&
                        (shader.GetPropertyFlags(property) & (ShaderPropertyFlags.PerRendererData | ShaderPropertyFlags.Gamma)) == 0;
                }
            }

            private readonly struct AnimatedValue
            {
                internal readonly bool Valid, Preserved;
                internal readonly float Value;
                internal AnimatedValue(float value, bool preserved) { Valid = true; Value = value; Preserved = preserved; }
            }

            private sealed class RendererProperties
            {
                internal bool Unsafe;
                internal readonly HashSet<string> NativeWriters = new(StringComparer.Ordinal);
                internal readonly Dictionary<string, List<AvatarAnimationIndex.BindingUse>> Writers = new(StringComparer.Ordinal);
                internal readonly Dictionary<string, AnimatedValue> Outputs = new(StringComparer.Ordinal);
            }

            internal InputCollection(AvatarAnimationIndex index, Func<Renderer, bool> allowInitialChanges)
            { this.index = index; this.allowInitialChanges = allowInitialChanges; }

            private ShaderProperties Schema(Shader shader)
            {
                if (!schemas.TryGetValue(shader, out var result)) schemas.Add(shader, result = new(shader));
                return result;
            }

            private RendererProperties RendererInputs(Renderer renderer)
            {
                if (renderers.TryGetValue(renderer, out var result)) return result;
                using var cost = AnalysisCost.Measure("Shared shader constants/renderer writers");
                result = new(); renderers.Add(renderer, result);
                foreach (var use in index.ForObject(renderer))
                {
                    string name = MaterialAnimationInputs.PropertyName(use.Binding.propertyName);
                    result.Unsafe |= !use.Complete || name == null && use.Binding.propertyName.StartsWith("material.", StringComparison.Ordinal);
                    if (name == null) continue;
                    if (!result.Writers.TryGetValue(name, out var writers)) result.Writers.Add(name, writers = new());
                    writers.Add(use);
                }
                foreach (var receiver in ContactOutputs.Writers(index.Avatar, renderer, index))
                    result.NativeWriters.UnionWith(ContactOutputs.Properties(receiver));
                return result;
            }

            private AnimatedValue Output(Renderer renderer, RendererProperties inputs, string name,
                IReadOnlyList<AvatarAnimationIndex.BindingUse> writers)
            {
                if (inputs.Outputs.TryGetValue(name, out var cached)) return cached;
                var output = Prove(); inputs.Outputs.Add(name, output); return output;
                AnimatedValue Prove()
                {
                    string binding = "material." + name; bool preserved = false;
                    foreach (var writer in writers)
                    {
                        if (writer.Binding.propertyName != binding || writer.Binding.isPPtrCurve) return default;
                        preserved |= writer.PreservedInput;
                    }
                    var initial = renderer.sharedMaterial;
                    if (initial == null || initial.shader == null || !Schema(initial.shader).Scalars.TryGetValue(name, out var property) ||
                        !MotionAnalysis.Finite(initial.GetFloat(property.Id)) ||
                        !AnimationUtility.GetFloatValue(renderer.gameObject, EditorCurveBinding.FloatCurve("", renderer.GetType(), binding), out float nativeInitial) ||
                        !index.ConstantOutputs.TryProve(renderer, binding, ConstantOutputValue.Scalar(nativeInitial), out var proof, out _)) return default;
                    return new AnimatedValue(proof.Value.Number, preserved);
                }
            }

            internal Dictionary<string, float> Read(Renderer renderer, Material material, ConstantDemand demand,
                out HashSet<string> initialChanges)
            {
                using var cost = AnalysisCost.Measure("Shared shader constants/property values");
                var result = new Dictionary<string, float>(StringComparer.Ordinal);
                initialChanges = new HashSet<string>(StringComparer.Ordinal);
                var inputs = RendererInputs(renderer);
                if (inputs.Unsafe) return result;
                foreach (var property in Schema(material.shader).Unique)
                {
                    string name = property.Name;
                    if (!demand.Contains(name) || inputs.NativeWriters.Contains(name)) continue;
                    float initial = material.GetFloat(property.Id);
                    if (!MotionAnalysis.Finite(initial)) continue;
                    float value = initial;
                    if (inputs.Writers.TryGetValue(name, out var writers))
                    {
                        var output = Output(renderer, inputs, name, writers);
                        if (!output.Valid) continue;
                        value = output.Value;
                        if (BitConverter.SingleToInt32Bits(value) != BitConverter.SingleToInt32Bits(initial))
                        {
                            if (allowInitialChanges?.Invoke(renderer) != true || output.Preserved) continue;
                            initialChanges.Add(name);
                        }
                    }
                    result.Add(name, value);
                }
                return result;
            }
        }
    }
}
