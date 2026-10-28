using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.ObjectOptimization;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.Dynamics;
using VRC.SDK3.Dynamics.Constraint.Components;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private VRCConstraintBase ConstraintWithZeroSources(Type type, Transform input, Transform unused, Transform target, string name)
        {
            var c = (VRCConstraintBase)PolicyChild(name).AddComponent(type);
            c.TargetTransform = target;
            c.Sources.Add(new VRCConstraintSource(unused, 0));
            c.Sources.Add(new VRCConstraintSource(input, 1));
            c.Sources.Add(new VRCConstraintSource(unused, 0));
            c.GlobalWeight = 1; c.IsActive = true; c.Locked = true;
            if (c is VRCParentConstraint parent)
            {
                parent.AffectsPositionX = parent.AffectsPositionY = parent.AffectsPositionZ = true;
                parent.AffectsRotationX = parent.AffectsRotationY = parent.AffectsRotationZ = true;
            }
            if (c is VRCScaleConstraint scale) scale.ScaleAtRest = scale.ScaleOffset = Vector3.one;
            c.ApplyConfigurationChanges(); return c;
        }

        private int PruneConstraintSources(ObjectOptimizationState settings, Dictionary<object, VirtualAnimatorController> controllers)
            => ConstraintSourcePruning.Apply(root, controllers, null, settings, null, new List<string>());

        [TestCase(2, false)] [TestCase(20, false)] [TestCase(2, true)] [TestCase(20, true)]
        public void ConstraintSourcePruningReleasesUnusedHierarchiesIncludingOverflowSources(int zeroCount, bool preserveSource)
        {
            var settings = ObjectSettings(); var input = PolicyChild("Input").transform;
            var unused = PolicyChild("Unused source").transform; var child = PolicyChild("Unused child", unused);
            var target = PolicyChild("Skin bone").transform;
            renderer.bones = new[] { target }; renderer.rootBone = target;
            var c = ConstraintWithZeroSources(typeof(VRCParentConstraint), input, unused, target, "Writer");
            for (int i = 2; i < zeroCount; i++) c.Sources.Add(new VRCConstraintSource(unused, 0));
            if (preserveSource) settings.ProtectedComponents.Add(ObjectRegistry.GetReference(unused));
            var controllers = ConstraintControllers();
            Assert.That(PruneConstraintSources(settings, controllers), Is.EqualTo(zeroCount));
            CollectObjects(settings, entries: controllers);
            Assert.That(c != null && input != null && target != null, Is.True);
            Assert.That(unused != null, Is.EqualTo(preserveSource));
            Assert.That(child == null, Is.True, "An unobserved child has no remaining source dependency.");
        }

        [TestCase(typeof(VRCParentConstraint))] [TestCase(typeof(VRCPositionConstraint))]
        [TestCase(typeof(VRCRotationConstraint))] [TestCase(typeof(VRCScaleConstraint))]
        [TestCase(typeof(VRCAimConstraint))] [TestCase(typeof(VRCLookAtConstraint))]
        public void NativeConstraintZeroSourcePruningPreservesMovingOutput(Type type)
        {
            var originalScene = SceneManager.GetActiveScene();
            var path = AssetDatabase.GUIDToAssetPath("4e3531870ac74a99b7804047f9db1d96");
            Assert.That(SceneManager.GetSceneByPath(path).isLoaded, Is.False);
            var nativeScene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
            SceneManager.MoveGameObjectToScene(root, nativeScene);
            try
            {
                var settings = ObjectSettings(); var input = PolicyChild("Input").transform;
                var unused = PolicyChild("Unused source").transform;
                var output = PolicyChild("Output").transform; var control = PolicyChild("Control").transform;
                input.localPosition = new Vector3(1, 2, 3); input.localRotation = Quaternion.Euler(20, 30, 40);
                input.localScale = new Vector3(2, 3, 4); unused.localPosition = new Vector3(-4, 6, 8);
                var c = ConstraintWithZeroSources(type, input, unused, output, "Writer");
                var reference = ConstraintWithZeroSources(type, input, unused, control, "Reference");
                settings.ProtectedComponents.Add(ObjectRegistry.GetReference(reference));
                var clip = Clip();
                for (int i = 0; i < 3; i++) AnimationUtility.SetEditorCurve(clip,
                    EditorCurveBinding.FloatCurve("Writer", type, "Sources.source" + i + ".Weight"),
                    AnimationCurve.Constant(0, 1, i == 1 ? 1 : 0));
                var controllers = ConstraintControllers(clip);
                EvaluateNativeConstraints(c, reference);
                Assert.That(output.localToWorldMatrix, Is.Not.EqualTo(Matrix4x4.identity), "The actual SDK solver must affect the fixture.");
                Assert.That(PruneConstraintSources(settings, controllers), Is.EqualTo(2));
                Assert.That(c.Sources.Count, Is.EqualTo(1)); Assert.That(c.Sources[0].SourceTransform, Is.SameAs(input));
                Assert.That(controllers.Values.SelectMany(v => v.AllReachableNodes()).OfType<VirtualClip>()
                    .SelectMany(v => v.GetFloatCurveBindings()).Where(b => b.type == type), Is.Empty,
                    "Old source indices must not survive array compaction.");
                foreach (var rootScale in new[] { Vector3.one, Vector3.one * 2.5f, new Vector3(2, 3, 4) })
                {
                    root.transform.localScale = rootScale; root.transform.localRotation = Quaternion.Euler(15, 25, 35);
                    input.localPosition += Vector3.one; input.localRotation *= Quaternion.Euler(10, 20, 30);
                    input.localScale += Vector3.one; unused.localPosition *= -3; unused.localScale = -Vector3.one;
                    EvaluateNativeConstraints();
                    for (int i = 0; i < 16; i++) Assert.That(output.localToWorldMatrix[i], Is.EqualTo(control.localToWorldMatrix[i]).Within(0.00001f));
                }
                Assert.That(PruneConstraintSources(settings, controllers), Is.Zero);
            }
            finally
            {
                SceneManager.MoveGameObjectToScene(root, scene);
                if (originalScene.IsValid() && originalScene.isLoaded) SceneManager.SetActiveScene(originalScene);
                EditorSceneManager.CloseScene(nativeScene, true);
            }
        }

        [TestCase("AnimatedWeight")] [TestCase("ProtectedConstraint")] [TestCase("ProtectedTarget")]
        [TestCase("ExternalReader")] [TestCase("OutsideScope")] [TestCase("AllZero")]
        [TestCase("MissingPositiveSource")] [TestCase("NegativeWeight")] [TestCase("NonfiniteWeight")]
        [TestCase("FrozenWorld")] [TestCase("Unlocked")] [TestCase("ObjectCurve")]
        [TestCase("Events")] [TestCase("AdditiveLayer")] [TestCase("MaskedLayer")]
        public void ConstraintZeroSourcePruningRetainsUnprovenOrObservedSettings(string condition)
        {
            var settings = ObjectSettings(); var input = PolicyChild("Input").transform;
            var unused = PolicyChild("Unused").transform; var output = PolicyChild("Output").transform;
            var c = ConstraintWithZeroSources(typeof(VRCParentConstraint), input, unused, output, "Writer");
            var clip = Clip(); var binding = EditorCurveBinding.FloatCurve("Writer", typeof(VRCParentConstraint), "Sources.source0.Weight");
            AnimationUtility.SetEditorCurve(clip, binding, AnimationCurve.Constant(0, 1, 0));
            switch (condition)
            {
                case "AnimatedWeight": AnimationUtility.SetEditorCurve(clip, binding, AnimationCurve.Linear(0, 0, 1, 1)); break;
                case "ProtectedConstraint": settings.ProtectedComponents.Add(ObjectRegistry.GetReference(c)); break;
                case "ProtectedTarget": settings.ProtectedComponents.Add(ObjectRegistry.GetReference(output)); break;
                case "ExternalReader": PolicyChild("Reader").AddComponent<DependencyAuthoringFixture>().Target = c; break;
                case "OutsideScope": settings.Scope.Root = output; settings.Scope.OriginalTransforms.Clear(); break;
                case "AllZero": c.Sources[1] = new VRCConstraintSource(input, 0); break;
                case "MissingPositiveSource": c.Sources[1] = new VRCConstraintSource(null, 1); break;
                case "NegativeWeight": c.Sources[1] = new VRCConstraintSource(input, -1); break;
                case "NonfiniteWeight": c.Sources[1] = new VRCConstraintSource(input, float.NaN); break;
                case "FrozenWorld": c.FreezeToWorld = true; break;
                case "Unlocked": c.Locked = false; break;
                case "ObjectCurve": AnimationUtility.SetObjectReferenceCurve(clip,
                    EditorCurveBinding.PPtrCurve("Writer", typeof(VRCParentConstraint), "Sources.source0.SourceTransform"),
                    new[] { new ObjectReferenceKeyframe { time = 0, value = input } }); break;
                case "Events": AnimationUtility.SetAnimationEvents(clip, new[] { new AnimationEvent { time = 0, functionName = "ChangeSource" } }); break;
            }
            var controllers = ConstraintControllers(clip);
            if (condition == "AdditiveLayer") controllers.Values.Single().Layers.First().BlendingMode = AnimatorLayerBlendingMode.Additive;
            if (condition == "MaskedLayer") controllers.Values.Single().Layers.First().AvatarMask =
                new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Track(new AvatarMask()));
            bool partial = condition == "AnimatedWeight" || condition == "AdditiveLayer" || condition == "MaskedLayer";
            Assert.That(PruneConstraintSources(settings, controllers), Is.EqualTo(partial ? 1 : 0));
            Assert.That(c.Sources.Count, Is.EqualTo(partial ? 2 : 3));
            if (partial) Assert.That(controllers.Values.SelectMany(v => v.AllReachableNodes()).OfType<VirtualClip>()
                .SelectMany(v => v.GetFloatCurveBindings()), Does.Contain(binding), "The unproven animated source stays intact");
        }
    }
}
