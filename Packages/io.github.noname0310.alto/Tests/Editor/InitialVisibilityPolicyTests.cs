using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.ObjectOptimization;
using Noname.AvatarTools.Occlusion;
using Noname.AvatarTools.ParameterPolicy;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private int BakeVisibility(VirtualAnimatorController controller, ObjectOptimizationState preservation,
            OptimizationBuildScope scope = null, IEnumerable<string> preserved = null,
            IDictionary<object, VirtualAnimatorController> entries = null)
        {
            entries ??= new Dictionary<object, VirtualAnimatorController> { [VRCAvatarDescriptor.AnimLayerType.FX] = controller };
            var index = AvatarAnimationIndex.Create(root, entries, null, preserved);
            return InitialVisibilityPolicy.Bake(root, index, AnimatorReachability.Analyze(root, entries.Values, preserved),
                scope ?? preservation.Scope, preservation, new List<string>());
        }

        [TestCase(false, false)] [TestCase(false, true)]
        [TestCase(true, false)] [TestCase(true, true)]
        public void InitialVisibilityPolicyMatchesNativeOutputAfterEvaluation(bool rendererProperty, bool writeDefaults)
        {
            Mesh("Sample"); var preservation = ObjectSettings(); var obj = PolicyChild("Visibility");
            var target = obj.AddComponent<MeshRenderer>();
            var binding = EditorCurveBinding.FloatCurve(obj.name, rendererProperty ? typeof(MeshRenderer) : typeof(GameObject),
                rendererProperty ? "m_Enabled" : "m_IsActive");
            var source = Controller(StaticCurve(binding, 0), StaticCurve(binding, 0));
            var states = source.layers[0].stateMachine.states;
            foreach (var state in states) state.state.writeDefaultValues = writeDefaults;
            var edge = Edge(states[0].state, states[1].state); edge.hasExitTime = true; edge.exitTime = 1; edge.duration = 0.2f;
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            float duration = controller.AllReachableNodes().OfType<VirtualClip>().Max(MotionAnalysis.EndTime);
            Assert.That(BakeVisibility(controller, preservation), Is.EqualTo(1));
            Assert.That(rendererProperty ? target.enabled : obj.activeSelf, Is.False);
            Assert.That(controller.AllReachableNodes().OfType<VirtualClip>().Any(c => c.GetFloatCurve(binding) != null), Is.False);
            Assert.That(controller.AllReachableNodes().OfType<VirtualClip>().Max(MotionAnalysis.EndTime), Is.EqualTo(duration));
            var built = CommitAnimator(controller, root);
            if (rendererProperty) target.enabled = true; else obj.SetActive(true);
            var before = SampleFrozen(source, (p, i) => { }, 40, () => (rendererProperty ? target.enabled : obj.activeSelf) ? 1 : 0);
            if (rendererProperty) target.enabled = false; else obj.SetActive(false);
            var after = SampleFrozen(built, (p, i) => { }, 40, () => (rendererProperty ? target.enabled : obj.activeSelf) ? 1 : 0);
            Assert.That(after, Is.EqualTo(before));
            Assert.That(before, Is.All.EqualTo(0));
            Assert.That(AnimationUtility.GetEditorCurve((AnimationClip)states[0].state.motion, binding), Is.Not.Null);
            Assert.That(BakeVisibility(controller, preservation), Is.Zero, "A second pass must not change the result.");
        }

        [TestCase("PlayableWeight", false)] [TestCase("LayerWeight", false)]
        [TestCase("OtherLayer", true)] [TestCase("WeightOne", true)]
        [TestCase("MissingStateOutput", false)] [TestCase("AnimatedOutput", false)]
        [TestCase("Mask", false)] [TestCase("Additive", false)]
        [TestCase("PreservedObject", false)] [TestCase("OutsideScope", false)]
        [TestCase("ParticleStop", false)] [TestCase("PartialLayer", false)] [TestCase("FirstLayerMask", false)]
        public void InitialVisibilityPolicyRetainsUnprovenOutputs(string mode, bool baked)
        {
            var preservation = ObjectSettings(); var obj = PolicyChild("Visibility"); obj.SetActive(false);
            var binding = EditorCurveBinding.FloatCurve(obj.name, typeof(GameObject), "m_IsActive");
            var clip = StaticCurve(binding, 1);
            if (mode == "AnimatedOutput") AnimationUtility.SetEditorCurve(clip, binding, AnimationCurve.Linear(0, 0, 1, 1));
            var source = mode == "MissingStateOutput" ? Controller(clip, Clip()) : Controller(clip);
            if (mode == "MissingStateOutput")
            {
                var states = source.layers[0].stateMachine.states;
                var transition = Edge(states[0].state, states[1].state); transition.hasExitTime = true; transition.exitTime = 1;
            }
            if (mode == "Mask")
            {
                var mask = Track(new AvatarMask()); mask.transformCount = 1; mask.SetTransformPath(0, obj.name); mask.SetTransformActive(0, true);
                var layers = source.layers; layers[0].avatarMask = mask; source.layers = layers;
            }
            if (mode == "Additive") { var layers = source.layers; layers[0].blendingMode = AnimatorLayerBlendingMode.Additive; source.layers = layers; }
            if (mode == "PreservedObject") preservation.ProtectedComponents.Add(ObjectRegistry.GetReference(obj.transform));
            if (mode == "ParticleStop") { var particles = obj.AddComponent<ParticleSystem>(); var main = particles.main; main.stopAction = ParticleSystemStopAction.Disable; }
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            if (mode == "PartialLayer" || mode == "FirstLayerMask")
            {
                var clone = new CloneContext(GenericPlatformAnimatorBindings.Instance);
                controller = clone.Clone(Controller(Clip()));
                var layer = clone.Clone(source).Layers.Single(); layer.DefaultWeight = mode == "PartialLayer" ? 0.4f : 1;
                controller.AddLayer(new LayerPriority(0), layer);
                if (mode == "FirstLayerMask")
                {
                    var mask = Track(new AvatarMask()); mask.transformCount = 1;
                    mask.SetTransformPath(0, obj.name); mask.SetTransformActive(0, false);
                    controller.Layers.First().AvatarMask = clone.Clone(mask);
                }
            }
            if (mode == "PlayableWeight")
            {
                var control = Track(ScriptableObject.CreateInstance<VRCPlayableLayerControl>());
                control.layer = VRC_PlayableLayerControl.BlendableLayer.FX; control.goalWeight = 0;
                var state = controller.Layers.First().StateMachine.DefaultState; state.Behaviours = state.Behaviours.Add(control);
            }
            if (mode == "LayerWeight" || mode == "OtherLayer" || mode == "WeightOne")
            {
                var control = Track(ScriptableObject.CreateInstance<VRCAnimatorLayerControl>());
                control.playable = VRC_AnimatorLayerControl.BlendableLayer.FX;
                control.layer = mode == "OtherLayer" ? 99 : controller.Layers.First().VirtualLayerIndex;
                control.goalWeight = mode == "WeightOne" ? 1 : 0;
                var state = controller.Layers.First().StateMachine.DefaultState; state.Behaviours = state.Behaviours.Add(control);
            }
            var scope = mode == "OutsideScope" ? new OptimizationBuildScope { Root = PolicyChild("Other scope").transform } : preservation.Scope;
            Assert.That(BakeVisibility(controller, preservation, scope), Is.EqualTo(baked ? 1 : 0));
            Assert.That(obj.activeSelf, Is.EqualTo(baked));
            Assert.That(controller.AllReachableNodes().OfType<VirtualClip>().Any(c => c.GetFloatCurve(binding) != null), Is.EqualTo(!baked));
        }

        [Test]
        public void InitialVisibilityPolicyPreservesSharedRootsAndLodWriters()
        {
            var preservation = ObjectSettings(); var obj = PolicyChild("Visibility");
            var target = obj.AddComponent<MeshRenderer>(); target.enabled = false;
            var binding = EditorCurveBinding.FloatCurve(obj.name, typeof(MeshRenderer), "m_Enabled");
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(StaticCurve(binding, 1)));
            var lod = PolicyChild("Lod").AddComponent<LODGroup>(); lod.SetLODs(new[] { new LOD(0.1f, new Renderer[] { target }) });
            Assert.That(BakeVisibility(controller, preservation), Is.Zero);
            UnityEngine.Object.DestroyImmediate(lod.gameObject);
            var nested = PolicyChild("Nested").AddComponent<Animator>();
            var other = new GameObject(obj.name); other.transform.SetParent(nested.transform); other.AddComponent<MeshRenderer>().enabled = false;
            var entries = new Dictionary<object, VirtualAnimatorController>
            { [VRCAvatarDescriptor.AnimLayerType.FX] = controller, [nested] = controller };
            Assert.That(BakeVisibility(controller, preservation, entries: entries), Is.Zero);
            Assert.That(target.enabled, Is.False);
        }

        [Test]
        public void InitialVisibilityPolicyHonorsPreservedParametersAndControllerOwnership()
        {
            var preservation = ObjectSettings(); var obj = PolicyChild("Visibility"); obj.SetActive(false);
            var binding = EditorCurveBinding.FloatCurve(obj.name, typeof(GameObject), "m_IsActive");
            var source = TreeController(("Dynamic", StaticCurve(binding, 1)));
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            Assert.That(BakeVisibility(controller, preservation, preserved: new[] { "Dynamic" }), Is.Zero);
            controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(StaticCurve(binding, 1)));
            var entries = new Dictionary<object, VirtualAnimatorController> { [VRCAvatarDescriptor.AnimLayerType.Gesture] = controller };
            Assert.That(BakeVisibility(controller, preservation, entries: entries), Is.Zero);
            Assert.That(obj.activeSelf, Is.False);
        }

        [TestCase(false)] [TestCase(true)]
        public void InitialVisibilityPolicyResolvesCrossPlayablePhysicalLayerReferences(bool controlsWriter)
        {
            var preservation = ObjectSettings(); var obj = PolicyChild("Visibility"); obj.SetActive(false);
            var binding = EditorCurveBinding.FloatCurve(obj.name, typeof(GameObject), "m_IsActive");
            var clone = new CloneContext(GenericPlatformAnimatorBindings.Instance);
            var fx = clone.Clone(Controller(Clip()));
            var writer = clone.Clone(Controller(StaticCurve(binding, 1))).Layers.Single(); writer.DefaultWeight = 1;
            fx.AddLayer(new LayerPriority(0), writer);
            var action = clone.Clone(Controller(Clip()));
            var control = Track(ScriptableObject.CreateInstance<VRCAnimatorLayerControl>());
            control.playable = VRC_AnimatorLayerControl.BlendableLayer.FX; control.layer = controlsWriter ? 1 : 0; control.goalWeight = 0;
            var state = action.Layers.First().StateMachine.DefaultState; state.Behaviours = state.Behaviours.Add(control);
            var entries = new Dictionary<object, VirtualAnimatorController>
            { [VRCAvatarDescriptor.AnimLayerType.FX] = fx, [VRCAvatarDescriptor.AnimLayerType.Action] = action };
            Assert.That(BakeVisibility(fx, preservation, entries: entries), Is.EqualTo(controlsWriter ? 0 : 1));
            Assert.That(obj.activeSelf, Is.EqualTo(!controlsWriter));
        }

        [Test]
        public void ConstantVisibilityInAPreservedTreeKeepsItsNativeDynamicMorphOutput()
        {
            Mesh("a"); var preservation = ObjectSettings(); var obj = PolicyChild("Visibility"); obj.SetActive(false);
            var binding = EditorCurveBinding.FloatCurve(obj.name, typeof(GameObject), "m_IsActive");
            var first = StaticCurve(binding, 1); var second = StaticCurve(binding, 1);
            AnimationUtility.SetEditorCurve(first, Binding("a"), AnimationCurve.Constant(0, 1, 0));
            AnimationUtility.SetEditorCurve(second, Binding("a"), AnimationCurve.Constant(0, 1, 100));
            var tree = Track(new BlendTree { blendType = BlendTreeType.Simple1D, blendParameter = "Preserved", useAutomaticThresholds = false });
            tree.children = new[] { new ChildMotion { motion = first, threshold = 0, timeScale = 1 }, new ChildMotion { motion = second, threshold = 1, timeScale = 1 } };
            var source = Controller(Clip()); source.AddParameter("Preserved", AnimatorControllerParameterType.Float);
            source.layers[0].stateMachine.defaultState.motion = tree;
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            Assert.That(BakeVisibility(controller, preservation, preserved: new[] { "Preserved" }), Is.EqualTo(1));
            var built = CommitAnimator(controller, root);
            obj.SetActive(false);
            var before = SampleFrozen(source, (p, i) => p.SetFloat("Preserved", i / 3f), 4,
                () => renderer.GetBlendShapeWeight(0) + (obj.activeSelf ? 1000 : 0));
            obj.SetActive(true);
            var after = SampleFrozen(built, (p, i) => p.SetFloat("Preserved", i / 3f), 4,
                () => renderer.GetBlendShapeWeight(0) + (obj.activeSelf ? 1000 : 0));
            Assert.That(before.Last() - before.First(), Is.GreaterThan(90));
            Assert.That(after, Is.EqualTo(before).Within(0.001f));
        }

        [Test]
        public void InitialVisibilityPolicyIsExplicitAndCapturedOnlyOnItsPlatform()
        {
            root.AddComponent<VRCAvatarDescriptor>();
            var component = root.AddComponent<ALTOAnimatorParameterPolicy>();
            Assert.That(component.BakeInitialVisibility, Is.False);
            component.BakeInitialVisibility = true;
            component.ApplyOnPlatforms = OptimizationPlatforms.None;
            var disabled = new BuildContext(root, null); ParameterPolicyPass.Capture(disabled);
            Assert.That(disabled.GetState<ParameterPolicyState>().BakeInitialVisibility, Is.False);
            component.ApplyOnPlatforms = OptimizationPlatforms.All;
            var enabled = new BuildContext(root, null); ParameterPolicyPass.Capture(enabled);
            Assert.That(enabled.GetState<ParameterPolicyState>().BakeInitialVisibility, Is.True);
            Assert.That(enabled.GetState<ParameterPolicyState>().Scope.Contains(root.transform), Is.True);
        }

        [Test]
        public void OcclusionUsesTheExplicitInitialVisibilityPolicy()
        {
            OcclusionGpuRequired(); var occlusion = OcclusionFixture(out var shell); shell.gameObject.SetActive(false);
            var preservation = ObjectSettings();
            var binding = EditorCurveBinding.FloatCurve("Shell", typeof(GameObject), "m_IsActive");
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(StaticCurve(binding, 1)));
            var entries = new Dictionary<object, VirtualAnimatorController> { [VRCAvatarDescriptor.AnimLayerType.FX] = controller };
            var scope = OptimizationBuildScope.Capture(occlusion, root);
            var before = OcclusionEvaluation.Run(root, occlusion, scope, AvatarAnimationIndex.Create(root, entries, null));
            Assert.That(before.Targets.Single().RemovedCount, Is.Zero);
            Assert.That(BakeVisibility(controller, preservation), Is.EqualTo(1));
            var after = OcclusionEvaluation.Run(root, occlusion, scope, AvatarAnimationIndex.Create(root, entries, null));
            Assert.That(after.Targets.Single().RemovedCount, Is.EqualTo(1));
            Assert.That(renderer.sharedMesh.triangles.Length, Is.EqualTo(3));
        }
    }
}
