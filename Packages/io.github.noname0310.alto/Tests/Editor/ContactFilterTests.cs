using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.ParameterPolicy;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.Contact.Components;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private ALTOContactFilter ContactFilter(GameObject owner = null)
        {
            if (root.GetComponent<VRCAvatarDescriptor>() == null) root.AddComponent<VRCAvatarDescriptor>();
            var component = (owner != null ? owner : root).AddComponent<ALTOContactFilter>();
            component.ApplyOnPlatforms = OptimizationPlatforms.All;
            return component;
        }

        [Test]
        public void ContactFilterDefaultsToAndroidAndEmptyKeepLists()
        {
            var component = root.AddComponent<ALTOContactFilter>();
            Assert.That(component.ApplyOnPlatforms, Is.EqualTo(OptimizationPlatforms.Android));
            Assert.That(component.FilterMode, Is.EqualTo(ContactFilterMode.KeepListed));
            Assert.That(component.Senders, Is.Empty); Assert.That(component.Receivers, Is.Empty); Assert.That(component.ObjectRoots, Is.Empty);
            Assert.That(OptimizationBuildScope.Applies(component, BuildTarget.Android), Is.True);
            Assert.That(OptimizationBuildScope.Applies(component, BuildTarget.StandaloneWindows64), Is.False);
        }

        [Test]
        public void ContactFilterEmptyKeepListRemovesGeneratedDisabledAndInactiveContactsButKeepsTransforms()
        {
            ContactFilter();
            var sender = PolicyChild("Sender").AddComponent<VRCContactSender>(); sender.enabled = false;
            var context = new BuildContext(root, null); ContactFilterPass.Capture(context);
            var receiver = PolicyChild("Generated receiver").AddComponent<VRCContactReceiver>(); receiver.gameObject.SetActive(false);
            var transforms = new[] { sender.transform, receiver.transform };
            ContactFilterPass.Apply(context);
            Assert.That(sender == null && receiver == null, Is.True);
            Assert.That(transforms.All(t => t != null), Is.True);
            Assert.That(root.GetComponent<ALTOContactFilter>() == null, Is.True);
        }

        [TestCase(ContactFilterMode.KeepListed)] [TestCase(ContactFilterMode.RemoveListed)]
        public void ContactFilterDirectListsSelectBothTypesIndependently(ContactFilterMode mode)
        {
            var component = ContactFilter(); component.FilterMode = mode;
            var sender = PolicyChild("Selected sender").AddComponent<VRCContactSender>();
            var receiver = PolicyChild("Selected receiver").AddComponent<VRCContactReceiver>();
            var otherSender = PolicyChild("Other sender").AddComponent<VRCContactSender>();
            var otherReceiver = PolicyChild("Other receiver").AddComponent<VRCContactReceiver>();
            component.Senders.Add(sender); component.Receivers.Add(receiver);
            var context = new BuildContext(root, null); ContactFilterPass.Capture(context); ContactFilterPass.Apply(context);
            bool keep = mode == ContactFilterMode.KeepListed;
            Assert.That(sender != null && receiver != null, Is.EqualTo(keep));
            Assert.That(otherSender != null && otherReceiver != null, Is.EqualTo(!keep));
        }

        [Test]
        public void ContactFilterEmptyRemoveListHasNoEffect()
        {
            var component = ContactFilter(); component.FilterMode = ContactFilterMode.RemoveListed;
            var sender = PolicyChild("Sender").AddComponent<VRCContactSender>();
            var receiver = PolicyChild("Receiver").AddComponent<VRCContactReceiver>();
            var context = new BuildContext(root, null); ContactFilterPass.Capture(context); ContactFilterPass.Apply(context);
            Assert.That(sender != null && receiver != null, Is.True);
        }

        [Test]
        public void ContactFilterScopeFollowsReparentedContactsAndDoesNotTouchOtherObjects()
        {
            var scope = PolicyChild("Scope"); ContactFilter(scope);
            var inside = PolicyChild("Inside", scope.transform).AddComponent<VRCContactSender>();
            var outside = PolicyChild("Outside").AddComponent<VRCContactReceiver>();
            var context = new BuildContext(root, null); ContactFilterPass.Capture(context);
            inside.transform.SetParent(root.transform, false);
            ContactFilterPass.Apply(context);
            Assert.That(inside == null && outside != null, Is.True);
        }

        [TestCase(true)] [TestCase(false)]
        public void ContactFilterObjectRootsFollowReparentingAndIncludeNewContacts(bool children)
        {
            var component = ContactFilter();
            var selected = PolicyChild("Selected"); var child = PolicyChild("Child", selected.transform);
            component.ObjectRoots.Add(new ContactObjectSelection { Root = selected.transform, IncludeChildren = children });
            var context = new BuildContext(root, null); ContactFilterPass.Capture(context);
            child.transform.SetParent(root.transform, false);
            var direct = selected.AddComponent<VRCContactSender>();
            var nested = PolicyChild("Generated", child.transform).AddComponent<VRCContactReceiver>();
            var unrelated = PolicyChild("Unrelated").AddComponent<VRCContactSender>();
            // The anchor is deliberately unrelated; selection follows the holder, not this anchor.
            direct.rootTransform = unrelated.transform;
            ContactFilterPass.Apply(context);
            Assert.That(direct != null, Is.True); Assert.That(nested != null, Is.EqualTo(children)); Assert.That(unrelated == null, Is.True);
        }

        [Test]
        public void ContactFilterDirectSelectionFollowsRegisteredReplacement()
        {
            using var registry = new ObjectRegistryScope(new ObjectRegistry(root.transform));
            var component = ContactFilter(); var original = PolicyChild("Original").AddComponent<VRCContactSender>(); component.Senders.Add(original);
            var context = new BuildContext(root, null); ContactFilterPass.Capture(context);
            var replacement = PolicyChild("Replacement").AddComponent<VRCContactSender>();
            ObjectRegistry.RegisterReplacedObject(original, replacement); Object.DestroyImmediate(original);
            var other = PolicyChild("Other").AddComponent<VRCContactReceiver>();
            ContactFilterPass.Apply(context);
            Assert.That(replacement != null && other == null, Is.True);
        }

        [TestCase("Disabled")] [TestCase("Platform")] [TestCase("EditorOnly")] [TestCase("AncestorFilter")]
        public void ContactFilterHonorsComponentAndAncestorPlatformExclusions(string reason)
        {
            var holder = PolicyChild("Settings"); var component = ContactFilter(holder); component.ScopeRoot = root.transform;
            var sender = PolicyChild("Sender").AddComponent<VRCContactSender>();
            if (reason == "Disabled") component.enabled = false;
            if (reason == "Platform") component.ApplyOnPlatforms = OptimizationPlatforms.None;
            if (reason == "EditorOnly") holder.tag = "EditorOnly";
            if (reason == "AncestorFilter") BuildPlatformFilters.Register("contact-filter-test", (t, c) => !t.IsChildOf(holder.transform));
            try
            {
                var context = new BuildContext(root, null); ContactFilterPass.Capture(context); ContactFilterPass.Apply(context);
                Assert.That(sender != null, Is.True); Assert.That(component == null, Is.True);
            }
            finally { BuildPlatformFilters.Register("contact-filter-test", (t, c) => true); }
        }

        [TestCase(false)] [TestCase(true)]
        public void ContactFilterRejectsSelectionsOutsideItsScope(bool objectRoot)
        {
            var component = ContactFilter(PolicyChild("Settings"));
            var outside = PolicyChild("Outside").AddComponent<VRCContactSender>();
            if (objectRoot) component.ObjectRoots.Add(new ContactObjectSelection { Root = outside.transform });
            else component.Senders.Add(outside);
            Assert.Throws<InvalidOperationException>(() => ContactFilterPass.Capture(new BuildContext(root, null)));
            Assert.That(outside != null, Is.True);
        }

        [Test]
        public void ContactFilterRejectsOverlappingEnabledFilters()
        {
            ContactFilter(); ContactFilter(PolicyChild("Second"));
            Assert.Throws<InvalidOperationException>(() => ContactFilterPass.Capture(new BuildContext(root, null)));
        }

        [Test]
        public void ContactFilterPreservedReceiverInputRejectsAllRemovalBeforeMutation()
        {
            ContactFilter(); var policy = Policy(); policy.PreserveParameters.Names.Add("Touch");
            var receiver = PolicyChild("Receiver").AddComponent<VRCContactReceiver>(); receiver.parameter = "Touch";
            var sender = PolicyChild("Sender").AddComponent<VRCContactSender>();
            var source = Controller(Clip()); source.AddParameter("Touch", AnimatorControllerParameterType.Float);
            var context = ExpressionBuild(null, source);
            ParameterPolicyPass.Capture(context); ParameterPolicyPass.RemoveMarkers(context); ContactFilterPass.Capture(context);
            Assert.Throws<InvalidOperationException>(() => ContactFilterPass.Apply(context));
            Assert.That(sender != null && receiver != null, Is.True);
        }

        [Test]
        public void ContactFilterRejectsReferencedContactsBeforeDeletingOtherContacts()
        {
            ContactFilter(); var sender = PolicyChild("Sender").AddComponent<VRCContactSender>();
            var receiver = PolicyChild("Receiver").AddComponent<VRCContactReceiver>();
            var consumer = PolicyChild("Consumer").AddComponent<ParameterPolicySourceFixture>(); consumer.ReferencedObject = receiver;
            var context = new BuildContext(root, null); ContactFilterPass.Capture(context);
            Assert.Throws<InvalidOperationException>(() => ContactFilterPass.Apply(context));
            Assert.That(sender != null && receiver != null, Is.True);
        }

        [Test]
        public void ContactFilterRemovesOnlyOrphanBindingsAndPreservesClipDurationAndSource()
        {
            ContactFilter(); var sender = PolicyChild("Sender").AddComponent<VRCContactSender>();
            var source = Clip(); var binding = EditorCurveBinding.FloatCurve("Sender", typeof(VRCContactSender), "m_Enabled");
            AnimationUtility.SetEditorCurve(source, binding, AnimationCurve.Linear(0, 1, 3, 0));
            var entries = ConstraintControllers(source);
            var clip = entries.Values.Single().AllReachableNodes().OfType<VirtualClip>().Single();
            var context = new BuildContext(root, null); ContactFilterPass.Capture(context); ContactFilterPass.Apply(context, entries);
            Assert.That(sender == null, Is.True); Assert.That(clip.GetFloatCurveBindings(), Has.No.Member(binding));
            Assert.That(MotionAnalysis.EndTime(clip), Is.EqualTo(3));
            Assert.That(AnimationUtility.GetCurveBindings(source), Does.Contain(binding));
        }

        [Test]
        public void ContactFilterSharedClipRetainsBindingForAnotherAnimatorRoot()
        {
            var left = PolicyChild("Left"); var right = PolicyChild("Right"); ContactFilter(left);
            var removed = PolicyChild("Contact", left.transform).AddComponent<VRCContactSender>();
            var kept = PolicyChild("Contact", right.transform).AddComponent<VRCContactSender>();
            var source = Clip(); var binding = EditorCurveBinding.FloatCurve("Contact", typeof(VRCContactSender), "m_Enabled");
            AnimationUtility.SetEditorCurve(source, binding, AnimationCurve.Constant(0, 2, 1));
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(source));
            var entries = new Dictionary<object, VirtualAnimatorController>
            { [left.AddComponent<Animator>()] = controller, [right.AddComponent<Animator>()] = controller };
            var context = new BuildContext(root, null); ContactFilterPass.Capture(context); ContactFilterPass.Apply(context, entries);
            Assert.That(removed == null && kept != null, Is.True);
            Assert.That(controller.AllReachableNodes().OfType<VirtualClip>().Single().GetFloatCurveBindings(), Does.Contain(binding));
        }

        [TestCase(false)] [TestCase(true)]
        public void ContactFilterObjectReferenceCurvesAreValidatedAndRemovedWhenOrphaned(bool orphaned)
        {
            ContactFilter(); var sender = PolicyChild("Sender").AddComponent<VRCContactSender>();
            var source = Clip();
            var binding = orphaned ? EditorCurveBinding.PPtrCurve("Sender", typeof(VRCContactSender), "rootTransform") :
                EditorCurveBinding.PPtrCurve("Consumer", typeof(ParameterPolicySourceFixture), "ReferencedObject");
            PolicyChild("Consumer").AddComponent<ParameterPolicySourceFixture>();
            AnimationUtility.SetObjectReferenceCurve(source, binding, new[] { new ObjectReferenceKeyframe { time = 2, value = orphaned ? (Object)root.transform : sender } });
            var entries = ConstraintControllers(source); var clip = entries.Values.Single().AllReachableNodes().OfType<VirtualClip>().Single();
            var context = new BuildContext(root, null); ContactFilterPass.Capture(context);
            if (!orphaned)
            {
                Assert.Throws<InvalidOperationException>(() => ContactFilterPass.Apply(context, entries));
                Assert.That(sender != null, Is.True);
            }
            else
            {
                ContactFilterPass.Apply(context, entries);
                Assert.That(sender == null, Is.True); Assert.That(clip.GetObjectCurveBindings(), Is.Empty);
                Assert.That(MotionAnalysis.EndTime(clip), Is.EqualTo(2));
            }
            Assert.That(AnimationUtility.GetObjectReferenceCurveBindings(source), Does.Contain(binding));
        }

        [Test]
        public void ContactFilterDeclinedRemovalFailsWithoutErasingBindings()
        {
            ContactFilter(); var sender = PolicyChild("Sender").AddComponent<VRCContactSender>();
            var binding = EditorCurveBinding.FloatCurve("Sender", typeof(VRCContactSender), "m_Enabled");
            var entries = ConstraintControllers(StaticCurve(binding, 1));
            var context = new BuildContext(root, null); ContactFilterPass.Capture(context);
            context.GetState<ContactFilterState>().DestroyObject = _ => { };
            Assert.Throws<InvalidOperationException>(() => ContactFilterPass.Apply(context, entries));
            Assert.That(sender != null, Is.True);
            Assert.That(entries.Values.Single().AllReachableNodes().OfType<VirtualClip>().Single().GetFloatCurveBindings(), Does.Contain(binding));
        }

        [Test]
        public void ContactFilterSdkNoOpDoesNotCloneOrRewriteControllers()
        {
            var filter = ContactFilter(); filter.FilterMode = ContactFilterMode.RemoveListed;
            var source = Controller(Clip()); var context = ExpressionBuild(null, source);
            var retained = PolicyChild("Unselected Contact").AddComponent<VRCContactSender>();
            ContactFilterPass.Capture(context); ContactFilterPass.Apply(context);
            AvatarOutputSdkFinalizer.Begin(root); AvatarOutputSdkFinalizer.Capture(context);
            AvatarOutputSdkFinalizer.Complete(root, _ => Assert.Fail("A no-op filter must not create assets."));
            Assert.That(retained != null, Is.True);
            Assert.That(root.GetComponent<VRCAvatarDescriptor>().baseAnimationLayers.Single().animatorController, Is.SameAs(source));
        }

        [TestCase(ContactFilterMode.KeepListed)] [TestCase(ContactFilterMode.RemoveListed)]
        public void ContactFilterSdkSelectionUsesTheCapturedReplacementRegistry(ContactFilterMode mode)
        {
            var filter = ContactFilter(); filter.FilterMode = mode;
            var original = PolicyChild("Selected Contact").AddComponent<VRCContactSender>(); filter.Senders.Add(original);
            var source = Controller(Clip()); var context = ExpressionBuild(null, source);
            VRCContactSender replacement;
            using (new ObjectRegistryScope(new ObjectRegistry(root.transform)))
            {
                ContactFilterPass.Capture(context);
                AvatarOutputSdkFinalizer.Begin(root); AvatarOutputSdkFinalizer.Capture(context);
                replacement = PolicyChild("Replacement Contact").AddComponent<VRCContactSender>();
                ObjectRegistry.RegisterReplacedObject(original, replacement); Object.DestroyImmediate(original);
            }
            AvatarOutputSdkFinalizer.Complete(root, o =>
            {
                Assert.That(mode, Is.EqualTo(ContactFilterMode.RemoveListed), "A retained replacement must not trigger controller rewriting.");
                Track(o);
            });
            Assert.That(replacement != null, Is.EqualTo(mode == ContactFilterMode.KeepListed));
            var built = (AnimatorController)root.GetComponent<VRCAvatarDescriptor>().baseAnimationLayers.Single().animatorController;
            if (mode == ContactFilterMode.KeepListed) Assert.That(built, Is.SameAs(source));
            else TrackControllerAssets(built);
        }

        [Test]
        public void ContactFilterSdkFinalizerRemovesLateContactsWithoutOtherOptimizers()
        {
            ContactFilter(); var source = Controller(Clip()); var context = ExpressionBuild(null, source);
            ContactFilterPass.Capture(context); ContactFilterPass.Apply(context);
            AvatarOutputSdkFinalizer.Begin(root); AvatarOutputSdkFinalizer.Capture(context);
            var late = PolicyChild("Late SDK Contact").AddComponent<VRCContactReceiver>();
            AvatarOutputSdkFinalizer.Complete(root, o => Track(o));
            Assert.That(late == null, Is.True);
            var descriptor = root.GetComponent<VRCAvatarDescriptor>();
            TrackControllerAssets((AnimatorController)descriptor.baseAnimationLayers.Single().animatorController);
            Assert.That(context.GetState<OptimizationBuildReport>().Messages.Any(m => m.StartsWith("Contact filter removed 0 Senders, 1 Receivers")), Is.True);
        }
    }
}
