using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.ObjectOptimization;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.Dynamics;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.Constraint.Components;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        [Test]
        public void NativeAnimatedConstraintSourcesKeepOffsetsAndMovingOutput(
            [Values(typeof(VRCParentConstraint), typeof(VRCPositionConstraint), typeof(VRCRotationConstraint),
                typeof(VRCScaleConstraint), typeof(VRCAimConstraint), typeof(VRCLookAtConstraint))] Type type,
            [Values(false, true)] bool overflow, [Values(1, 2, 3)] int survivors)
        {
            bool singleSurvivor = survivors == 1;
            var previousScene = SceneManager.GetActiveScene();
            var path = AssetDatabase.GUIDToAssetPath("4e3531870ac74a99b7804047f9db1d96");
            Assert.That(SceneManager.GetSceneByPath(path).isLoaded, Is.False);
            var nativeScene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
            SceneManager.MoveGameObjectToScene(root, nativeScene);
            try
            {
                var settings = ObjectSettings();
                var inputs = Enumerable.Range(0, 4).Select(i => PolicyChild("Input " + i).transform).ToArray();
                var output = PolicyChild("Output").transform; var expected = PolicyChild("Expected").transform;
                var c = ConstraintWithZeroSources(type, inputs[0], inputs[3], output, "Writer");
                var reference = ConstraintWithZeroSources(type, inputs[0], inputs[3], expected, "Reference");
                settings.ProtectedComponents.Add(ObjectRegistry.GetReference(reference));
                int count = overflow ? 19 : 5;
                var sources = Enumerable.Range(0, count).Select(i => new VRCConstraintSource(
                    i == 1 ? inputs[0] : !singleSurvivor && i == 3 ? inputs[1] :
                        survivors == 3 && i == count - 1 ? inputs[2] : inputs[3],
                    i == 1 || survivors >= 2 && i == 3 || survivors == 3 && i == count - 1 ? 1 : 0)
                    { ParentPositionOffset = new Vector3(0.1f * i, 0.02f * i, -0.03f * i),
                      ParentRotationOffset = new Vector3(i, -2 * i, 3 * i) }).ToArray();
                c.Sources = new VRCConstraintSourceKeyableList(sources);
                reference.Sources = new VRCConstraintSourceKeyableList(sources);
                c.ApplyConfigurationChanges(); reference.ApplyConfigurationChanges();
                var clip = Clip(); clip.name = "Animated sources";
                void Curve(string property, AnimationCurve curve) => AnimationUtility.SetEditorCurve(clip,
                    EditorCurveBinding.FloatCurve("Writer", type, property), curve);
                Curve("Sources.source0.Weight", AnimationCurve.Constant(0, 2, 0));
                Curve("Sources.source1.Weight", AnimationCurve.Linear(0, 0, 2, 1));
                if (!singleSurvivor)
                {
                    Curve("Sources.source3.Weight", AnimationCurve.Linear(0, 0, 2, 0.7f));
                    if (!overflow && survivors == 3) Curve("Sources.source4.Weight", AnimationCurve.Linear(0, 0, 2, 0.4f));
                }
                Curve("GlobalWeight", AnimationCurve.Linear(0, 0.2f, 2, 1));
                if (type == typeof(VRCParentConstraint))
                {
                    Curve("Sources.source0.ParentPositionOffset.x", AnimationCurve.Linear(0, 2, 2, 30));
                    string offsetSource = singleSurvivor ? "Sources.source1." : "Sources.source3.";
                    Curve(offsetSource + "ParentPositionOffset.x", AnimationCurve.Linear(0, 0.2f, 2, 1.7f));
                    Curve(offsetSource + "ParentRotationOffset.z", AnimationCurve.Linear(0, -70, 2, 95));
                }
                var referenceClip = Track(UnityEngine.Object.Instantiate(clip));
                foreach (var binding in AnimationUtility.GetCurveBindings(referenceClip))
                {
                    var curve = AnimationUtility.GetEditorCurve(referenceClip, binding);
                    AnimationUtility.SetEditorCurve(referenceClip, binding, null);
                    var rebound = binding; rebound.path = "Reference";
                    AnimationUtility.SetEditorCurve(referenceClip, rebound, curve);
                }
                var original = EditorJsonUtility.ToJson(clip);
                var controllers = ConstraintControllers(clip);
                EvaluateNativeConstraints(c, reference);
                Assert.That(PruneConstraintSources(settings, controllers), Is.EqualTo(count - Math.Max(2, survivors)));
                Assert.That(c.Sources.Select(s => s.SourceTransform),
                    Is.EqualTo(singleSurvivor ? new[] { inputs[0], inputs[0] } : inputs.Take(survivors)));
                var built = CommitAnimator(controllers.Values.Single(), root);
                var result = built.animationClips.Single();
                Assert.That(result.length, Is.EqualTo(clip.length));
                var bindings = AnimationUtility.GetCurveBindings(result);
                Assert.That(bindings.Any(b => b.propertyName == "Sources.source0.Weight"), Is.True);
                Assert.That(bindings.Any(b => b.propertyName == "Sources.source1.Weight"), Is.EqualTo(!singleSurvivor));
                Assert.That(bindings.Any(b => b.propertyName == "Sources.source3.Weight"), Is.False);
                float greatestMotion = 0;
                foreach (float time in new[] { 0f, 0.1f, 0.4f, 0.8f, 1.2f, 1.6f, 2f })
                {
                    root.transform.localScale = new Vector3(1 + time, 1 + 0.2f * time, 1 + 0.5f * time);
                    root.transform.localRotation = Quaternion.Euler(10 * time, 20 * time, -15 * time);
                    for (int i = 0; i < inputs.Length; i++)
                    {
                        inputs[i].localPosition = new Vector3(i + 1 + time, 2 - i * time, 3 + i);
                        inputs[i].localRotation = Quaternion.Euler(170 - i * 110 + 20 * time, i * 100 - 175, 45 * i);
                        inputs[i].localScale = new Vector3(1 + i + time, 2 + time, 3 + i);
                    }
                    referenceClip.SampleAnimation(root, time); result.SampleAnimation(root, time);
                    c.ApplyConfigurationChanges(); reference.ApplyConfigurationChanges();
                    EvaluateNativeConstraints();
                    var actualMatrix = output.localToWorldMatrix; var expectedMatrix = expected.localToWorldMatrix;
                    for (int i = 0; i < 16; i++)
                        Assert.That(actualMatrix[i], Is.EqualTo(expectedMatrix[i]).Within(0.0001f),
                            type.Name + " at time " + time + ", matrix index " + i);
                    greatestMotion = Mathf.Max(greatestMotion, (output.localPosition - Vector3.zero).magnitude,
                        Quaternion.Angle(output.localRotation, Quaternion.identity), (output.localScale - Vector3.one).magnitude);
                }
                Assert.That(greatestMotion, Is.GreaterThan(0.1f), "The native solver must produce a nontrivial output");
                Assert.That(EditorJsonUtility.ToJson(clip), Is.EqualTo(original));
                // Restore the stored source values before checking analysis idempotence.
                c.Sources = new VRCConstraintSourceKeyableList(singleSurvivor ?
                    new[] { sources[1], new VRCConstraintSource(inputs[0], 0) } :
                    survivors == 2 ? new[] { sources[1], sources[3] } : new[] { sources[1], sources[3], sources[count - 1] });
                c.ApplyConfigurationChanges();
                Assert.That(PruneConstraintSources(settings, controllers), Is.Zero);
            }
            finally
            {
                SceneManager.MoveGameObjectToScene(root, scene);
                if (previousScene.IsValid() && previousScene.isLoaded) SceneManager.SetActiveScene(previousScene);
                EditorSceneManager.CloseScene(nativeScene, true);
            }
        }

        [TestCase("Count")] [TestCase("DormantSlot")] [TestCase("OverflowBinding")]
        [TestCase("UnknownMember")] [TestCase("FreezeAnimation")] [TestCase("UnlockAnimation")]
        public void AnimatedSourceCompactionRejectsUnprovenStructure(string condition)
        {
            var settings = ObjectSettings(); var input = PolicyChild("Input").transform;
            var unused = PolicyChild("Unused").transform; var output = PolicyChild("Output").transform;
            var c = ConstraintWithZeroSources(typeof(VRCParentConstraint), input, unused, output, "Writer");
            string property = condition == "Count" ? "Sources.totalLength" :
                condition == "DormantSlot" ? "Sources.source15.Weight" :
                condition == "OverflowBinding" ? "Sources.overflowList.Array.data[0].Weight" :
                condition == "UnknownMember" ? "Sources.source1.FutureField" :
                condition == "FreezeAnimation" ? "FreezeToWorld" : "Locked";
            var clip = Clip(); AnimationUtility.SetEditorCurve(clip,
                EditorCurveBinding.FloatCurve("Writer", typeof(VRCParentConstraint), property), AnimationCurve.Linear(0, 0, 1, 1));
            Assert.That(PruneConstraintSources(settings, ConstraintControllers(clip)), Is.Zero);
            Assert.That(c.Sources.Count, Is.EqualTo(3));
        }

        [TestCase(false)] [TestCase(true)]
        public void NativeSourceCompactionPreservesWriteDefaultsAndTransitionHistory(bool writeDefaults)
        {
            Mesh("Face");
            var previousScene = SceneManager.GetActiveScene();
            var nativeScene = EditorSceneManager.OpenScene(AssetDatabase.GUIDToAssetPath("4e3531870ac74a99b7804047f9db1d96"), OpenSceneMode.Additive);
            SceneManager.MoveGameObjectToScene(root, nativeScene);
            try
            {
                var settings = ObjectSettings(); var input = PolicyChild("Input").transform;
                var unused = PolicyChild("Unused").transform;
                var output = PolicyChild("Output").transform; var expected = PolicyChild("Expected").transform;
                var c = ConstraintWithZeroSources(typeof(VRCParentConstraint), input, unused, output, "Writer");
                var reference = ConstraintWithZeroSources(typeof(VRCParentConstraint), input, unused, expected, "Reference");
                settings.ProtectedComponents.Add(ObjectRegistry.GetReference(reference));
                var clip = Clip();
                foreach (string owner in new[] { "Writer", "Reference" })
                {
                    AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(owner, typeof(VRCParentConstraint),
                        "Sources.source1.Weight"), AnimationCurve.Constant(0, 0, 0.3f));
                    AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(owner, typeof(VRCParentConstraint),
                        "Sources.source1.ParentPositionOffset.x"), AnimationCurve.Constant(0, 0, 2));
                }
                var source = Controller(clip, Clip()); source.AddParameter("Toggle", AnimatorControllerParameterType.Bool);
                var states = source.layers[0].stateMachine.states.Select(s => s.state).ToArray();
                foreach (var state in states) state.writeDefaultValues = writeDefaults;
                Edge(states[0], states[1], "Toggle", 0, AnimatorConditionMode.If);
                Edge(states[1], states[0], "Toggle", 0, AnimatorConditionMode.IfNot);
                var virtualController = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
                var entries = new Dictionary<object, VirtualAnimatorController> { [VRCAvatarDescriptor.AnimLayerType.FX] = virtualController };
                Assert.That(ConstraintSourcePruning.Apply(root, entries, null, settings, new[] { "Toggle" }, new List<string>()), Is.EqualTo(1));
                var built = CommitAnimator(virtualController, root);
                EvaluateNativeConstraints(c, reference);
                var samples = SampleFrozen(built, (p, frame) =>
                    { p.SetBool("Toggle", frame % 8 >= 4); input.localPosition = new Vector3(frame * 0.2f, 1, 2); }, 24, () =>
                    {
                        c.ApplyConfigurationChanges(); reference.ApplyConfigurationChanges(); EvaluateNativeConstraints();
                        for (int i = 0; i < 16; i++) Assert.That(output.localToWorldMatrix[i],
                            Is.EqualTo(expected.localToWorldMatrix[i]).Within(0.0001f));
                        Assert.That(c.Sources[0].Weight, Is.EqualTo(reference.Sources[1].Weight));
                        return c.Sources[0].Weight;
                    });
                Assert.That(samples, Does.Contain(0.3f));
                Assert.That(samples.Any(v => v == 1), Is.EqualTo(writeDefaults), "Default restoration differs from latched animation values");
            }
            finally
            {
                SceneManager.MoveGameObjectToScene(root, scene);
                if (previousScene.IsValid() && previousScene.isLoaded) SceneManager.SetActiveScene(previousScene);
                EditorSceneManager.CloseScene(nativeScene, true);
            }
        }

        [TestCase(0.3f)] [TestCase(2f)]
        public void NormalizationAliasReleasesAnUnusedHierarchyWithoutReducingSlotCount(float weight)
        {
            var settings = ObjectSettings(); var input = PolicyChild("Live source").transform;
            var unused = PolicyChild("Unused source").transform; var target = PolicyChild("Skin bone").transform;
            renderer.bones = new[] { target }; renderer.rootBone = target;
            var c = ConstraintWithZeroSources(typeof(VRCParentConstraint), input, unused, target, "Writer");
            c.Sources = new VRCConstraintSourceKeyableList(new[]
                { new VRCConstraintSource(unused, 0), new VRCConstraintSource(input, weight) });
            c.ApplyConfigurationChanges();
            var controllers = ConstraintControllers();
            Assert.That(PruneConstraintSources(settings, controllers), Is.GreaterThan(0), "Reference replacement is dependency progress");
            Assert.That(c.Sources.Count, Is.EqualTo(2));
            Assert.That(c.Sources.Select(s => s.SourceTransform), Is.EqualTo(new[] { input, input }));
            Assert.That(c.Sources.Select(s => s.Weight), Is.EqualTo(new[] { weight, 0 }));
            CollectObjects(settings, entries: controllers);
            Assert.That(unused == null, Is.True); Assert.That(c != null && input != null && target != null, Is.True);
            Assert.That(PruneConstraintSources(settings, controllers), Is.Zero, "An existing normalization alias is stable");
        }

        [TestCase(false)] [TestCase(true)]
        public void AnimatedSourceCompactionRetainsSharedBindingTargets(bool mappedPath)
        {
            var settings = ObjectSettings(); var input = PolicyChild("Input").transform;
            var unused = PolicyChild("Unused").transform; var target = PolicyChild("Target").transform;
            var first = ConstraintWithZeroSources(typeof(VRCParentConstraint), input, unused, target, "Writer");
            var other = PolicyChild("Other root"); var child = PolicyChild(mappedPath ? "Old writer" : "Writer", other.transform);
            var second = child.AddComponent<VRCParentConstraint>(); second.Sources = new VRCConstraintSourceKeyableList(first.Sources.ToArray());
            second.GlobalWeight = 1; second.Locked = true; second.ApplyConfigurationChanges();
            var animator = other.AddComponent<Animator>();
            var clip = StaticCurve(EditorCurveBinding.FloatCurve(mappedPath ? "Old writer" : "Writer", typeof(VRCParentConstraint), "Sources.source1.Weight"), 1);
            var controller = Controller(clip); animator.runtimeAnimatorController = controller;
            var virtualController = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(controller);
            var entries = new Dictionary<object, VirtualAnimatorController>
                { [VRCAvatarDescriptor.AnimLayerType.FX] = virtualController, [animator] = virtualController };
            var paths = mappedPath ? new Dictionary<string, string> { ["Old writer"] = "Writer" } : null;
            Assert.That(ConstraintSourcePruning.Apply(root, entries, paths, settings, null, new List<string>()), Is.Zero);
            Assert.That(first.Sources.Count, Is.EqualTo(3)); Assert.That(second.Sources.Count, Is.EqualTo(3));
        }
    }
}
