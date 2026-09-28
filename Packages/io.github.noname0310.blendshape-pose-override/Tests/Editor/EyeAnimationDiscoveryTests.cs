using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.modular_avatar.core;
using Noname.AvatarTools.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDK3.Avatars.Components;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.Tests
{
    public sealed class EyeAnimationDiscoveryTests
    {
        private Scene scene;
        private GameObject root, model;
        private VRCAvatarDescriptor descriptor;
        private BlendShapePoseOverride component;
        private readonly List<Object> owned = new List<Object>();

        private T Own<T>(T obj) where T : Object { owned.Add(obj); return obj; }

        [SetUp]
        public void SetUp()
        {
            scene = EditorSceneManager.NewPreviewScene();
            root = new GameObject("Discovery Test") { hideFlags = HideFlags.HideAndDontSave };
            SceneManager.MoveGameObjectToScene(root, scene);
            descriptor = root.AddComponent<VRCAvatarDescriptor>();
            model = Child("Model", root.transform);
            var face = Child("Face", model.transform).AddComponent<SkinnedMeshRenderer>();
            var mesh = Own(new Mesh());
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
            mesh.triangles = new[] { 0, 1, 2 };
            var delta = new[] { Vector3.down, Vector3.down, Vector3.down };
            mesh.AddBlendShapeFrame("CustomBlink", 100, delta, null, null);
            foreach (string name in PoseOverridePresets.MmdEyeShapeNames) mesh.AddBlendShapeFrame(name, 100, delta, null, null);
            face.sharedMesh = mesh;
            descriptor.customEyeLookSettings.eyelidType = VRCAvatarDescriptor.EyelidType.Blendshapes;
            descriptor.customEyeLookSettings.eyelidsSkinnedMesh = face;
            descriptor.customEyeLookSettings.eyelidsBlendshapes = new[] { 0, -1, -1 };
            component = face.gameObject.AddComponent<BlendShapePoseOverride>();
            component.TargetMesh = face;
            component.Overrides.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            EditorSceneManager.ClosePreviewScene(scene);
            foreach (var obj in owned) if (obj != null) Object.DestroyImmediate(obj);
            owned.Clear();
        }

        private GameObject Child(string name, Transform parent)
        {
            var obj = new GameObject(name);
            obj.transform.SetParent(parent, false);
            return obj;
        }

        private AnimationClip Clip(string name, string path = "Face", bool animatorOnly = false)
        {
            var clip = Own(new AnimationClip { name = name });
            AnimationUtility.SetEditorCurve(clip,
                EditorCurveBinding.FloatCurve(animatorOnly ? "" : path, animatorOnly ? typeof(Animator) : typeof(SkinnedMeshRenderer), animatorOnly ? "Proxy" : "blendShape.CustomBlink"),
                AnimationCurve.Constant(0, 0, 75));
            return clip;
        }

        private BlendTree One(string parameter, params (float threshold, Motion motion)[] children)
        {
            var tree = Own(new BlendTree { name = "Uninformative tree", blendType = BlendTreeType.Simple1D, blendParameter = parameter, useAutomaticThresholds = false });
            tree.children = children.Select(c => new ChildMotion { threshold = c.threshold, motion = c.motion, timeScale = 1 }).ToArray();
            return tree;
        }

        private BlendTree Two(string x, string y, params (float position, Motion motion)[] children)
        {
            var tree = Own(new BlendTree { name = "Another tree", blendType = BlendTreeType.FreeformCartesian2D, blendParameter = x, blendParameterY = y });
            tree.children = children.Select(c => new ChildMotion { position = new Vector2(c.position, c.position), motion = c.motion, timeScale = 1 }).ToArray();
            return tree;
        }

        private AnimatorController Controller(params Motion[] motions)
        {
            var controller = Own(new AnimatorController { name = "Uninformative controller" });
            var parameters = new HashSet<string>();
            var visited = new HashSet<Motion>();
            Action<Motion> collect = null;
            collect = motion =>
            {
                if (motion == null || !visited.Add(motion) || !(motion is BlendTree tree)) return;
                if (tree.blendType == BlendTreeType.Direct)
                    foreach (var child in tree.children) parameters.Add(child.directBlendParameter);
                else
                {
                    parameters.Add(tree.blendParameter);
                    if (tree.blendType != BlendTreeType.Simple1D) parameters.Add(tree.blendParameterY);
                }
                foreach (var child in tree.children) collect(child.motion);
            };
            foreach (var motion in motions) collect(motion);
            controller.parameters = parameters.Where(p => !string.IsNullOrEmpty(p))
                .Select(p => new AnimatorControllerParameter { name = p, type = AnimatorControllerParameterType.Float }).ToArray();
            var parent = Own(new AnimatorStateMachine());
            var nested = Own(new AnimatorStateMachine());
            parent.stateMachines = new[] { new ChildAnimatorStateMachine { stateMachine = nested } };
            nested.states = motions.Select(m => new ChildAnimatorState { state = Own(new AnimatorState { motion = m }) }).ToArray();
            controller.layers = new[] { new AnimatorControllerLayer { name = "Layer", stateMachine = parent, defaultWeight = 1, syncedLayerIndex = -1 } };
            return controller;
        }

        private ModularAvatarMergeAnimator Merge(RuntimeAnimatorController controller, Transform relativeRoot = null, bool absolute = false)
        {
            var merge = Child("Settings", model.transform).AddComponent<ModularAvatarMergeAnimator>();
            merge.animator = controller;
            merge.pathMode = absolute ? MergeAnimatorPathMode.Absolute : MergeAnimatorPathMode.Relative;
            merge.relativePathRoot.Set((relativeRoot ?? model.transform).gameObject);
            return merge;
        }

        private Dictionary<EyePose, AnimationClip> SixPoses(string path = "Face")
            => Enum.GetValues(typeof(EyePose)).Cast<EyePose>().ToDictionary(p => p, p => Clip("Asset " + (17 - (int)p), path));

        private AnimatorController EyeController(Dictionary<EyePose, AnimationClip> clips, string prefix = "")
        {
            var open = Clip("EyeClosedJoyful_Left", AnimationUtility.GetCurveBindings(clips[EyePose.Closed])[0].path);
            var left = One(prefix + "v2/EyeLidLeft", (0.75f, open),
                (0, One(prefix + "v2/SmileSadLeft", (0.8f, clips[EyePose.JoyfulLeft]), (0.1f, clips[EyePose.ClosedLeft]))));
            var right = One(prefix + "v2/EyeLidRight", (0,
                One(prefix + "v2/SmileSadRight", (0.1f, clips[EyePose.ClosedRight]), (0.8f, clips[EyePose.JoyfulRight]))), (0.75f, open));
            var both = Two(prefix + "v2/EyeLidRight", prefix + "v2/EyeLidLeft", (0.75f, open),
                (0, Two(prefix + "v2/SmileSadRight", prefix + "v2/SmileSadLeft", (0.8f, clips[EyePose.Joyful]), (0.1f, clips[EyePose.Closed]))));
            var wrapper = Own(new BlendTree { blendType = BlendTreeType.Direct, blendParameter = "v2/EyeLidLeft" });
            wrapper.children = new[] { left, right, both }.Select(t => new ChildMotion { motion = t, directBlendParameter = "Enabled", timeScale = 1 }).ToArray();
            return Controller(wrapper);
        }

        private Dictionary<string, EyePose> Requests()
        {
            var requests = PoseOverridePresets.GetMmdRequests(component.TargetMesh.sharedMesh);
            requests.Add("CustomBlink", EyePose.Closed);
            PoseOverridePresets.AddMissingEntries(component, requests.Keys);
            return requests;
        }

        [TestCase("")]
        [TestCase("OSCm/Proxy/")]
        public void ParametersResolveSixDistinctPosesRegardlessOfNamesAndChildOrder(string prefix)
        {
            var clips = SixPoses();
            var merge = Merge(EyeController(clips, prefix));
            merge.gameObject.SetActive(false);
            var result = EyeAnimationDiscovery.Find(component);
            foreach (var pair in clips)
                Assert.AreSame(pair.Value, result.Candidates[pair.Key].Single().Clip, pair.Key.ToString());
        }

        [Test]
        public void ButtonsFillAllEightRowsAndInferTheMergeAnimationRoot()
        {
            var clips = SixPoses();
            Merge(EyeController(clips));
            var requests = Requests();
            var report = EyePresetAutoAssignment.FillMissing(component, requests);
            Assert.AreEqual(8, report.Assigned);
            Assert.IsEmpty(report.Warnings);
            Assert.AreEqual(PoseAnimationPathMode.Relative, component.PathMode);
            Assert.AreSame(model.transform, component.RelativePathRoot);
            foreach (var entry in component.Overrides) Assert.AreSame(clips[requests[entry.BlendShapeName]], entry.Animation);
            Assert.IsTrue(PoseOverrideConfiguration.TryResolve(component, out _, out var error), error);
        }

        [Test]
        public void ExistingManualAssignmentsAndValidPathSettingsArePreserved()
        {
            var clips = SixPoses();
            Merge(EyeController(clips));
            var requests = Requests();
            var manual = Clip("Manually Selected");
            component.Overrides[0].Animation = manual;
            component.RelativePathRoot = model.transform;
            var report = EyePresetAutoAssignment.FillMissing(component, requests);
            Assert.AreEqual(7, report.Assigned);
            Assert.AreEqual(1, report.Preserved);
            Assert.AreSame(manual, component.Overrides[0].Animation);
            Assert.AreSame(model.transform, component.RelativePathRoot);
            Assert.AreEqual(0, EyePresetAutoAssignment.FillMissing(component, requests).Assigned);
        }

        [Test]
        public void DescriptorBaseAndSpecialControllersAreBothSearched()
        {
            var clips = SixPoses("Model/Face");
            descriptor.baseAnimationLayers = new[] { new VRCAvatarDescriptor.CustomAnimLayer { type = VRCAvatarDescriptor.AnimLayerType.FX, animatorController = Controller(One("v2/EyeLidLeft", (0, clips[EyePose.ClosedLeft]))) } };
            descriptor.specialAnimationLayers = new[] { new VRCAvatarDescriptor.CustomAnimLayer { type = VRCAvatarDescriptor.AnimLayerType.Sitting, animatorController = Controller(One("v2/EyeLidRight", (0, clips[EyePose.ClosedRight]))) } };
            var result = EyeAnimationDiscovery.Find(component);
            Assert.AreSame(clips[EyePose.ClosedLeft], result.Candidates[EyePose.ClosedLeft].Single().Clip);
            Assert.AreSame(clips[EyePose.ClosedRight], result.Candidates[EyePose.ClosedRight].Single().Clip);
        }

        [Test]
        public void AbsoluteMergePathsAndCombinedEyeParameterAreSupported()
        {
            var closed = Clip("A", "Model/Face");
            var joyful = Clip("B", "Model/Face");
            Merge(Controller(One("v2/EyeLid", (0, One("v2/SmileFrown", (0, closed), (1, joyful))))), absolute: true);
            var requests = Requests();
            var report = EyePresetAutoAssignment.FillMissing(component, requests);
            Assert.AreEqual(4, report.Assigned); // VRC, MMD blink, smile, and relaxed closed eyes.
            Assert.AreEqual(PoseAnimationPathMode.Absolute, component.PathMode);
            Assert.AreSame(closed, component.Overrides.Single(e => e.BlendShapeName == "CustomBlink").Animation);
        }

        [Test]
        public void AnimatorOverrideControllerReturnsReplacementClips()
        {
            var clips = SixPoses();
            var controller = EyeController(clips);
            var replacement = Clip("Replacement Without A Pose Name");
            var wrapper = Own(new AnimatorOverrideController(controller));
            wrapper.ApplyOverrides(new[] { new KeyValuePair<AnimationClip, AnimationClip>(clips[EyePose.Closed], replacement) });
            Merge(wrapper);
            Assert.AreSame(replacement, EyeAnimationDiscovery.Find(component).Candidates[EyePose.Closed].Single().Clip);
        }

        [Test]
        public void SyncedLayerEffectiveMotionsAreIncluded()
        {
            var first = Clip("First");
            var second = Clip("Second");
            var controller = Controller(One("v2/EyeLidLeft", (0, first)));
            var layers = controller.layers.ToList();
            layers.Add(new AnimatorControllerLayer { name = "Synced", stateMachine = Own(new AnimatorStateMachine()), syncedLayerIndex = 0, defaultWeight = 1 });
            controller.layers = layers.ToArray();
            controller.AddParameter("v2/EyeLidRight", AnimatorControllerParameterType.Float);
            var state = layers[0].stateMachine.stateMachines[0].stateMachine.states[0].state;
            controller.SetStateEffectiveMotion(state, One("v2/EyeLidRight", (0, second)), 1);
            Merge(controller);
            var result = EyeAnimationDiscovery.Find(component);
            Assert.AreSame(first, result.Candidates[EyePose.ClosedLeft].Single().Clip);
            Assert.AreSame(second, result.Candidates[EyePose.ClosedRight].Single().Clip);
        }

        [Test]
        public void SmoothingCurvesAndMisleadingParameterNamesAreNotPoseCandidates()
        {
            var clips = SixPoses();
            Merge(EyeController(clips, "OSCm/Proxy/"));
            var smooth = Clip("EyeClosed", animatorOnly: true);
            var decoy = Clip("EyeClosed");
            Merge(Controller(One("v2/EyeLidLeft", (0, smooth)), One("v2/EyeLidRightSmoother", (0, decoy)), One("custom/v2/EyeLidLeftExtra", (0, decoy))));
            var result = EyeAnimationDiscovery.Find(component);
            foreach (var pose in result.Candidates.Values) Assert.AreEqual(1, pose.Count);
        }

        [Test]
        public void ClipsForOtherRenderersAndWrongMergeRootsAreIgnored()
        {
            var clips = SixPoses();
            Merge(EyeController(clips));
            var wrong = SixPoses("OtherFace");
            Merge(EyeController(wrong));
            Merge(EyeController(SixPoses()), root.transform);
            var result = EyeAnimationDiscovery.Find(component);
            foreach (var pair in clips) Assert.AreSame(pair.Value, result.Candidates[pair.Key].Single().Clip);
        }

        [Test]
        public void ConflictingControllersLeaveEmptyRowsForManualAssignment()
        {
            Merge(EyeController(SixPoses()));
            Merge(EyeController(SixPoses()));
            var report = EyePresetAutoAssignment.FillMissing(component, Requests());
            Assert.AreEqual(0, report.Assigned);
            Assert.AreEqual(8, report.Warnings.Count);
            Assert.IsTrue(report.Warnings.All(w => w.Contains("Multiple")));
            Assert.IsTrue(component.Overrides.All(e => e.Animation == null));
        }

        [Test]
        public void ReusedControllerAndClipDoNotCreateFalseAmbiguity()
        {
            var clips = SixPoses();
            var controller = EyeController(clips);
            Merge(controller);
            Merge(controller);
            var report = EyePresetAutoAssignment.FillMissing(component, Requests());
            Assert.AreEqual(8, report.Assigned);
            Assert.IsEmpty(report.Warnings);
        }

        [Test]
        public void RootInferenceDoesNotBreakAnExistingManualClip()
        {
            Merge(EyeController(SixPoses()));
            var requests = Requests();
            component.Overrides[0].Animation = Clip("Manual Absolute", "Model/Face");
            component.PathMode = PoseAnimationPathMode.Absolute;
            var report = EyePresetAutoAssignment.FillMissing(component, requests);
            Assert.AreEqual(0, report.Assigned);
            Assert.AreEqual(PoseAnimationPathMode.Absolute, component.PathMode);
            Assert.IsNull(component.RelativePathRoot);
            Assert.AreEqual(7, report.Warnings.Count);
        }

        [Test]
        public void ABlendedClosedPointOrUnknownSelectorIsNotGuessed()
        {
            var first = Clip("A");
            var second = Clip("B");
            Merge(Controller(One("v2/EyeLidLeft", (-1, first), (1, second)),
                One("v2/EyeLidRight", (0, One("Artist Custom Parameter", (0, first), (1, second))))));
            Assert.IsTrue(EyeAnimationDiscovery.Find(component).Candidates.Values.All(v => v.Count == 0));
        }
    }
}
