using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.Analysis
{
    // Generated shaders are saved as native assets, without a .shader text file.
    // Keep their actual immutable source available to later build analyses. The
    // weak key cannot extend the lifetime of a discarded build's shader objects.
    internal static class ShaderBuildSource
    {
        private sealed class Record
        {
            internal string Text, Path;
            internal HashSet<string> RemainingConstants;
            internal Shader[] Dependencies;
            internal ShaderVertexIdRewriter.Layout Layout;
        }
        private static readonly ConditionalWeakTable<Shader, Record> generated = new();

        internal static void Register(Shader shader, string text, string sourcePath, IEnumerable<string> remainingConstants = null,
            Shader[] dependencies = null)
        {
            if (shader == null || text == null) throw new ArgumentNullException();
            generated.Remove(shader);
            generated.Add(shader, new Record { Text = text, Path = sourcePath,
                RemainingConstants = remainingConstants == null ? null : new HashSet<string>(remainingConstants, StringComparer.Ordinal),
                Dependencies = dependencies == null ? null : (Shader[])dependencies.Clone() });
        }

        // CreateShaderAsset may inline UsePass and omit the owner from native
        // dependency serialization. Retain the actual generation-time objects;
        // resolving their names again here could accept a different shader.
        internal static HashSet<Shader> Dependencies(Shader shader)
        {
            var result = new HashSet<Shader>(); var pending = new Stack<Shader>(); pending.Push(shader);
            while (pending.Count != 0)
            {
                var current = pending.Pop();
                if (current == null) throw new InvalidOperationException("a generated shader dependency was destroyed");
                if (!result.Add(current)) continue;
                if (result.Count > 4096) throw new InvalidOperationException("shader dependency graph proof budget exceeded");
                foreach (var child in ShaderPropertyUsage.CollectNativeShaderDependencies(current))
                    if (!result.Contains(child)) pending.Push(child);
                if (generated.TryGetValue(current, out var record) && record.Dependencies != null)
                    foreach (var child in record.Dependencies) if (child == null || !result.Contains(child)) pending.Push(child);
            }
            return result;
        }

        internal static bool CanSpecialize(Shader shader, IEnumerable<string> names) =>
            !generated.TryGetValue(shader, out var record) || record.RemainingConstants == null || record.RemainingConstants.Overlaps(names);

        // False means unknown, not empty. Only a complete source rewrite may
        // certify which declarations remain; dependency demand is separate.
        internal static bool TryRemainingConstants(Shader shader, out IReadOnlyCollection<string> names)
        {
            names = null;
            if (shader == null || !generated.TryGetValue(shader, out var record) || record.RemainingConstants == null) return false;
            names = record.RemainingConstants; return true;
        }

        internal static ShaderVertexIdRewriter.Layout Layout(Shader shader, string source) =>
            generated.TryGetValue(shader, out var record) && record.Text == source
                ? record.Layout ??= ShaderVertexIdRewriter.Layout.Parse(source, allowSharedPrograms: true, allowDependencies: true)
                : ShaderVertexIdRewriter.Layout.Parse(source, allowSharedPrograms: true, allowDependencies: true);

        internal static bool TryGenerated(Shader shader, out string text, out string path)
        {
            text = path = null;
            if (shader == null || !generated.TryGetValue(shader, out var record)) return false;
            text = record.Text; path = record.Path; return true;
        }

        internal static string PhysicalPath(Shader shader) => TryGenerated(shader, out _, out string path)
            ? path : ShaderPropertyUsage.PhysicalPath(AssetDatabase.GetAssetPath(shader));

        internal static bool TryRead(Shader shader, out string text, out string path)
        {
            if (TryGenerated(shader, out text, out path)) return true;
            path = PhysicalPath(shader); text = null;
            if (path == null || !File.Exists(path) || !Path.GetExtension(path).Equals(".shader", StringComparison.OrdinalIgnoreCase)) return false;
            text = File.ReadAllText(path); return true;
        }
    }
}
