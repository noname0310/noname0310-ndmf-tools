using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.ParameterLayout;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDK3.Dynamics.Contact.Components;
using VRC.SDKBase;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private void MultiplexContextMenu()
        {
            var descriptor = root.GetComponent<VRCAvatarDescriptor>();
            descriptor.expressionsMenu = ExpressionMenu(ExpressionControl("Persistent output", "Output"));
            descriptor.expressionParameters.parameters = descriptor.expressionParameters.parameters.Concat(new[]
            { LayoutParameter("Output") }).ToArray();
        }

        private void MultiplexContextWriter(AnimatorState state, string kind)
        {
            var driver = state.behaviours.OfType<VRC_AvatarParameterDriver>().Single();
            driver.parameters.Clear();
            if (kind == "Curve")
            {
                var clip = Clip();
                AnimationUtility.SetEditorCurve(clip, ParameterBinding("Buffer"), AnimationCurve.Linear(0, 0, 1, 1));
                state.motion = clip;
            }
            else driver.parameters.Add(new VRC_AvatarParameterDriver.Parameter
            {
                name = "Buffer", type = kind == "Add" ? VRC_AvatarParameterDriver.ChangeType.Add : VRC_AvatarParameterDriver.ChangeType.Random,
                value = 1, valueMin = 0, valueMax = 1
            });
        }

        private MultiplexSelectionResult MultiplexContextSelection(AnimatorController source, bool preserve = false)
        {
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            return MultiplexSelection.Analyze(root, new[] { controller },
                preserve ? new HashSet<string> { "Output" } : new HashSet<string>(), new List<string>());
        }

        [TestCase("Curve", false, false)] [TestCase("Curve", false, true)]
        [TestCase("Curve", true, false)] [TestCase("Curve", true, true)]
        [TestCase("Add", false, false)] [TestCase("Add", false, true)]
        [TestCase("Add", true, false)] [TestCase("Add", true, true)]
        [TestCase("Random", false, false)] [TestCase("Random", false, true)]
        [TestCase("Random", true, false)] [TestCase("Random", true, true)]
        public void MultiplexingContextKeepsWriterInfluenceInItsExecutingInstance(string writer, bool network, bool reverse)
        {
            var fixture = InstanceFixture(network, reverse);
            MultiplexContextWriter(fixture.writer, writer);
            MultiplexContextMenu();
            var result = MultiplexContextSelection(fixture.source);
            bool dynamic = network && !reverse;
            Assert.That(result.Eligible.Contains("Output"), Is.EqualTo(!dynamic));
            if (dynamic) Assert.That(result.Excluded["Output"], Is.EqualTo(MultiplexExclusion.DynamicInput));
            Assert.That(MultiplexContextSelection(fixture.source, preserve: true).Excluded["Output"] & MultiplexExclusion.Preserved,
                Is.EqualTo(MultiplexExclusion.Preserved), "Explicit preservation still wins.");
        }

        [TestCase("Curve")] [TestCase("Add")] [TestCase("Random")]
        public void MultiplexingContextIgnoresWritersInProvenUnreachableStates(string writer)
        {
            var fixture = InstanceFixture(network: true);
            MultiplexContextWriter(fixture.writer, writer);
            fixture.initial.transitions.Single(t => t.destinationState == fixture.writer).mute = true;
            MultiplexContextMenu();
            Assert.That(MultiplexContextSelection(fixture.source).Eligible, Does.Contain("Output"));
        }

        [TestCase("SharedBehaviour")] [TestCase("SharedClip")] [TestCase("Sensor")]
        [TestCase("UnknownIdentity")] [TestCase("ChildAnimator")]
        public void MultiplexingContextRetainsSharedExternalAndUnverifiedWriterPaths(string path)
        {
            var fixture = InstanceFixture();
            MultiplexContextWriter(fixture.writer, path == "SharedClip" ? "Curve" : "Add");
            if (path == "SharedBehaviour") fixture.initial.behaviours = fixture.writer.behaviours;
            if (path == "SharedClip") fixture.initial.motion = fixture.writer.motion;
            if (path == "Sensor") PolicyChild("Sensor").AddComponent<VRCContactReceiver>().parameter = "Buffer";
            if (path == "UnknownIdentity") fixture.source.parameters = fixture.source.parameters.Where(p => p.name != "IsLocal").ToArray();
            if (path == "ChildAnimator") PolicyChild("Independent owner").AddComponent<Animator>().runtimeAnimatorController = fixture.source;
            MultiplexContextMenu();
            Assert.That(MultiplexContextSelection(fixture.source).Excluded["Output"], Is.EqualTo(MultiplexExclusion.DynamicInput));
        }

        [TestCase(false)] [TestCase(true)]
        public void MultiplexingContextSkipsRemoteLocalOnlyWriters(bool network)
        {
            var fixture = InstanceFixture(network, reverse: true);
            MultiplexContextWriter(fixture.writer, "Add");
            fixture.writer.behaviours.OfType<VRC_AvatarParameterDriver>().Single().localOnly = true;
            MultiplexContextMenu();
            Assert.That(MultiplexContextSelection(fixture.source).Eligible, Does.Contain("Output"));
        }

        [TestCase(false)] [TestCase(true)]
        public void MultiplexingContextProofMatchesNativeOutputWhenTheOtherInstanceChangesItsPrivateBuffer(bool reverse)
        {
            var fixture = InstanceFixture(reverse: reverse);
            MultiplexContextWriter(fixture.writer, "Add");
            MultiplexContextMenu();
            Assert.That(MultiplexContextSelection(fixture.source).Eligible, Does.Contain("Output"));
            float first = SampleInstanceController(fixture.source, reverse, .1f, .35f);
            fixture.writer.behaviours.OfType<VRC_AvatarParameterDriver>().Single().parameters[0].value = 100;
            float second = SampleInstanceController(fixture.source, reverse, .9f, .35f);
            Assert.That(first, Is.EqualTo(35).Within(.001));
            Assert.That(second, Is.EqualTo(first).Within(.001), "The other instance's Driver cannot change this private value.");
            Assert.That(SampleInstanceController(fixture.source, reverse, .1f, .8f), Is.EqualTo(80).Within(.001),
                "The actual reader input remains observable; it was not frozen or deleted.");
        }
    }
}
