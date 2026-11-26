using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.Materials;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        [Test]
        public void SharedShaderConstants_IdenticalOpaqueStereoArtifactPreservesUsefulSpecialization()
        {
            RequireRuntimeShaderTarget();
            var shader = Shader.Find(SharedShaderName); Assert.That(shader, Is.Not.Null);
            var original = Track(new Material(shader));
            Color beforeFirst = SharedShaderPixel(original, 0), beforeSecond = SharedShaderPixel(original, 1);
            bool enabled = ContentFactCache.Enabled;
            try
            {
                ContentFactCache.Enabled = false;
                var statistics = new SharedShaderConstantSource.Statistics();
                var domains = new Dictionary<string, float[]> { ["_Different"] = new[] { original.GetFloat("_Different") } };
                Assert.That(SharedShaderConstantSource.TryCreate(shader, new Dictionary<string, float> { ["_Shared"] = .25f },
                    SharedShaderTarget, new ShaderPropertyUsage(), out var generated, out string source, out _, out string issue, statistics, domains), Is.True, issue);
                Track(generated);
                Assert.That(statistics.IdenticalRuntimePrograms, Is.GreaterThan(0),
                    "The stereo variants require exact complete-artifact identity when public buffer slots are ambiguous.");
                Assert.That(statistics.RuntimeArtifactCompiles, Is.GreaterThan(0));
                Assert.That(statistics.NativeCompiles, Is.EqualTo(2 * statistics.ValidatedPrograms + statistics.RuntimeArtifactCompiles),
                    "Additional opaque-artifact compilations must be included in the native budget and diagnostics.");
                Assert.That(source, Does.Contain("static const float _Shared"));
                var copy = Track(new Material(original) { shader = generated });
                Assert.That(SharedShaderPixel(copy, 0), Is.EqualTo(beforeFirst));
                Assert.That(SharedShaderPixel(copy, 1), Is.EqualTo(beforeSecond));
                TestContext.WriteLine(statistics.Detail);
            }
            finally { ContentFactCache.Enabled = enabled; }
        }

        [Test]
        public void SharedShaderConstants_ValueDomainCertificateRequiresTheSameEvidence()
        {
            RequireRuntimeShaderTarget();
            var shader = Shader.Find(SharedShaderName); var constants = new Dictionary<string, float> { ["_Shared"] = .25f };
            var domains = new Dictionary<string, float[]> { ["_Different"] = new[] { .2f, .8f } };
            bool enabled = ContentFactCache.Enabled;
            try
            {
                ContentFactCache.Enabled = true;
                Assert.That(SharedShaderConstantSource.TryCreate(shader, constants, SharedShaderTarget, new ShaderPropertyUsage(),
                    out var generated, out _, out _, out string issue, uniformDomains: domains), Is.True, issue); Track(generated);
                var withoutEvidence = new SharedShaderConstantSource.Statistics();
                Assert.That(SharedShaderConstantSource.TryCreate(shader, constants, SharedShaderTarget, new ShaderPropertyUsage(),
                    out var retained, out _, out _, out _, withoutEvidence), Is.False,
                    "A source-only caller cannot inherit another build's proved material-value domain.");
                Assert.That(retained, Is.Null); Assert.That(withoutEvidence.CacheHits, Is.Zero);
                foreach (float exceptional in new[] { BitConverter.Int32BitsToSingle(0x33000000), float.NaN })
                {
                    var changedDomain = new SharedShaderConstantSource.Statistics();
                    domains["_Different"] = new[] { .2f, exceptional };
                    Assert.That(SharedShaderConstantSource.TryCreate(shader, constants, SharedShaderTarget, new ShaderPropertyUsage(),
                        out retained, out _, out _, out _, changedDomain, domains), Is.False,
                        "A new halfway-rounding or NaN value invalidates the previously proved domain certificate.");
                    Assert.That(retained, Is.Null); Assert.That(changedDomain.CacheHits, Is.Zero);
                }
            }
            finally { ContentFactCache.Enabled = enabled; }
        }

        [Test]
        public void SharedShaderConstants_RetainedOriginsRefineActualSourceEditsAndCacheProof()
        {
            RequireRuntimeShaderTarget();
            var shader = Shader.Find(SharedShaderName); Assert.That(shader, Is.Not.Null);
            var original = Track(new Material(shader)); original.SetFloat("_Shared", SignedZero(true));
            var constants = new Dictionary<string, float>
            {
                ["_Shared"] = original.GetFloat("_Shared"), ["_Included"] = original.GetFloat("_Included"),
                ["_Different"] = original.GetFloat("_Different"), ["_Animated"] = original.GetFloat("_Animated"),
                ["_UnsupportedArray"] = 0, ["_UnsupportedBuffer"] = 0, ["_Writable"] = 0
            };
            var domains = constants.ToDictionary(p => p.Key, p => new[] { p.Value });
            var before = new List<int[]>();
            foreach (bool alternate in new[] { false, true })
            {
                if (alternate) original.EnableKeyword("ALTO_TEST_SHARED_ALTERNATE"); else original.DisableKeyword("ALTO_TEST_SHARED_ALTERNATE");
                for (int pass = 0; pass < 2; pass++)
                {
                    Color pixel = SharedShaderPixel(original, pass);
                    before.Add(new[] { pixel.r, pixel.g, pixel.b, pixel.a }.Select(BitConverter.SingleToInt32Bits).ToArray());
                }
            }
            bool enabled = ContentFactCache.Enabled;
            try
            {
                ContentFactCache.Enabled = false;
                var cold = new SharedShaderConstantSource.Statistics();
                Assert.That(SharedShaderConstantSource.TryCreate(shader, constants, SharedShaderTarget, new ShaderPropertyUsage(),
                    out var generated, out string source, out _, out string issue, cold, domains), Is.True, issue); Track(generated);
                Assert.That(cold.RetainedInputs, Is.GreaterThan(0), "The signed-zero source declaration was never eligible for a literal.");
                Assert.That(cold.ValidatedPrograms, Is.GreaterThanOrEqualTo(cold.PlannedPrograms),
                    "A successful refinement must finish all original variants, even after earlier candidates fail.");
                Assert.That(cold.NativeCompiles, Is.LessThanOrEqualTo(SharedShaderConstantSource.MaximumPrograms * 2));
                Assert.That(source, Does.Contain("static const float _Included").And.Not.Contain("static const float _Shared")
                    .And.Not.Contain("static const float _Different"),
                    "Keep the useful independent edit while retaining the edit that changes vectorized signed-zero arithmetic.");
                var copy = Track(new Material(original) { shader = generated }); int frame = 0;
                foreach (bool alternate in new[] { false, true })
                {
                    if (alternate) copy.EnableKeyword("ALTO_TEST_SHARED_ALTERNATE"); else copy.DisableKeyword("ALTO_TEST_SHARED_ALTERNATE");
                    for (int pass = 0; pass < 2; pass++)
                    {
                        Color pixel = SharedShaderPixel(copy, pass);
                        Assert.That(new[] { pixel.r, pixel.g, pixel.b, pixel.a }.Select(BitConverter.SingleToInt32Bits).ToArray(),
                            Is.EqualTo(before[frame++]), "Every observed component must retain its original bits.");
                    }
                }
                ContentFactCache.Enabled = true;
                Assert.That(SharedShaderConstantSource.TryCreate(shader, constants, SharedShaderTarget, new ShaderPropertyUsage(),
                    out var populate, out _, out _, out issue, uniformDomains: domains), Is.True, issue); Track(populate);
                var warm = new SharedShaderConstantSource.Statistics();
                Assert.That(SharedShaderConstantSource.TryCreate(shader, constants, SharedShaderTarget, new ShaderPropertyUsage(),
                    out var cached, out string cachedSource, out _, out issue, warm, domains), Is.True, issue); Track(cached);
                Assert.That(cachedSource, Is.EqualTo(source)); Assert.That(warm.CacheHits, Is.EqualTo(1));
                Assert.That(warm.ValidatedPrograms, Is.Zero); Assert.That(warm.NativeCompiles, Is.Zero);
                TestContext.WriteLine("Cold: " + cold.Detail + "\nWarm: " + warm.Detail);
            }
            finally { ContentFactCache.Enabled = enabled; }
        }

        [TestCase("NaN")] [TestCase("Halfway")]
        public void SharedShaderConstants_CompleteUseDomainsRetainUnprovedArithmetic(string exceptional)
        {
            RequireRuntimeShaderTarget();
            var first = SharedShaderMaterial(renderer);
            var other = PolicyChild("Exceptional material user").AddComponent<MeshRenderer>();
            var second = SharedShaderMaterial(other);
            second.SetFloat("_Different", exceptional == "NaN" ? float.NaN : BitConverter.Int32BitsToSingle(0x33000000));
            var diagnostics = new List<string>();
            Assert.That(SpecializeSharedShaders(SharedShaderControllers(), diagnostics), Is.Zero,
                "Every user's exact domain participates; NaN and halfway MAD/ADD results have no bit-identity proof.\n" + string.Join("\n", diagnostics));
            Assert.That(renderer.sharedMaterial, Is.SameAs(first)); Assert.That(other.sharedMaterial, Is.SameAs(second));
        }

        [Test]
        public void SharedShaderConstants_OpaqueRuntimeIdentityIncludesPrefixAndEveryPublicLayout()
        {
            RequireRuntimeShaderTarget();
            var shader = Shader.Find(SharedShaderName); Assert.That(shader, Is.Not.Null);
            var pass = ShaderUtil.GetShaderData(shader).GetSubshader(0).GetPass(0);
            var keywords = new[] { "STEREO_CUBEMAP_RENDER_ON" };
            var original = pass.CompileVariant(ShaderType.Vertex, keywords, ShaderCompilerPlatform.D3D,
                SharedShaderTarget, GraphicsTier.Tier1, false);
            var candidate = pass.CompileVariant(ShaderType.Vertex, keywords, ShaderCompilerPlatform.D3D,
                SharedShaderTarget, GraphicsTier.Tier1, false);
            Assert.That(original.Success && candidate.Success, Is.True);
            Assert.That(SharedShaderConstantSource.IdenticalRuntimeArtifacts(original, candidate), Is.True);
            byte[] bytes = candidate.ShaderData.ToArray(); int dxbc = -1;
            for (int offset = 0; offset + 4 <= bytes.Length; offset++)
                if (bytes[offset] == 'D' && bytes[offset + 1] == 'X' && bytes[offset + 2] == 'B' && bytes[offset + 3] == 'C')
                { dxbc = offset; break; }
            Assert.That(dxbc, Is.GreaterThan(0), "The fixture must include Unity's opaque runtime prefix.");
            bytes[0] ^= 1;
            Assert.That(SharedShaderConstantSource.IdenticalRuntimeArtifacts(original, ArtifactField(candidate, "ShaderData", bytes)), Is.False,
                "Equal DXBC payloads do not authorize ignoring a changed runtime prefix.");
            Assert.That(SharedShaderConstantSource.IdenticalRuntimeArtifacts(original, ArtifactField(candidate, "Success", false)), Is.False);
            Assert.That(SharedShaderConstantSource.IdenticalRuntimeArtifacts(original,
                ArtifactField(candidate, "Attributes", Array.Empty<VertexAttribute>())), Is.False);
            var buffers = candidate.ConstantBuffers.ToArray(); Assert.That(buffers.Length, Is.GreaterThan(0));
            buffers[0] = ArtifactField(buffers[0], "Name", buffers[0].Name + "_Changed");
            Assert.That(SharedShaderConstantSource.IdenticalRuntimeArtifacts(original, ArtifactField(candidate, "ConstantBuffers", buffers)), Is.False);
            Assert.That(SharedShaderConstantSource.IdenticalRuntimeArtifacts(original, ArtifactField(candidate, "TextureBindings",
                new[] { ArtifactField(default(ShaderData.TextureBindingInfo), "Name", "_Changed") })), Is.False);
        }

        private static T ArtifactField<T>(T original, string name, object replacement) where T : struct
        {
            object boxed = original;
            var field = typeof(T).GetField("<" + name + ">k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null, "The fixture requires Unity's immutable public compile-result metadata.");
            field.SetValue(boxed, replacement); return (T)boxed;
        }

        [TestCase(0x00000001u)] [TestCase(0x80000001u)]
        [TestCase(0x007fffffu)] [TestCase(0x807fffffu)]
        [TestCase(0x00800000u)] [TestCase(0x80800000u)]
        [TestCase(0x3dcccccdu)] [TestCase(0x3f800001u)] [TestCase(0x7f7fffffu)]
        public void SharedShaderConstants_NumericBoundaryPreservesNativeUniformBits(uint bits)
        {
            RequireRuntimeShaderTarget();
            var shader = Shader.Find("Hidden/Noname/ALTO/SharedShaderNumericBits");
            Assert.That(shader, Is.Not.Null);
            var original = Track(new Material(shader)); renderer.sharedMaterial = original;
            original.SetFloat("_Value", BitConverter.Int32BitsToSingle(unchecked((int)bits)));
            // Encode asuint into ordinary byte-sized color channels. Reading a
            // subnormal float directly through a render target can flush its value
            // independently of whether the shader observed the original bits.
            uint before = NumericPixelBits(SharedShaderPixel(original, 0));
            Assert.That(before, Is.EqualTo(bits), "The native uniform must expose the requested bits.");
            var diagnostics = new List<string>();
            Assert.That(SpecializeSharedShaders(SharedShaderControllers(), diagnostics), Is.EqualTo(1), string.Join("\n", diagnostics));
            uint after = NumericPixelBits(SharedShaderPixel(renderer.sharedMaterial, 0));
            Assert.That(after, Is.EqualTo(before), "A declaration literal must preserve the bits observed by asuint.");
        }

        [TestCase("NegativeZeroMultiply", false)] [TestCase("NegativeZeroMultiply", true)]
        [TestCase("PositiveUnderflow", false)] [TestCase("PositiveUnderflow", true)]
        [TestCase("NegativeUnderflow", false)] [TestCase("NegativeUnderflow", true)]
        [TestCase("CancelThenNegate", false)] [TestCase("CancelThenNegate", true)]
        [TestCase("RoundingThreshold", false)] [TestCase("RoundingThreshold", true)]
        public void SharedShaderConstants_DerivedNormalArithmeticPreservesNativeOutputBits(string operation, bool precise)
        {
            RequireRuntimeShaderTarget();
            var (valueBits, expression) = DerivedArithmeticExpression(operation, precise);
            string observation = operation == "RoundingThreshold"
                ? "observed > 0.0f ? 0xffffffffu : 0u" : "asuint(observed)";
            string directory = "Assets/__ALTO_ShaderArithmetic_" + Guid.NewGuid().ToString("N"); Directory.CreateDirectory(directory);
            try
            {
                string name = "Hidden/ALTO/DerivedArithmetic/" + Guid.NewGuid().ToString("N");
                string source = "Shader \"" + name + "\" { Properties { _Value (\"Value\",Float)=1 _Stable (\"Stable\",Float)=0.25 }\n" +
                    "SubShader { Cull Off ZWrite Off ZTest Always Pass { HLSLPROGRAM\n#pragma target 5.0\n#pragma vertex vert\n#pragma fragment frag\n" +
                    "#include \"UnityCG.cginc\"\nfloat _Value; float _Stable;\n" +
                    "v2f_img vert(appdata_img input) { return vert_img(input); }\n" +
                    "float4 frag(v2f_img input):SV_Target {\n" + expression + "\nuint bits=" + observation + ";\n" +
                    "return float4(bits & 255u,(bits >> 8) & 255u,(bits >> 16) & 255u,(bits >> 24) & 255u)/255.0;\n}\nENDHLSL\n}\n" +
                    "Pass { HLSLPROGRAM\n#pragma vertex vert_img\n#pragma fragment stable\n#include \"UnityCG.cginc\"\n" +
                    "float _Stable; float4 stable(v2f_img input):SV_Target{return _Stable;}\nENDHLSL\n} } Fallback Off }";
                var shader = ImportDependencyShader(directory, "Arithmetic", source);
                var original = Track(new Material(shader)); renderer.sharedMaterial = original;
                original.SetFloat("_Value", BitConverter.Int32BitsToSingle(unchecked((int)valueBits)));
                Assert.That(unchecked((uint)BitConverter.SingleToInt32Bits(original.GetFloat("_Value"))), Is.EqualTo(valueBits));
                uint before = NumericPixelBits(SharedShaderPixel(original, 0));
                Assert.That(NumericPixelBits(SharedShaderPixel(original, 0)), Is.EqualTo(before), "The native reference draw must be repeatable.");
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    renderer.sharedMaterial = original;
                    var diagnostics = new List<string>();
                    Assert.That(SpecializeSharedShaders(SharedShaderControllers(), diagnostics), Is.EqualTo(1), string.Join("\n", diagnostics));
                    uint after = NumericPixelBits(SharedShaderPixel(renderer.sharedMaterial, 0));
                    TestContext.WriteLine(operation + "; precise=" + precise + "; attempt=" + attempt + "; input=0x" + valueBits.ToString("X8") +
                        "; uniform=0x" + before.ToString("X8") + "; specialized=0x" + after.ToString("X8") +
                        "; device=" + SystemInfo.graphicsDeviceType + "/" + SystemInfo.graphicsDeviceName);
                    Assert.That(after, Is.EqualTo(before),
                        "Compare shader-visible output, including signed zero and threshold selection; no numerical relaxation is authorized.");
                    var pass = ShaderUtil.GetShaderData(renderer.sharedMaterial.shader).GetSubshader(0).GetPass(1);
                    var program = pass.PreprocessVariant(ShaderType.Fragment, Array.Empty<string>(), ShaderCompilerPlatform.D3D,
                        SharedShaderTarget, GraphicsTier.Tier1, false);
                    Assert.That(D3DShaderInputs.CompileUncached(program.PreprocessedCode, "stable", ShaderType.Fragment,
                        out var inputs, out string issue), Is.True, issue);
                    Assert.That(inputs.Any(i => i.Kind == D3DShaderInputs.InputKind.Uniform && i.Semantic == "_Stable" && i.ReadMask != 0),
                        Is.False, "The independent direct output still specializes; retaining the entire shader cannot pass this regression.");
                    Assert.That(SharedShaderPixel(renderer.sharedMaterial, 1).r, Is.EqualTo(.25f));
                }
            }
            finally { AssetDatabase.DeleteAsset(directory); }
        }

        [TestCase("NegativeZeroMultiply", true)]
        [TestCase("NegativeUnderflow", false)] [TestCase("NegativeUnderflow", true)]
        [TestCase("CancelThenNegate", false)] [TestCase("CancelThenNegate", true)]
        [TestCase("RoundingThreshold", false)]
        public void ShaderArithmetic_NativeThresholdKeepsObservedVertexInput(string operation, bool precise)
        {
            RequireRuntimeShaderTarget();
            var (valueBits, expression) = DerivedArithmeticExpression(operation, precise);
            string condition = operation == "RoundingThreshold" ? "observed > 0.0f" : "(asuint(observed) & 0x80000000u) != 0u";
            string directory = "Assets/__ALTO_ShaderArithmeticInput_" + Guid.NewGuid().ToString("N"); Directory.CreateDirectory(directory);
            try
            {
                string name = "Hidden/ALTO/ArithmeticInput/" + Guid.NewGuid().ToString("N");
                string source = "Shader \"" + name + "\" { Properties { _Value (\"Value\",Float)=1 }\n" +
                    "SubShader { Cull Off ZWrite Off ZTest Always Pass { HLSLPROGRAM\n#pragma target 5.0\n#pragma vertex vert\n#pragma fragment frag\n" +
                    "#include \"UnityCG.cginc\"\nfloat _Value;\nstruct Output { float4 pos:SV_POSITION; float value:TEXCOORD0; };\n" +
                    "Output vert(appdata_img input,uint id:SV_VertexID) { Output output; output.pos=vert_img(input).pos;\n" + expression +
                    "\noutput.value=(" + condition + ") ? (float)(id+1u)*0.125f : 0.0f; return output; }\n" +
                    "float4 frag(Output input):SV_Target { return float4(input.value,0,0,1); }\nENDHLSL\n} } Fallback Off }";
                var shader = ImportDependencyShader(directory, "ArithmeticInput", source);
                var material = Track(new Material(shader)); renderer.sharedMaterial = material;
                material.SetFloat("_Value", BitConverter.Int32BitsToSingle(unchecked((int)valueBits)));
                float native = SharedShaderPixel(material, 0).r;
                Assert.That(native, Is.GreaterThan(0f), "The actual vertex program must take the branch which observes SV_VertexID.");
                TestContext.WriteLine(operation + "; precise=" + precise + "; native vertex output=" + native.ToString("R") +
                    "; device=" + SystemInfo.graphicsDeviceType + "/" + SystemInfo.graphicsDeviceName);
                var sources = new ShaderPropertyUsage();
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    string issue = sources.MeshInputs.Issue(material, renderer, FrameIndex(), ShaderInputQuery.VertexId, SharedShaderTarget);
                    Assert.That(issue, Is.Not.Null, "Strict-source constant folding cannot certify absence from a different native program.");
                    Assert.That(issue, Does.Contain("may depend").Or.Contain("unverified"));
                    Assert.That(SharedShaderPixel(material, 0).r, Is.EqualTo(native));
                }
            }
            finally { AssetDatabase.DeleteAsset(directory); }
        }

        [Test]
        public void ShaderArithmetic_VertexAbsenceAssumptionRemainsAnObservableMaterialProperty()
        {
            RequireRuntimeShaderTarget();
            string directory = "Assets/__ALTO_ShaderAssumption_" + Guid.NewGuid().ToString("N"); Directory.CreateDirectory(directory);
            try
            {
                string source = "Shader \"Hidden/ALTO/NativeAssumption/" + Guid.NewGuid().ToString("N") +
                    "\" { Properties { _Gate (\"Gate\",Float)=1 } SubShader { Pass { HLSLPROGRAM\n" +
                    "#pragma target 5.0\n#pragma vertex vert\n#pragma fragment frag\nfloat _Gate;\n" +
                    "float4 vert(float4 position:POSITION,uint id:SV_VertexID):SV_POSITION {\n" +
                    "if(_Gate!=0.0f) position.x+=(id&1u)*0.01f; return position; }\n" +
                    "float4 frag():SV_Target{return 1;}\nENDHLSL\n} } Fallback Off }";
                var shader = ImportDependencyShader(directory, "Assumption", source);
                var material = Track(new Material(shader)); renderer.sharedMaterial = material;
                Assert.That(material.GetFloat("_Gate"), Is.EqualTo(1));
                material.SetFloat("_Gate", 0); SavedFloat(material, "_Obsolete", 7);
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    renderer.sharedMaterial = material;
                    var sources = new ShaderPropertyUsage(); var index = FrameIndex();
                    Assert.That(sources.MeshInputs.Issue(material, renderer, index, ShaderInputQuery.VertexId, SharedShaderTarget), Is.Null,
                        "The native zero gate provides a useful vertex-input absence proof.");
                    Assert.That(sources.MayRead(material, renderer, index, "_Gate", new HashSet<string>(), SharedShaderTarget), Is.True,
                        "A constant which justified another input's absence cannot prove its own property unobserved.");
                    var unused = SavedMaterialProperties.Unused(material, sources, new VrcFallbackPropertyContract());
                    var cleaned = Track(SavedMaterialProperties.CleanCopy(material, unused));
                    Assert.That(SavedFloats(cleaned), Does.Contain("_Gate").And.Not.Contain("_Obsolete"));
                    Assert.That(cleaned.GetFloat("_Gate"), Is.Zero, "Removing the override would restore the shader's default one.");
                    renderer.sharedMaterial = cleaned;
                    Assert.That(sources.MeshInputs.Issue(cleaned, renderer, FrameIndex(), ShaderInputQuery.VertexId, SharedShaderTarget), Is.Null);
                }
            }
            finally { AssetDatabase.DeleteAsset(directory); }
        }

        [TestCase(false)] [TestCase(true)]
        public void ShaderArithmetic_OriginalSemanticInventorySurvivesUnsupportedNativeLoop(bool readsVertexId)
        {
            RequireRuntimeShaderTarget();
            string directory = "Assets/__ALTO_NativeSemanticInventory_" + Guid.NewGuid().ToString("N"); Directory.CreateDirectory(directory);
            try
            {
                string source = "Shader \"Hidden/ALTO/NativeSemanticInventory/" + Guid.NewGuid().ToString("N") +
                    "\" { Properties { _Iterations (\"Iterations\",Float)=4 } SubShader { Pass { HLSLPROGRAM\n" +
                    "#pragma target 5.0\n#pragma vertex vert\n#pragma fragment frag\nfloat _Iterations;\n" +
                    "float4 vert(float4 position:POSITION,uint id:SV_VertexID):SV_POSITION {\n" +
                    "[loop] for(uint step=0u;step<(uint)_Iterations;step++) position.x=position.x*0.75f+0.01f;\n" +
                    (readsVertexId ? "position.y+=(id&1u)*0.01f;\n" : "") +
                    "return position; }\nfloat4 frag():SV_Target{return 1;}\nENDHLSL\n} } Fallback Off }";
                var shader = ImportDependencyShader(directory, "SemanticInventory", source);
                var material = Track(new Material(shader)); renderer.sharedMaterial = material;
                var pass = ShaderUtil.GetShaderData(shader).GetSubshader(0).GetPass(0);
                var native = pass.CompileVariant(ShaderType.Vertex, Array.Empty<string>(), ShaderCompilerPlatform.D3D,
                    SharedShaderTarget, GraphicsTier.Tier1, true);
                Assert.That(native.Success, Is.True);
                Assert.That(D3DConstantEquivalence.TryInputs(native, new Dictionary<string, float> { ["_Iterations"] = 4 },
                    ShaderType.Vertex, out _, out string unsupported), Is.False,
                    "The fixture must exercise an unsupported original native loop.");
                Assert.That(unsupported, Is.Not.Empty);
                Assert.That(D3DShaderInputs.Reflect(native.ShaderData, ShaderType.Vertex, out var inventory, out string error), Is.True, error);
                Assert.That(ShaderInputQuery.VertexId.MayReadCompiled(inventory), Is.EqualTo(readsVertexId));
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    var sources = new ShaderPropertyUsage(); var index = FrameIndex();
                    string issue = sources.MeshInputs.Issue(material, renderer, index, ShaderInputQuery.VertexId, SharedShaderTarget);
                    Assert.That(issue == null, Is.EqualTo(!readsVertexId), issue);
                    Assert.That(sources.MayRead(material, renderer, index, "_Iterations", new HashSet<string>(), SharedShaderTarget), Is.True,
                        "Semantic-only reflection must never certify a named uniform absent.");
                    var position = sources.MeshInputs.PositionInputs(material, renderer, index, SharedShaderTarget);
                    Assert.That(position.Issue, Is.Null, position.Issue);
                    Assert.That(position.Reads.TryGetValue(("POSITION", 0u), out byte positionMask), Is.True);
                    Assert.That(positionMask, Is.EqualTo(15), "Every original position component still reaches SV_POSITION");
                    Assert.That(position.Reads.TryGetValue(("SV_VERTEXID", 0u), out byte idMask), Is.EqualTo(readsVertexId));
                    if (readsVertexId) Assert.That(idMask, Is.EqualTo(1));
                }
            }
            finally { AssetDatabase.DeleteAsset(directory); }
        }

        private static (uint Bits, string Expression) DerivedArithmeticExpression(string operation, bool precise)
        {
            uint bits = operation switch
            {
                "NegativeZeroMultiply" => 0xbf800000u,
                "PositiveUnderflow" => 0x00800000u,
                "NegativeUnderflow" => 0x80800000u,
                "CancelThenNegate" => 0x3e800000u,
                "RoundingThreshold" => 0x3f800001u,
                _ => throw new ArgumentOutOfRangeException(nameof(operation))
            };
            string qualifier = precise ? "precise " : "";
            string expression = operation switch
            {
                "NegativeZeroMultiply" => qualifier + "float observed = _Value * 0.0f;",
                "PositiveUnderflow" or "NegativeUnderflow" => qualifier + "float observed = _Value * 0.5f;",
                "CancelThenNegate" => qualifier + "float cancelled = _Value - 0.25f;\n" + qualifier + "float observed = -cancelled;",
                // A fused multiply/add retains a residual which a separately
                // rounded multiply/subtract loses in the native counterexample.
                "RoundingThreshold" => qualifier + "float product = _Value * 1.00000011920928955078125f;\n" +
                    qualifier + "float observed = product - 1.0000002384185791015625f;",
                _ => throw new ArgumentOutOfRangeException(nameof(operation))
            };
            return (bits, expression);
        }

        private static uint NumericPixelBits(Color pixel)
        {
            uint result = 0;
            for (int channel = 0; channel < 4; channel++)
            {
                int value = Mathf.RoundToInt(pixel[channel] * 255);
                Assert.That(value, Is.InRange(0, 255));
                result |= (uint)value << (channel * 8);
            }
            return result;
        }
    }
}
