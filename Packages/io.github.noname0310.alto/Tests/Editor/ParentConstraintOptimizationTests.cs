using System.Collections;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.ObjectOptimization;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using VRC.Dynamics;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.Constraint.Components;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private VRCParentConstraint FollowingParent(Transform input, Transform output, string name = "Parent writer", bool onTarget = false)
        {
            var c = (onTarget ? output.gameObject : PolicyChild(name)).AddComponent<VRCParentConstraint>();
            c.TargetTransform = output; c.Sources.Add(new VRCConstraintSource(input, 1));
            c.PositionAtRest = Vector3.zero; c.RotationAtRest = Vector3.zero;
            c.AffectsPositionX = c.AffectsPositionY = c.AffectsPositionZ = true;
            c.AffectsRotationX = c.AffectsRotationY = c.AffectsRotationZ = true;
            c.enabled = true; c.IsActive = true; c.Locked = true; c.GlobalWeight = 1;
            c.ApplyConfigurationChanges(); return c;
        }

        private int SimplifyParents(ObjectOptimizationState settings,
            Dictionary<object, VirtualAnimatorController> controllers = null, IReadOnlyDictionary<string, string> paths = null)
            => ParentConstraintOptimization.Apply(root, controllers ?? ConstraintControllers(), paths, settings, null, new List<string>());

        [TestCase(1f)] [TestCase(2f)] [TestCase(0.5f)]
        public void ParentConstraintBecomesParentingWhileItsSourceContinuesMoving(float sourceScale)
        {
            var settings = ObjectSettings(); var input = PolicyChild("Input").transform;
            var output = PolicyChild("Output").transform; input.localScale = Vector3.one * sourceScale;
            var c = FollowingParent(input, output);
            var clip = Clip();
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Input", typeof(Transform), "m_LocalPosition.x"),
                AnimationCurve.Linear(0, 0, 1, 4));
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(c.name, typeof(VRCParentConstraint), "GlobalWeight"),
                AnimationCurve.Constant(0, 1, 1));
            var controllers = ConstraintControllers(clip);
            Assert.That(SimplifyParents(settings, controllers), Is.EqualTo(1));
            Assert.That(c == null, Is.True); Assert.That(output.parent, Is.SameAs(input));
            Assert.That(output.localScale, Is.EqualTo(Vector3.one / sourceScale));
            Assert.That(controllers.Values.SelectMany(v => v.AllReachableNodes()).OfType<VirtualClip>()
                .SelectMany(v => v.GetFloatCurveBindings()).Any(b => b.type == typeof(VRCParentConstraint)), Is.False);
            input.localPosition = new Vector3(2, 3, 4); input.localRotation = Quaternion.Euler(20, 40, 60);
            Assert.That(Vector3.Distance(output.position, input.position), Is.LessThan(0.00001f));
            Assert.That(SimplifyParents(settings, controllers), Is.Zero);
        }

        [TestCase(0f)] [TestCase(1f)] [TestCase(3f)]
        public void ParentConstraintProofUsesRelativeFramesEvenWhenCommonRootIsCollapsedOrNonuniform(float rootScale)
        {
            var settings = ObjectSettings(); root.transform.localScale = new Vector3(rootScale, rootScale * 2, rootScale * 3);
            var input = PolicyChild("Input").transform; var output = PolicyChild("Output").transform;
            var c = FollowingParent(input, output);
            Assert.That(SimplifyParents(settings), Is.EqualTo(1)); Assert.That(c == null, Is.True);
            Assert.That(output.parent, Is.SameAs(input));
        }

        [TestCase("PartialWeight")] [TestCase("PartialSource")] [TestCase("MultipleSources")]
        [TestCase("FrozenWorld")] [TestCase("LocalSpace")] [TestCase("PositionOffset")]
        [TestCase("PartialAxes")] [TestCase("NonuniformSource")] [TestCase("NonuniformParent")]
        [TestCase("NegativeScale")] [TestCase("AnimatedScale")] [TestCase("AnimatedTarget")]
        [TestCase("OtherWriter")] [TestCase("ProtectedTarget")] [TestCase("ProtectedConstraint")]
        [TestCase("OutsideScope")] [TestCase("ExternalSource")] [TestCase("Cycle")]
        [TestCase("InitialPose")] [TestCase("CollapsedInitialMismatch")] [TestCase("InactiveSource")]
        [TestCase("AnimatedVisibility")] [TestCase("AnimatedEnabled")] [TestCase("AnimatedWeight")]
        [TestCase("ExternalReader")] [TestCase("ObjectCurve")] [TestCase("NameCollision")]
        [TestCase("AdditiveLayer")] [TestCase("AdditivePlayable")] [TestCase("Events")]
        [TestCase("ConstraintCycle")] [TestCase("AnimatedConstraintCycle")]
        public void ParentConstraintKeepsRelationsThatCannotBeRepresentedByParenting(string condition)
        {
            var settings = ObjectSettings(); var input = PolicyChild("Input").transform;
            var previous = PolicyChild("Previous").transform; var output = PolicyChild("Output", previous).transform;
            var c = FollowingParent(input, output); var clip = Clip();
            switch (condition)
            {
                case "PartialWeight": c.GlobalWeight = 0.5f; break;
                case "PartialSource": c.Sources[0] = new VRCConstraintSource(input, 0.5f); break;
                case "MultipleSources": c.Sources.Add(new VRCConstraintSource(input, 1)); break;
                case "FrozenWorld": c.FreezeToWorld = true; break;
                case "LocalSpace": c.SolveInLocalSpace = true; break;
                case "PositionOffset": var entry = c.Sources[0]; entry.ParentPositionOffset = Vector3.right; c.Sources[0] = entry; break;
                case "PartialAxes": c.AffectsPositionY = false; break;
                case "NonuniformSource": input.localScale = new Vector3(1, 2, 1); break;
                case "NonuniformParent": previous.localScale = new Vector3(1, 2, 1); break;
                case "NegativeScale": input.localScale = -Vector3.one; break;
                case "AnimatedScale": AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Input", typeof(Transform), "m_LocalScale.x"), AnimationCurve.Linear(0, 1, 1, 2)); break;
                case "AnimatedTarget": AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Previous/Output", typeof(Transform), "m_LocalPosition.x"), AnimationCurve.Linear(0, 0, 1, 2)); break;
                case "OtherWriter": output.gameObject.AddComponent<VRCPositionConstraint>(); break;
                case "ProtectedTarget": settings.ProtectedComponents.Add(ObjectRegistry.GetReference(output)); break;
                case "ProtectedConstraint": settings.ProtectedComponents.Add(ObjectRegistry.GetReference(c)); break;
                case "OutsideScope": settings.Scope.Root = output; settings.Scope.OriginalTransforms.Clear(); break;
                case "ExternalSource": var outside = Track(new GameObject("Outside")); SceneManager.MoveGameObjectToScene(outside, scene); c.Sources[0] = new VRCConstraintSource(outside.transform, 1); break;
                case "Cycle": input.SetParent(output, false); break;
                case "InitialPose": output.localPosition = Vector3.right; break;
                case "CollapsedInitialMismatch": root.transform.localScale = Vector3.zero; output.localPosition = Vector3.right; break;
                case "InactiveSource": input.gameObject.SetActive(false); break;
                case "AnimatedVisibility": AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Input", typeof(GameObject), "m_IsActive"), AnimationCurve.Linear(0, 1, 1, 0)); break;
                case "AnimatedEnabled": AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(c.name, typeof(VRCParentConstraint), "m_Enabled"), AnimationCurve.Linear(0, 1, 1, 0)); break;
                case "AnimatedWeight": AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(c.name, typeof(VRCParentConstraint), "GlobalWeight"), AnimationCurve.Linear(0, 1, 1, 0)); break;
                case "ExternalReader": PolicyChild("Reader").AddComponent<DependencyAuthoringFixture>().Target = c; break;
                case "ObjectCurve": AnimationUtility.SetObjectReferenceCurve(clip, EditorCurveBinding.PPtrCurve(c.name, typeof(VRCParentConstraint), "TargetTransform"), new[] { new ObjectReferenceKeyframe { time = 0, value = input } }); break;
                case "NameCollision": PolicyChild(output.name, input); break;
                case "AdditiveLayer": case "AdditivePlayable": AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(c.name, typeof(VRCParentConstraint), "GlobalWeight"), AnimationCurve.Constant(0, 0, 1)); break;
                case "Events": AnimationUtility.SetAnimationEvents(clip, new[] { new AnimationEvent { time = 0, functionName = "ModifyParent" } }); break;
                case "ConstraintCycle": case "AnimatedConstraintCycle":
                    var sourceWriter = input.gameObject.AddComponent<VRCPositionConstraint>();
                    sourceWriter.Sources.Add(new VRCConstraintSource(condition == "ConstraintCycle" ? output : previous, 1));
                    if (condition == "AnimatedConstraintCycle") AnimationUtility.SetObjectReferenceCurve(clip,
                        EditorCurveBinding.PPtrCurve("Input", typeof(VRCPositionConstraint), "Sources.source0.SourceTransform"),
                        new[] { new ObjectReferenceKeyframe { time = 0, value = output } });
                    break;
            }
            var controllers = ConstraintControllers(clip);
            if (condition == "AdditiveLayer") controllers.Values.Single().Layers.First().BlendingMode = AnimatorLayerBlendingMode.Additive;
            if (condition == "AdditivePlayable") controllers = new() { [VRCAvatarDescriptor.AnimLayerType.Additive] = controllers.Values.Single() };
            var before = output.parent; var matrix = output.localToWorldMatrix;
            Assert.That(SimplifyParents(settings, controllers), Is.Zero);
            Assert.That(c != null, Is.True); Assert.That(output.parent, Is.SameAs(before));
            Assert.That(output.localToWorldMatrix, Is.EqualTo(matrix));
        }

        [TestCase(false)] [TestCase(true)]
        public void ZeroWeightParentConstraintRequiresItsRestPoseToMatchTheExistingLocalPose(bool matches)
        {
            var settings = ObjectSettings(); var input = PolicyChild("Input").transform; var output = PolicyChild("Output").transform;
            var c = FollowingParent(input, output); c.GlobalWeight = 0;
            if (!matches) c.PositionAtRest = Vector3.right;
            var clip = Clip(); AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(c.name,
                typeof(VRCParentConstraint), "GlobalWeight"), AnimationCurve.Constant(0, 1, 0));
            var previous = output.parent;
            Assert.That(SimplifyParents(settings, ConstraintControllers(clip)), Is.EqualTo(matches ? 1 : 0));
            Assert.That(c == null, Is.EqualTo(matches)); Assert.That(output.parent, Is.SameAs(previous));
            Assert.That(output.localPosition, Is.EqualTo(Vector3.zero));
        }

        [Test]
        public void ParentingUpdatesDescendantAnimationPathsThroughTheSharedNdmfService()
        {
            var settings = ObjectSettings(); var marker = settings.Component;
            marker.RemoveUnusedObjects = marker.RemoveUnusedTransformOutputs = marker.PruneUnusedSensorParameters = false;
            var input = PolicyChild("Input").transform; var output = PolicyChild("Output").transform;
            var child = PolicyChild("Animated child", output);
            FollowingParent(input, output);
            var clip = Clip(); AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Output/Animated child", typeof(GameObject), "m_IsActive"), AnimationCurve.Linear(0, 0, 1, 1));
            var descriptor = root.GetComponent<VRCAvatarDescriptor>(); descriptor.customizeAnimationLayers = true;
            descriptor.baseAnimationLayers = new[] { new VRCAvatarDescriptor.CustomAnimLayer { type = VRCAvatarDescriptor.AnimLayerType.FX, animatorController = Controller(clip) } };
            descriptor.specialAnimationLayers = System.Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>();
            var context = new BuildContext(root, null); ObjectOptimizationPass.Capture(context);
            ObjectOptimizationPass.ApplyFinal(context);
            Assert.That(output.parent, Is.SameAs(input));
            var paths = descriptor.baseAnimationLayers.Single().animatorController.animationClips.SelectMany(AnimationUtility.GetCurveBindings)
                .Where(b => b.type == typeof(GameObject) && b.propertyName == "m_IsActive").Select(b => b.path).ToArray();
            Assert.That(paths, Does.Contain("Input/Output/Animated child"));
            Assert.That(child.transform.parent, Is.SameAs(output));
        }

        [TestCase(true)] [TestCase(false)]
        public void NativeParentConstraintAndParentingAgreeThroughMotionAndAvatarScaling(bool onTarget)
        {
            var originalScene = SceneManager.GetActiveScene(); var path = AssetDatabase.GUIDToAssetPath("4e3531870ac74a99b7804047f9db1d96");
            Assert.That(SceneManager.GetSceneByPath(path).isLoaded, Is.False);
            var nativeScene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive); SceneManager.MoveGameObjectToScene(root, nativeScene);
            try
            {
                var settings = ObjectSettings(); var input = PolicyChild("Input").transform; input.localScale = Vector3.one * 2;
                var previous = PolicyChild("Previous").transform;
                var output = PolicyChild("Output", previous).transform; var control = PolicyChild("Control", previous).transform;
                var c = FollowingParent(input, output, onTarget: onTarget); var reference = FollowingParent(input, control, "Reference writer", onTarget: onTarget);
                settings.ProtectedComponents.Add(ObjectRegistry.GetReference(reference));
                input.localPosition = new Vector3(1, 2, 3); input.localRotation = Quaternion.Euler(20, 40, 10);
                EvaluateNativeConstraints(c, reference);
                Assert.That(Vector3.Distance(control.position, input.position), Is.LessThan(0.00001f),
                    "Native SDK evaluation must run; owner=" + reference.transform.position + ", enabled=" + reference.enabled + ", active=" + reference.IsActive);
                Assert.That(SimplifyParents(settings), Is.EqualTo(1)); Assert.That(c == null, Is.True);
                Assert.That(output.parent, Is.SameAs(input));
                foreach (var scale in new[] { Vector3.one, Vector3.one * 2.5f, new Vector3(2, 3, 4) })
                {
                    root.transform.localScale = scale; root.transform.localRotation = Quaternion.Euler(25, 40, 15);
                    input.localPosition += Vector3.one; input.localRotation *= Quaternion.Euler(35, 20, 70);
                    previous.localRotation *= Quaternion.Euler(15, 25, 35);
                    EvaluateNativeConstraints();
                    Assert.That(Vector3.Distance(control.position, input.position), Is.LessThan(0.00001f));
                    for (int i = 0; i < 16; i++) Assert.That(output.localToWorldMatrix[i], Is.EqualTo(control.localToWorldMatrix[i]).Within(0.00001f));
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
