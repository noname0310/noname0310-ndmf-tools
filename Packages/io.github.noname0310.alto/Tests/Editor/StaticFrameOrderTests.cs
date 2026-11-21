using System;
using System.Linq;
using System.Reflection;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.ObjectOptimization;
using NUnit.Framework;
using UnityEngine;
using VRC.Dynamics;
using VRC.SDK3.Dynamics.Contact.Components;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        [TestCase("All")] [TestCase("Prefix")] [TestCase("Suffix")]
        [TestCase("BothEnds")] [TestCase("Interior")] [TestCase("Interleaved")]
        public void StaticFrameFlattenPreservesComponentTraversalIncludingPartialBranches(string selection)
        {
            var settings = RelocationSettings();
            var before = PolicyChild("Before").AddComponent<VRCContactReceiver>();
            var frame = PolicyChild("Frame").transform;
            var children = Enumerable.Range(0, 5).Select(i => PolicyChild("Child" + i, frame).transform).ToArray();
            var after = PolicyChild("After").AddComponent<VRCContactReceiver>();
            bool Movable(int i) => selection == "All" || selection == "Prefix" && i < 2 || selection == "Suffix" && i > 2 ||
                selection == "BothEnds" && (i < 2 || i > 2) || selection == "Interior" && i == 2 || selection == "Interleaved" && i % 2 == 0;
            for (int i = 0; i < children.Length; i++)
            {
                // Multiple components and nested descendants must retain order too.
                children[i].gameObject.AddComponent<VRCContactReceiver>();
                children[i].gameObject.AddComponent<VRCContactReceiver>();
                PolicyChild("Nested", children[i]).AddComponent<VRCContactReceiver>();
                if (!Movable(i)) settings.ProtectedSubtrees.Add(new OptimizationBuildScope { Root = children[i] });
            }
            var original = root.GetComponentsInChildren<VRCContactReceiver>(true);
            int changed = Flatten(settings, FrameIndex());
            Assert.That(changed, Is.EqualTo(selection == "Interior" ? 0 : 1));
            Assert.That(root.GetComponentsInChildren<VRCContactReceiver>(true), Is.EqualTo(original));
            Assert.That(frame == null, Is.EqualTo(selection == "All"));
            for (int i = 0; i < children.Length; i++)
            {
                bool relocated = Movable(i) && !(i == 2 && (selection == "Interior" || selection == "Interleaved"));
                Assert.That(children[i].parent, Is.SameAs(relocated ? root.transform : frame));
            }
            Assert.That(before.transform.GetSiblingIndex(), Is.Zero);
            Assert.That(after.transform.GetSiblingIndex(), Is.EqualTo(root.transform.childCount - 1));
        }

        [Test]
        public void StaticFrameFlattenPreservesNativeCompetingContactOutput()
        {
            var frame = PolicyChild("Frame");
            var first = PolicyChild("First", frame.transform).AddComponent<VRCContactReceiver>();
            var second = PolicyChild("Second").AddComponent<VRCContactReceiver>();
            first.parameter = second.parameter = "Shared";
            var before = root.GetComponentsInChildren<VRCContactReceiver>(true);
            Assert.That(Flatten(RelocationSettings(), FrameIndex()), Is.EqualTo(1));
            var after = root.GetComponentsInChildren<VRCContactReceiver>(true);
            var animator = root.AddComponent<Animator>(); var controller = Controller(Clip());
            controller.AddParameter("Shared", AnimatorControllerParameterType.Float);
            animator.runtimeAnimatorController = controller; animator.Rebind();
            float original = EvaluateCompetingContacts(before, animator, first);
            float optimized = EvaluateCompetingContacts(after, animator, first);
            Assert.That(original, Is.Zero);
            Assert.That(optimized, Is.EqualTo(original));
            Assert.That(frame == null, Is.True, "Preserving order must not disable valid flattening.");
        }

        [Test]
        public void StaticFramePartialFlattenDoesNotApplyCoordinateEditsToRetainedInteriorBranches()
        {
            var settings = RelocationSettings(); var frame = PolicyChild("Frame").transform; frame.localPosition = Vector3.one;
            var children = Enumerable.Range(0, 5).Select(i => PolicyChild("Child" + i, frame).transform).ToArray();
            foreach (var child in children) child.gameObject.AddComponent<Light>();
            foreach (int i in new[] { 1, 3 }) settings.ProtectedSubtrees.Add(new OptimizationBuildScope { Root = children[i] });
            var clip = Clip();
            foreach (char axis in "xyz") clip.SetCurve("Frame/Child2", typeof(Transform), "m_LocalPosition." + axis, AnimationCurve.Constant(0, 1, .5f));
            var index = FrameIndex(clip);
            Assert.That(Flatten(settings, index), Is.EqualTo(1));
            Assert.That(children[0].parent, Is.SameAs(root.transform)); Assert.That(children[4].parent, Is.SameAs(root.transform));
            Assert.That(children[2].parent, Is.SameAs(frame));
            var built = index.Controllers.Single().AllReachableNodes().OfType<VirtualClip>().Single();
            foreach (var binding in built.GetFloatCurveBindings())
            {
                Assert.That(binding.path, Is.EqualTo("Frame/Child2"));
                Assert.That(built.GetFloatCurve(binding).Evaluate(0), Is.EqualTo(.5f));
            }
        }

        private float EvaluateCompetingContacts(VRCContactReceiver[] order, Animator animator, VRCContactReceiver high)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var previous = ContactManager.Inst;
            var owner = new GameObject("Contact output fixture");
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(owner, scene);
            var manager = owner.AddComponent<ContactManager>(); manager.enabled = false;
            try
            {
                manager.Init();
                foreach (var receiver in order)
                {
                    receiver.receiverType = ContactReceiver.ReceiverType.Proximity;
                    receiver.paramAccess = new AnimParameterAccessAvatarSDK(animator, "Shared");
                    manager.AddContact(receiver);
                    var data = manager.receiverData[receiver.receiverId]; data.collisionValue = receiver == high ? 1 : 0;
                    manager.receiverData[receiver.receiverId] = data;
                }
                typeof(ContactManager).GetField("_wasScheduled", flags).SetValue(manager, true);
                typeof(ContactManager).GetField("_stopwatch", flags).SetValue(manager, System.Diagnostics.Stopwatch.StartNew());
                typeof(ContactManager).GetMethod("HandleDynamicsFrameComplete", flags).Invoke(manager, null);
                return animator.GetFloat("Shared");
            }
            finally
            {
                manager.Dispose(); UnityEngine.Object.DestroyImmediate(owner); ContactManager.Inst = previous;
            }
        }
    }
}
