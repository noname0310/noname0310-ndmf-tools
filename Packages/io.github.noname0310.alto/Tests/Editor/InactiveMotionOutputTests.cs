using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.ObjectOptimization;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        [TestCase(false, false, false)] [TestCase(false, false, true)]
        [TestCase(false, true, false)] [TestCase(false, true, true)]
        [TestCase(true, false, false)] [TestCase(true, false, true)]
        [TestCase(true, true, false)] [TestCase(true, true, true)]
        public void ZeroWeightVisibilityTracksPreserveNativeDefaultsAndDynamicSiblings(bool rendererProperty, bool initial, bool writeDefaults)
        {
            Mesh("a"); var settings = ObjectSettings(); var obj = PolicyChild("Inactive branch");
            var target = obj.AddComponent<MeshRenderer>();
            if (rendererProperty) target.enabled = initial; else obj.SetActive(initial);
            var binding = EditorCurveBinding.FloatCurve(obj.name, rendererProperty ? typeof(MeshRenderer) : typeof(GameObject),
                rendererProperty ? "m_Enabled" : "m_IsActive");
            var inactive = Clip(); AnimationUtility.SetEditorCurve(inactive, binding, AnimationCurve.Linear(0, 0, 0.7f, 1));
            var source = TreeController(("Zero", inactive), ("Dynamic", StaticCurve(Binding("a"), 80)));
            source.layers[0].stateMachine.defaultState.writeDefaultValues = writeDefaults;
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var before = SampleFrozen(source, (p, i) => { p.SetFloat("Zero", 0); p.SetFloat("Dynamic", i / 3f); }, 4,
                () => renderer.GetBlendShapeWeight(0) + ((rendererProperty ? target.enabled : obj.activeSelf) ? 1000 : 0));
            if (rendererProperty) target.enabled = initial; else obj.SetActive(initial);
            OptimizeObjectCurves(controller, settings);
            if (!initial) Assert.That(controller.AllReachableNodes().OfType<VirtualClip>().Any(c => c.GetFloatCurve(binding) != null), Is.False);
            Assert.That(controller.AllReachableNodes().OfType<VirtualClip>().Max(MotionAnalysis.EndTime), Is.EqualTo(0.7f));
            var built = CommitAnimator(controller, root);
            if (rendererProperty) target.enabled = initial; else obj.SetActive(initial);
            var after = SampleFrozen(built, (p, i) => { p.SetFloat("Zero", 0); p.SetFloat("Dynamic", i / 3f); }, 4,
                () => renderer.GetBlendShapeWeight(0) + ((rendererProperty ? target.enabled : obj.activeSelf) ? 1000 : 0));
            Assert.That(before.Last() - before.First(), Is.GreaterThan(70));
            Assert.That(after, Is.EqualTo(before).Within(0.001f));
            Assert.That(AnimationUtility.GetEditorCurve(inactive, binding), Is.Not.Null);
        }

        [TestCase(false)] [TestCase(true)]
        public void ZeroWeightCompleteTransformTracksPreserveNativeOutput(bool writeDefaults)
        {
            Mesh("a"); var settings = ObjectSettings(); var obj = PolicyChild("Unused offset");
            obj.transform.localPosition = new Vector3(0.1f, -0.2f, 0.3f);
            var initial = obj.transform.localPosition;
            var inactive = Clip();
            foreach (char axis in "xyz") AnimationUtility.SetEditorCurve(inactive,
                EditorCurveBinding.FloatCurve(obj.name, typeof(Transform), "m_LocalPosition." + axis), AnimationCurve.Linear(0, 10, 1, 20));
            var source = TreeController(("Zero", inactive), ("Dynamic", StaticCurve(Binding("a"), 80)));
            source.layers[0].stateMachine.defaultState.writeDefaultValues = writeDefaults;
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var before = SampleFrozen(source, (p, i) => { p.SetFloat("Zero", 0); p.SetFloat("Dynamic", i / 3f); }, 4,
                () => obj.transform.localPosition.x + obj.transform.localPosition.y * 3 + obj.transform.localPosition.z * 7);
            obj.transform.localPosition = initial;
            OptimizeObjectCurves(controller, settings);
            var built = CommitAnimator(controller, root); obj.transform.localPosition = initial;
            var after = SampleFrozen(built, (p, i) => { p.SetFloat("Zero", 0); p.SetFloat("Dynamic", i / 3f); }, 4,
                () => obj.transform.localPosition.x + obj.transform.localPosition.y * 3 + obj.transform.localPosition.z * 7);
            Assert.That(after, Is.EqualTo(before).Within(0.00001f));
        }

        [TestCase("ExternalInput")] [TestCase("OtherController")] [TestCase("Normalized")]
        public void ZeroWeightProofDoesNotDiscardOtherPossibleWriters(string reason)
        {
            var settings = ObjectSettings(); var obj = PolicyChild("Visibility"); obj.SetActive(false);
            var binding = EditorCurveBinding.FloatCurve(obj.name, typeof(GameObject), "m_IsActive");
            var source = TreeController(("Zero", StaticCurve(binding, 1)));
            if (reason == "ExternalInput")
            {
                var parameters = Track(ScriptableObject.CreateInstance<VRCExpressionParameters>());
                parameters.parameters = new[] { new VRCExpressionParameters.Parameter { name = "Zero", valueType = VRCExpressionParameters.ValueType.Float } };
                root.GetComponent<VRCAvatarDescriptor>().expressionParameters = parameters;
            }
            if (reason == "Normalized") using (var serialized = new SerializedObject(source.layers[0].stateMachine.defaultState.motion))
            { serialized.FindProperty("m_NormalizedBlendValues").boolValue = true; serialized.ApplyModifiedPropertiesWithoutUndo(); }
            var clone = new CloneContext(GenericPlatformAnimatorBindings.Instance); var controller = clone.Clone(source);
            var entries = new Dictionary<object, VirtualAnimatorController> { [VRCAvatarDescriptor.AnimLayerType.FX] = controller };
            if (reason == "OtherController") entries.Add(VRCAvatarDescriptor.AnimLayerType.Gesture, clone.Clone(Controller(StaticCurve(binding, 1))));
            InvariantObjectCurves.Apply(root, entries, null, settings, AnimatorReachability.Analyze(root, entries.Values), new List<string>());
            Assert.That(controller.AllReachableNodes().OfType<VirtualClip>().Any(c => c.GetFloatCurve(binding) != null), Is.True);
        }

        [TestCase("LOD")] [TestCase("ParticleStop")] [TestCase("TrailAutodestruct")]
        public void LosslessVisibilityAnalysisRetainsNativeWriters(string reason)
        {
            var settings = ObjectSettings(); var obj = PolicyChild("Native writer");
            EditorCurveBinding binding;
            if (reason == "LOD")
            {
                var target = obj.AddComponent<MeshRenderer>(); target.enabled = false;
                var lod = PolicyChild("LOD controller").AddComponent<LODGroup>(); lod.SetLODs(new[] { new LOD(0.1f, new Renderer[] { target }) });
                binding = EditorCurveBinding.FloatCurve(obj.name, typeof(MeshRenderer), "m_Enabled");
            }
            else
            {
                if (reason == "ParticleStop") { var particles = obj.AddComponent<ParticleSystem>(); var main = particles.main; main.stopAction = ParticleSystemStopAction.Disable; }
                else obj.AddComponent<TrailRenderer>().autodestruct = true;
                binding = EditorCurveBinding.FloatCurve(obj.name, typeof(GameObject), "m_IsActive");
            }
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(StaticCurve(binding, reason == "LOD" ? 0 : 1)));
            OptimizeObjectCurves(controller, settings);
            Assert.That(controller.AllReachableNodes().OfType<VirtualClip>().Any(c => c.GetFloatCurve(binding) != null), Is.True);
        }

        [TestCase(false)] [TestCase(true)]
        public void PreservedInputKeepsVisibleNativeOutputWhileInactiveRendererIsCollected(bool canActivate)
        {
            Mesh("a"); var settings = ObjectSettings();
            var parent = PolicyChild("Hidden"); parent.SetActive(false);
            var child = PolicyChild("Output", parent.transform); var hidden = child.AddComponent<SkinnedMeshRenderer>();
            hidden.sharedMesh = renderer.sharedMesh;
            var hiddenBinding = Binding("a", "Hidden/Output");
            var clip = StaticCurve(Binding("a"), 100);
            AnimationUtility.SetEditorCurve(clip, hiddenBinding, AnimationCurve.Constant(0, 1, 75));
            if (canActivate) AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Hidden", typeof(GameObject), "m_IsActive"),
                AnimationCurve.Constant(0, 1, 1));
            var source = TreeController(("Preserved", clip));
            var before = SampleFrozen(source, (p, i) => p.SetFloat("Preserved", i / 3f), 4, () => renderer.GetBlendShapeWeight(0));
            parent.SetActive(false);
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            CollectObjects(settings, controller, new[] { "Preserved" });
            Assert.That(hidden == null, Is.EqualTo(!canActivate));
            Assert.That(child != null && parent != null, Is.EqualTo(canActivate), "Only an observable output keeps the hierarchy.");
            Assert.That(controller.Parameters.ContainsKey("Preserved"), Is.True);
            Assert.That(controller.AllReachableNodes().OfType<VirtualClip>().Any(c => c.GetFloatCurve(hiddenBinding) != null), Is.EqualTo(canActivate));
            var built = CommitAnimator(controller, root); if (parent != null) parent.SetActive(false);
            var after = SampleFrozen(built, (p, i) => p.SetFloat("Preserved", i / 3f), 4, () => renderer.GetBlendShapeWeight(0));
            Assert.That(before.Last() - before.First(), Is.GreaterThan(90));
            Assert.That(after, Is.EqualTo(before).Within(0.001f));
            Assert.That(AnimationUtility.GetEditorCurve(clip, hiddenBinding), Is.Not.Null);
        }
    }
}
