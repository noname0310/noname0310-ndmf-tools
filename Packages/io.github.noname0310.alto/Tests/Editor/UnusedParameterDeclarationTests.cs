using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.AnimatorOptimization;
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
        [TestCase(false)] [TestCase(true)]
        public void UnreferencedAnimatorDeclarationsRespectTheirIndependentOption(bool enabled)
        {
            var source = Controller(Clip());
            source.AddParameter("Unused", AnimatorControllerParameterType.Float);
            var result = OptimizeAnimator(source, new() { ParameterDeclarations = enabled });
            Assert.That(result.report.ParameterDeclarations, Is.EqualTo(enabled ? 1 : 0));
            Assert.That(result.controller.Parameters.ContainsKey("Unused"), Is.EqualTo(!enabled));
            Assert.That(source.parameters.Select(p => p.name), Does.Contain("Unused"));
            Assert.That(AnimatorOptimizer.Optimize(root, new[] { result.controller }, new() { ParameterDeclarations = enabled }).Changes, Is.Zero);
        }

        [TestCase("Expression")] [TestCase("LocalExpression")] [TestCase("Menu")] [TestCase("Contact")]
        [TestCase("Builtin")] [TestCase("Preserve")] [TestCase("ExternalRead")] [TestCase("ExternalWrite")]
        [TestCase("Transition")] [TestCase("Speed")] [TestCase("Tree")] [TestCase("Curve")]
        [TestCase("Driver")] [TestCase("Event")]
        public void AnimatorDeclarationsKeepRuntimeAndSerializedReferences(string kind)
        {
            string name = kind == "Builtin" ? "GestureLeft" : "Kept";
            var source = Controller(Clip(), Clip());
            source.AddParameter(name, AnimatorControllerParameterType.Float);
            source.AddParameter("Unused", AnimatorControllerParameterType.Float);
            var states = source.layers[0].stateMachine.states.Select(s => s.state).ToArray();
            if (kind == "Expression" || kind == "LocalExpression")
            {
                root.AddComponent<VRCAvatarDescriptor>();
                FreezeExpressions(new VRCExpressionParameters.Parameter { name = name, networkSynced = kind == "Expression" });
            }
            if (kind == "Menu") root.AddComponent<VRCAvatarDescriptor>().expressionsMenu = ExpressionMenu(ExpressionControl("Menu", name));
            if (kind == "Contact") root.AddComponent<VRCContactReceiver>().parameter = name;
            if (kind == "Transition") Edge(states[0], states[1], name, .5f, AnimatorConditionMode.Greater);
            if (kind == "Speed") { states[0].speedParameter = name; states[0].speedParameterActive = true; }
            if (kind == "Tree")
            {
                var tree = Track(new BlendTree { blendType = BlendTreeType.Simple1D, blendParameter = name });
                tree.AddChild(Clip(), 0); tree.AddChild(Clip(), 1); states[0].motion = tree;
            }
            if (kind == "Curve") states[0].motion = StaticCurve(ParameterBinding(name), 1);
            if (kind == "Driver") Driver(states[0], name, 1);
            if (kind == "Event") AnimationUtility.SetAnimationEvents((AnimationClip)states[0].motion,
                new[] { new AnimationEvent { functionName = "Observe" } });
            const string provider = "Animator declaration retention test";
            ExternalParameterAccess.Register(provider, access => {
                if (kind == "ExternalRead") access.Readers.Add(n => n == name);
                if (kind == "ExternalWrite") access.Writers.Add(n => n == name);
            });
            try
            {
                var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
                var report = AnimatorOptimizer.Optimize(root, new[] { controller }, new() { ParameterDeclarations = true },
                    preservedParameters: kind == "Preserve" ? new[] { name } : null, finalParameterDeclarations: true);
                Assert.That(controller.Parameters.ContainsKey(name), Is.True);
                Assert.That(report.ParameterDeclarations, Is.EqualTo(kind == "Event" ? 0 : 1));
            }
            finally { ExternalParameterAccess.Register(provider, _ => { }); }
        }

        [Test]
        public void DeclarationRemovalRetainsSharedDefaultsInAllControllers()
        {
            var a = Controller(Clip());
            a.AddParameter(new AnimatorControllerParameter { name = "Shared", type = AnimatorControllerParameterType.Float, defaultFloat = .2f });
            var b = Controller(StaticCurve(ParameterBinding("Shared"), 1));
            b.AddParameter(new AnimatorControllerParameter { name = "Shared", type = AnimatorControllerParameterType.Float, defaultFloat = .7f });
            a.AddParameter("Unused", AnimatorControllerParameterType.Float);
            var clone = new CloneContext(GenericPlatformAnimatorBindings.Instance);
            var controllers = new[] { clone.Clone(a), clone.Clone(b) };
            var report = AnimatorOptimizer.Optimize(root, controllers, new() { ParameterDeclarations = true }, finalParameterDeclarations: true);
            Assert.That(report.ParameterDeclarations, Is.EqualTo(1));
            Assert.That(controllers.All(c => c.Parameters.ContainsKey("Shared")), Is.True);
            Assert.That(controllers[0].Parameters["Shared"].defaultFloat, Is.EqualTo(.2f));
            Assert.That(controllers[1].Parameters["Shared"].defaultFloat, Is.EqualTo(.7f));
        }

        [Test]
        public void DeadDynamicFeedbackTreesReleaseInternalDeclarations()
        {
            Mesh("Visible");
            var source = TreeController(("B", StaticCurve(ParameterBinding("A"), 1)), ("A", StaticCurve(ParameterBinding("B"), 1)));
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var settings = new AnimatorOptimizationSettings { StateLogic = true, ParameterDeclarations = true };
            var result = AnimatorOptimizer.Optimize(root, new[] { controller }, settings, finalParameterDeclarations: true);
            Assert.That(result.ParameterCurves, Is.EqualTo(2));
            Assert.That(result.Trees, Is.EqualTo(1));
            Assert.That(result.ParameterDeclarations, Is.EqualTo(2));
            Assert.That(controller.Layers.Count(), Is.EqualTo(1));
            Assert.That(controller.Layers.First().StateMachine.DefaultState.Motion, Is.TypeOf<VirtualClip>());
            Assert.That(AnimatorOptimizer.Optimize(root, new[] { controller }, settings, finalParameterDeclarations: true).Changes, Is.Zero);
        }

        [Test]
        public void EarlyPassLeavesDeclarationsAvailableToLateGenerators()
        {
            var source = Controller(Clip());
            source.AddParameter("LateInput", AnimatorControllerParameterType.Float);
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var settings = new AnimatorOptimizationSettings { ParameterDeclarations = true };
            Assert.That(AnimatorOptimizer.Optimize(root, new[] { controller }, settings).ParameterDeclarations, Is.Zero);
            Assert.That(controller.Parameters.ContainsKey("LateInput"), Is.True);
            Assert.That(AnimatorOptimizer.Optimize(root, new[] { controller }, settings, finalParameterDeclarations: true).ParameterDeclarations, Is.EqualTo(1));
        }

        [Test]
        public void ProxyMotionInterfacesDoNotRetainUnrelatedControllerDeclarations()
        {
            var normal = Controller(Clip());
            normal.AddParameter("Unused", AnimatorControllerParameterType.Float);
            normal.AddParameter("Shared", AnimatorControllerParameterType.Float);
            var opaque = Controller(Clip());
            opaque.AddParameter("Shared", AnimatorControllerParameterType.Float);
            opaque.AddParameter("ProxyInput", AnimatorControllerParameterType.Float);
            var clone = new CloneContext(GenericPlatformAnimatorBindings.Instance);
            var controllers = new[] { clone.Clone(normal), clone.Clone(opaque) };
            controllers[1].Layers.First().StateMachine.DefaultState.Motion = VirtualClip.FromMarker(Clip());
            var report = AnimatorOptimizer.Optimize(root, controllers, new() { ParameterDeclarations = true }, finalParameterDeclarations: true);
            Assert.That(report.ParameterDeclarations, Is.EqualTo(1));
            Assert.That(controllers[0].Parameters.Keys, Is.EquivalentTo(new[] { "Shared" }));
            Assert.That(controllers[1].Parameters.Keys, Is.EquivalentTo(new[] { "Shared", "ProxyInput" }));
        }
    }
}
