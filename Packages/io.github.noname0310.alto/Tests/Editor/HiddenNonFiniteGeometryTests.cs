using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.Meshes;
using Noname.AvatarTools.ObjectOptimization;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.Constraint.Components;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private string NonFiniteShader(string mode, out Material material)
        {
            string directory = "Assets/__ALTONonFinite_" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(directory);
            string body = mode switch
            {
                "Clamp" => "return float4(saturate(v.p.xyz),1);",
                "UniformBranch" => "float4 p=v.p; if(_Gate>0) p=saturate(p); return p;",
                "UvRecovery" => "return float4(v.uv*1.5-0.75,0,1);",
                "NaNRecovery" => "bool bad=(asuint(v.p.x)&0x7fffffffu)>0x7f800000u;return float4(bad?v.uv*1.5-0.75:v.p.xy,0,1);",
                "Payload" => "return float4((asuint(v.p.xy)&65535u)*(1.0/65535.0),0,1);",
                "Displace" => "return UnityObjectToClipPos(v.p)+float4(v.uv,0,0);",
                "VertexId" => "return float4(v.id%3==1?0.8:-0.8,v.id%3==2?0.8:-0.8,0,1);",
                "VaryingBufferIndex" => "return _Positions[3+(v.id&1u)];",
                "PayloadBufferIndex" => "return _Positions[3+(asuint(v.p.x)&1u)];",
                "UniformBufferIndex" => "return _Positions[3+((uint)_Gate&1u)];",
                "NaNBitCondition" => "return asuint(v.p.x)!=0u?float4(v.uv,0,1):v.p;",
                "Reciprocal" => "return float4(rcp(v.p.xyz),1);",
                "PositiveClamp" => "return float4(rcp(max(v.p.xyz,0.000244140625)),1);",
                "NegativeClamp" => "return float4(rcp(min(v.p.xyz,-0.000244140625)),1);",
                "MinimumNormalClamp" => "return float4(rcp(max(v.p.xyz,asfloat(0x00800000u))),1);",
                "SubnormalClamp" => "return float4(rcp(max(v.p.xyz,asfloat(0x007fffffu))),1);",
                "SelfDot" => "return float4(rsqrt(dot(v.p.xy,v.p.xy)),0,0,1);",
                "CrossDot" => "return float4(rsqrt(dot(v.p.xy,v.p.yx)),0,0,1);",
                "UniformBooleanMask" => "uint m=_Gate>0?0xffffffffu:0u;return float4(asfloat(asuint(v.p.xyz)&m),1);",
                "MixedNaNAndFinite" => "float2 p=v.uv.x>0.5?v.p.xy:v.uv;return float4(min(p,0.5),0,1);",
                "ReducedPrecision" => "return float4(v.p);",
                _ => "return UnityObjectToClipPos(v.p);"
            };
            string Pass(string value) => "Pass { Cull Off ZWrite Off ZTest Always\nHLSLPROGRAM\n" +
                "#pragma target 4.5\n#pragma vertex vert\n#pragma fragment frag\n#include \"UnityCG.cginc\"\n" +
                "float _Gate; float4 _Positions[8]; struct V{" + (mode == "ReducedPrecision" ? "min16float4" : "float4") +
                " p:POSITION;float2 uv:TEXCOORD0;uint id:SV_VertexID;};\n" +
                "float4 vert(V v):SV_POSITION{" + value + "}\nfloat4 frag():SV_Target{return float4(1,0,0,1);}\nENDHLSL\n}\n";
            string path = directory + "/Probe.shader";
            File.WriteAllText(path, "Shader \"Hidden/ALTO/NonFinite/" + Guid.NewGuid().ToString("N") +
                "\" {Properties{_Gate(\"Gate\",Float)=0}SubShader{" + Pass(body) +
                (mode == "ShadowRecovery" ? Pass("return float4(v.uv*1.5-0.75,0,1);").Replace("Pass {", "Pass { Tags{\"LightMode\"=\"ShadowCaster\"}") : "") +
                "}Fallback Off}");
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
            Assert.That(shader, Is.Not.Null);
            material = Track(new Material(shader)); return directory;
        }

        private (Mesh Mesh, Transform Bone, Transform Moving, AnimationClip Clip) NonFiniteFixture(bool leading = true, bool allAxes = true)
        {
            var mesh = Mesh();
            mesh.vertices = new[] { new Vector3(-.9f, -.6f, 0), new Vector3(-.1f, -.6f, 0), new Vector3(-.5f, .6f, 0),
                new Vector3(.1f, -.6f, 0), new Vector3(.9f, -.6f, 0), new Vector3(.5f, .6f, 0) };
            mesh.normals = Enumerable.Repeat(Vector3.forward, 6).ToArray();
            mesh.tangents = Enumerable.Repeat(new Vector4(1, 0, 0, 1), 6).ToArray();
            mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.zero, Vector2.right, Vector2.up };
            mesh.triangles = new[] { 0, 1, 2, 3, 4, 5 };
            var moving = PolicyChild("Moving").transform;
            var bone = PolicyChild("Hidden bone", moving).transform;
            var visible = PolicyChild("Visible bone").transform;
            renderer.bones = new[] { bone, visible }; renderer.rootBone = root.transform;
            mesh.bindposes = new[] { Matrix4x4.identity, Matrix4x4.identity };
            mesh.boneWeights = Enumerable.Range(0, 6).Select(i => i >= 3
                ? new BoneWeight { boneIndex0 = 1, weight0 = 1, boneIndex1 = 1, boneIndex2 = 1, boneIndex3 = 1 }
                : leading ? new BoneWeight { boneIndex0 = 0, weight0 = .75f, boneIndex1 = 1, weight1 = .25f }
                : new BoneWeight { boneIndex0 = 1, weight0 = .75f, boneIndex1 = 0, weight1 = .25f }).ToArray();
            mesh.AddBlendShapeFrame("Remaining", 100, new[] { Vector3.right, Vector3.up, Vector3.forward,
                Vector3.zero, Vector3.zero, Vector3.zero }, new Vector3[6], new Vector3[6]);
            renderer.localBounds = new Bounds(Vector3.zero, Vector3.one * 10);
            renderer.updateWhenOffscreen = false;
            var clip = Clip();
            foreach (string axis in allAxes ? new[] { "x", "y", "z" } : new[] { "x", "y" })
            {
                // The bulk-key constructor silently drops NaN keys in Unity.
                // AddKey is the native path used for a nonfinite animation key.
                var curve = new AnimationCurve(); curve.AddKey(new Keyframe(0, float.NaN));
                Assert.That(curve.length, Is.EqualTo(1));
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Moving/Hidden bone", typeof(Transform),
                    "m_LocalScale." + axis), curve);
                var stored = AnimationUtility.GetEditorCurve(clip, EditorCurveBinding.FloatCurve("Moving/Hidden bone", typeof(Transform), "m_LocalScale." + axis));
                Assert.That(stored.length, Is.EqualTo(1), "The native clip must contain the nonfinite key.");
                Assert.That(float.IsNaN(stored.keys[0].value), Is.True);
            }
            return (mesh, bone, moving, clip);
        }

        private AvatarAnimationIndex NonFiniteIndex(AnimatorController source, params string[] preserved)
        {
            var controller = new CloneContext(VRChatPlatformAnimatorBindings.Instance).Clone(source);
            return AvatarAnimationIndex.Create(root, new Dictionary<object, VirtualAnimatorController>
                { [VRCAvatarDescriptor.AnimLayerType.FX] = controller }, null, preserved);
        }

        private int RemoveNonFinite(AvatarAnimationIndex index, bool initial, ShaderPropertyUsage shaders, out string message)
        {
            var scope = new OptimizationBuildScope { Root = root.transform };
            var settings = new MeshOptimizationState { Scope = scope, RemoveHiddenGeometry = true };
            var objectSettings = new ObjectOptimizationState { Scope = scope };
            var references = AvatarDependencyGraph.Create(root, index, objectSettings, false, shaders);
            var messages = new List<string>();
            int result = PermanentlyHiddenGeometry.Apply(root, new[] { renderer }, settings, objectSettings, index,
                references, shaders, BuildTarget.StandaloneWindows64, copy => Track(copy), messages,
                allowInitialChanges: _ => initial);
            message = string.Join("\n", messages); return result;
        }

        [TestCase("Affine", true)] [TestCase("Clamp", true)] [TestCase("UniformBranch", true)]
        [TestCase("UvRecovery", false)] [TestCase("NaNRecovery", false)] [TestCase("Payload", false)]
        [TestCase("Displace", false)] [TestCase("VertexId", false)] [TestCase("ShadowRecovery", false)]
        [TestCase("VaryingBufferIndex", false)] [TestCase("PayloadBufferIndex", false)] [TestCase("UniformBufferIndex", true)]
        [TestCase("NaNBitCondition", false)] [TestCase("Reciprocal", false)]
        [TestCase("PositiveClamp", true)] [TestCase("NegativeClamp", true)]
        [TestCase("MinimumNormalClamp", true)] [TestCase("SubnormalClamp", false)]
        [TestCase("SelfDot", true)] [TestCase("CrossDot", false)]
        [TestCase("UniformBooleanMask", true)] [TestCase("MixedNaNAndFinite", false)]
        public void HiddenNonFiniteGeometry_NativeProgramRequiresNondrawingPosition(string mode, bool expected)
        {
            string directory = NonFiniteShader(mode, out var material);
            try
            {
                NonFiniteFixture(); renderer.sharedMaterial = material;
                var shaders = new ShaderPropertyUsage(); var index = FrameIndex();
                var proof = shaders.MeshInputs.PositionInputs(material, renderer, index, BuildTarget.StandaloneWindows64, true);
                if (proof.NonFinitePositionIsHidden != expected)
                {
                    TestContext.WriteLine(proof.NonFiniteDiagnostic);
                    var compiled = ShaderUtil.GetShaderData(material.shader).GetSubshader(0).GetPass(0).CompileVariant(
                        ShaderType.Vertex, Array.Empty<string>(), ShaderCompilerPlatform.D3D, BuildTarget.StandaloneWindows64, GraphicsTier.Tier1, true);
                    D3DVertexNonFiniteProof.TryProve(compiled.ShaderData, out string issue, out string program);
                    TestContext.WriteLine(issue + "\n" + program);
                }
                Assert.That(proof.Issue, Is.Null);
                Assert.That(proof.NonFinitePositionIsHidden, Is.EqualTo(expected), proof.NonFiniteIssue);
                int calls = shaders.MeshInputs.CompileCalls;
                Assert.That(shaders.MeshInputs.PositionInputs(material, renderer, index, BuildTarget.StandaloneWindows64, true), Is.SameAs(proof));
                Assert.That(shaders.MeshInputs.CompileCalls, Is.EqualTo(calls));
            }
            finally { AssetDatabase.DeleteAsset(directory); }
        }

        [TestCase("Clip", true)] [TestCase("Direct", true)] [TestCase("Selected1D", true)]
        [TestCase("NoInitialPermission", false)] [TestCase("LowerInfluence", false)] [TestCase("PartialScale", false)]
        [TestCase("VariableSelection", false)] [TestCase("PartialWeight", false)] [TestCase("ActiveConstraint", false)]
        [TestCase("DisabledConstraint", true)] [TestCase("LaterWriteDefaults", false)] [TestCase("ShaderRecovery", false)]
        [TestCase("SparseDirect", false)] [TestCase("PoisonedSkinRoot", false)] [TestCase("MissingSkinRoot", false)]
        [TestCase("NullSecondaryBone", false)] [TestCase("NonFiniteSecondaryBindpose", false)]
        [TestCase("AutomaticBounds", false)] [TestCase("AnimatedBoundsFalse", true)] [TestCase("ChangingBounds", false)]
        public void HiddenNonFiniteGeometry_RequiresPersistentProducerAndRetainsInterfaces(string mode, bool expected)
        {
            var fixture = NonFiniteFixture(mode != "LowerInfluence", mode != "PartialScale");
            string directory = NonFiniteShader(mode == "ShaderRecovery" ? "UvRecovery" : "Affine", out var material);
            try
            {
                renderer.sharedMaterial = material;
                var source = Controller(fixture.Clip);
                if (mode == "AutomaticBounds" || mode == "AnimatedBoundsFalse") renderer.updateWhenOffscreen = true;
                if (mode == "AnimatedBoundsFalse" || mode == "ChangingBounds") fixture.Clip.SetCurve("", typeof(SkinnedMeshRenderer),
                    MeshMergeAnalysis.UpdateWhenOffscreenProperty, mode == "AnimatedBoundsFalse" ? AnimationCurve.Constant(0, 1, 0) : AnimationCurve.Linear(0, 0, 1, 1));
                if (mode == "PoisonedSkinRoot") renderer.rootBone = fixture.Bone;
                if (mode == "MissingSkinRoot") renderer.rootBone = null;
                if (mode == "NullSecondaryBone") renderer.bones = new[] { fixture.Bone, null };
                if (mode == "NonFiniteSecondaryBindpose")
                {
                    var poses = fixture.Mesh.bindposes; poses[1][0] = float.NaN; fixture.Mesh.bindposes = poses;
                }
                if (mode == "ActiveConstraint" || mode == "DisabledConstraint")
                {
                    var constraint = fixture.Bone.gameObject.AddComponent<VRCScaleConstraint>(); constraint.IsActive = true;
                    if (mode == "DisabledConstraint")
                        fixture.Clip.SetCurve("Moving/Hidden bone", typeof(VRCScaleConstraint), "IsActive", AnimationCurve.Constant(0, 1, 0));
                }
                if (mode == "Direct" || mode == "PartialWeight" || mode == "SparseDirect")
                {
                    var tree = Track(new BlendTree { blendType = BlendTreeType.Direct });
                    source.AddParameter(new AnimatorControllerParameter { name = "Weight", type = AnimatorControllerParameterType.Float,
                        defaultFloat = mode == "PartialWeight" ? .5f : 1 });
                    tree.children = new[] { new ChildMotion { motion = fixture.Clip, directBlendParameter = "Weight", timeScale = 1 } };
                    if (mode == "SparseDirect")
                    {
                        tree.children = tree.children.Concat(new[] { new ChildMotion { motion = Clip(), directBlendParameter = "Weight", timeScale = 1 } }).ToArray();
                        source.layers[0].stateMachine.defaultState.writeDefaultValues = false;
                    }
                    source.layers[0].stateMachine.defaultState.motion = tree;
                }
                if (mode == "Selected1D" || mode == "VariableSelection")
                {
                    var visible = Clip();
                    foreach (string axis in new[] { "x", "y", "z" })
                        visible.SetCurve("Moving/Hidden bone", typeof(Transform), "m_LocalScale." + axis, AnimationCurve.Constant(0, 1, 1));
                    source = FixedOneDimensionalController(0, new[] { fixture.Clip, visible }, new[] { 0f, 1f });
                }
                if (mode == "LaterWriteDefaults")
                {
                    var extra = Controller(Clip()).layers[0]; extra.name = "Later reset"; extra.defaultWeight = 1;
                    source.layers = new[] { source.layers[0], extra };
                }
                var index = NonFiniteIndex(source, mode == "VariableSelection" ? new[] { "Selection" } : Array.Empty<string>());
                var shaders = new ShaderPropertyUsage();
                var bounds = renderer.localBounds;
                int removed = RemoveNonFinite(index, mode != "NoInitialPermission", shaders, out string message);
                Assert.That(removed, Is.EqualTo(expected ? 1 : 0), message);
                Assert.That(renderer.sharedMesh.vertexCount, Is.EqualTo(fixture.Mesh.vertexCount));
                Assert.That(renderer.sharedMesh.vertices, Is.EqualTo(fixture.Mesh.vertices));
                Assert.That(renderer.sharedMesh.blendShapeCount, Is.EqualTo(1));
                Assert.That(renderer.sharedMesh.bindposes.SelectMany(p => Enumerable.Range(0, 16).Select(i => BitConverter.SingleToInt32Bits(p[i]))),
                    Is.EqualTo(fixture.Mesh.bindposes.SelectMany(p => Enumerable.Range(0, 16).Select(i => BitConverter.SingleToInt32Bits(p[i])))));
                Assert.That(renderer.sharedMesh.boneWeights, Is.EqualTo(fixture.Mesh.boneWeights));
                Assert.That(renderer.sharedMesh.bounds, Is.EqualTo(fixture.Mesh.bounds));
                Assert.That(renderer.localBounds, Is.EqualTo(bounds));
                Assert.That(renderer.sharedMesh.triangles, Is.EqualTo(expected ? new[] { 3, 4, 5 } : fixture.Mesh.triangles));
                Assert.That(float.IsNaN(AnimationUtility.GetEditorCurve(fixture.Clip,
                    EditorCurveBinding.FloatCurve("Moving/Hidden bone", typeof(Transform), "m_LocalScale.x")).keys[0].value), Is.True);
            }
            finally { AssetDatabase.DeleteAsset(directory); }
        }

        [Test]
        public void HiddenNonFiniteGeometry_ReducedPrecisionPositionRetainsUnknown()
        {
            string directory = NonFiniteShader("ReducedPrecision", out var material);
            try
            {
                var compiled = ShaderUtil.GetShaderData(material.shader).GetSubshader(0).GetPass(0).CompileVariant(
                    ShaderType.Vertex, Array.Empty<string>(), ShaderCompilerPlatform.D3D, BuildTarget.StandaloneWindows64, GraphicsTier.Tier1, true);
                Assert.That(compiled.Success, Is.True);
                bool proved = D3DVertexNonFiniteProof.TryProve(compiled.ShaderData, out string issue, out string program, out uint precision);
                TestContext.WriteLine("Native POSITION minimum precision=" + precision);
                bool retainedPrecision = precision != 0 || program != null && program.Contains("min16");
                Assert.That(proved, Is.EqualTo(!retainedPrecision), issue);
            }
            finally { AssetDatabase.DeleteAsset(directory); }
        }

        [TestCase(SkinQuality.Bone1, true)] [TestCase(SkinQuality.Bone2, true)]
        [TestCase(SkinQuality.Bone4, true)] [TestCase(SkinQuality.Auto, true)] [TestCase(SkinQuality.Bone1, false)]
        public void HiddenNonFiniteGeometry_NativeSkinningKeepsLeadingNaNAcrossPoses(SkinQuality quality, bool leading)
        {
            var fixture = NonFiniteFixture(leading);
            renderer.quality = quality;
            var index = NonFiniteIndex(Controller(fixture.Clip));
            var proof = new NonFiniteSkinningProof(root, index).Vertices(renderer, true);
            Assert.That(proof.Take(3), Is.All.EqualTo(leading));
            Assert.That(proof.Skip(3), Is.All.EqualTo(false));
            var baked = Track(new Mesh());
            int frame = 0;
            SampleFrozen(Controller(fixture.Clip), (_, i) =>
            {
                frame = i; fixture.Moving.localPosition = new Vector3(i * .1f, -.2f, .3f);
                fixture.Moving.localRotation = Quaternion.Euler(i * 17, i * 29, i * 11);
                fixture.Moving.localScale = new Vector3(i % 2 == 0 ? 1 : -1, .5f + i, 1.3f);
                renderer.SetBlendShapeWeight(0, (i - 2) * 100);
            }, 6, () =>
            {
                Assert.That(float.IsNaN(fixture.Bone.localScale.x) && float.IsNaN(fixture.Bone.localScale.y) && float.IsNaN(fixture.Bone.localScale.z), Is.True);
                // The native bounds calculation reports the intentionally NaN
                // bone even when Bone1 omits its lower-weight contribution.
                LogAssert.Expect(LogType.Assert, "Invalid AABB result");
                renderer.BakeMesh(baked);
                var positions = baked.vertices;
                Assert.That(positions.Take(3).All(p => float.IsNaN(p.x) && float.IsNaN(p.y) && float.IsNaN(p.z)),
                    Is.EqualTo(leading), "Native skinning frame " + frame);
                Assert.That(positions.Skip(3).All(p => MotionAnalysis.Finite(p.x) && MotionAnalysis.Finite(p.y) && MotionAnalysis.Finite(p.z)), Is.True);
                return 0;
            });
        }

        [Test]
        public void HiddenNonFiniteGeometry_ZeroWeightPoisonIsNotARequiredInfluence()
        {
            var fixture = NonFiniteFixture(); renderer.quality = SkinQuality.Bone4;
            var weights = fixture.Mesh.boneWeights;
            for (int i = 3; i < 6; i++) weights[i].boneIndex1 = 0;
            fixture.Mesh.boneWeights = weights;
            var proof = new NonFiniteSkinningProof(root, NonFiniteIndex(Controller(fixture.Clip))).Vertices(renderer, true);
            Assert.That(proof.Skip(3), Is.All.EqualTo(false), "A zero-weight influence can be omitted by another quality/backend.");
            var baked = Track(new Mesh());
            SampleFrozen(Controller(fixture.Clip), (_, __) => { }, 1, () =>
            {
                Assert.That(float.IsNaN(fixture.Bone.localScale.x), Is.True);
                LogAssert.Expect(LogType.Assert, "Invalid AABB result"); renderer.BakeMesh(baked);
                Assert.That(baked.vertices.Skip(3).All(p => float.IsNaN(p.x) && float.IsNaN(p.y) && float.IsNaN(p.z)), Is.True,
                    "Native zero-times-NaN contamination is not evidence of a retained nonzero influence.");
                return 0;
            });
        }

        [TestCase(BuildTarget.Android)] [TestCase(BuildTarget.iOS)] [TestCase(BuildTarget.StandaloneOSX)]
        public void HiddenNonFiniteGeometry_OtherTargetsKeepUnknown(BuildTarget target)
        {
            var fixture = NonFiniteFixture();
            string directory = NonFiniteShader("Affine", out var material);
            try
            {
                renderer.sharedMaterial = material;
                var proof = new ShaderPropertyUsage().MeshInputs.PositionInputs(material, renderer,
                    NonFiniteIndex(Controller(fixture.Clip)), target, true);
                Assert.That(proof.NonFinitePositionIsHidden, Is.False);
                Assert.That(proof.Issue ?? proof.NonFiniteIssue, Is.Not.Null);
            }
            finally { AssetDatabase.DeleteAsset(directory); }
        }

        [UnityTest] public IEnumerator HiddenNonFiniteGeometry_NativeDrawMatchesIndexRemoval() => NonFiniteDraw(false);
        [UnityTest] public IEnumerator HiddenNonFiniteGeometry_NativeShaderCanRecoverVisibleGeometry() => NonFiniteDraw(true);
        [UnityTest] public IEnumerator HiddenNonFiniteGeometry_NativeMixedNaNRecoveryRemainsVisible() => NonFiniteDraw(true, "MixedNaNAndFinite");

        private IEnumerator NonFiniteDraw(bool recovery, string recoveryMode = "UvRecovery")
        {
            OcclusionGpuRequired();
            var fixture = NonFiniteFixture();
            if (recovery) fixture.Mesh.triangles = new[] { 0, 1, 2 };
            string directory = NonFiniteShader(recovery ? recoveryMode : "Affine", out var material);
            renderer.sharedMaterial = material;
            var camera = PolicyChild("Camera").AddComponent<Camera>(); camera.scene = scene; camera.enabled = false;
            camera.renderingPath = RenderingPath.Forward; camera.orthographic = true; camera.orthographicSize = 1;
            camera.transform.position = new Vector3(0, 0, -3); camera.transform.rotation = Quaternion.identity;
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black;
            var target = RenderTexture.GetTemporary(96, 96, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var graph = PlayableGraph.Create("Nonfinite skinning counterexample");
            try
            {
                var controller = Controller(fixture.Clip);
                var index = NonFiniteIndex(controller);
                var animator = root.AddComponent<Animator>(); animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                var playable = AnimatorControllerPlayable.Create(graph, controller);
                AnimationPlayableOutput.Create(graph, "Avatar", animator).SetSourcePlayable(playable); graph.Play();
                graph.Evaluate(1f / 60); yield return null;
                var before = DrawAssembly(camera, target);
                Assert.That(before.Count(p => p.r > 20), Is.GreaterThan(0), "The control triangle must draw.");
                int removed = RemoveNonFinite(index, true, new ShaderPropertyUsage(), out string message);
                Assert.That(removed, Is.EqualTo(recovery ? 0 : 1), message);
                graph.Evaluate(1f / 60); yield return null;
                var after = DrawAssembly(camera, target);
                Assert.That(after, Is.EqualTo(before), "Compiled shader behavior and native skinning must agree with index removal.");
            }
            finally
            {
                graph.Destroy(); RenderTexture.ReleaseTemporary(target); AssetDatabase.DeleteAsset(directory);
            }
        }
    }
}
