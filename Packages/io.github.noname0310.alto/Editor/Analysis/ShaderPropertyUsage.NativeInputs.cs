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
        private readonly Dictionary<string, string> nativeFileDigests = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string[]> nativeImportedDependencies = new(StringComparer.Ordinal);
        private bool nativeEnvironmentRead;
        private string nativeEnvironment;

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
                foreach (var dependency in dependencies)
                {
                    string asset = AssetDatabase.GetAssetPath(dependency);
                    if (string.IsNullOrEmpty(asset)) { nativeDependencyIdentities.Add(shader, null); return null; }
                    parts.Add(asset); parts.Add(AssetDatabase.GetAssetDependencyHash(asset).ToString());
                    parts.Add(dependency.name);
                    parts.Add(dependency.keywordSpace.keywords.Length.ToString());
                    foreach (var keyword in dependency.keywordSpace.keywords)
                    { parts.Add(keyword.name); parts.Add(keyword.isOverridable ? "global" : "local"); }
                }
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
            foreach (var keyword in Shader.globalKeywords)
            { parts.Add(keyword.name); parts.Add(Shader.IsKeywordEnabled(keyword) ? "enabled" : "disabled"); }
            return nativeEnvironment = ContentFactCache.Shared.Key("native shader environment", parts.ToArray());
        }
    }
}
