using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.ObjectOptimization;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.Dynamics;
using VRC.SDK3.Dynamics.Constraint.Components;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private static IEnumerable<TestCaseData> SelfTargetDependencyCases()
        {
            foreach (var type in new[] { typeof(VRCAimConstraint), typeof(VRCLookAtConstraint),
                typeof(VRCParentConstraint), typeof(VRCPositionConstraint), typeof(VRCRotationConstraint), typeof(VRCScaleConstraint) })
            foreach (bool missingWrapper in new[] { false, true })
            foreach (bool observed in new[] { false, true })
                yield return new TestCaseData(type, missingWrapper, observed);
        }

        private VRCConstraintBase SelfTargetConstraint(Type type, bool missingWrapper)
        {
            var owner = PolicyChild("Constraint owner");
            var constraint = (VRCConstraintBase)owner.AddComponent(type);
            constraint.IsActive = false;
            constraint.Sources.Add(new VRCConstraintSource(PolicyChild("Constraint input").transform, 1));
            constraint.TargetTransform = null;
            if (missingWrapper)
            {
                var missing = PolicyChild("Removed target");
                constraint.TargetTransform = missing.transform;
                UnityEngine.Object.DestroyImmediate(missing);
                Assert.That(ReferenceEquals(constraint.TargetTransform, null), Is.False);
            }
            Assert.That(constraint.TargetTransform == null, Is.True);
            return constraint;
        }

        [TestCaseSource(nameof(SelfTargetDependencyCases))]
        public void SelfTargetDependenciesCollectOnlyUnobservedConstraintOutputs(Type type, bool missingWrapper, bool observed)
        {
            var settings = ObjectSettings();
            var constraint = SelfTargetConstraint(type, missingWrapper);
            var owner = constraint.gameObject; var input = constraint.Sources[0].SourceTransform;
            if (observed) renderer.bones = new[] { owner.transform };
            var clip = InitialTransformClip(owner.transform); var original = EditorJsonUtility.ToJson(clip);
            var entries = ConstraintControllers(clip);
            var index = AvatarAnimationIndex.Create(root, entries, null);
            var graph = AvatarDependencyGraph.Create(root, index, settings, collectObjects: true);
            Assert.That(graph.Constraints[constraint], Is.SameAs(owner.transform));
            Assert.That(graph.IsLive(constraint), Is.EqualTo(observed));

            UnusedTransformOutputs.Apply(root, entries, null, settings, null, new List<string>());
            CollectObjects(settings, entries: entries);
            Assert.That(constraint != null && owner != null && input != null, Is.EqualTo(observed));
            if (!observed) Assert.That(constraint == null && owner == null && input == null, Is.True);
            var output = entries.Values.Single().AllReachableNodes().OfType<VirtualClip>().Single();
            Assert.That(output.GetFloatCurveBindings().Any(b => b.path == "Constraint owner"), Is.EqualTo(observed));
            Assert.That(MotionAnalysis.EndTime(output), Is.EqualTo(.3f));
            Assert.That(EditorJsonUtility.ToJson(clip), Is.EqualTo(original));
        }

        [TestCase("ExternalTarget")] [TestCase("RootTarget")]
        [TestCase("PreservedConstraint")] [TestCase("PreservedSubtree")] [TestCase("OutsideScope")]
        [TestCase("SerializedReader")] [TestCase("ObjectCurve")] [TestCase("PreservedInput")]
        public void SelfTargetDependenciesRetainExternalObservedAndPreservedConstraints(string reason)
        {
            var settings = ObjectSettings(reason == "OutsideScope" ? PolicyChild("Scope") : root);
            var constraint = SelfTargetConstraint(typeof(VRCParentConstraint), true);
            var owner = constraint.gameObject; var input = constraint.Sources[0].SourceTransform;
            var clip = reason == "PreservedInput" ? InitialTransformClip(owner.transform) : Clip();
            if (reason == "ExternalTarget")
            {
                var external = Track(new GameObject("Another avatar output")); SceneManager.MoveGameObjectToScene(external, scene);
                constraint.TargetTransform = external.transform;
            }
            if (reason == "RootTarget") constraint.TargetTransform = root.transform;
            if (reason == "PreservedConstraint") settings.ProtectedComponents.Add(ObjectRegistry.GetReference(constraint));
            if (reason == "PreservedSubtree") settings.ProtectedSubtrees.Add(new OptimizationBuildScope { Root = owner.transform });
            if (reason == "SerializedReader") root.AddComponent<DependencyAuthoringFixture>().Target = constraint;
            if (reason == "ObjectCurve")
            {
                root.AddComponent<DependencyAuthoringFixture>();
                AnimationUtility.SetObjectReferenceCurve(clip, EditorCurveBinding.PPtrCurve("", typeof(DependencyAuthoringFixture), "Target"),
                    new[] { new ObjectReferenceKeyframe { time = 0, value = constraint } });
            }
            var source = reason == "PreservedInput" ? TreeController(("Protected input", clip)) : Controller(clip);
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            CollectObjects(settings, controller, reason == "PreservedInput" ? new[] { "Protected input" } : null);
            Assert.That(constraint != null && owner != null && input != null, Is.EqualTo(reason != "PreservedInput"));
            if (reason == "PreservedInput") Assert.That(controller.Parameters.ContainsKey("Protected input"), Is.True);
        }

        [Test]
        public void SelfTargetDependencyCollectionPreservesNativeConstraintDrivenSkinning()
        {
            var originalScene = SceneManager.GetActiveScene();
            var scenePath = AssetDatabase.GUIDToAssetPath("4e3531870ac74a99b7804047f9db1d96");
            Assert.That(SceneManager.GetSceneByPath(scenePath).isLoaded, Is.False);
            var nativeScene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
            SceneManager.MoveGameObjectToScene(root, nativeScene);
            try
            {
                var settings = ObjectSettings();
                var live = (VRCRotationConstraint)SelfTargetConstraint(typeof(VRCRotationConstraint), true);
                live.IsActive = live.Locked = true; live.GlobalWeight = 1;
                live.AffectsRotationX = live.AffectsRotationY = live.AffectsRotationZ = true;
                live.ApplyConfigurationChanges();
                var input = live.Sources[0].SourceTransform;
                var output = AssemblyPart("Visible output", new[] { live.transform }, Vector3.right);
                var unused = SelfTargetConstraint(typeof(VRCParentConstraint), true);
                unused.name = "Unused constraint";
                var poses = new[] { Quaternion.identity, Quaternion.Euler(20, 35, -10) };
                var expected = new List<Vector3[]>();
                EvaluateNativeConstraints(live);
                foreach (var pose in poses)
                {
                    input.localRotation = pose; EvaluateNativeConstraints(); expected.Add(AssemblyWorldVertices(output));
                }
                Assert.That(expected[0].Where((v, i) => Vector3.Distance(v, expected[1][i]) > .01f), Is.Not.Empty);
                CollectObjects(settings);
                Assert.That(unused == null, Is.True);
                Assert.That(live != null && input != null && output != null, Is.True);
                for (int pose = 0; pose < poses.Length; pose++)
                {
                    input.localRotation = poses[pose]; EvaluateNativeConstraints();
                    var actual = AssemblyWorldVertices(output);
                    for (int vertex = 0; vertex < actual.Length; vertex++)
                        Assert.That(Vector3.Distance(actual[vertex], expected[pose][vertex]), Is.LessThan(.00001f));
                }
            }
            finally
            {
                SceneManager.MoveGameObjectToScene(root, scene);
                if (originalScene.IsValid() && originalScene.isLoaded) SceneManager.SetActiveScene(originalScene);
                EditorSceneManager.CloseScene(nativeScene, true);
            }
        }
    }
}
