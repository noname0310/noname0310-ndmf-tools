using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.AnimatorOptimization;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private static float SignedZero(bool negative) => BitConverter.Int32BitsToSingle(negative ? int.MinValue : 0);

        private int[] SampleMaterialZeroBits(AnimatorController source)
        {
            renderer.SetPropertyBlock(null); var samples = new List<int>();
            try
            {
                SampleFrozen(source, (playable, frame) => { }, 24, () =>
                {
                    var block = new MaterialPropertyBlock(); renderer.GetPropertyBlock(block);
                    float value = block.HasProperty("_Scalar") ? block.GetFloat("_Scalar") : renderer.sharedMaterial.GetFloat("_Scalar");
                    samples.Add(BitConverter.SingleToInt32Bits(value)); return 0;
                });
                return samples.ToArray();
            }
            finally { renderer.SetPropertyBlock(null); }
        }

        [TestCase(false, false, false, false)] [TestCase(false, false, false, true)]
        [TestCase(false, true, false, false)] [TestCase(false, true, false, true)]
        [TestCase(true, false, false, false)] [TestCase(true, false, false, true)]
        [TestCase(true, true, false, false)] [TestCase(true, true, false, true)]
        [TestCase(false, true, true, false)] [TestCase(false, true, true, true)]
        [TestCase(true, false, true, false)] [TestCase(true, false, true, true)]
        public void ConstantMaterialSignedZero_NativePropertyBlockBitsSurviveBaking(bool initialNegative,
            bool outputNegative, bool mixedKeys, bool writeDefaults)
        {
            Mesh("Timing");
            var material = Track(new Material(Shader.Find("Hidden/Noname/ALTO/ConstantMaterialOutputs")));
            material.SetFloat("_Scalar", SignedZero(initialNegative)); renderer.sharedMaterial = material;
            var binding = EditorCurveBinding.FloatCurve("", typeof(SkinnedMeshRenderer), "material._Scalar");
            var clip = Clip();
            var curve = mixedKeys ? new AnimationCurve(
                new Keyframe(0, SignedZero(outputNegative), float.PositiveInfinity, float.PositiveInfinity),
                new Keyframe(.1f, SignedZero(!outputNegative), float.PositiveInfinity, float.PositiveInfinity),
                new Keyframe(.3f, SignedZero(!outputNegative), float.PositiveInfinity, float.PositiveInfinity)) :
                AnimationCurve.Constant(0, .3f, SignedZero(outputNegative));
            AnimationUtility.SetEditorCurve(clip, binding, curve);
            var source = Controller(clip); source.layers[0].stateMachine.defaultState.writeDefaultValues = writeDefaults;
            var expected = SampleMaterialZeroBits(source);
            var index = ConstantOutputIndex(source, out var controller);
            bool proved = index.ConstantOutputs.TryProve(renderer, binding.propertyName,
                ConstantOutputValue.Scalar(material.GetFloat("_Scalar")), out var proof, out _);
            TestContext.WriteLine("Authored bits=" + BitConverter.SingleToInt32Bits(material.GetFloat("_Scalar")) +
                "; key bits=" + string.Join(",", AnimationUtility.GetEditorCurve(clip, binding).keys.Select(k => BitConverter.SingleToInt32Bits(k.value))) +
                "; native bits=" + string.Join(",", expected.Distinct()) + "; proof=" + proved +
                (proved ? "; proof bits=" + BitConverter.SingleToInt32Bits(proof.Value.Number) : ""));
            var report = new AnimatorOptimizationReport();
            MaterialConstants.Optimize(root, new Dictionary<object, nadena.dev.ndmf.animator.VirtualAnimatorController>
                { [VRCAvatarDescriptor.AnimLayerType.FX] = controller }, null, copy => Track(copy), report, allowInitialChanges: _ => true);
            var actual = SampleMaterialZeroBits(CommitAnimator(controller, root));
            Assert.That(actual, Is.EqualTo(expected), "Deleting a material curve must preserve the shader-visible IEEE-754 bits.");
        }

        [TestCase(false)] [TestCase(true)]
        public void ConstantMaterialSignedZero_ExplicitProofMatchesNativeBits(bool writeDefaults)
        {
            Mesh("Timing");
            var material = Track(new Material(Shader.Find("Hidden/Noname/ALTO/ConstantMaterialOutputs")));
            material.SetFloat("_Scalar", .2f); renderer.sharedMaterial = material;
            var binding = EditorCurveBinding.FloatCurve("", typeof(SkinnedMeshRenderer), "material._Scalar");
            var clip = StaticCurve(binding, SignedZero(true)); var source = Controller(clip);
            source.layers[0].stateMachine.defaultState.writeDefaultValues = writeDefaults;
            var native = SampleMaterialZeroBits(source); var index = ConstantOutputIndex(source, out _);
            if (!index.ConstantOutputs.TryProve(renderer, binding.propertyName, ConstantOutputValue.Scalar(.2f), out var proof, out _)) return;
            TestContext.WriteLine("Native bits=" + string.Join(",", native.Distinct()) + "; proof bits=" + BitConverter.SingleToInt32Bits(proof.Value.Number));
            Assert.That(native, Is.All.EqualTo(BitConverter.SingleToInt32Bits(proof.Value.Number)),
                "A shader constant may only use the actual native animation output bit pattern.");
        }

        [TestCase(false, true)] [TestCase(true, true)]
        [TestCase(false, false)] [TestCase(true, false)]
        public void ConstantMaterialSignedZero_BakedAssetRetainsScalarAndVectorBitsAfterReload(bool negative, bool allowTextSerialization)
        {
            string serializedPath = Path.Combine("Library", "ALTO_MaterialZero_" + Guid.NewGuid().ToString("N") + ".asset");
            try
            {
                Mesh("Timing"); float value = SignedZero(negative);
                var original = Track(new Material(Shader.Find("Hidden/Noname/ALTO/ConstantMaterialOutputs")));
                original.SetFloat("_Scalar", .2f); original.SetVector("_Vector", Vector4.one); renderer.sharedMaterial = original;
                var clip = Clip();
                foreach (string property in new[] { "_Scalar", "_Vector.x", "_Vector.y", "_Vector.z", "_Vector.w" })
                    AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(SkinnedMeshRenderer), "material." + property),
                        AnimationCurve.Constant(0, .3f, value));
                ConstantOutputIndex(Controller(clip), out var controller);
                var report = new AnimatorOptimizationReport();
                Material baked = null;
                MaterialConstants.Optimize(root, new Dictionary<object, nadena.dev.ndmf.animator.VirtualAnimatorController>
                    { [VRCAvatarDescriptor.AnimLayerType.FX] = controller }, null, asset => baked = (Material)Track(asset),
                    report, allowInitialChanges: _ => true);
                Assert.That(report.MaterialCurves, Is.EqualTo(5)); Assert.That(baked, Is.Not.Null);
                // Exercise Unity's native file serialization in isolation. Changing
                // EditorSettings.serializationMode would convert unrelated assets.
                UnityEditorInternal.InternalEditorUtility.SaveToSerializedFileAndForget(
                    new UnityEngine.Object[] { baked }, serializedPath, allowTextSerialization);
                var loaded = UnityEditorInternal.InternalEditorUtility.LoadSerializedFileAndForget(serializedPath);
                foreach (var asset in loaded) Track(asset);
                var reloaded = loaded.OfType<Material>().Single();
                Assert.That(reloaded, Is.Not.Null.And.Not.SameAs(baked));
                int expected = BitConverter.SingleToInt32Bits(value);
                Assert.That(BitConverter.SingleToInt32Bits(reloaded.GetFloat("_Scalar")), Is.EqualTo(expected));
                var vector = reloaded.GetVector("_Vector");
                for (int channel = 0; channel < 4; channel++)
                    Assert.That(BitConverter.SingleToInt32Bits(vector[channel]), Is.EqualTo(expected));
            }
            finally { if (File.Exists(serializedPath)) File.Delete(serializedPath); }
        }
    }
}
