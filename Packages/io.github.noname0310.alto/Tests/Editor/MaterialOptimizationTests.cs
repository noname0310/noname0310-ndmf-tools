using System.Linq;
using System;
using nadena.dev.ndmf;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.Materials;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private static void SavedFloat(Material material, string name, float value)
        {
            using var serialized = new SerializedObject(material);
            var array = serialized.FindProperty("m_SavedProperties.m_Floats");
            var entry = array.GetArrayElementAtIndex(array.arraySize++);
            entry.FindPropertyRelative("first").stringValue = name; entry.FindPropertyRelative("second").floatValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static string[] SavedFloats(Material material)
        {
            using var serialized = new SerializedObject(material);
            var array = serialized.FindProperty("m_SavedProperties.m_Floats");
            return Enumerable.Range(0, array.arraySize).Select(i => array.GetArrayElementAtIndex(i).FindPropertyRelative("first").stringValue).ToArray();
        }

        [Test]
        public void MaterialCleaningKeepsHiddenShaderAndFallbackInputsWithoutMutatingTheSource()
        {
            var source = OutputMaterial(); SavedFloat(source, "_ObsoleteValue", 8); SavedFloat(source, "_Metallic", 0.7f);
            SavedFloat(source, "_HiddenUniform", 3); SavedFloat(source, "_MacroOnly", 4); SavedFloat(source, "_AnotherObsoleteValue", 9);
            string before = EditorJsonUtility.ToJson(source);
            var unused = SavedMaterialProperties.Unused(source, new ShaderPropertyUsage(), new VrcFallbackPropertyContract());
            Assert.That(unused.Count, Is.EqualTo(2));
            var cleaned = Track(SavedMaterialProperties.CleanCopy(source, unused));
            Assert.That(SavedFloats(cleaned), Does.Not.Contain("_ObsoleteValue").And.Not.Contain("_AnotherObsoleteValue"));
            foreach (string name in new[] { "_Metallic", "_HiddenUniform", "_MacroOnly" }) Assert.That(SavedFloats(cleaned), Does.Contain(name));
            Assert.That(EditorJsonUtility.ToJson(source), Is.EqualTo(before));
        }

        [TestCase("Keyword")] [TestCase("Queue")] [TestCase("Tag")] [TestCase("Property")]
        public void EquivalentMaterialsRequireEverySerializedRenderingSetting(string difference)
        {
            var first = OutputMaterial(); var second = Track(UnityEngine.Object.Instantiate(first)); second.name = "Different editor label";
            Assert.That(SavedMaterialProperties.Equivalent(first, second), Is.True);
            if (difference == "Keyword") second.EnableKeyword("TEST_FEATURE");
            if (difference == "Queue") second.renderQueue = 2450;
            if (difference == "Tag") second.SetOverrideTag("VRCFallback", "Hidden");
            if (difference == "Property") second.SetColor("_Color", Color.red);
            Assert.That(SavedMaterialProperties.Equivalent(first, second), Is.False);
        }

        [Test]
        public void MaterialVariantsRetainTheirInheritanceContract()
        {
            var parent = OutputMaterial(); SavedFloat(parent, "_ObsoleteValue", 3);
            var variant = OutputMaterial(); variant.parent = parent;
            var before = EditorJsonUtility.ToJson(variant);
            Assert.That(SavedMaterialProperties.Unused(variant, new ShaderPropertyUsage(), new VrcFallbackPropertyContract()), Is.Empty);
            Assert.That(SavedMaterialProperties.Equivalent(parent, variant), Is.False);
            Assert.That(EditorJsonUtility.ToJson(variant), Is.EqualTo(before));
        }

        [Test]
        public void MaterialOutputPruningKeepsParametersUsedBySafetyFallbacks()
        {
            renderer.sharedMaterial = OutputMaterial(); var clip = Clip();
            AnimationUtility.SetEditorCurve(clip, MaterialBinding("_Metallic"), AnimationCurve.Constant(0, 1, 0.5f));
            AnimationUtility.SetEditorCurve(clip, MaterialBinding("_ObsoleteValue"), AnimationCurve.Constant(0, 1, 1));
            var result = PruneMaterialOutputs(Controller(clip));
            Assert.That(result.Report.RemovedCurves, Is.EqualTo(1));
            var remaining = result.Controller.AllReachableNodes().OfType<nadena.dev.ndmf.animator.VirtualClip>()
                .SelectMany(c => c.GetFloatCurveBindings()).Select(b => b.propertyName).ToArray();
            Assert.That(remaining, Does.Contain("material._Metallic")); Assert.That(remaining, Does.Not.Contain("material._ObsoleteValue"));
        }

        private void MaterialDescriptor(UnityEditor.Animations.AnimatorController controller = null)
        {
            var descriptor = root.AddComponent<VRCAvatarDescriptor>(); descriptor.customizeAnimationLayers = true;
            descriptor.baseAnimationLayers = controller == null ? Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>() :
                new[] { new VRCAvatarDescriptor.CustomAnimLayer { type = VRCAvatarDescriptor.AnimLayerType.FX, animatorController = controller, isDefault = false } };
            descriptor.specialAnimationLayers = Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>();
        }

        [TestCase("None")] [TestCase("Preserve")] [TestCase("OutsideScope")] [TestCase("ExternalReader")]
        public void MaterialBuildPassKeepsProtectedInterfacesAndSourceAssets(string protection)
        {
            var first = PolicyChild("First").AddComponent<SkinnedMeshRenderer>(); var second = PolicyChild("Second").AddComponent<SkinnedMeshRenderer>();
            var a = OutputMaterial(); SavedFloat(a, "_ObsoleteValue", 3);
            var b = Track(UnityEngine.Object.Instantiate(a)); b.name = "Second material";
            first.sharedMaterial = a; second.sharedMaterial = b;
            string beforeA = EditorJsonUtility.ToJson(a), beforeB = EditorJsonUtility.ToJson(b);
            MaterialDescriptor(); var component = root.AddComponent<ALTOTraceAndOptimizeMaterials>();
            if (protection == "Preserve") component.PreserveMaterials.Add(a);
            if (protection == "OutsideScope") component.ScopeRoot = second.transform;
            if (protection == "ExternalReader") PolicyChild("Reader").AddComponent<DependencyAuthoringFixture>().Material = a;
            var context = new BuildContext(root, null); MaterialOptimizationPass.Capture(context); MaterialOptimizationPass.Prepare(context); MaterialOptimizationPass.Apply(context);
            foreach (var material in new[] { first.sharedMaterial, second.sharedMaterial }.Distinct()) if (material != a && material != b) Track(material);
            Assert.That(first.sharedMaterial == a, Is.EqualTo(protection != "None"));
            Assert.That(SavedFloats(second.sharedMaterial), Does.Not.Contain("_ObsoleteValue"));
            Assert.That(first.sharedMaterial == second.sharedMaterial, Is.EqualTo(protection == "None"));
            Assert.That(EditorJsonUtility.ToJson(a), Is.EqualTo(beforeA)); Assert.That(EditorJsonUtility.ToJson(b), Is.EqualTo(beforeB));
            Assert.That(component == null, Is.True);
        }

        [Test]
        public void MaterialBuildPassRewritesSafeSwapClipsButKeepsTheOriginalClip()
        {
            var part = PolicyChild("Part").AddComponent<SkinnedMeshRenderer>(); var a = OutputMaterial(); var b = Track(UnityEngine.Object.Instantiate(a));
            part.sharedMaterial = a; var clip = Clip();
            var binding = EditorCurveBinding.PPtrCurve("Part", typeof(SkinnedMeshRenderer), "m_Materials.Array.data[0]");
            AnimationUtility.SetObjectReferenceCurve(clip, binding, new[] { new ObjectReferenceKeyframe { time = 0, value = b } });
            MaterialDescriptor(Controller(clip)); root.AddComponent<ALTOTraceAndOptimizeMaterials>();
            var context = new BuildContext(root, null); MaterialOptimizationPass.Capture(context); MaterialOptimizationPass.Prepare(context); MaterialOptimizationPass.Apply(context);
            var finalController = root.GetComponent<VRCAvatarDescriptor>().baseAnimationLayers.Single().animatorController;
            var finalClip = finalController.animationClips.Single(c => AnimationUtility.GetObjectReferenceCurveBindings(c).Length > 0);
            Assert.That(AnimationUtility.GetObjectReferenceCurve(finalClip, binding).Single().value, Is.SameAs(part.sharedMaterial));
            Assert.That(AnimationUtility.GetObjectReferenceCurve(clip, binding).Single().value, Is.SameAs(b));
        }

        [Test]
        public void MaterialBuildPassRetainsExternalMergeOutputsContainingProtectedInputs()
        {
            var first = PolicyChild("First").AddComponent<SkinnedMeshRenderer>(); var second = PolicyChild("Second").AddComponent<SkinnedMeshRenderer>();
            var state = new MaterialOptimizationState { Component = root.AddComponent<ALTOTraceAndOptimizeMaterials>() };
            state.Inputs.Add((first, false)); state.Inputs.Add((second, true));
            state.Component.BuildInputRenderers = new Renderer[] { first, first }; UnityEngine.Object.DestroyImmediate(second);
            Assert.That(MaterialOptimizationPass.ProtectedInput(state, first), Is.True);
        }
    }
}
