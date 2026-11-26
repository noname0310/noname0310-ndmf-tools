using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Noname.AvatarTools.Analysis
{
    // Unity's imported alternatives include the engine's built-in lighting
    // pragmas. Treating every keyword as an independent bit invents impossible
    // light/shadow combinations. The unfiltered collection query enumerates
    // alternatives without requiring a draw or recording a material's state.
    internal sealed class NativeShaderVariants
    {
        private readonly string[][] variants;
        private readonly HashSet<string> represented;
        private NativeShaderVariants(string[][] variants)
        { this.variants = variants; represented = variants.SelectMany(v => v).ToHashSet(StringComparer.Ordinal); }

        internal static NativeShaderVariants Read(Shader shader, int maximum)
        {
            var method = typeof(ShaderUtil).GetMethod("GetShaderVariantEntriesFiltered",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null,
                new[] { typeof(Shader), typeof(int), typeof(string[]), typeof(ShaderVariantCollection),
                    typeof(int[]).MakeByRefType(), typeof(string[]).MakeByRefType(), typeof(string[]).MakeByRefType() }, null);
            if (method == null) throw new InvalidOperationException("native shader variant enumeration is unavailable");
            // A non-null, empty exclusion collection is required by the native
            // API. Query one extra entry so truncation can never be a proof.
            var exclusions = new ShaderVariantCollection();
            try
            {
                object[] arguments = { shader, checked(maximum + 1), Array.Empty<string>(), exclusions, null, null, null };
                method.Invoke(null, arguments);
                var types = arguments[4] as int[]; var entries = arguments[5] as string[];
                if (types == null || entries == null || types.Length != entries.Length || entries.Length == 0)
                    throw new InvalidOperationException("native shader variant enumeration is incomplete");
                if (entries.Length > maximum) throw new InvalidOperationException("native shader variant enumeration exceeded its bound");
                return new NativeShaderVariants(entries.Select(e => e.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToArray()).ToArray());
            }
            finally { UnityEngine.Object.DestroyImmediate(exclusions); }
        }

        internal string[][] Project(LocalKeyword[] stageKeywords, int maximum)
            => Project(variants, represented, stageKeywords.Select(k => k.name), maximum);

        internal static string[][] Project(IEnumerable<string[]> variants, ISet<string> represented,
            IEnumerable<string> stageKeywords, int maximum)
        {
            var scope = stageKeywords.ToHashSet(StringComparer.Ordinal);
            // Collection entries do not expose every engine-created keyword
            // (notably stereo). Unrepresented names remain independent choices.
            var missing = scope.Where(n => !represented.Contains(n)).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            var projected = new Dictionary<string, string[]>(StringComparer.Ordinal);
            foreach (var variant in variants)
            {
                var names = variant.Where(scope.Contains).OrderBy(n => n, StringComparer.Ordinal).ToArray();
                projected.TryAdd(string.Join(";", names), names);
            }
            foreach (string name in missing)
            {
                if (projected.Count > maximum / 2) throw new InvalidOperationException("native shader stage alternatives exceed the validation bound");
                foreach (var variant in projected.Values.ToArray())
                {
                    var names = variant.Append(name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
                    projected.Add(string.Join(";", names), names);
                }
            }
            if (projected.Count == 0 || projected.Count > maximum)
                throw new InvalidOperationException("native shader stage alternatives exceed the validation bound");
            // Union across all pass types is deliberately conservative. Native
            // pass types are not unique identifiers for repeated ShaderLab passes.
            return projected.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Value).ToArray();
        }
    }
}
