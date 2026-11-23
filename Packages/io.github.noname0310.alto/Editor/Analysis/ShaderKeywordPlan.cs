using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace Noname.AvatarTools.Analysis
{
    // Enumerate declared alternatives after substituting proven material state.
    // Directive parsing uses the same tokenization as preprocessing. Conditional,
    // imported or overlapping declarations retain independent choices.
    internal sealed class ShaderKeywordPlan
    {
        private static readonly Regex Identifier = new(@"^[_A-Za-z][_A-Za-z0-9]*$", RegexOptions.CultureInvariant);
        private static readonly Regex VariantPragma = new(@"^(shader_feature|multi_compile)(?:_local)?(?:_(vertex|fragment|geometry|hull|domain|raytracing))?$", RegexOptions.CultureInvariant);

        internal string[][] Variants;
        internal string Issue;
        internal int KeywordCount, FixedCount;
        internal long CombinationCount;

        internal sealed class DeclarationSet
        {
            internal readonly HashSet<string> Names, Skipped;
            internal readonly List<string[]> Groups;
            internal DeclarationSet(string source, ShaderType stage, ShaderCompilerPlatform? backend, bool includesVerified)
            {
                Names = Declarations(source, stage, out var groups, out var skipped, backend, includesVerified);
                Groups = groups; Skipped = skipped;
            }
        }

        internal static string MaterialSignature(Material material) => material == null ? "" :
            string.Join(";", material.enabledKeywords.Select(k => k.name).OrderBy(n => n, StringComparer.Ordinal));

        internal static ShaderKeywordPlan Create(string source, ShaderType stage, LocalKeyword[] keywords,
            Material material, bool ownsMaterial, int maximumVariants, ShaderPreprocessorGraph dependencies = null,
            ShaderCompilerPlatform? backend = null, Func<string, bool> representativeChoice = null)
            => Create(new DeclarationSet(source, stage, backend, dependencies?.IncludesWithoutKeywordPragmas == true),
                keywords, material, ownsMaterial, maximumVariants, dependencies, representativeChoice);

        internal static ShaderKeywordPlan Create(DeclarationSet declarations, LocalKeyword[] keywords,
            Material material, bool ownsMaterial, int maximumVariants, ShaderPreprocessorGraph dependencies = null,
            Func<string, bool> representativeChoice = null)
        {
            var names = keywords.Select(k => k.name).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            var declared = declarations.Names;
            var fixedValues = new Dictionary<string, bool>(StringComparer.Ordinal);
            if (material != null && ownsMaterial)
                foreach (var keyword in keywords)
                {
                    // Engine-created keywords (stereo, instancing, lighting) are not
                    // artist-controlled merely because keywordSpace exposes them.
                    if (!declared.Contains(keyword.name) || keyword.isDynamic || keyword.isOverridable ||
                        keyword.type != ShaderKeywordType.Plugin) continue;
                    fixedValues[keyword.name] = material.IsKeywordEnabled(keyword);
                }
            if (dependencies != null)
                foreach (string name in names.Where(dependencies.CanIgnore))
                    if (!fixedValues.ContainsKey(name)) fixedValues[name] = false;
            if (representativeChoice != null)
                foreach (string name in names.Where(representativeChoice))
                    if (!fixedValues.ContainsKey(name)) fixedValues[name] = false;
            return Enumerate(names, declarations.Groups, fixedValues, declarations.Skipped, maximumVariants);
        }

        internal static HashSet<string> Declarations(string source, ShaderType stage,
            out List<string[]> groups, out HashSet<string> skipped, ShaderCompilerPlatform? backend = null,
            bool includesVerified = false)
        {
            var parsedGroups = new List<string[]>(); var parsedSkipped = new HashSet<string>(StringComparer.Ordinal);
            groups = parsedGroups; skipped = parsedSkipped;
            var declared = new HashSet<string>(StringComparer.Ordinal);
            var conditionalNames = new HashSet<string>(StringComparer.Ordinal);
            bool importedPragmas = false;
            void Visit(IEnumerable<PpInstruction> instructions, bool conditional)
            {
                foreach (var instruction in instructions)
                {
                    if (instruction.Directive == "conditional")
                    { foreach (var arm in instruction.Branches) Visit(arm.Body, true); continue; }
                    if (instruction.Directive == "include_with_pragmas" || instruction.Directive == "include" && !includesVerified)
                        importedPragmas = true;
                    if (instruction.Directive != "pragma") continue;
                    var words = instruction.Tokens.Select(t => t.Text).ToArray();
                    if (words.Length == 0) continue;
                    if (!conditional && words[0] == "skip_variants" && words.Skip(1).All(n => Identifier.IsMatch(n)))
                    { parsedSkipped.UnionWith(words.Skip(1)); continue; }
                    var pragma = VariantPragma.Match(words[0]);
                    string[] options;
                    if (!pragma.Success)
                    {
                        if (words[0] != "multi_compile_fog") continue;
                        options = new[] { "", "FOG_LINEAR", "FOG_EXP", "FOG_EXP2" };
                    }
                    else
                    {
                        if (pragma.Groups[2].Success && !StageApplies(pragma.Groups[2].Value, stage, backend)) continue;
                        options = words.Skip(1).ToArray();
                        if (options.Length == 0 || options.Any(n => !Identifier.IsMatch(n))) continue;
                        declared.UnionWith(options.Where(n => n.Any(c => c != '_')));
                    }
                    options = options.Select(n => n.All(c => c == '_') ? "" : n).Append("").Distinct().ToArray();
                    if (conditional) conditionalNames.UnionWith(options.Where(n => n.Length != 0));
                    else parsedGroups.Add(options);
                }
            }
            try { Visit(new PpDocument("<shader keyword declarations>", source ?? "").Body, false); }
            catch (PpException)
            {
                // Partial parsing is not a proof of exclusive choices or stripping.
                parsedGroups.Clear(); parsedSkipped.Clear(); declared.Clear(); return declared;
            }
            // A second declaration may enable a name together with another member
            // of an earlier set. Preserve that possibility even when the second
            // declaration is conditional or imported from an uninspected file.
            if (importedPragmas) { parsedGroups.Clear(); parsedSkipped.Clear(); }
            else parsedGroups.RemoveAll(g => g.Any(conditionalNames.Contains));
            return declared;
        }

        private static bool StageApplies(string declared, ShaderType stage, ShaderCompilerPlatform? backend)
        {
            // Unity ignores stage suffixes on these APIs. Ignoring declarations
            // from another stage would miss overlapping keyword sets and could
            // incorrectly infer mutual exclusion between names shared by them.
            if (backend == ShaderCompilerPlatform.Vulkan || backend == ShaderCompilerPlatform.GLES3x || backend == ShaderCompilerPlatform.OpenGLCore) return true;
            if (declared.Equals(stage.ToString(), StringComparison.OrdinalIgnoreCase)) return true;
            if (backend != ShaderCompilerPlatform.Metal) return false;
            return (stage == ShaderType.Vertex || stage == ShaderType.Hull || stage == ShaderType.Domain) &&
                (declared == "vertex" || declared == "hull" || declared == "domain");
        }

        internal static ShaderKeywordPlan Enumerate(string[] names, IEnumerable<string[]> declarations,
            IReadOnlyDictionary<string, bool> fixedValues, ISet<string> skipped, int maximumVariants)
        {
            var plan = new ShaderKeywordPlan { KeywordCount = names.Length };
            var universe = names.ToHashSet(StringComparer.Ordinal);
            var known = fixedValues.Where(p => universe.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
            foreach (string name in skipped.Where(universe.Contains))
            {
                if (known.TryGetValue(name, out bool enabled) && enabled)
                { plan.Issue = "material enables a stripped shader keyword"; return plan; }
                known[name] = false;
            }
            plan.FixedCount = known.Count;
            var groups = declarations.Select(g => g.Select(n => universe.Contains(n) ? n : "").Distinct().OrderBy(n => n, StringComparer.Ordinal).ToArray())
                .Where(g => g.Any(n => n.Length != 0)).ToArray();
            var occurrences = groups.SelectMany(g => g.Where(n => n.Length != 0)).GroupBy(n => n).ToDictionary(g => g.Key, g => g.Count());
            var dimensions = new List<string[][]>();
            var covered = new HashSet<string>(StringComparer.Ordinal);
            foreach (var group in groups)
            {
                // Reusing a name across multiple declarations can combine features;
                // do not infer mutual exclusion for any overlapping group.
                if (group.Any(n => n.Length != 0 && occurrences[n] != 1)) continue;
                var options = group.Where(option => group.Where(n => n.Length != 0)
                    .All(n => !known.TryGetValue(n, out bool enabled) || enabled == (n == option)))
                    .Select(n => n.Length == 0 ? Array.Empty<string>() : new[] { n }).ToArray();
                if (options.Length == 0) { plan.Issue = "inconsistent material keyword alternatives"; return plan; }
                dimensions.Add(options); covered.UnionWith(group.Where(n => n.Length != 0));
            }
            foreach (string name in names.Where(n => !covered.Contains(n)))
                dimensions.Add(known.TryGetValue(name, out bool enabled)
                    ? new[] { enabled ? new[] { name } : Array.Empty<string>() }
                    : new[] { Array.Empty<string>(), new[] { name } });
            long combinations = 1;
            foreach (var dimension in dimensions)
            {
                combinations = Math.Min((long)maximumVariants + 1, combinations * dimension.Length);
                if (combinations > maximumVariants) break;
            }
            plan.CombinationCount = combinations;
            if (combinations > maximumVariants)
            { plan.Issue = "shader keyword combinations exceed the bounded analysis limit (" + plan.KeywordCount +
                " keywords, " + plan.FixedCount + " fixed, more than " + maximumVariants + " reachable combinations)"; return plan; }
            var variants = new List<string[]> { Array.Empty<string>() };
            foreach (var dimension in dimensions)
                variants = variants.SelectMany(prefix => dimension.Select(suffix => prefix.Concat(suffix).ToArray())).ToList();
            plan.Variants = variants.Select(v => v.OrderBy(n => n, StringComparer.Ordinal).ToArray()).ToArray();
            return plan;
        }
    }
}
