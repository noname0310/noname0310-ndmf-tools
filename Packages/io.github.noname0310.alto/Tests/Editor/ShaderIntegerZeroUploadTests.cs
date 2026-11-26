using System;
using System.IO;
using Noname.AvatarTools.Analysis;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        [Test]
        public void NativeSemanticIntegerZeroUploadPreservesScalarBitsAndTypedPropertyOwnership()
        {
            RequireD3DAnalysis();
            string directory = "Assets/__ALTOIntegerZeroUpload_" + Guid.NewGuid().ToString("N"); Directory.CreateDirectory(directory);
            RenderTexture target = null; Texture2D pixels = null; var previous = RenderTexture.active;
            try
            {
                string path = directory + "/Probe.shader";
                File.WriteAllText(path, "Shader \"Hidden/ALTO/IntegerZeroUpload/" + Guid.NewGuid().ToString("N") + "\" { Properties {" +
                    "_Int(\"Int\",Float)=0 _Uint(\"Uint\",Range(0,3))=0 _Bool(\"Bool\",Float)=0 _Integer(\"Integer\",Integer)=1 _Color(\"Color\",Color)=(0,0,0,0)" +
                    "} SubShader { Pass { Cull Off ZTest Always ZWrite Off\nHLSLPROGRAM\n#pragma target 4.0\n#pragma vertex vert\n#pragma fragment frag\n" +
                    "int _Int;uint _Uint;bool _Bool;int _Integer;float4 _Color;\n" +
                    "float4 vert(uint id:SV_VertexID):SV_POSITION{float2 p=float2((id<<1)&2,id&2);return float4(p*2-1,0,1);}\n" +
                    "float4 frag():SV_Target{return float4(_Int==0?1:0,_Uint==0?1:0,_Bool?0:1,1);}\nENDHLSL\n} } Fallback Off }");
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                var shader = AssetDatabase.LoadAssetAtPath<Shader>(path); Assert.That(ShaderUtil.ShaderHasError(shader), Is.False);
                var material = Track(new Material(shader)); renderer.sharedMaterial = material;
                material.SetFloat("_Int", 0); material.SetFloat("_Uint", 0); material.SetFloat("_Bool", 0); material.SetInteger("_Integer", 1);
                var constants = FrameIndex().MaterialInputs.ConstantUniforms(renderer, material);
                foreach (string name in new[] { "_Int", "_Uint", "_Bool" }) Assert.That(BitConverter.SingleToInt32Bits(constants[name]), Is.Zero);
                Assert.That(constants.ContainsKey("_Integer"), Is.False, "A nonzero integer property must use GetInteger and remain outside the float uniform contract.");
                Assert.That(constants.ContainsKey("_Color"), Is.False, "A color's GetFloat fallback cannot create a scalar-zero premise.");
                target = new RenderTexture(1, 1, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear); target.Create();
                pixels = new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true);
                RenderTexture.active = target; GL.Clear(false, true, Color.clear);
                Assert.That(material.SetPass(0), Is.True); Graphics.DrawProceduralNow(MeshTopology.Triangles, 3);
                pixels.ReadPixels(new Rect(0, 0, 1, 1), 0, 0); pixels.Apply();
                var pixel = pixels.GetPixel(0, 0);
                Assert.That(pixel, Is.EqualTo(Color.white), "The actual native int/uint/bool uniforms must all observe the positive-zero uploads.");
            }
            finally
            {
                RenderTexture.active = previous;
                if (pixels != null) UnityEngine.Object.DestroyImmediate(pixels);
                if (target != null) UnityEngine.Object.DestroyImmediate(target);
                AssetDatabase.DeleteAsset(directory);
            }
        }
    }
}
