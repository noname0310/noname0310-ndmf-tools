using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.Meshes;
using Noname.AvatarTools.ObjectOptimization;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using VRC.Dynamics;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.Constraint.Components;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private VRCScaleConstraint LocalScaleConstraint(Transform input, Transform target)
        {
            var constraint = PolicyChild("Scale writer").AddComponent<VRCScaleConstraint>();
            constraint.TargetTransform = target;
            constraint.Sources.Add(new VRCConstraintSource(input, 1));
            constraint.ScaleOffset = constraint.ScaleAtRest = Vector3.one;
            constraint.SolveInLocalSpace = true;
            constraint.GlobalWeight = 1;
            constraint.AffectsScaleX = constraint.AffectsScaleY = constraint.AffectsScaleZ = true;
            constraint.Locked = true; constraint.IsActive = true;
            constraint.ApplyConfigurationChanges();
            return constraint;
        }

        private Dictionary<object, VirtualAnimatorController> ConstraintControllers(AnimationClip clip = null) =>
            new() { [VRCAvatarDescriptor.AnimLayerType.FX] = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(clip ?? Clip())) };

        [TestCase(typeof(AimConstraint), false)] [TestCase(typeof(LookAtConstraint), false)]
        [TestCase(typeof(ParentConstraint), false)] [TestCase(typeof(PositionConstraint), false)]
        [TestCase(typeof(RotationConstraint), false)] [TestCase(typeof(ScaleConstraint), true)]
        [TestCase(typeof(VRCAimConstraint), false)] [TestCase(typeof(VRCLookAtConstraint), false)]
        [TestCase(typeof(VRCParentConstraint), false)] [TestCase(typeof(VRCPositionConstraint), false)]
        [TestCase(typeof(VRCRotationConstraint), false)] [TestCase(typeof(VRCScaleConstraint), true)]
        public void NativeConstraintWriterIndexSeparatesSourcesAndConfiguredOutputs(Type type, bool writesScale)
        {
            var input = PolicyChild("Input").transform;
            var owner = PolicyChild("Owner");
            var constraint = owner.AddComponent(type);
            Transform target;
            if (constraint is VRCConstraintBase sdk)
            {
                target = PolicyChild("Output").transform; sdk.TargetTransform = target;
                sdk.Sources.Add(new VRCConstraintSource(input, 1));
            }
            else
            {
                target = owner.transform;
                ((IConstraint)constraint).AddSource(new ConstraintSource { sourceTransform = input, weight = 1 });
            }
            var index = AvatarAnimationIndex.Create(root, ConstraintControllers(), null);
            var mutation = TransformMutationAnalysis.Create(root, index);
            Assert.That(mutation.Mutable.Contains(input), Is.False, "A source is read, not written");
            Assert.That(mutation.Mutable.Contains(target), Is.True);
            Assert.That(mutation.MayChangeScale(target), Is.EqualTo(writesScale));
            if (target != owner.transform) Assert.That(mutation.Mutable.Contains(owner.transform), Is.False);
        }

        [TestCase(typeof(VRCAimConstraint), false)] [TestCase(typeof(VRCLookAtConstraint), false)]
        [TestCase(typeof(VRCParentConstraint), false)] [TestCase(typeof(VRCPositionConstraint), false)]
        [TestCase(typeof(VRCRotationConstraint), false)] [TestCase(typeof(VRCScaleConstraint), true)]
        public void MissingSdkConstraintTargetStillWritesItsOwnTransform(Type type, bool writesScale)
        {
            var input = PolicyChild("Input").transform;
            var owner = PolicyChild("Owner");
            var constraint = (VRCConstraintBase)owner.AddComponent(type);
            constraint.Sources.Add(new VRCConstraintSource(input, 1));
            var missing = PolicyChild("Removed target");
            constraint.TargetTransform = missing.transform;
            UnityEngine.Object.DestroyImmediate(missing);
            Assert.That(constraint.TargetTransform == null, Is.True);
            Assert.That(ReferenceEquals(constraint.TargetTransform, null), Is.False,
                "Exercise Unity's missing-object wrapper, not an ordinary CLR null.");

            var index = AvatarAnimationIndex.Create(root, ConstraintControllers(), null);
            var mutation = TransformMutationAnalysis.Create(root, index);
            Assert.That(mutation.Mutable, Does.Contain(owner.transform));
            Assert.That(mutation.MayChangeScale(owner.transform), Is.EqualTo(writesScale));
            Assert.That(mutation.Mutable.Contains(input), Is.False, "Constraint sources remain readers.");
            var renderer = AssemblyPart("Weighted output", new[] { owner.transform }, Vector3.right);
            var plan = StaticSkinningPlan.Create(renderer, bone => bone != root.transform && !mutation.Mutable.Contains(bone));
            Assert.That(plan.ReboundEntries, Is.Zero, "A missing optional target must not make a driven skin bone static.");
            Assert.That(plan.Bones[0], Is.SameAs(owner.transform));
        }

        [TestCase(1f)] [TestCase(1.25f)] [TestCase(-0.75f)] [TestCase(0f)]
        public void ConstantLocalScaleConstraintsPreserveNeutralNonNeutralAndCollapsedScales(float scale)
        {
            var settings = ObjectSettings();
            var input = PolicyChild("Input").transform; var target = PolicyChild("Output").transform;
            input.localScale = new Vector3(5, scale, 8); target.localScale = new Vector3(2, scale, 3);
            var constraint = LocalScaleConstraint(input, target);
            constraint.AffectsScaleX = constraint.AffectsScaleZ = false;
            var before = target.localScale;
            Assert.That(RedundantConstraints.Apply(root, ConstraintControllers(), null, settings, null, new List<string>()), Is.EqualTo(1));
            Assert.That(constraint == null, Is.True);
            Assert.That(target.localScale.Equals(before), Is.True);
            Assert.That(input.localScale.Equals(new Vector3(5, scale, 8)), Is.True);
            Assert.That(RedundantConstraints.Apply(root, ConstraintControllers(), null, settings, null, new List<string>()), Is.Zero);
        }

        [TestCase("WorldSpace")] [TestCase("FrozenWorld")] [TestCase("GlobalWeight")] [TestCase("SourceWeight")]
        [TestCase("MultipleSources")] [TestCase("Offset")] [TestCase("DifferentInitialScale")]
        [TestCase("RootSource")] [TestCase("RootTarget")] [TestCase("ExternalSource")]
        [TestCase("OwnerOutsideScope")] [TestCase("TargetOutsideScope")]
        [TestCase("PreservedConstraint")] [TestCase("PreservedTarget")]
        [TestCase("OtherTargetWriter")] [TestCase("OtherSourceWriter")]
        [TestCase("AnimatedSource")] [TestCase("AnimatedTarget")] [TestCase("AnimatedWeight")]
        [TestCase("AnimatedConstraintTarget")] [TestCase("ComponentObjectKey")]
        [TestCase("AuthoringReference")] [TestCase("Events")]
        public void ConstantConstraintRemovalRetainsUnprovenAndObservedOutputs(string condition)
        {
            var settings = ObjectSettings();
            var input = PolicyChild("Input").transform; var target = PolicyChild("Output").transform;
            var constraint = LocalScaleConstraint(input, target);
            var clip = Clip();
            switch (condition)
            {
                case "WorldSpace": constraint.SolveInLocalSpace = false; break;
                case "FrozenWorld": constraint.FreezeToWorld = true; break;
                case "GlobalWeight": constraint.GlobalWeight = 0.5f; break;
                case "SourceWeight": constraint.Sources[0] = new VRCConstraintSource(input, 0.5f); break;
                case "MultipleSources": constraint.Sources.Add(new VRCConstraintSource(input, 1)); break;
                case "Offset": constraint.ScaleOffset = new Vector3(1, 1.01f, 1); break;
                case "DifferentInitialScale": input.localScale = new Vector3(1, 1.01f, 1); break;
                case "RootSource": constraint.Sources[0] = new VRCConstraintSource(root.transform, 1); break;
                case "RootTarget": constraint.TargetTransform = root.transform; break;
                case "ExternalSource":
                    var outside = Track(new GameObject("Other avatar")); SceneManager.MoveGameObjectToScene(outside, scene);
                    constraint.Sources[0] = new VRCConstraintSource(outside.transform, 1); break;
                case "OwnerOutsideScope": settings.Scope.Root = target; settings.Scope.OriginalTransforms.Clear(); break;
                case "TargetOutsideScope": settings.Scope.Root = constraint.transform; settings.Scope.OriginalTransforms.Clear(); break;
                case "PreservedConstraint": settings.ProtectedComponents.Add(ObjectRegistry.GetReference(constraint)); break;
                case "PreservedTarget": settings.ProtectedComponents.Add(ObjectRegistry.GetReference(target)); break;
                case "OtherTargetWriter": target.gameObject.AddComponent<ScaleConstraint>(); break;
                case "OtherSourceWriter": input.gameObject.AddComponent<ScaleConstraint>(); break;
                case "AnimatedSource": case "AnimatedTarget":
                    var transform = condition == "AnimatedSource" ? input : target;
                    AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(transform.name, typeof(Transform), "m_LocalScale.y"),
                        AnimationCurve.Linear(0, 1, 1, 2)); break;
                case "AnimatedWeight":
                    AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(constraint.name, typeof(VRCScaleConstraint), "GlobalWeight"),
                        AnimationCurve.Linear(0, 0, 1, 1)); break;
                case "AnimatedConstraintTarget":
                    AnimationUtility.SetObjectReferenceCurve(clip, EditorCurveBinding.PPtrCurve(constraint.name, typeof(VRCScaleConstraint), "TargetTransform"),
                        new[] { new ObjectReferenceKeyframe { time = 0, value = input } }); break;
                case "ComponentObjectKey":
                    root.AddComponent<DependencyAuthoringFixture>();
                    AnimationUtility.SetObjectReferenceCurve(clip, EditorCurveBinding.PPtrCurve("", typeof(DependencyAuthoringFixture), "Target"),
                        new[] { new ObjectReferenceKeyframe { time = 0, value = constraint } }); break;
                case "AuthoringReference": PolicyChild("Reader").AddComponent<DependencyAuthoringFixture>().Target = constraint; break;
                case "Events": AnimationUtility.SetAnimationEvents(clip,
                    new[] { new AnimationEvent { time = 0, functionName = "ModifyScale" } }); break;
            }
            var before = target.localScale;
            Assert.That(RedundantConstraints.Apply(root, ConstraintControllers(clip), null, settings, null, new List<string>()), Is.Zero);
            Assert.That(constraint != null, Is.True);
            Assert.That(target.localScale.Equals(before), Is.True);
        }

        [TestCase(false)] [TestCase(true)]
        public void RedundantConstraintOptionSurvivesAuthoringMarkerRemoval(bool enabled)
        {
            var settings = ObjectSettings(); var component = settings.Component;
            component.RemoveRedundantConstraints = enabled;
            component.RemoveUnusedObjects = component.RemoveUnusedTransformOutputs = component.PruneUnusedSensorParameters = false;
            var input = PolicyChild("Input").transform; var target = PolicyChild("Output").transform;
            var constraint = LocalScaleConstraint(input, target);
            var context = new BuildContext(root, null); ObjectOptimizationPass.Capture(context);
            UnityEngine.Object.DestroyImmediate(component);
            ObjectOptimizationPass.ApplyFinal(context);
            Assert.That(constraint == null, Is.EqualTo(enabled));
        }

        // Unity may defer Start indefinitely in an unfocused EditMode runner. Drive
        // the fixture lifecycle and the SDK's real scheduler explicitly; never
        // substitute a hand-written constraint equation for the native evaluator.
        private void EvaluateNativeConstraints(params VRCConstraintBase[] initialize)
        {
            // Diagnostic-only SDK reflection isolates the fixture from loaded user scenes.
            // SendMessage would also call Start on unrelated components sharing an owner.
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
            var manager = typeof(VRCDynamicsScheduler).Assembly.GetType("VRC.Dynamics.VRCConstraintManager");
            var registered = manager.GetMethod("IsConstraintRegistered", flags);
            var register = manager.GetMethod("RegisterConstraint", flags);
            var unregister = manager.GetMethod("UnregisterConstraint", flags);
            var start = typeof(VRCConstraintBase).GetMethod("Start", System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.That(registered != null && register != null && unregister != null && start != null, Is.True,
                "The native test harness needs the installed SDK registration/lifecycle methods.");
            var foreign = Resources.FindObjectsOfTypeAll<VRCConstraintBase>()
                .Where(c => !c.transform.IsChildOf(root.transform) && (bool)registered.Invoke(null, new object[] { c })).ToArray();
            try
            {
                VRCDynamicsScheduler.CompleteDynamicsFrame();
                foreach (var constraint in foreign) unregister.Invoke(null, new object[] { constraint });
                foreach (var constraint in initialize) start.Invoke(constraint, null);
                VRCDynamicsScheduler.UpdateConstraints(true);
                VRCDynamicsScheduler.CompleteDynamicsFrame();
            }
            finally
            {
                foreach (var constraint in foreign)
                    if (constraint != null) register.Invoke(null, new object[] { constraint });
            }
        }

        [Test]
        public void NativeMissingSdkTargetPreservesSkinnedOutputAfterAssembly()
        {
            var originalScene = SceneManager.GetActiveScene();
            var scenePath = AssetDatabase.GUIDToAssetPath("4e3531870ac74a99b7804047f9db1d96");
            Assert.That(SceneManager.GetSceneByPath(scenePath).isLoaded, Is.False);
            var nativeScene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
            SceneManager.MoveGameObjectToScene(root, nativeScene);
            try
            {
                var input = PolicyChild("Input").transform;
                var output = PolicyChild("Driven skin bone").transform;
                var constraint = output.gameObject.AddComponent<VRCRotationConstraint>();
                constraint.Sources.Add(new VRCConstraintSource(input, 1));
                constraint.AffectsRotationX = constraint.AffectsRotationY = constraint.AffectsRotationZ = true;
                constraint.GlobalWeight = 1; constraint.Locked = constraint.IsActive = true;
                var missing = PolicyChild("Removed target"); constraint.TargetTransform = missing.transform;
                UnityEngine.Object.DestroyImmediate(missing); constraint.ApplyConfigurationChanges();
                var source = AssemblyPart("Source", new[] { output }, Vector3.right);
                var mutation = TransformMutationAnalysis.Create(root,
                    AvatarAnimationIndex.Create(root, ConstraintControllers(), null));
                var plan = StaticSkinningPlan.Create(source, bone => bone != root.transform && !mutation.Mutable.Contains(bone));
                using var assembly = SkinnedMeshAssembler.Create(new[] { source }, false,
                    skinning: new Dictionary<SkinnedMeshRenderer, StaticSkinningPlan> { [source] = plan });
                var result = AssemblyOutput(assembly);
                var initial = AssemblyWorldVertices(source);
                input.localRotation = Quaternion.Euler(20, 35, -10);
                EvaluateNativeConstraints(constraint);
                var before = AssemblyWorldVertices(source); var after = AssemblyWorldVertices(result);
                Assert.That(before.Where((point, index) => Vector3.Distance(point, initial[index]) > 0.01f), Is.Not.Empty,
                    "The native constraint must move the supposedly static skin bone.");
                for (int vertex = 0; vertex < before.Length; vertex++)
                    Assert.That(Vector3.Distance(before[vertex], after[vertex]), Is.LessThan(0.00001f));
            }
            finally
            {
                SceneManager.MoveGameObjectToScene(root, scene);
                if (originalScene.IsValid() && originalScene.isLoaded) SceneManager.SetActiveScene(originalScene);
                EditorSceneManager.CloseScene(nativeScene, true);
            }
        }

        [Test]
        public void NativeConstraintHarnessLeavesForeignSceneOutputsAndRegistrationIntact()
        {
            var originalScene = SceneManager.GetActiveScene();
            var scenePath = AssetDatabase.GUIDToAssetPath("4e3531870ac74a99b7804047f9db1d96");
            Assert.That(SceneManager.GetSceneByPath(scenePath).isLoaded, Is.False);
            var nativeScene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
            SceneManager.MoveGameObjectToScene(root, nativeScene);
            try
            {
                var input = PolicyChild("Input").transform; input.localScale = Vector3.one * 1.25f;
                var output = PolicyChild("Output").transform;
                var own = LocalScaleConstraint(input, output);
                var foreign = Track(new GameObject("Another avatar output")); SceneManager.MoveGameObjectToScene(foreign, nativeScene);
                var other = foreign.AddComponent<VRCScaleConstraint>();
                other.Sources.Add(new VRCConstraintSource(input, 1)); other.SolveInLocalSpace = true;
                other.ScaleOffset = other.ScaleAtRest = Vector3.one;
                other.AffectsScaleX = other.AffectsScaleY = other.AffectsScaleZ = true;
                other.GlobalWeight = 1; other.Locked = other.IsActive = true; other.ApplyConfigurationChanges();
                typeof(VRCConstraintBase).GetMethod("Start", System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(other, null);
                EvaluateNativeConstraints(own);
                Assert.That(output.localScale, Is.EqualTo(input.localScale));
                Assert.That(foreign.transform.localScale, Is.EqualTo(Vector3.one), "Do not evaluate unrelated loaded avatars.");
                var manager = typeof(VRCDynamicsScheduler).Assembly.GetType("VRC.Dynamics.VRCConstraintManager");
                var registered = manager.GetMethod("IsConstraintRegistered", System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                Assert.That((bool)registered.Invoke(null, new object[] { other }), Is.True, "Restore the original registration.");
            }
            finally
            {
                SceneManager.MoveGameObjectToScene(root, scene);
                if (originalScene.IsValid() && originalScene.isLoaded) SceneManager.SetActiveScene(originalScene);
                EditorSceneManager.CloseScene(nativeScene, true);
            }
        }

        [Test]
        public void NativeLocalScaleRemainsUnchangedAfterConstraintRemovalAndParentFrameChanges()
        {
            var originalScene = SceneManager.GetActiveScene();
            var scenePath = AssetDatabase.GUIDToAssetPath("4e3531870ac74a99b7804047f9db1d96");
            Assert.That(SceneManager.GetSceneByPath(scenePath).isLoaded, Is.False);
            var nativeScene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
            SceneManager.MoveGameObjectToScene(root, nativeScene);
            try
            {
                var settings = ObjectSettings();
                var sourceParent = PolicyChild("Source parent").transform;
                var targetParent = PolicyChild("Target parent").transform;
                var input = PolicyChild("Input", sourceParent).transform;
                var target = PolicyChild("Output", targetParent).transform;
                input.localScale = new Vector3(1, 1.25f, 1);
                var constraint = LocalScaleConstraint(input, target);
                EvaluateNativeConstraints(constraint);
                Assert.That(target.localScale.Equals(input.localScale), Is.True, "The native SDK evaluator must actually run before comparison");
                sourceParent.localScale = new Vector3(2, 3, 4); sourceParent.localRotation = Quaternion.Euler(20, 40, 10);
                targetParent.localScale = new Vector3(4, 2, 3); targetParent.localRotation = Quaternion.Euler(50, 10, 70);
                EvaluateNativeConstraints();
                var before = target.localScale;
                Assert.That(before.Equals(input.localScale), Is.True);
                Assert.That(RedundantConstraints.Apply(root, ConstraintControllers(), null, settings, null, new List<string>()), Is.EqualTo(1));
                root.transform.localScale = Vector3.one * 2.5f;
                sourceParent.localRotation = Quaternion.Euler(75, 20, 15);
                targetParent.localScale = new Vector3(3, 5, 2);
                EvaluateNativeConstraints();
                Assert.That(constraint == null, Is.True);
                Assert.That(target.localScale.Equals(before), Is.True);
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
