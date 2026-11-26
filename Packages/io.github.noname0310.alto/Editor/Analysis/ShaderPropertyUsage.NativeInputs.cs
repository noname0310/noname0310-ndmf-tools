using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Noname.AvatarTools.Analysis
{
    internal sealed partial class ShaderPropertyUsage
    {
        private readonly Dictionary<Shader, string> nativeDependencyIdentities = new();
        private readonly Dictionary<(ShaderData.Pass, string), string> nativeArtifactIdentities = new();
        private readonly Dictionary<string, string> nativeFileDigests = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string[]> nativeImportedDependencies = new(StringComparer.Ordinal);
        private bool nativeEnvironmentRead;
        private string nativeEnvironment;

        internal string NativeArtifactIdentity(ShaderData.Pass pass, string compiler)
        {
            var key = (pass, compiler);
            if (nativeArtifactIdentities.TryGetValue(key, out string previous)) return previous;
            Shader owner = null; PassIdentifier identifier = default;
            foreach (var pair in passSnapshots)
                if (pair.Value == pass)
                {
                    owner = pair.Key.Item1;
                    identifier = new PassIdentifier((uint)pair.Key.Item2, (uint)pair.Key.Item3);
                    break;
                }
            // CompileVariant is invoked on this pass, after any UsePass alias has
            // been proved. Its importing consumer is not a compiler input. Keep
            // consumer coverage proofs separate, with their full dependencies.
            string identity = owner == null ? null : NativePreprocessIdentity(owner, owner, pass, identifier, compiler);
            nativeArtifactIdentities.Add(key, identity); return identity;
        }

        internal string NativePreprocessIdentity(Shader root, Shader owner, ShaderData.Pass pass, PassIdentifier identifier, string compiler)
        {
            if (!ContentFactCache.Enabled || compiler == null) return null;
            try
            {
                string dependencies = NativeDependencyIdentity(root);
                if (dependencies == null) return null;
                return ContentFactCache.Shared.Key("native shader preprocessing", compiler, dependencies,
                    ProgramSourceId(owner), identifier.SubshaderIndex.ToString(), identifier.PassIndex.ToString(), pass.Name,
                    ContentFactCache.TextDigest(PassSource(pass)));
            }
            catch (Exception) { return null; }
        }

        private string NativeDependencyIdentity(Shader shader)
        {
            if (nativeDependencyIdentities.TryGetValue(shader, out string previous)) return previous;
            using var cost = AnalysisCost.Measure("Native shader dependency validation");
            var programs = GetPrograms(shader);
            string identity = null;
            var proof = Get(shader);
            if (programs.Uncertain == null && programs.NativeDependenciesComplete &&
                !proof.MayExpandName("__TIME__") && !proof.MayExpandName("__DATE__") && !proof.MayExpandName("__TIMESTAMP__") &&
                NativeEnvironment() is string environment)
            {
                // Include all candidate paths, even absent and inactive includes.
                // A newly installed optional package must invalidate the result.
                // Also include Unity's imported dependency graph: generated
                // passes can resolve includes relative to #line origins that
                // differ from our conservative source-discovery directories.
                var paths = new HashSet<string>(programs.NativeDependencyPaths, StringComparer.Ordinal);
                var dependencies = programs.NativeShaderDependencies.OrderBy(s => ProgramSourceId(s), StringComparer.Ordinal).ToArray();
                foreach (var dependency in dependencies)
                {
                    if (ShaderBuildSource.TryGenerated(dependency, out _, out _)) continue;
                    string asset = AssetDatabase.GetAssetPath(dependency);
                    if (string.IsNullOrEmpty(asset)) { nativeDependencyIdentities.Add(shader, null); return null; }
                    if (!nativeImportedDependencies.TryGetValue(asset, out var imported))
                        nativeImportedDependencies.Add(asset, imported = AssetDatabase.GetDependencies(asset, true));
                    foreach (string item in imported)
                    {
                        string path = PhysicalPath(item);
                        if (path != null) paths.Add(path);
                    }
                }
                var parts = new List<string> { environment, paths.Count.ToString() };
                foreach (string path in paths.OrderBy(p => p, StringComparer.Ordinal))
                {
                    parts.Add(path);
                    // Hash exact bytes, including encoding. Invalid text bytes
                    // must not alias through a lossy managed decoder.
                    if (!nativeFileDigests.TryGetValue(path, out string digest))
                    {
                        if (File.Exists(path))
                        {
                            if (nativeFileDigests.Count >= 4096 || new FileInfo(path).Length > 4 * 1024 * 1024)
                            { nativeDependencyIdentities.Add(shader, null); return null; }
                            digest = ShaderAnalysisCache.Digest(File.ReadAllBytes(path));
                        }
                        nativeFileDigests.Add(path, digest);
                    }
                    parts.Add(digest);
                }
                parts.Add(dependencies.Length.ToString());
                var records = new List<(string Origin, List<string> Values)>();
                foreach (var dependency in dependencies)
                {
                    var record = new List<string>();
                    string asset = AssetDatabase.GetAssetPath(dependency);
                    if (ShaderBuildSource.TryGenerated(dependency, out string source, out string sourcePath))
                    { record.Add(sourcePath); record.Add(ContentFactCache.TextDigest(source)); }
                    else
                    {
                        if (string.IsNullOrEmpty(asset)) { nativeDependencyIdentities.Add(shader, null); return null; }
                        record.Add(asset); record.Add(AssetDatabase.GetAssetDependencyHash(asset).ToString());
                    }
                    record.Add(dependency.name);
                    record.Add(dependency.keywordSpace.keywords.Length.ToString());
                    foreach (var keyword in dependency.keywordSpace.keywords.OrderBy(k => k.name, StringComparer.Ordinal))
                    {
                        record.Add(keyword.name); record.Add(keyword.isOverridable ? "global" : "local");
                        record.Add(keyword.type.ToString()); record.Add(keyword.isDynamic ? "dynamic" : "variant");
                        // Global keywords only override matching, overridable
                        // entries in this shader's imported keyword space. An
                        // unrelated shader import must not invalidate every pass.
                        if (keyword.isOverridable) record.Add(Shader.IsKeywordEnabled(keyword.name) ? "enabled" : "disabled");
                    }
                    records.Add((ProgramSourceId(dependency), record));
                }
                // ProgramSourceId identifies an include directory, not a shader.
                // Distinct files in the same directory need a total ordering;
                // HashSet/Unity-object enumeration order is not persistent input.
                records.Sort((a, b) =>
                {
                    int order = string.CompareOrdinal(a.Origin, b.Origin);
                    for (int i = 0; order == 0 && i < Math.Min(a.Values.Count, b.Values.Count); i++)
                        order = string.CompareOrdinal(a.Values[i], b.Values[i]);
                    return order != 0 ? order : a.Values.Count.CompareTo(b.Values.Count);
                });
                foreach (var record in records) parts.AddRange(record.Values);
                identity = ContentFactCache.Shared.Key("native shader dependencies", parts.ToArray());
            }
            nativeDependencyIdentities.Add(shader, identity); return identity;
        }

        private string NativeEnvironment()
        {
            if (nativeEnvironmentRead) return nativeEnvironment;
            nativeEnvironmentRead = true;
            // These are live serialized settings: reading ProjectSettings files
            // alone would miss edits made through editor APIs without a save.
            var parts = new List<string> { EditorUserBuildSettings.activeBuildTarget.ToString(),
                EditorUserBuildSettings.selectedBuildTargetGroup.ToString(), QualitySettings.activeColorSpace.ToString(),
                QualitySettings.GetQualityLevel().ToString(), Application.unityVersion };
            foreach (string name in new[] { "GraphicsSettings", "QualitySettings", "PlayerSettings" })
            {
                var settings = Unsupported.GetSerializedAssetInterfaceSingleton(name);
                if (settings == null) return null;
                parts.Add(name); parts.Add(EditorJsonUtility.ToJson(settings));
            }
            return nativeEnvironment = ContentFactCache.Shared.Key("native shader environment", parts.ToArray());
        }
    }
}
