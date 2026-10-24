using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;
using VRC.SDK3.Avatars.Components;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed class BlendShapeOptimizationTests
    {
        private readonly List<Object> objects = new();
        private Scene scene;
        private GameObject root;
        private SkinnedMeshRenderer renderer;

        [SetUp]
        public void SetUp()
        {
            scene = EditorSceneManager.NewPreviewScene();
            root = Track(new GameObject("Avatar"));
            SceneManager.MoveGameObjectToScene(root, scene);
            renderer = root.AddComponent<SkinnedMeshRenderer>();
        }

        [TearDown]
        public void TearDown()
        {
            for (int i = objects.Count - 1; i >= 0; i--) if (objects[i] != null) Object.DestroyImmediate(objects[i]);
            objects.Clear();
            EditorSceneManager.ClosePreviewScene(scene);
        }

        private T Track<T>(T value) where T : Object { objects.Add(value); return value; }

        private void TrackControllerAssets(AnimatorController controller)
        {
            var seen = new HashSet<Object>();
            var pending = new Queue<Object>(); pending.Enqueue(controller);
            while (pending.Count > 0)
            {
                var asset = pending.Dequeue();
                if (asset == null || !seen.Add(asset) || EditorUtility.IsPersistent(asset)) continue;
                if (asset is AnimatorController || asset is AnimatorStateMachine || asset is AnimatorState ||
                    asset is AnimatorTransitionBase || asset is Motion || asset is StateMachineBehaviour || asset is AvatarMask)
                    Track(asset);
                else continue;
                foreach (var reference in EditorUtility.CollectDependencies(new[] { asset }))
                    if (reference != asset) pending.Enqueue(reference);
            }
        }

        private Mesh Mesh(params string[] shapes)
        {
            var mesh = Track(new Mesh { name = "Test mesh" });
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
            mesh.normals = Enumerable.Repeat(Vector3.forward, 3).ToArray();
            mesh.tangents = Enumerable.Repeat(new Vector4(1, 0, 0, -1), 3).ToArray();
            mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.up };
            mesh.triangles = new[] { 0, 1, 2 };
            foreach (var shape in shapes) Frame(mesh, shape, 100, Vector3.right * (mesh.blendShapeCount + 1));
            renderer.sharedMesh = mesh;
            return mesh;
        }

        private static void Frame(Mesh mesh, string name, float weight, Vector3 delta)
            => mesh.AddBlendShapeFrame(name, weight, Enumerable.Repeat(delta, 3).ToArray(),
                Enumerable.Repeat(delta * 0.1f, 3).ToArray(), Enumerable.Repeat(delta * 0.2f, 3).ToArray());

        private AnimationClip Clip(params string[] names)
        {
            var clip = Track(new AnimationClip { name = "Reset" });
            foreach (var name in names) AnimationUtility.SetEditorCurve(clip, Binding(name), AnimationCurve.Constant(0, 1, 0));
            return clip;
        }

        private static EditorCurveBinding Binding(string name, string path = "")
            => EditorCurveBinding.FloatCurve(path, typeof(SkinnedMeshRenderer), "blendShape." + name);

        private AnimatorController Controller(params AnimationClip[] clips)
        {
            var controller = Track(new AnimatorController());
            var machine = Track(new AnimatorStateMachine());
            var states = clips.Select(clip =>
            {
                var state = Track(new AnimatorState { name = clip.name, motion = clip, writeDefaultValues = true });
                return new ChildAnimatorState { state = state };
            }).ToArray();
            machine.states = states;
            machine.defaultState = states[0].state;
            controller.layers = new[] { new AnimatorControllerLayer { name = "Base", defaultWeight = 1, stateMachine = machine } };
            return controller;
        }

        private (AnimationUsage usage, VirtualAnimatorController controller) Usage(params AnimationClip[] clips)
        {
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(clips));
            var usage = new AnimationUsage();
            usage.AddController(root.transform, root.transform, controller);
            return (usage, controller);
        }

        private OptimizationPlan Plan(AnimationUsage usage, params string[] preserved)
            => OptimizationPlan.Create(renderer, usage, true, new HashSet<string>(preserved));

        [Test]
        public void FreezeKeepsRawNormalsTangentsUvAndAnimatedFrames()
        {
            var source = Mesh("static", "animated");
            renderer.SetBlendShapeWeight(0, 40); renderer.SetBlendShapeWeight(1, 30);
            var animated = Clip("animated");
            AnimationUtility.SetEditorCurve(animated, Binding("animated"), AnimationCurve.Linear(0, 0, 1, 100));
            var plan = Plan(Usage(animated).usage);
            Assert.That(plan.Frozen.Select(s => s.Name), Is.EqualTo(new[] { "static" }));
            var result = Track(MeshOptimization.Generate(plan, MeshOptimization.CreateRecipe(plan)));
            MeshOptimization.Apply(plan, result);
            Assert.That(result.vertices[0].x, Is.EqualTo(0.4f).Within(1e-6));
            Assert.That(result.normals[0].x, Is.EqualTo(0.04f).Within(1e-6));
            Assert.That(result.tangents[0].x, Is.EqualTo(1.08f).Within(1e-6));
            Assert.That(result.tangents[0].w, Is.EqualTo(-1));
            Assert.That(result.uv, Is.EqualTo(source.uv));
            Assert.That(result.triangles, Is.EqualTo(source.triangles));
            Assert.That(result.blendShapeCount, Is.EqualTo(1));
            Assert.That(renderer.GetBlendShapeWeight(0), Is.EqualTo(30));
            Assert.That(source.vertices[0], Is.EqualTo(Vector3.zero));
            Assert.That(source.blendShapeCount, Is.EqualTo(2));
        }

        [Test]
        public void OverlappingResetSetsProduceAbcDAndEAndKeepSourceClips()
        {
            Mesh("a", "b", "c", "d", "e");
            var weights = new[] { 20f, 60f, 80f, 10f, 30f };
            for (int i = 0; i < weights.Length; i++) renderer.SetBlendShapeWeight(i, weights[i]);
            var clips = new[] { Clip("a", "b", "c"), Clip("a", "b", "c", "d"), Clip("a", "b", "c", "e") };
            var (usage, controller) = Usage(clips);
            var plan = Plan(usage);
            Assert.That(plan.Groups.Count, Is.EqualTo(3));
            Assert.That(plan.Groups[0].Inputs.Select(s => s.Name), Is.EqualTo(new[] { "a", "b", "c" }));
            var recipe = MeshOptimization.CreateRecipe(plan);
            Assert.That(recipe.Groups[0].Vertices[0].x, Is.EqualTo(0.2f + 1.2f + 2.4f).Within(1e-6));
            plan.RewriteCurves();
            var counts = controller.AllReachableNodes().OfType<VirtualClip>().Select(c => c.GetFloatCurveBindings().Count()).OrderBy(n => n);
            Assert.That(counts, Is.EqualTo(new[] { 1, 2, 2 }));
            Assert.That(AnimationUtility.GetCurveBindings(clips[0]).Length, Is.EqualTo(3));
            Assert.That(plan.Groups.All(g => g.DefaultWeight == 100), Is.True);
        }

        [Test]
        public void AnyNonzeroCurveAnywherePreventsGrouping()
        {
            Mesh("a"); renderer.SetBlendShapeWeight(0, 60);
            var other = Clip("a");
            AnimationUtility.SetEditorCurve(other, Binding("a"), AnimationCurve.Constant(0, 1, 1));
            Assert.That(Plan(Usage(Clip("a"), other).usage).HasChanges, Is.False);
        }

        [Test]
        public void OnlyConstantZeroCurvesAreAccepted()
        {
            Assert.That(AnimationUsage.IsConstantZero(AnimationCurve.Constant(0, 2, 0)), Is.True);
            Assert.That(AnimationUsage.IsConstantZero(new AnimationCurve(new Keyframe(0, 0))), Is.True);
            Assert.That(AnimationUsage.IsConstantZero(new AnimationCurve(new Keyframe(0, 0, 0, 5), new Keyframe(1, 0, -5, 0))), Is.False);
            Assert.That(AnimationUsage.IsConstantZero(AnimationCurve.Constant(0, 2, 0.001f)), Is.False);
        }

        [Test]
        public void ZeroDefaultShapeBecomesEmptyChannelAndClipDurationIsPreserved()
        {
            Mesh("a");
            var clip = Clip("a");
            AnimationUtility.SetEditorCurve(clip, Binding("a"), AnimationCurve.Constant(0, 4, 0));
            var (usage, controller) = Usage(clip);
            var plan = Plan(usage);
            var recipe = MeshOptimization.CreateRecipe(plan);
            Assert.That(recipe.Groups[0].Vertices.All(v => v == Vector3.zero), Is.True);
            Assert.That(plan.Groups[0].DefaultWeight, Is.Zero);
            plan.RewriteCurves();
            var output = controller.AllReachableNodes().OfType<VirtualClip>().Single();
            Assert.That(output.GetFloatCurve(output.GetFloatCurveBindings().Single()).keys.Last().time, Is.EqualTo(4));
        }

        [Test]
        public void MultiFrameStaticShapeInterpolatesButAnimatedShapeIsKept()
        {
            var mesh = Mesh(); Frame(mesh, "nonlinear", 50, Vector3.right); Frame(mesh, "nonlinear", 100, Vector3.right * 4);
            renderer.SetBlendShapeWeight(0, 75);
            var staticPlan = Plan(new AnimationUsage());
            Assert.That(MeshOptimization.CreateRecipe(staticPlan).Frozen.Vertices[0].x, Is.EqualTo(2.5f));
            Assert.That(Plan(Usage(Clip("nonlinear")).usage).HasChanges, Is.False);
        }

        [TestCase(-10f)] [TestCase(120f)]
        public void UnsupportedDefaultsArePreserved(float value)
        {
            Mesh("a"); renderer.SetBlendShapeWeight(0, value);
            Assert.That(Plan(Usage(Clip("a")).usage).HasChanges, Is.False);
        }

        [Test]
        public void AdditiveAndMeshSwapContextsArePreserved()
        {
            Mesh("a"); renderer.SetBlendShapeWeight(0, 60);
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(Clip("a")));
            var usage = new AnimationUsage();
            usage.AddController(root.transform, root.transform, controller, new HashSet<VirtualClip>(controller.AllReachableNodes().OfType<VirtualClip>()));
            Assert.That(Plan(usage).HasChanges, Is.False);
            usage = new AnimationUsage(); usage.MeshSwaps.Add(renderer);
            Assert.That(Plan(usage).HasChanges, Is.False);
        }

        [Test]
        public void MmdMarkerVisemesAndDescriptorEyelidIndicesArePreserved()
        {
            Mesh("static", "まばたき", "custom", "vrc.blink", "viseme");
            var descriptor = root.AddComponent<VRCAvatarDescriptor>();
            descriptor.VisemeSkinnedMesh = renderer; descriptor.VisemeBlendShapes = new[] { "viseme" };
            var eye = descriptor.customEyeLookSettings;
            eye.eyelidsSkinnedMesh = renderer; eye.eyelidsBlendshapes = new[] { 3, -1, -1 };
            descriptor.customEyeLookSettings = eye;
            root.AddComponent<BlendShapeOptimizationExclude>().BlendShapes.Add("custom");
            var preserve = TraceAndOptimizeBlendShapesPlugin.ProtectedNames(renderer, descriptor, true);
            var names = TraceAndOptimizeBlendShapesPlugin.CaptureEyelids(descriptor);
            var plan = Plan(new AnimationUsage(), preserve.ToArray());
            Assert.That(plan.Frozen.Select(s => s.Name), Is.EqualTo(new[] { "static" }));
            MeshOptimization.Apply(plan, Track(MeshOptimization.Generate(plan, MeshOptimization.CreateRecipe(plan))));
            TraceAndOptimizeBlendShapesPlugin.RestoreEyelids(descriptor, names);
            Assert.That(descriptor.customEyeLookSettings.eyelidsBlendshapes, Is.EqualTo(new[] { 2, -1, -1 }));
        }

        [Test]
        public void ChildAnimatorBindingsResolveRelativeToTheirOwnRoot()
        {
            var child = Track(new GameObject("Child")); child.transform.SetParent(root.transform, false);
            var body = Track(new GameObject("Body")); body.transform.SetParent(child.transform, false);
            var mesh = Mesh("a"); renderer = body.AddComponent<SkinnedMeshRenderer>(); renderer.sharedMesh = mesh;
            var clip = Clip(); AnimationUtility.SetEditorCurve(clip, Binding("a", "Body"), AnimationCurve.Constant(0, 1, 70));
            var virtualController = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(clip));
            var usage = new AnimationUsage(); usage.AddController(root.transform, child.transform, virtualController);
            Assert.That(Plan(usage).HasChanges, Is.False);
        }

        [Test]
        public void DestroyedAnimatorEntriesAreIgnoredWithoutLosingLiveControllerBindings()
        {
            Mesh("a", "b");
            var liveClip = Clip("b");
            AnimationUtility.SetEditorCurve(liveClip, Binding("b"), AnimationCurve.Constant(0, 1, 50));
            var liveAnimator = root.AddComponent<Animator>();
            liveAnimator.runtimeAnimatorController = Controller(liveClip);
            var child = Track(new GameObject("Removed animator")); child.transform.SetParent(root.transform, false);
            var removedAnimator = child.AddComponent<Animator>();
            removedAnimator.runtimeAnimatorController = Controller(Clip("a"));
            var context = new BuildContext(root, null);
            var controllers = context.ActivateExtensionContextRecursive<VirtualControllerContext>();
            try
            {
                Object.DestroyImmediate(removedAnimator);
                var usage = AnimationUsage.Collect(root, controllers);
                Assert.That(usage.ClipCount, Is.EqualTo(1));
                Assert.That(usage.Get(renderer, "b").Count, Is.EqualTo(1));
                Assert.That(Plan(usage).Frozen.Select(s => s.Name), Is.EqualTo(new[] { "a" }));
            }
            finally
            {
                context.DeactivateExtensionContext<VirtualControllerContext>();
                TrackControllerAssets((AnimatorController)liveAnimator.runtimeAnimatorController);
            }
        }

        [TestCase(false, true, 0.5f)] [TestCase(false, true, 1f)]
        [TestCase(true, true, 0.5f)] [TestCase(false, false, 0.5f)]
        public void WeightedGroupMatchesDirectBlendTreeAndPartialLayer(bool normalize, bool writeDefaults, float layerWeight)
        {
            Mesh("a", "b"); renderer.SetBlendShapeWeight(0, 20); renderer.SetBlendShapeWeight(1, 60);
            var clips = new[] { Clip("a", "b"), Clip("a", "b") };
            var controller = Controller(clips);
            var tree = Track(new BlendTree { blendType = BlendTreeType.Direct });
            tree.children = new[] { new ChildMotion { motion = clips[0], directBlendParameter = "P", timeScale = 1 },
                new ChildMotion { motion = clips[1], directBlendParameter = "Q", timeScale = 1 } };
            using (var serialized = new SerializedObject(tree))
            {
                serialized.FindProperty("m_NormalizedBlendValues").boolValue = normalize; serialized.ApplyModifiedPropertiesWithoutUndo();
            }
            var machine = controller.layers[0].stateMachine;
            machine.defaultState.motion = tree; machine.defaultState.writeDefaultValues = writeDefaults;
            var baseMachine = Track(new AnimatorStateMachine());
            var baseState = Track(new AnimatorState { name = "Baseline", motion = Clip(), writeDefaultValues = true });
            baseMachine.states = new[] { new ChildAnimatorState { state = baseState } }; baseMachine.defaultState = baseState;
            controller.layers = new[] { new AnimatorControllerLayer { name = "Base", stateMachine = baseMachine, defaultWeight = 1 },
                new AnimatorControllerLayer { name = "Reset", stateMachine = machine, defaultWeight = layerWeight } };
            controller.parameters = new[] { new AnimatorControllerParameter { name = "P", type = AnimatorControllerParameterType.Float },
                new AnimatorControllerParameter { name = "Q", type = AnimatorControllerParameterType.Float } };
            var virtualController = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(controller);
            var usage = new AnimationUsage(); usage.AddController(root.transform, root.transform, virtualController);
            var plan = Plan(usage);
            var optimized = Track(new GameObject("Optimized")); SceneManager.MoveGameObjectToScene(optimized, scene);
            var optimizedRenderer = optimized.AddComponent<SkinnedMeshRenderer>();
            optimizedRenderer.sharedMesh = Track(MeshOptimization.Generate(plan, MeshOptimization.CreateRecipe(plan)));
            optimizedRenderer.SetBlendShapeWeight(0, plan.Groups[0].DefaultWeight);
            plan.RewriteCurves();
            var optimizedAnimator = optimized.AddComponent<Animator>();
            var context = new BuildContext(optimized, null);
            var controllers = context.ActivateExtensionContextRecursive<VirtualControllerContext>();
            controllers.Controllers[optimizedAnimator] = virtualController;
            context.DeactivateExtensionContext<VirtualControllerContext>();
            var optimizedController = Track((AnimatorController)optimizedAnimator.runtimeAnimatorController);
            TrackControllerAssets(optimizedController);
            optimizedAnimator.runtimeAnimatorController = null;
            Object.DestroyImmediate(optimizedAnimator);
            foreach (var weights in new[] { Vector2.zero, new Vector2(0.25f, 0), new Vector2(0.5f, 0.5f), Vector2.one })
            {
                renderer.SetBlendShapeWeight(0, 20); renderer.SetBlendShapeWeight(1, 60);
                optimizedRenderer.SetBlendShapeWeight(0, 100);
                float source = Evaluate(root, renderer, controller, weights);
                float result = Evaluate(optimized, optimizedRenderer, optimizedController, weights);
                Assert.That(result, Is.EqualTo(source).Within(1e-5), "P/Q=" + weights);
            }
        }

        [Test]
        public void BuildPassSeesControllerAddedAfterCaptureAndPreservesSourceAssets()
        {
            var source = Mesh("static", "a", "b", "other");
            renderer.SetBlendShapeWeight(0, 30); renderer.SetBlendShapeWeight(1, 20); renderer.SetBlendShapeWeight(2, 60);
            var descriptor = root.AddComponent<VRCAvatarDescriptor>();
            root.AddComponent<Animator>();
            var optimizer = root.AddComponent<TraceAndOptimizeBlendShapes>();
            optimizer.PreserveMmdBlendShapes = false;
            var context = new BuildContext(root, null);
            TraceAndOptimizeBlendShapesPlugin.Capture(context);
            var clip = Clip("a", "b", "other");
            AnimationUtility.SetEditorCurve(clip, Binding("other"), AnimationCurve.Constant(0, 1, 25));
            var controller = Controller(clip);
            descriptor.baseAnimationLayers = new[] { new VRCAvatarDescriptor.CustomAnimLayer {
                type = VRCAvatarDescriptor.AnimLayerType.FX, isDefault = false, animatorController = controller } };
            descriptor.specialAnimationLayers = Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>();
            TraceAndOptimizeBlendShapesPlugin.Apply(context);
            Track(renderer.sharedMesh);
            var outputController = (AnimatorController)descriptor.baseAnimationLayers[0].animatorController;
            TrackControllerAssets(outputController);
            Assert.That(renderer.sharedMesh.blendShapeCount, Is.EqualTo(2));
            Assert.That(renderer.sharedMesh.GetBlendShapeIndex("other"), Is.GreaterThanOrEqualTo(0));
            Assert.That(renderer.sharedMesh.vertices[0].x, Is.EqualTo(0.3f).Within(1e-6));
            Assert.That(outputController, Is.Not.SameAs(controller));
            Assert.That(AnimationUtility.GetCurveBindings(outputController.animationClips.Single()).Length, Is.EqualTo(2));
            Assert.That(AnimationUtility.GetCurveBindings(clip).Length, Is.EqualTo(3));
            Assert.That(source.blendShapeCount, Is.EqualTo(4));
        }

        [Test]
        public void ScopeOnlyChangesRequestedMeshesButAnalyzesAvatarController()
        {
            var source = Mesh("a");
            var child = Track(new GameObject("Scoped")); child.transform.SetParent(root.transform, false);
            var scopedRenderer = child.AddComponent<SkinnedMeshRenderer>(); scopedRenderer.sharedMesh = source;
            var descriptor = root.AddComponent<VRCAvatarDescriptor>(); root.AddComponent<Animator>();
            var optimizer = child.AddComponent<TraceAndOptimizeBlendShapes>();
            optimizer.PreserveMmdBlendShapes = false;
            var clip = Clip(); AnimationUtility.SetEditorCurve(clip, Binding("a", "Scoped"), AnimationCurve.Constant(0, 1, 50));
            descriptor.baseAnimationLayers = new[] { new VRCAvatarDescriptor.CustomAnimLayer {
                type = VRCAvatarDescriptor.AnimLayerType.FX, animatorController = Controller(clip) } };
            descriptor.specialAnimationLayers = Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>();
            var context = new BuildContext(root, null);
            TraceAndOptimizeBlendShapesPlugin.Capture(context); TraceAndOptimizeBlendShapesPlugin.Apply(context);
            TrackControllerAssets((AnimatorController)descriptor.baseAnimationLayers[0].animatorController);
            Assert.That(renderer.sharedMesh, Is.SameAs(source));
            Assert.That(scopedRenderer.sharedMesh, Is.SameAs(source));
        }

        private float Evaluate(GameObject target, SkinnedMeshRenderer targetRenderer, AnimatorController controller, Vector2 weights)
        {
            var animator = target.GetComponent<Animator>();
            if (animator == null) animator = target.AddComponent<Animator>();
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            var graph = PlayableGraph.Create("BlendShape equivalence test");
            try
            {
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                var playable = AnimatorControllerPlayable.Create(graph, controller);
                var output = AnimationPlayableOutput.Create(graph, "Test", animator); output.SetSourcePlayable(playable);
                playable.SetFloat("P", weights.x); playable.SetFloat("Q", weights.y);
                graph.Play(); graph.Evaluate(0.016f); graph.Evaluate(0.016f);
                var baked = Track(new Mesh()); targetRenderer.BakeMesh(baked);
                return baked.vertices[0].x;
            }
            finally { graph.Destroy(); Object.DestroyImmediate(animator); }
        }
    }
}
