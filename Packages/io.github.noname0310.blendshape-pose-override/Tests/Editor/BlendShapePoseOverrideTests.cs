using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.preview;
using Noname.AvatarTools.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDK3.Avatars.Components;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.Tests
{
    public sealed class BlendShapePoseOverrideTests
    {
        private Scene scene;
        private GameObject root;
        private VRCAvatarDescriptor descriptor;
        private SkinnedMeshRenderer renderer;
        private BlendShapePoseOverride component;
        private readonly List<Object> owned = new List<Object>();

        [SetUp]
        public void SetUp()
        {
            scene = EditorSceneManager.NewPreviewScene();
            root = new GameObject("PoseOverrideTests") { hideFlags = HideFlags.HideAndDontSave };
            SceneManager.MoveGameObjectToScene(root, scene);
            descriptor = root.AddComponent<VRCAvatarDescriptor>();
            renderer = CreateRenderer("Face");
            descriptor.customEyeLookSettings.eyelidType = VRCAvatarDescriptor.EyelidType.Blendshapes;
            descriptor.customEyeLookSettings.eyelidsSkinnedMesh = renderer;
            descriptor.customEyeLookSettings.eyelidsBlendshapes = new[] { 1, -1, -1 };
            component = root.AddComponent<BlendShapePoseOverride>();
            component.TargetMesh = renderer;
            component.PathMode = PoseAnimationPathMode.Absolute;
            component.Overrides = new List<BlendShapePoseEntry>();
        }

        [TearDown]
        public void TearDown()
        {
            EditorSceneManager.ClosePreviewScene(scene);
            foreach (var obj in owned) if (obj != null) Object.DestroyImmediate(obj);
            owned.Clear();
        }

        private GameObject Child(string name, Transform parent = null)
        {
            var obj = new GameObject(name);
            obj.transform.SetParent(parent ?? root.transform, false);
            return obj;
        }

        private SkinnedMeshRenderer CreateRenderer(string name)
        {
            var mesh = new Mesh { name = name + " Mesh" };
            owned.Add(mesh);
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
            mesh.normals = Enumerable.Repeat(Vector3.forward, 3).ToArray();
            mesh.tangents = Enumerable.Repeat(new Vector4(1, 0, 0, -1), 3).ToArray();
            mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.up };
            mesh.triangles = new[] { 0, 1, 2 };
            AddFrame(mesh, "Customize", 100, new Vector3(0, -0.3f, 0));
            AddFrame(mesh, "Close", 100, new Vector3(0, -1, 0));
            AddFrame(mesh, "Smile", 35, new Vector3(0.15f, 0.1f, 0));
            AddFrame(mesh, "Smile", 100, new Vector3(0.4f, 0.2f, 0));
            AddFrame(mesh, "Alternate", 100, new Vector3(-0.2f, -0.4f, 0));
            var result = Child(name).AddComponent<SkinnedMeshRenderer>();
            result.sharedMesh = mesh;
            result.SetBlendShapeWeight(0, 40);
            return result;
        }

        private static void AddFrame(Mesh mesh, string name, float weight, Vector3 delta)
            => mesh.AddBlendShapeFrame(name, weight, Enumerable.Repeat(delta, 3).ToArray(),
                Enumerable.Repeat(delta * 0.2f, 3).ToArray(), Enumerable.Repeat(delta * 0.3f, 3).ToArray());

        private AnimationClip Clip(string path, params (string name, float weight)[] values)
        {
            var clip = new AnimationClip { frameRate = 60, name = "Authored Pose" };
            owned.Add(clip);
            foreach (var value in values)
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, typeof(SkinnedMeshRenderer), "blendShape." + value.name),
                    new AnimationCurve(new Keyframe(0, value.weight)));
            return clip;
        }

        private void Entry(string target, AnimationClip clip)
            => component.Overrides.Add(new BlendShapePoseEntry { BlendShapeName = target, Animation = clip });

        private PoseOverridePlan Resolve()
        {
            Assert.IsTrue(PoseOverrideConfiguration.TryResolve(component, out var plan, out var error), error);
            return plan;
        }

        private PoseOverrideResult Generate()
        {
            var plan = Resolve();
            var result = PoseOverrideProcessor.Generate(plan.Renderer, plan.Poses);
            owned.Add(result.Mesh);
            return result;
        }

        private Mesh Bake(params (string name, float weight)[] values)
        {
            var weights = Enumerable.Range(0, renderer.sharedMesh.blendShapeCount).Select(renderer.GetBlendShapeWeight).ToArray();
            var mesh = new Mesh();
            owned.Add(mesh);
            try
            {
                foreach (var value in values) renderer.SetBlendShapeWeight(renderer.sharedMesh.GetBlendShapeIndex(value.name), value.weight);
                renderer.BakeMesh(mesh, true);
                return mesh;
            }
            finally
            {
                for (int i = 0; i < weights.Length; i++) renderer.SetBlendShapeWeight(i, weights[i]);
            }
        }

        private static void AssertPose(Mesh expected, Mesh actual)
        {
            Assert.AreEqual(expected.vertexCount, actual.vertexCount);
            for (int i = 0; i < expected.vertexCount; i++)
            {
                Assert.Less(Vector3.Distance(expected.vertices[i], actual.vertices[i]), 0.00001f, "Vertex " + i);
                Assert.Less(Vector3.Distance(expected.normals[i], actual.normals[i]), 0.00001f, "Normal " + i);
                Assert.Less(Vector4.Distance(expected.tangents[i], actual.tangents[i]), 0.00001f, "Tangent " + i);
            }
        }

        [Test]
        public void ResetUsesNearestDescriptorOnlyAsAnEditableInitialDefault()
        {
            var nestedRoot = Child("Nested Avatar");
            var nestedDescriptor = nestedRoot.AddComponent<VRCAvatarDescriptor>();
            var other = CreateRenderer("Other Face");
            other.transform.SetParent(nestedRoot.transform, false);
            nestedDescriptor.customEyeLookSettings.eyelidsSkinnedMesh = other;
            nestedDescriptor.customEyeLookSettings.eyelidsBlendshapes = new[] { 3, -1, -1 };
            var childComponent = Child("Settings", nestedRoot.transform).AddComponent<BlendShapePoseOverride>();
            Assert.AreSame(nestedDescriptor, childComponent.FindDescriptor());
            Assert.AreSame(other, childComponent.TargetMesh);
            Assert.IsEmpty(childComponent.Overrides);
            nestedDescriptor.customEyeLookSettings.eyelidsSkinnedMesh = renderer;
            Assert.AreSame(other, childComponent.TargetMesh);
        }

        [Test]
        public void SelectedMeshWorksWithoutDescriptorEyelidSettings()
        {
            var other = CreateRenderer("Other Face");
            component.TargetMesh = other;
            descriptor.customEyeLookSettings.eyelidType = VRCAvatarDescriptor.EyelidType.None;
            descriptor.customEyeLookSettings.eyelidsSkinnedMesh = null;
            descriptor.customEyeLookSettings.eyelidsBlendshapes = null;
            Entry("Alternate", Clip("Other Face", ("Smile", 75)));
            Assert.AreSame(other, Resolve().Renderer);
            var result = Generate();
            Assert.AreSame(other.sharedMesh, result.Source);
            Assert.AreEqual("Alternate", result.Mesh.GetBlendShapeName(result.TargetIndices.Single()));
        }

        [TestCase(0)]
        [TestCase(25)]
        public void CurrentCustomizationIsTheBasisAndAuthoredPoseIsReached(float originalTargetWeight)
        {
            renderer.SetBlendShapeWeight(1, originalTargetWeight);
            var source = renderer.sharedMesh;
            var basis = Bake();
            var desired = Bake(("Customize", 65), ("Close", 60));
            Entry("Close", Clip("Face", ("Customize", 65), ("Close", 60)));
            var result = Generate();
            Assert.AreSame(source, renderer.sharedMesh);
            Assert.AreEqual(originalTargetWeight, renderer.GetBlendShapeWeight(1));
            result.Apply(renderer);
            Assert.AreEqual(40, renderer.GetBlendShapeWeight(0));
            Assert.AreEqual(0, renderer.GetBlendShapeWeight(1));
            AssertPose(basis, Bake());
            AssertPose(desired, Bake(("Close", 100)));
            Assert.AreEqual(1, result.Mesh.GetBlendShapeFrameCount(1));
            Assert.AreEqual(100, result.Mesh.GetBlendShapeFrameWeight(1, 0));
        }

        [Test]
        public void MultipleOverridesShareOriginalBasisAndAreOrderIndependent()
        {
            renderer.SetBlendShapeWeight(1, 20);
            renderer.SetBlendShapeWeight(3, 35);
            var basis = Bake();
            var first = Bake(("Close", 55), ("Alternate", 80));
            var second = Bake(("Close", 90), ("Smile", 65));
            Entry("Close", Clip("Face", ("Close", 55), ("Alternate", 80)));
            Entry("Alternate", Clip("Face", ("Close", 90), ("Smile", 65)));
            var forward = Generate();
            component.Overrides.Reverse();
            var reverse = Generate();
            forward.Apply(renderer);
            AssertPose(basis, Bake());
            AssertPose(first, Bake(("Close", 100)));
            AssertPose(second, Bake(("Alternate", 100)));
            var combinedForward = Bake(("Close", 30), ("Alternate", 70));
            reverse.Apply(renderer);
            AssertPose(combinedForward, Bake(("Close", 30), ("Alternate", 70)));
        }

        [TestCase(150)]
        [TestCase(-50)]
        public void MultiFrameAndOutOfRangeWeightsUseUnityEvaluation(float desiredWeight)
        {
            renderer.SetBlendShapeWeight(2, 40);
            var desired = Bake(("Smile", desiredWeight));
            Entry("Close", Clip("Face", ("Smile", desiredWeight)));
            Generate().Apply(renderer);
            AssertPose(desired, Bake(("Close", 100)));
        }

        [Test]
        public void NegativeFramesWithNonzeroContributionAtZeroPreserveBasis()
        {
            AddFrame(renderer.sharedMesh, "Signed", -100, new Vector3(0.1f, 0, 0));
            AddFrame(renderer.sharedMesh, "Signed", 100, new Vector3(0.3f, 0, 0));
            // The renderer was assigned before these frames were added; initialize the new weight slot.
            renderer.SetBlendShapeWeight(renderer.sharedMesh.GetBlendShapeIndex("Signed"), 0);
            var basis = Bake();
            var desired = Bake(("Signed", 70));
            Entry("Signed", Clip("Face", ("Signed", 70)));
            Generate().Apply(renderer);
            AssertPose(basis, Bake());
            AssertPose(desired, Bake(("Signed", 100)));
        }

        [Test]
        public void OtherFramesAndMeshChannelsArePreservedAndSourceIsUntouched()
        {
            var source = renderer.sharedMesh;
            var originalVertices = source.vertices;
            var originalDeltas = new Vector3[source.vertexCount];
            source.GetBlendShapeFrameVertices(2, 1, originalDeltas, null, null);
            Entry("Close", Clip("Face", ("Close", 70)));
            var result = Generate();
            CollectionAssert.AreEqual(originalVertices, source.vertices);
            CollectionAssert.AreEqual(source.uv, result.Mesh.uv);
            CollectionAssert.AreEqual(source.triangles, result.Mesh.triangles);
            Assert.AreEqual(source.blendShapeCount, result.Mesh.blendShapeCount);
            Assert.AreEqual(2, result.Mesh.GetBlendShapeFrameCount(2));
            Assert.AreEqual(35, result.Mesh.GetBlendShapeFrameWeight(2, 0));
            Assert.AreEqual(100, result.Mesh.GetBlendShapeFrameWeight(2, 1));
            var resultDeltas = new Vector3[source.vertexCount];
            result.Mesh.GetBlendShapeFrameVertices(2, 1, resultDeltas, null, null);
            CollectionAssert.AreEqual(originalDeltas, resultDeltas);
            for (int i = 0; i < source.blendShapeCount; i++)
                Assert.AreEqual(source.GetBlendShapeName(i), result.Mesh.GetBlendShapeName(i));
        }

        [TestCase(PoseAnimationPathMode.Absolute, false)]
        [TestCase(PoseAnimationPathMode.Relative, false)]
        [TestCase(PoseAnimationPathMode.Relative, true)]
        public void PathModesResolveFromExpectedRoots(PoseAnimationPathMode mode, bool customRoot)
        {
            Object.DestroyImmediate(component);
            var group = Child("Group");
            renderer.transform.SetParent(group.transform, false);
            component = group.AddComponent<BlendShapePoseOverride>();
            component.PathMode = mode;
            component.RelativePathRoot = customRoot ? renderer.transform : null;
            component.Overrides.Clear();
            string path = mode == PoseAnimationPathMode.Absolute ? "Group/Face" : customRoot ? "" : "Face";
            Entry("Close", Clip(path, ("Close", 50)));
            Assert.AreEqual(50, Resolve().Poses.Single().Weights["Close"]);
        }

        [Test]
        public void LongClipUsesTimeZeroAndOtherRendererCurvesAreIgnored()
        {
            var clip = Clip("Face", ("Close", 30));
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Close"),
                AnimationCurve.Linear(0, 30, 1, 90));
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Missing", typeof(SkinnedMeshRenderer), "blendShape.Close"),
                AnimationCurve.Constant(0, 1, 100));
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Face", typeof(Transform), "m_LocalPosition.x"),
                AnimationCurve.Constant(0, 1, 5));
            Entry("Close", clip);
            var plan = Resolve();
            Assert.AreEqual(30, plan.Poses[0].Weights["Close"]);
            Assert.IsTrue(PoseOverrideConfiguration.HasMultipleFrames(clip));
            Assert.IsTrue(plan.Warnings.Any(w => w.Contains("Ignoring 2")));
            Assert.AreEqual(Vector3.zero, renderer.transform.localPosition);
        }

        [Test]
        public void InvalidPathsMissingShapesAndDuplicateTargetsAreRejected()
        {
            Entry("Close", Clip("Wrong", ("Close", 60)));
            Assert.IsFalse(PoseOverrideConfiguration.TryResolve(component, out _, out var error));
            StringAssert.Contains("No first-frame", error);
            component.Overrides[0].Animation = Clip("Face", ("Missing", 50));
            Assert.IsFalse(PoseOverrideConfiguration.TryResolve(component, out _, out error));
            StringAssert.Contains("missing blendshape", error);
            component.Overrides[0].Animation = Clip("Face", ("Close", 60));
            Entry("Close", Clip("Face", ("Smile", 50)));
            Assert.IsFalse(PoseOverrideConfiguration.TryResolve(component, out _, out error));
            StringAssert.Contains("already overridden", error);
        }

        [Test]
        public void MultipleComponentsCanUseDifferentRenderersButNotTheSameRenderer()
        {
            Entry("Close", Clip("Face", ("Close", 60)));
            var other = Child("Other Settings").AddComponent<BlendShapePoseOverride>();
            other.TargetMesh = renderer;
            other.Overrides.Add(new BlendShapePoseEntry { BlendShapeName = "Close", Animation = component.Overrides[0].Animation });
            Assert.IsFalse(PoseOverrideConfiguration.TryResolve(component, out _, out var error));
            StringAssert.Contains("Only one", error);
            other.TargetMesh = CreateRenderer("Another Face");
            Resolve();
        }

        [Test]
        public void VrcPresetResolvesOnlyTheDescriptorSlotWithoutANameFallback()
        {
            AddFrame(renderer.sharedMesh, "vrc.Blink", 100, Vector3.down);
            descriptor.customEyeLookSettings.eyelidsBlendshapes[0] = 3;
            Assert.IsTrue(PoseOverridePresets.TryGetVrcBlink(component, out var name, out var error), error);
            Assert.AreEqual("Alternate", name);
            Assert.AreEqual(1, PoseOverridePresets.AddMissingEntries(component, new[] { name }));
            Assert.IsNull(component.Overrides[0].Animation);
            descriptor.customEyeLookSettings.eyelidsBlendshapes[0] = -1;
            Assert.IsFalse(PoseOverridePresets.TryGetVrcBlink(component, out _, out _));
            Assert.GreaterOrEqual(renderer.sharedMesh.GetBlendShapeIndex("vrc.Blink"), 0);
        }

        [Test]
        public void VrcPresetDoesNotApplyTheDescriptorIndexToADifferentTargetMesh()
        {
            component.TargetMesh = CreateRenderer("Other Face");
            Assert.IsFalse(PoseOverridePresets.TryGetVrcBlink(component, out _, out var error));
            StringAssert.Contains("Set Target Mesh", error);
            Assert.IsEmpty(component.Overrides);
        }

        [Test]
        public void MmdPresetUsesSevenExactNamesWithoutASeparatorAndKeepsIndependentMappings()
        {
            // The spellings are taken from the scene mesh, including its half-width right-wink name.
            var names = new[]
            {
                "\u307e\u3070\u305f\u304d", "\u7b11\u3044", "\u30a6\u30a3\u30f3\u30af",
                "\u30a6\u30a3\u30f3\u30af\u53f3", "\u30a6\u30a3\u30f3\u30af\uff12",
                "\uff73\uff68\uff9d\uff78\uff12\u53f3", "\u306a\u3054\u307f"
            };
            foreach (string name in names) AddFrame(renderer.sharedMesh, name, 100, Vector3.down);
            AddFrame(renderer.sharedMesh, "\u306f\u3045", 100, Vector3.up);
            descriptor.customEyeLookSettings.eyelidsSkinnedMesh = null;
            var available = PoseOverridePresets.GetAvailableMmdShapes(renderer.sharedMesh);
            CollectionAssert.AreEqual(names, available);
            Assert.AreEqual(7, PoseOverridePresets.AddMissingEntries(component, available));
            CollectionAssert.AreEqual(names, component.Overrides.Select(e => e.BlendShapeName));
            Assert.IsTrue(component.Overrides.All(e => e.Animation == null));
            var blinkClip = Clip("Face", ("Close", 65));
            var smileClip = Clip("Face", ("Smile", 75));
            component.Overrides[0].Animation = blinkClip;
            component.Overrides[1].Animation = smileClip;
            Assert.AreEqual(0, PoseOverridePresets.AddMissingEntries(component, available));
            Assert.AreEqual(7, component.Overrides.Count);
            Assert.AreSame(blinkClip, component.Overrides[0].Animation);
            Assert.AreSame(smileClip, component.Overrides[1].Animation);
            Assert.IsTrue(component.Overrides.Skip(2).All(e => e.Animation == null));
        }

        [Test]
        public void EmptyListBuildIsANoOpAndDoesNotRequireATargetMesh()
        {
            var source = renderer.sharedMesh;
            component.TargetMesh = null;
            Assert.IsEmpty(Resolve().Poses);
            BlendShapePoseOverridePlugin.Apply(new BuildContext(root, null));
            Assert.AreSame(source, renderer.sharedMesh);
            Assert.IsNull(root.GetComponent<BlendShapePoseOverride>());
        }

        [Test]
        public void BuildProcessesInactiveObjectsStripsComponentsAndIgnoresPreviewWeight()
        {
            Entry("Close", Clip("Face", ("Close", 65)));
            var basis = Bake();
            var desired = Bake(("Close", 65));
            component.PreviewWeight = 83;
            var disabled = Child("Disabled Settings").AddComponent<BlendShapePoseOverride>();
            disabled.enabled = false;
            renderer.gameObject.SetActive(false);
            var context = new BuildContext(root, null);
            BlendShapePoseOverridePlugin.Apply(context);
            owned.Add(renderer.sharedMesh);
            Assert.IsEmpty(root.GetComponentsInChildren<BlendShapePoseOverride>(true));
            renderer.gameObject.SetActive(true);
            AssertPose(basis, Bake());
            AssertPose(desired, Bake(("Close", 100)));
        }

        [Test]
        public void PreviewMatchesBuildOutputWithoutChangingOriginalMeshOrWeights()
        {
            Entry("Close", Clip("Face", ("Close", 65), ("Customize", 70)));
            var source = renderer.sharedMesh;
            var desired = Bake(("Close", 65), ("Customize", 70));
            var proxy = Child("Proxy").AddComponent<SkinnedMeshRenderer>();
            proxy.sharedMesh = source;
            proxy.SetBlendShapeWeight(0, 40);
            component.PreviewWeight = 100;
            var filter = new PoseOverridePreview();
            var pairs = new[] { ((Renderer)renderer, (Renderer)proxy) };
            var node = filter.Instantiate(RenderGroup.For(renderer).WithData(component, (a, b) => a == b), pairs, new ComputeContext("Pose Override Test")).GetAwaiter().GetResult();
            try
            {
                node.OnFrame(renderer, proxy);
                var baked = new Mesh();
                owned.Add(baked);
                proxy.BakeMesh(baked, true);
                AssertPose(desired, baked);
                Assert.AreSame(source, renderer.sharedMesh);
                Assert.AreEqual(0, renderer.GetBlendShapeWeight(1));
                Assert.AreEqual(40, renderer.GetBlendShapeWeight(0));
            }
            finally { node.Dispose(); }
        }
    }
}
