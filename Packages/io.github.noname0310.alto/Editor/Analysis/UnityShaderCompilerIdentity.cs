using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.Analysis
{
    internal static class UnityShaderCompilerIdentity
    {
        // The editor installation and loaded compiler binaries are immutable for
        // an editor domain. Companion DLLs can change independently of the compiler
        // executable; include absent optional backends as part of this identity.
        private static readonly Lazy<string> D3DValue = new(() =>
        {
            try
            {
                string directory = Path.Combine(EditorApplication.applicationContentsPath, "Tools");
                var parts = new List<string> { Application.unityVersion, typeof(ShaderUtil).Module.ModuleVersionId.ToString() };
                foreach (string file in new[] { "UnityShaderCompiler.exe", "D3DCompiler_47.dll", "dxcompiler.dll", "dxil.dll" })
                {
                    string path = Path.Combine(directory, file);
                    if (!File.Exists(path) && (file == "UnityShaderCompiler.exe" || file == "D3DCompiler_47.dll")) return null;
                    parts.Add(file); parts.Add(File.Exists(path) ? ShaderAnalysisCache.Digest(File.ReadAllBytes(path)) : "absent");
                }
                return string.Join("/", parts);
            }
            catch (Exception) { return null; }
        });

        internal static string D3D => D3DValue.Value;
    }
}
