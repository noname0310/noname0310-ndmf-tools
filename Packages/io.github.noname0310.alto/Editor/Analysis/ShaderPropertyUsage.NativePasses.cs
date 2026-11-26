using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.Analysis
{
    internal sealed partial class ShaderPropertyUsage
    {
        private readonly Dictionary<ShaderData.Pass, ShaderData.Pass> importedNativePasses = new();
        private readonly HashSet<Shader> importedNativeOwners = new();

        // UsePass inserts an existing program. Keep both source/dependency
        // inventories and both variant plans, but allow their native requests to
        // share after checking the importing shader's effective keyword scope.
        // Per-variant preprocessing equality is checked by the native consumer.
        internal ShaderData.Pass ImportedNativePass(ShaderData.Pass pass)
            => importedNativePasses.TryGetValue(pass, out var owner) ? owner : pass;

        private void RegisterImportedNativePasses(Programs inventory)
        {
            using var cost = AnalysisCost.Measure("Shader discovery/imported pass ownership");
            foreach (var shader in inventory.NativeShaderDependencies)
            {
                if (!importedNativeOwners.Add(shader)) continue;
                try
                {
                    // Use the exact source already inspected for this shader,
                    // including generated sources without a physical text asset.
                    List<List<string>> slots = null;
                    if (shaderSources.TryGetValue(shader, out string source))
                    {
                        // The parser only recognizes a literal UsePass token. A
                        // source without that spelling cannot establish an alias.
                        if (source.IndexOf("UsePass", StringComparison.Ordinal) < 0) continue;
                        slots = Artifacts.NativeImports(source);
                    }
                    else if (importedMetadata.TryGetValue(shader, out var metadata) && metadata.DependencyIssue == null)
                    {
                        slots = new List<List<string>>(metadata.Subshaders.Count);
                        foreach (var subshader in metadata.Subshaders)
                        {
                            var imports = new List<string>(subshader.Count);
                            foreach (var pass in subshader) imports.Add(pass.Import);
                            slots.Add(imports);
                        }
                    }
                    var data = ShaderUtil.GetShaderData(shader);
                    if (slots == null || slots.Count != data.SerializedSubshaderCount || slots.Count > data.SubshaderCount) continue;
                    for (int sub = 0; sub < slots.Count; sub++)
                    {
                        if (slots[sub].Count != data.GetSerializedSubshader(sub).PassCount) continue;
                        for (int index = 0; index < slots[sub].Count; index++)
                        {
                            string reference = slots[sub][index];
                            if (reference == null || !passSnapshots.TryGetValue((shader, sub, index, false), out var imported)) continue;
                            int split = reference.LastIndexOf('/');
                            if (split <= 0 || split == reference.Length - 1) continue;
                            string name = reference.Substring(split + 1);
                            if (!string.Equals(imported.Name, name, StringComparison.Ordinal)) continue;
                            string ownerName = reference.Substring(0, split);
                            Shader owner = null; bool ambiguous = false;
                            foreach (var dependency in inventory.NativeShaderDependencies)
                            {
                                if (dependency.name != ownerName) continue;
                                if (owner != null && owner != dependency) { ambiguous = true; break; }
                                owner = dependency;
                            }
                            if (ambiguous || owner == null || owner == shader ||
                                !NativeKeywordScope(shader).SequenceEqual(NativeKeywordScope(owner), StringComparer.Ordinal)) continue;
                            // Do not choose a supported subshader or a duplicate
                            // named pass ourselves. Such imports remain distinct.
                            var candidates = passSnapshots.Where(p => p.Key.Item1 == owner && !p.Key.Item4 &&
                                string.Equals(p.Value.Name, name, StringComparison.Ordinal)).Select(p => p.Value).ToArray();
                            if (candidates.Length != 1) continue;
                            var candidate = candidates[0];
                            if (PassSource(imported) != PassSource(candidate) || imported.IsGrabPass != candidate.IsGrabPass ||
                                ShaderMeshInputs.Stages.Any(s => imported.HasShaderStage(s) != candidate.HasShaderStage(s))) continue;
                            importedNativePasses[imported] = candidate;
                        }
                    }
                }
                catch (Exception) { /* Unsupported ShaderLab forms keep separate native requests. */ }
            }
        }

        private static IEnumerable<string> NativeKeywordScope(Shader shader) => shader.keywordSpace.keywords
            .Select(k => k.name + ":" + k.isOverridable + ":" + k.isDynamic + ":" + k.type)
            .OrderBy(k => k, StringComparer.Ordinal);

        // Preserve native pass order. A generated surface program, shared code,
        // conditional ShaderLab or an unexpected block cannot establish an alias.
        internal static List<List<string>> NativeImportSlots(string source)
        {
            var tokens = PpLexer.Lex(source).Where(t => t.Kind != PpTokenKind.Newline).ToArray();
            var partner = new Dictionary<int, int>(); var stack = new Stack<int>();
            for (int i = 0; i < tokens.Length; i++)
            {
                string word = tokens[i].Text;
                if (tokens[i].Kind == PpTokenKind.Identifier && (word == "CGPROGRAM" || word == "HLSLPROGRAM"))
                {
                    string end = word == "CGPROGRAM" ? "ENDCG" : "ENDHLSL";
                    while (++i < tokens.Length && tokens[i].Text != end) { }
                    if (i == tokens.Length) return null;
                    continue;
                }
                if (word == "#" || word == "CGINCLUDE" || word == "HLSLINCLUDE") return null;
                if (word == "{") stack.Push(i);
                else if (word == "}")
                {
                    if (stack.Count == 0) return null;
                    int begin = stack.Pop(); partner[begin] = i;
                }
            }
            if (stack.Count != 0 || tokens.Length < 4 || tokens[0].Text != "Shader" || tokens[2].Text != "{") return null;
            var result = new List<List<string>>();
            for (int i = 3; i < tokens.Length - 1; i++)
            {
                if (tokens[i].Text != "SubShader")
                { if (tokens[i].Text == "{") i = partner[i]; continue; }
                if (++i >= tokens.Length || tokens[i].Text != "{") return null;
                int end = partner[i]; var passes = new List<string>(); result.Add(passes);
                for (i++; i < end; i++)
                {
                    string word = tokens[i].Text;
                    if (word == "Pass" || word == "GrabPass")
                    {
                        if (++i >= end || tokens[i].Text != "{") return null;
                        passes.Add(null); i = partner[i];
                    }
                    else if (word == "UsePass")
                    {
                        if (++i >= end || tokens[i].Kind != PpTokenKind.String || tokens[i].Text.Contains("\\")) return null;
                        passes.Add(tokens[i].Text.Substring(1, tokens[i].Text.Length - 2));
                    }
                    else if (word == "CGPROGRAM" || word == "HLSLPROGRAM") return null;
                    else if (word == "{") i = partner[i];
                }
            }
            return result;
        }
    }
}
