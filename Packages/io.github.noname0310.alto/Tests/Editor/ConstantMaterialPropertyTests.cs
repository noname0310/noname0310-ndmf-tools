using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.AnimatorOptimization;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using VRC.SDK3.Avatars.Components;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private AnimationClip ConstantMaterialPropertyClip(string property, string channels, Vector4 value)
        {
            var clip = Clip();
            for (int c = 0; c < Math.Max(1, channels.Length); c++)
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(SkinnedMeshRenderer),
                    "material." + property + (channels.Length == 0 ? "" : "." + channels[c])), AnimationCurve.Constant(0, .3f, value[c]));
            return clip;
        }

        [TestCase("_Scalar", "", 0)] [TestCase("_Vector", "xyzw", 1)]
        [TestCase("_Color", "rgba", 2)] [TestCase("_MainTex_ST", "xyzw", 3)]
        public void ConstantMaterialProperties_WholeGroupMatchesNativePixels(string property, string channels, int output)
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) Assert.Ignore("A graphics device is required.");
            Mesh(); var material = Track(new Material(Shader.Find("Hidden/Noname/ALTO/ConstantMaterialOutputs")));
            material.SetFloat("_Output", output); renderer.sharedMaterial = material;
            string original = EditorJsonUtility.ToJson(material);
            var source = Controller(ConstantMaterialPropertyClip(property, channels, new Vector4(.2f, .4f, .6f, .8f)));
            source.layers[0].stateMachine.defaultState.writeDefaultValues = false;
            var camera = PolicyChild("Camera").AddComponent<Camera>(); camera.scene = scene; camera.enabled = false;
            camera.transform.localPosition = new Vector3(.5f, .5f, -4); camera.orthographic = true; camera.orthographicSize = .65f;
            camera.renderingPath = RenderingPath.Forward; camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black;
            var target = RenderTexture.GetTemporary(48, 48, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            try
            {
                var before = SampleMaterialFrames(source, camera, target);
                Assert.That(before[0].Max(c => c.r), Is.GreaterThan(3), "The fixture must render the material.");
                renderer.SetPropertyBlock(null);
                var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
                var report = new AnimatorOptimizationReport();
                MaterialConstants.Optimize(root, new Dictionary<object, VirtualAnimatorController> { [VRCAvatarDescriptor.AnimLayerType.FX] = controller },
                    null, m => Track(m), report, allowInitialChanges: _ => true);
                Assert.That(report.MaterialCurves, Is.EqualTo(Math.Max(1, channels.Length)));
                var after = SampleMaterialFrames(CommitAnimator(controller, root), camera, target);
                for (int frame = 0; frame < before.Length; frame++)
                    Assert.That(before[frame].Zip(after[frame], (a, b) => Math.Max(Math.Max(Math.Abs(a.r - b.r), Math.Abs(a.g - b.g)),
                        Math.Abs(a.b - b.b))).Max(), Is.LessThanOrEqualTo(1), property + " frame " + frame);
                Assert.That(EditorJsonUtility.ToJson(material), Is.EqualTo(original));
            }
            finally { RenderTexture.ReleaseTemporary(target); }
        }

        [TestCase("PartialChannels")] [TestCase("DifferentFootprints")] [TestCase("NoPermission")]
        [TestCase("PreservedMaterial")] [TestCase("PropertyBlock")]
        public void ConstantMaterialProperties_RetainsUnprovedGroups(string reason)
        {
            var material = Track(new Material(Shader.Find("Hidden/Noname/ALTO/ConstantMaterialOutputs"))); renderer.sharedMaterial = material;
            var clip = ConstantMaterialPropertyClip("_Vector", "xyzw", new Vector4(.2f, .4f, .6f, .8f));
            var other = Clip();
            var binding = EditorCurveBinding.FloatCurve("", typeof(SkinnedMeshRenderer), "material._Vector.w");
            if (reason == "PartialChannels" || reason == "DifferentFootprints")
            {
                if (reason == "DifferentFootprints") AnimationUtility.SetEditorCurve(other, binding, AnimationUtility.GetEditorCurve(clip, binding));
                AnimationUtility.SetEditorCurve(clip, binding, null);
            }
            if (reason == "PropertyBlock")
            { var block = new MaterialPropertyBlock(); block.SetVector("_Vector", Vector4.one); renderer.SetPropertyBlock(block); }
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(clip, other));
            var report = new AnimatorOptimizationReport();
            MaterialConstants.Optimize(root, new Dictionary<object, VirtualAnimatorController> { [VRCAvatarDescriptor.AnimLayerType.FX] = controller },
                null, m => Track(m), report, allowInitialChanges: _ => reason != "NoPermission", preservedMaterial: _ => reason == "PreservedMaterial");
            Assert.That(report.MaterialCurves, Is.Zero); Assert.That(renderer.sharedMaterial, Is.SameAs(material));
        }
    }
}
