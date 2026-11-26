using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Noname.AvatarTools.Analysis
{
    internal sealed partial class ShaderPropertyUsage
    {
        // Native handles and path existence are valid only within this source
        // epoch. External build callbacks/imports still discard the entire epoch.
        // Text-only artifacts can survive it under their exact content identity.
        private sealed class NativeDiscovery
        {
            internal string[] Properties;
            internal string AssetPath, PhysicalPath, GeneratedText;
            internal bool Generated, Builtin, Editable, HasError;
            internal ShaderData Data;
            internal string PassIssue;
            internal readonly List<(ShaderData.Pass Pass, PassIdentifier Identifier, string Source)> Passes = new();
        }
        private readonly Dictionary<Shader, NativeDiscovery> discoveries = new();
        private readonly Dictionary<string, bool> fileExistence = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> physicalIncludes = new(StringComparer.Ordinal);

        private bool SourceExists(string path)
        {
            if (path == null) return false;
            if (fileExistence.TryGetValue(path, out bool exists)) return exists;
            return fileExistence[path] = File.Exists(path);
        }
        private string IncludePhysicalPath(string path)
        {
            if (physicalIncludes.TryGetValue(path, out string physical)) return physical;
            return physicalIncludes[path] = PhysicalPath(path);
        }
        private NativeDiscovery Discover(Shader shader)
        {
            if (discoveries.TryGetValue(shader, out var previous))
            { AnalysisCost.Count("Shader discovery/native inventory reuse"); return previous; }
            using var cost = AnalysisCost.Measure("Shader discovery/native inventory");
            var result = new NativeDiscovery();
            result.HasError = ShaderUtil.ShaderHasError(shader);
            if (result.HasError) { discoveries.Add(shader, result); return result; }
            int properties = shader.GetPropertyCount();
            result.Properties = new string[properties];
            for (int i = 0; i < properties; i++) result.Properties[i] = shader.GetPropertyName(i);
            result.AssetPath = AssetDatabase.GetAssetPath(shader);
            result.PhysicalPath = ShaderBuildSource.PhysicalPath(shader);
            result.Generated = ShaderBuildSource.TryGenerated(shader, out result.GeneratedText, out _);
            result.Builtin = result.AssetPath == "Resources/unity_builtin_extra" || result.AssetPath == "Library/unity default resources";
            result.Editable = result.Generated || !result.Builtin && result.PhysicalPath != null && SourceExists(result.PhysicalPath) &&
                Path.GetExtension(result.PhysicalPath).Equals(".shader", StringComparison.OrdinalIgnoreCase);
            result.Data = ShaderUtil.GetShaderData(shader);
            int count = result.Builtin ? result.Data.SubshaderCount : result.Data.SerializedSubshaderCount;
            if (!result.Builtin && count > result.Data.SubshaderCount)
                result.PassIssue = "not all serialized subshaders are inspectable";
            else
            {
                if (count == 0) result.PassIssue = "shader contains no inspectable subshaders";
                for (int i = 0; i < count; i++)
                {
                    var sub = result.Builtin ? result.Data.GetSubshader(i) : result.Data.GetSerializedSubshader(i);
                    int passes = sub.PassCount;
                    for (int j = 0; j < passes; j++)
                    {
                        var key = (shader, i, j, result.Builtin);
                        if (!passSnapshots.TryGetValue(key, out var pass)) passSnapshots.Add(key, pass = sub.GetPass(j));
                        string source = PassSource(pass);
                        if (string.IsNullOrWhiteSpace(source))
                        { if (!pass.IsGrabPass) result.PassIssue ??= "a shader pass has no inspectable source"; continue; }
                        result.Passes.Add((pass, new PassIdentifier((uint)i, (uint)j), Artifacts.WithoutComments(source)));
                    }
                }
            }
            discoveries.Add(shader, result);
            return result;
        }
    }
}
