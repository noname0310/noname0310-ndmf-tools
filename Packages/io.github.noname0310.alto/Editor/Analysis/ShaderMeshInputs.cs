using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace Noname.AvatarTools.Analysis
{
    // A bounded absence proof. Unity owns HLSL preprocessing. A native compiler
    // can refine declarations into actual reads, using proven constant scalar
    // material inputs. No current-keyword sample grants merge permission.
    // Results belong to the same build/source snapshot as ShaderPropertyUsage.
    internal sealed class ShaderMeshInputs
    {
        internal const int MaximumKeywords = 6;
        internal const int MaximumCallsPerShader = 1024;
        internal const int MaximumCallsPerSnapshot = 2048;
        internal const int MaximumCompilesPerSnapshot = 128;
        private const string IndexIssue = "shader may depend on vertex, primitive or skinning indices/weights";
        private static readonly ShaderType[] Stages = { ShaderType.Vertex, ShaderType.Fragment,
            ShaderType.Geometry, ShaderType.Hull, ShaderType.Domain };
        private static readonly GraphicsTier[] Tiers = { GraphicsTier.Tier1, GraphicsTier.Tier2, GraphicsTier.Tier3 };
        private static readonly Regex Identifiers = new(@"\b[_A-Za-z][_A-Za-z0-9]*\b", RegexOptions.CultureInvariant);
        private static readonly string[] OriginalInputs = { "SV_VertexID", "SV_PrimitiveID", "BLENDINDICES" };
        // Both absence proofs retain indexed semantics. This can count
        // stereo stage outputs as inputs, but never assumes an unparsed interface
        // is safe. The existing source-wide proof remains a separate fast path.
        private static readonly Regex IndexSemantic = new(@"^(SV_VertexID|SV_PrimitiveID|BLENDINDICES)[0-9]*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex WeightSemantic = new(@"^BLENDWEIGHT[0-9]*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private readonly ShaderPropertyUsage sources;
        private readonly Dictionary<(Shader, BuildTarget, bool), string> cache = new();
        private readonly Dictionary<(Shader, BuildTarget, bool, string), string> materialCache = new();
        private readonly Dictionary<string, (D3DShaderInputs.Input[] Inputs, string Error)> compiled = new(StringComparer.Ordinal);
        internal int PreprocessCalls { get; private set; }
        internal int CompileCalls { get; private set; }

        internal ShaderMeshInputs(ShaderPropertyUsage sources) { this.sources = sources; }

        internal string Issue(Shader shader, bool addSkinning, BuildTarget? target)
        {
            var source = sources.Get(shader);
            string original = SourceIssue(source, addSkinning);
            // Keep the cheap, platform-independent proof whenever it already suffices.
            // A caller without an explicit build target cannot narrow that proof.
            if (original == null || !target.HasValue || shader == null) return original;
            var key = (shader, target.Value, addSkinning);
            if (cache.TryGetValue(key, out string cached)) return cached;
            string result;
            try { result = Inspect(shader, target.Value, addSkinning, null); }
            catch (Exception exception) { result = Unknown("variant inspection failed: " + exception.Message); }
            cache.Add(key, result);
            return result;
        }

        internal string Issue(Material material, Renderer renderer, AvatarAnimationIndex animations, bool addSkinning, BuildTarget? target)
        {
            if (material == null || renderer == null || animations == null || !target.HasValue)
                return Issue(material != null ? material.shader : null, addSkinning, target);
            var shader = material.shader;
            string original = SourceIssue(sources.Get(shader), addSkinning);
            if (original == null || shader == null) return original;
            var constants = animations.MaterialInputs.ConstantUniforms(renderer, material);
            if (constants.Count == 0) return Issue(shader, addSkinning, target);
            string signature = string.Join(";", constants.OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => p.Key + ":" + BitConverter.ToInt32(BitConverter.GetBytes(p.Value), 0)));
            var key = (shader, target.Value, addSkinning, signature);
            if (materialCache.TryGetValue(key, out string cached)) return cached;
            string result;
            try { result = Inspect(shader, target.Value, addSkinning, constants); }
            catch (Exception exception) { result = Unknown("material input inspection failed: " + exception.Message); }
            materialCache.Add(key, result); return result;
        }

        internal static string SourceIssue(ShaderPropertyUsage.Proof proof, bool addSkinning)
        {
            if (proof.Uncertain != null) return Unknown(proof.Uncertain);
            if (proof.Tokens.Any(t => IsInput(t, addSkinning)) ||
                OriginalInputs.Concat(addSkinning ? new[] { "BLENDWEIGHT" } : Array.Empty<string>())
                    .Any(name => MayPasteInput(proof, name))) return IndexIssue;
            return null;
        }

        private static bool MayPasteInput(ShaderPropertyUsage.Proof proof, string name)
        {
            if (!proof.TokenPasting) return false;
            // Over-approximate nested macro expansion by concatenating source
            // tokens. Recognize the entire semantic family, including arbitrary
            // numeric suffixes and case variants, rather than sampling indices.
            var possible = new bool[name.Length + 1]; possible[0] = true;
            for (int start = 0; start < name.Length; start++)
            {
                if (!possible[start]) continue;
                foreach (string token in proof.Tokens)
                {
                    int length = Math.Min(token.Length, name.Length - start);
                    if (length == 0 || string.Compare(name, start, token, 0, length, StringComparison.OrdinalIgnoreCase) != 0) continue;
                    if (start + length == name.Length && token.Skip(length).All(c => c >= '0' && c <= '9')) return true;
                    if (length == token.Length) possible[start + length] = true;
                }
            }
            return false;
        }

        internal static bool IsInput(string identifier, bool addSkinning) =>
            IndexSemantic.IsMatch(identifier) || addSkinning && WeightSemantic.IsMatch(identifier);

        internal static string InspectPreprocessed(bool success, string code, bool addSkinning)
        {
            // Unity can report success with empty code for an unavailable program.
            // This is not evidence that a runtime input is unused.
            if (!success || string.IsNullOrWhiteSpace(code)) return Unknown("a shader variant has no verified preprocessed program");
            if (code.Length > 4 * 1024 * 1024) return Unknown("preprocessed shader size limit reached");
            code = ShaderPropertyUsage.StripComments(code);
            // Line/pragma directives are metadata, not identifiers used by HLSL.
            // Any other remaining directive means preprocessing is incomplete.
            foreach (string line in code.Split('\n'))
            {
                string text = line.TrimStart();
                if (text.StartsWith("#", StringComparison.Ordinal))
                {
                    if (Regex.IsMatch(text, @"^#\s*(line\b|pragma\b|[0-9])", RegexOptions.CultureInvariant)) continue;
                    return Unknown("an unsupported directive remains after shader preprocessing");
                }
                foreach (Match token in Identifiers.Matches(text))
                    if (IsInput(token.Value, addSkinning)) return IndexIssue;
            }
            return null;
        }

        private string Inspect(Shader shader, BuildTarget target, bool addSkinning, IReadOnlyDictionary<string, float> constants)
        {
            var programs = sources.GetPrograms(shader);
            if (programs.Uncertain != null) return Unknown(programs.Uncertain);
            if (programs.Passes.Count == 0) return Unknown("no inspectable shader programs");
            var backends = Backends(PlayerSettings.GetGraphicsAPIs(target));
            if (backends == null || backends.Length == 0) return Unknown("unverified graphics API coverage");
            var units = programs.Passes.SelectMany(p => Stages.Where(p.Pass.HasShaderStage)
                .SelectMany(stage => backends.Select(backend => {
                    var id = p.Identifier;
                    // Unity 2022's pass-keyword API cannot address imported
                    // UsePass entries or built-in fallback pass indices reliably.
                    // Use their complete shader-wide universe without issuing
                    // invalid native queries that log Editor errors.
                    var names = (programs.ShaderWideKeywordOwners.Contains(p.Shader) ? p.Shader.keywordSpace.keywords :
                        ShaderUtil.GetPassKeywords(p.Shader, id, stage, backend)).Select(k => k.name)
                        .Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToArray();
                    return (p.Shader, p.Pass, Stage: stage, Backend: backend, Keywords: names);
                }))).ToArray();
            if (units.Length == 0) return Unknown("no inspectable shader stages");

            // Stage/API keyword metadata reduces irrelevant combinations without
            // assuming that lighting, stereo or the material's current keywords
            // cannot change. Large programs remain explicitly unverified.
            var excessive = units.FirstOrDefault(u => u.Keywords.Length > MaximumKeywords);
            if (excessive.Keywords != null)
                return Unknown("shader keyword combinations exceed the bounded analysis limit (" + excessive.Keywords.Length +
                    " keywords in " + excessive.Pass.Name + ", " + excessive.Stage + ", " + excessive.Backend + ")");
            long calls = units.Sum(u => (long)(1 << u.Keywords.Length)) * Tiers.Length;
            if (calls > MaximumCallsPerShader || calls > MaximumCallsPerSnapshot - PreprocessCalls)
                return Unknown("shader variant coverage exceeds the bounded analysis limit");
            foreach (var unit in units)
                    foreach (var tier in Tiers)
                        foreach (var enabled in KeywordCombinations(unit.Keywords))
                        {
                            string issue = Preprocess(unit.Pass, unit.Stage, enabled, unit.Backend, target, tier, addSkinning, constants);
                            if (issue != null) return issue;
                        }
            return null;
        }

        private string Preprocess(ShaderData.Pass pass, ShaderType stage, string[] keywords,
            ShaderCompilerPlatform backend, BuildTarget target, GraphicsTier tier, bool addSkinning, IReadOnlyDictionary<string, float> constants)
        {
            if (PreprocessCalls >= MaximumCallsPerSnapshot) return Unknown("shader preprocessing budget exhausted");
            PreprocessCalls++;
            var variant = pass.PreprocessVariant(stage, keywords, backend, target, tier, false);
            string issue = InspectPreprocessed(variant.Success, variant.PreprocessedCode, addSkinning);
            if (issue != IndexIssue) return issue;
            if (backend != ShaderCompilerPlatform.D3D || Application.platform != RuntimePlatform.WindowsEditor) return issue;
            string program = ShaderConstantInputs.Specialize(variant.PreprocessedCode, constants, out int count);
            string stageName = stage switch { ShaderType.Vertex => "vertex", ShaderType.Fragment => "fragment", ShaderType.Geometry => "geometry",
                ShaderType.Hull => "hull", ShaderType.Domain => "domain", _ => null };
            var entries = Regex.Matches(ShaderPropertyUsage.StripComments(pass.SourceCode),
                @"(?m)^\s*#\s*pragma\s+" + stageName + @"\s+([_A-Za-z][_A-Za-z0-9]*)\s*(?://.*)?$")
                .Cast<Match>().Select(m => m.Groups[1].Value).Distinct().ToArray();
            if (entries.Length != 1) return Unknown("shader entry point is not uniquely inspectable");
            string key;
            using (var hash = SHA256.Create()) key = Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(
                stage + ":" + entries[0] + ":" + (count != 0) + ":" + program)));
            if (!compiled.TryGetValue(key, out var proof))
            {
                if (CompileCalls >= MaximumCompilesPerSnapshot) return Unknown("shader compiler analysis budget exhausted");
                CompileCalls++;
                if (count != 0)
                {
                    D3DShaderInputs.Compile(program, entries[0], stage, out var inputs, out string error);
                    proof = (inputs, error);
                }
                else
                {
                    var result = pass.CompileVariant(stage, keywords, backend, target, tier, true);
                    if (!result.Success) proof = (null, "shader variant compilation failed");
                    else { D3DShaderInputs.Reflect(result.ShaderData, stage, out var inputs, out string error); proof = (inputs, error); }
                }
                compiled.Add(key, proof);
            }
            if (proof.Inputs == null) return Unknown(proof.Error ?? "shader input reflection was incomplete");
            return proof.Inputs.Any(i => i.ReadMask != 0 && IsInput(i.Semantic, addSkinning)) ? IndexIssue : null;
        }

        // Enumerating every subset also includes impossible keyword combinations.
        // Extra cases may retain a mesh, but cannot prove absence from a partial sample.
        internal static IEnumerable<string[]> KeywordCombinations(string[] names)
        {
            if (names.Length > MaximumKeywords) throw new ArgumentOutOfRangeException(nameof(names));
            for (int mask = 0; mask < 1 << names.Length; mask++)
                yield return names.Where((_, i) => (mask & (1 << i)) != 0).ToArray();
        }

        internal static ShaderCompilerPlatform[] Backends(IEnumerable<GraphicsDeviceType> apis)
        {
            var result = new HashSet<ShaderCompilerPlatform>();
            foreach (var api in apis)
            {
                switch (api)
                {
                    case GraphicsDeviceType.Direct3D11:
                    case GraphicsDeviceType.Direct3D12: result.Add(ShaderCompilerPlatform.D3D); break;
                    case GraphicsDeviceType.OpenGLCore: result.Add(ShaderCompilerPlatform.OpenGLCore); break;
                    case GraphicsDeviceType.OpenGLES3: result.Add(ShaderCompilerPlatform.GLES3x); break;
                    case GraphicsDeviceType.Vulkan: result.Add(ShaderCompilerPlatform.Vulkan); break;
                    case GraphicsDeviceType.Metal: result.Add(ShaderCompilerPlatform.Metal); break;
                    default: return null;
                }
            }
            return result.OrderBy(p => p).ToArray();
        }

        private static string Unknown(string reason) => "unverified shader inputs: " + reason;
    }
}
