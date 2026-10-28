using System;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.Build;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDK3.Avatars.Components;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        [TestCase("Unused")] [TestCase("SkinBone")] [TestCase("VisibleDescendant")]
        [TestCase("RootTarget")] [TestCase("ExternalTarget")] [TestCase("MixedTargets")]
        [TestCase("PreservedComponent")] [TestCase("PreservedSubtree")] [TestCase("OutsideScope")]
        [TestCase("SerializedReader")] [TestCase("ObjectCurve")]
        public void HeadChopCollectionFollowsObservedTargetsAndPreservation(string consumer)
        {
            var settings = ObjectSettings(consumer == "OutsideScope" ? PolicyChild("Other scope") : root);
            var owner = PolicyChild("Head visibility"); var target = PolicyChild("Controlled bone");
            var chop = owner.AddComponent<VRCHeadChop>();
            chop.targetBones = new[] { new VRCHeadChop.HeadChopBone { transform = target.transform, scaleFactor = 1 } };
            var clip = Clip();
            if (consumer == "SkinBone") renderer.bones = new[] { target.transform };
            if (consumer == "VisibleDescendant") PolicyChild("Visible mesh", target.transform).AddComponent<MeshRenderer>();
            if (consumer == "RootTarget") chop.targetBones[0].transform = root.transform;
            if (consumer == "ExternalTarget")
            {
                var external = Track(new GameObject("External output")); SceneManager.MoveGameObjectToScene(external, scene);
                chop.targetBones[0].transform = external.transform;
            }
            if (consumer == "MixedTargets") chop.targetBones = chop.targetBones.Append(new VRCHeadChop.HeadChopBone { transform = root.transform }).ToArray();
            if (consumer == "PreservedComponent") settings.ProtectedComponents.Add(ObjectRegistry.GetReference(chop));
            if (consumer == "PreservedSubtree") settings.ProtectedSubtrees.Add(new OptimizationBuildScope { Root = owner.transform });
            if (consumer == "SerializedReader") root.AddComponent<DependencyAuthoringFixture>().Target = chop;
            if (consumer == "ObjectCurve")
            {
                root.AddComponent<DependencyAuthoringFixture>();
                AnimationUtility.SetObjectReferenceCurve(clip, EditorCurveBinding.PPtrCurve("", typeof(DependencyAuthoringFixture), "Target"),
                    new[] { new ObjectReferenceKeyframe { value = chop } });
            }
            CollectObjects(settings, new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(clip)));
            Assert.That(chop != null && owner != null, Is.EqualTo(consumer != "Unused"));
            if (consumer == "Unused") Assert.That(target == null, Is.True);
        }

        [TestCase("NullArray")] [TestCase("Empty")] [TestCase("EmptyEntry")]
        public void HeadChopWithNoControlledTargetsIsCollected(string kind)
        {
            var settings = ObjectSettings(); var owner = PolicyChild("Empty head visibility"); var chop = owner.AddComponent<VRCHeadChop>();
            chop.targetBones = kind == "NullArray" ? null : kind == "Empty" ? Array.Empty<VRCHeadChop.HeadChopBone>() :
                new[] { new VRCHeadChop.HeadChopBone() };
            CollectObjects(settings);
            Assert.That(chop == null && owner == null, Is.True);
        }

        [Test]
        public void HeadChopAnimationAloneDoesNotKeepDeadTargetsAndPreservesSourceAndTiming()
        {
            var settings = ObjectSettings(); var owner = PolicyChild("Head visibility"); var target = PolicyChild("Unused bone");
            var chop = owner.AddComponent<VRCHeadChop>();
            chop.targetBones = new[] { new VRCHeadChop.HeadChopBone { transform = target.transform } };
            var binding = EditorCurveBinding.FloatCurve(owner.name, typeof(VRCHeadChop), "globalScaleFactor");
            var clip = Clip(); AnimationUtility.SetEditorCurve(clip, binding, AnimationCurve.Linear(0, 0, 2, 1));
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(clip));
            CollectObjects(settings, controller);
            Assert.That(chop == null && target == null, Is.True);
            var output = controller.AllReachableNodes().OfType<VirtualClip>().Single();
            Assert.That(output.GetFloatCurveBindings(), Has.No.Member(binding));
            Assert.That(MotionAnalysis.EndTime(output), Is.EqualTo(2));
            Assert.That(AnimationUtility.GetCurveBindings(clip), Does.Contain(binding));
        }
    }
}
