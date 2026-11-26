using System;
using System.Collections.Generic;
using System.IO;
using Noname.AvatarTools.Analysis;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        [TestCase("", "", true)]
        [TestCase("Conservative True", "", false)]
        [TestCase("Conservative True", "Conservative False", true)]
        [TestCase("Conservative False", "Conservative True", false)]
        [TestCase("Conservative [_State]", "", false)]
        [TestCase("Conservative [_State]", "Conservative False", true)]
        [TestCase("", "Conservative [_State]", false)]
        [TestCase("", "// Conservative True\n", true)]
        [TestCase("", "Tags{\"Conservative\"=\"True\"}", true)]
        [TestCase("", "HLSLPROGRAM\nfloat Conservative; // True\nENDHLSL\n", true)]
        public void ShaderRasterizationState_UsesInheritedRasterCommands(string inherited, string local, bool safe)
        {
            string source = "Shader \"Probe\" { Properties{ Conservative(\"Conservative True\",Float)=1 } SubShader { " +
                inherited + " Pass { " + local + " } } }";
            Assert.That(ShaderRasterizationState.Parse(source).Issue() == null, Is.EqualTo(safe));
        }

        [TestCase("Safe", true)] [TestCase("Unsafe", false)] [TestCase("Missing", false)]
        public void ShaderRasterizationState_ResolvesNamedPassCoverage(string name, bool safe)
        {
            const string source = "Shader \"Probe\" { SubShader { Conservative True " +
                "Pass { Name \"Safe\" Conservative False } Pass { Name \"Unsafe\" } } }";
            Assert.That(ShaderRasterizationState.Parse(source).Issue(new HashSet<string>(new[] { name }, StringComparer.OrdinalIgnoreCase)) == null,
                Is.EqualTo(safe));
        }

        [TestCase("Conservative True", false)] [TestCase("Conservative False", true)]
        public void ShaderRasterizationState_SurfaceProgramsAndImportsRespectInheritedState(string state, bool safe)
        {
            Assert.That(ShaderRasterizationState.Parse("Shader \"Probe\"{SubShader{" + state +
                " CGPROGRAM\n#pragma surface surf Standard\nENDCG\n}}").Issue() == null, Is.EqualTo(safe));
            Assert.That(ShaderRasterizationState.Parse("Shader \"Probe\"{SubShader{" + state + " UsePass \"Other/Pass\"}}").Issue() == null,
                Is.EqualTo(safe));
        }

        [Test]
        public void ShaderRasterizationState_GrabPassIsAnObservableDrawEffect()
        {
            Assert.That(ShaderRasterizationState.Parse("Shader \"Probe\" { SubShader { GrabPass { \"_CopiedScene\" } Pass {} } }").Issue(),
                Does.Contain("GrabPass"));
        }

        [TestCase(false, false)] [TestCase(false, true)] [TestCase(true, false)]
        public void ShaderRasterizationState_NativeDependenciesIncludeFallbackAndSelectedImports(bool fallback, bool unsafePass)
        {
            string directory = "Assets/__ALTORasterState_" + Guid.NewGuid().ToString("N"); Directory.CreateDirectory(directory);
            string shaderName = "Hidden/ALTO/RasterSource/" + Guid.NewGuid().ToString("N");
            const string program = "HLSLPROGRAM\n#pragma vertex vert\n#pragma fragment frag\n" +
                "float4 vert(float4 p:POSITION):SV_POSITION{return p;}\nfloat4 frag():SV_Target{return 1;}\nENDHLSL\n";
            try
            {
                string sourcePath = directory + "/Source.shader", mainPath = directory + "/Main.shader";
                File.WriteAllText(sourcePath, "Shader \"" + shaderName + "\"{SubShader{" +
                    "Pass {Name \"SAFE\" Conservative False\n" + program + "} Pass {Name \"UNSAFE\" Conservative True\n" + program + "}} Fallback Off}");
                AssetDatabase.ImportAsset(sourcePath, ImportAssetOptions.ForceSynchronousImport);
                File.WriteAllText(mainPath, "Shader \"" + shaderName + "/Main\"{SubShader{" +
                    (fallback ? "Pass{\n" + program + "}" : "UsePass \"" + shaderName + (unsafePass ? "/UNSAFE\"" : "/SAFE\"")) + "}" +
                    (fallback ? "Fallback \"" + shaderName + "\"" : "Fallback Off") + "}");
                AssetDatabase.ImportAsset(mainPath, ImportAssetOptions.ForceSynchronousImport);
                var shader = AssetDatabase.LoadAssetAtPath<Shader>(mainPath);
                var sources = new ShaderPropertyUsage();
                Assert.That(sources.GetPrograms(shader).Uncertain, Is.Null);
                Assert.That(sources.GetPrograms(shader).DegenerateRasterizationIssue == null, Is.EqualTo(!fallback && !unsafePass));
            }
            finally { AssetDatabase.DeleteAsset(directory); }
        }
    }
}
