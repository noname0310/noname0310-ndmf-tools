using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.ParameterLayout;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDK3.Dynamics.Contact.Components;
using Parameter = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters.Parameter;
using ValueType = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters.ValueType;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        [Test, Combinatorial]
        public void LatePaddingRestoresKnownLocalConsumersWithoutChangingTheirValues(
            [Values(ValueType.Bool, ValueType.Int, ValueType.Float)] ValueType type,
            [Values("Unchanged", "Default", "Saved", "Both", "Interpolation")] string desktopChange,
            [Values(false, true)] bool oldPaddingLeftLocal)
        {
            var descriptor = LayoutDescriptor();
            var source = Track(ScriptableObject.CreateInstance<VRCExpressionParameters>());
            var local = LayoutParameter("LocalInput", type, false);
            local.defaultValue = type == ValueType.Float ? .5f : 0; local.saved = true;
            local.useCustomLerpDuration = true; local.customLerpDuration = .125f;
            source.parameters = new[] { LayoutParameter("Face"), local }; descriptor.expressionParameters = source;
            var controller = Controller(Clip());
            controller.AddParameter("LocalInput", type == ValueType.Float ? AnimatorControllerParameterType.Float :
                type == ValueType.Int ? AnimatorControllerParameterType.Int : AnimatorControllerParameterType.Bool);
            descriptor.baseAnimationLayers = new[] { new VRCAvatarDescriptor.CustomAnimLayer
                { type = VRCAvatarDescriptor.AnimLayerType.FX, animatorController = controller, isDefault = false } };
            string controllerBefore = EditorJsonUtility.ToJson(controller);
            using var fixture = new LayoutTestStore();
            fixture.Store.Commit(LayoutRecord(LayoutParameter("Face"), LayoutParameter("LocalInput", type)));
            string referenceBefore = System.IO.File.ReadAllText(fixture.Store.PathFor(LayoutId));
            var context = new BuildContext(root, null); ParameterLayoutSdkSession.Begin(root);
            ParameterLayoutSdkSession.Defer(context, new ParameterLayoutSettings(), ParameterLayoutPlatform.Android, new OptimizationBuildReport(), fixture.Store);
            ParameterLayoutSdkSession.Prepare(root, value => Track(value), fixture.Store);
            var prepared = Track(descriptor.expressionParameters);
            string padding = prepared.parameters.Single(p => p.networkSynced && p.name != "Face").name;
            var late = Track(UnityEngine.Object.Instantiate(prepared));
            late.parameters = prepared.parameters.Where(p => p.name != padding).Select(ParameterLayoutModel.Copy).ToArray();
            var desktop = late.parameters.Single(p => p.name == "LocalInput");
            desktop.networkSynced = true;
            if (desktopChange == "Default" || desktopChange == "Both") desktop.defaultValue = 1;
            if (desktopChange == "Saved" || desktopChange == "Both") desktop.saved = false;
            if (desktopChange == "Interpolation") { desktop.useCustomLerpDuration = false; desktop.customLerpDuration = .5f; }
            if (oldPaddingLeftLocal)
            {
                var abandoned = ParameterLayoutModel.Copy(prepared.parameters.Single(p => p.name == padding));
                abandoned.networkSynced = false;
                late.parameters = late.parameters.Concat(new[] { abandoned }).ToArray();
            }
            descriptor.expressionParameters = late;
            string lateBefore = EditorJsonUtility.ToJson(late);
            Assert.That(new ParameterPaddingUsage(root).IsUnobserved("LocalInput"), Is.False);
            ParameterLayoutSdkSession.Complete(root, value => Track(value), fixture.Store);
            var output = descriptor.expressionParameters.parameters;
            Assert.That(output.Where(p => p.networkSynced).Select(p => p.name), Is.EqualTo(new[] { "Face", padding }));
            var restored = output.Single(p => p.name == "LocalInput");
            Assert.That(JsonUtility.ToJson(restored), Is.EqualTo(JsonUtility.ToJson(local)));
            Assert.That(output.Single(p => p.name == padding).saved, Is.False);
            Assert.That(output.Single(p => p.name == padding).defaultValue, Is.Zero);
            Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(controllerBefore));
            Assert.That(EditorJsonUtility.ToJson(late), Is.EqualTo(lateBefore));
            Assert.That(System.IO.File.ReadAllText(fixture.Store.PathFor(LayoutId)), Is.EqualTo(referenceBefore));
        }

        [TestCase("UnknownSource")] [TestCase("WrongSource")] [TestCase("MissingLocalSnapshot")]
        [TestCase("LocalTypeChanged")] [TestCase("LocalBitWidthChanged")] [TestCase("LocalRepresentationChanged")]
        [TestCase("PaddingObserved")]
        public void LatePaddingRejectsUnprovenRestorationOfAnObservedLocalInput(string damage)
        {
            var local = LayoutParameter("LocalInput", ValueType.Float, false); local.defaultValue = .5f; local.saved = true;
            var aligned = ParameterLayoutModel.Align(LayoutRecord(LayoutParameter("LocalInput")), new[] { local }, AdditionalSyncedParameterPolicy.Error);
            var before = ParameterLayoutModel.Capture(aligned.Parameters);
            var late = ParameterLayoutModel.Copy(local); late.networkSynced = true;
            if (damage == "UnknownSource") aligned.PaddingReferenceNames.Clear();
            if (damage == "WrongSource") aligned.PaddingReferenceNames[before.Single().Name] = "DifferentInput";
            late.defaultValue = 0; late.saved = false;
            if (damage == "LocalTypeChanged") local.valueType = ValueType.Int;
            if (damage == "LocalBitWidthChanged") local.bitWidth = 6;
            if (damage == "LocalRepresentationChanged") local.flippedRepresentation = true;
            Assert.Throws<InvalidOperationException>(() => ParameterLayoutModel.ReconcileLatePadding(before, new[] { late }, aligned.PaddingNames,
                name => damage != "PaddingObserved" && name != "LocalInput", aligned.PaddingReferenceNames,
                damage == "MissingLocalSnapshot" ? null : new[] { local }));
        }

        [TestCase(ValueType.Bool)] [TestCase(ValueType.Int)] [TestCase(ValueType.Float)]
        public void LatePaddingRenamesPreserveWireSlotsAndLocalLogicalValues(ValueType type)
        {
            var reference = LayoutRecord(LayoutParameter("Face"), LayoutParameter("Removed", type), LayoutParameter("Gate", ValueType.Bool));
            var aligned = ParameterLayoutModel.Align(reference, new[] {
                LayoutParameter("Gate", ValueType.Bool), LayoutParameter("Removed", type, false), LayoutParameter("Face")
            }, AdditionalSyncedParameterPolicy.Error);
            var before = ParameterLayoutModel.Capture(aligned.Parameters);
            var late = aligned.Parameters.Select(ParameterLayoutModel.Copy).ToList();
            late[1].name = "UnrelatedGeneratedFiller"; late[1].defaultValue = 1; late[1].saved = true;
            var oldPadding = ParameterLayoutModel.Copy(aligned.Parameters[1]); oldPadding.networkSynced = false;
            late.Add(oldPadding);
            var snapshot = late.Select(p => (p.name, p.valueType, p.networkSynced, p.defaultValue, p.saved)).ToArray();
            var result = ParameterLayoutModel.ReconcileLatePadding(before, late.ToArray(), aligned.PaddingNames, _ => true);
            Assert.That(result.Where(p => p.networkSynced).Select(p => (p.name, p.valueType)),
                Is.EqualTo(aligned.Parameters.Where(p => p.networkSynced).Select(p => (p.name, p.valueType))));
            Assert.That(result.Single(p => p.name == "Removed").networkSynced, Is.False);
            Assert.That(result.Single(p => p.name == "Removed").defaultValue, Is.EqualTo(.375f));
            Assert.That(result.Single(p => aligned.PaddingNames.Contains(p.name)).saved, Is.False);
            Assert.That(late.Select(p => (p.name, p.valueType, p.networkSynced, p.defaultValue, p.saved)), Is.EqualTo(snapshot));
        }

        [TestCase("NativeRenamed")] [TestCase("NativeLocal")] [TestCase("ExtraChannel")]
        [TestCase("PaddingObserved")] [TestCase("ReplacementObserved")] [TestCase("WrongSlot")]
        [TestCase("WrongType")] [TestCase("UnownedPadding")]
        public void LatePaddingProofNeverAcceptsAnUnverifiedTransportChange(string change)
        {
            var aligned = ParameterLayoutModel.Align(LayoutRecord(LayoutParameter("Face"), LayoutParameter("Removed", ValueType.Bool)),
                new[] { LayoutParameter("Face") }, AdditionalSyncedParameterPolicy.Error);
            var before = ParameterLayoutModel.Capture(aligned.Parameters);
            var late = aligned.Parameters.Select(ParameterLayoutModel.Copy).ToList();
            string padding = late[1].name; late[1].name = "Replacement";
            if (change == "NativeRenamed") late[0].name = "EncodedFace";
            if (change == "NativeLocal") late[0].networkSynced = false;
            if (change == "ExtraChannel") late.Add(LayoutParameter("AnotherChannel", ValueType.Int));
            if (change == "WrongSlot") late.Reverse();
            if (change == "WrongType") late[1].valueType = ValueType.Int;
            if (change == "UnownedPadding") aligned.PaddingNames.Clear();
            bool Unobserved(string name) => !(change == "PaddingObserved" && name == padding) &&
                !(change == "ReplacementObserved" && name == "Replacement");
            Assert.Throws<InvalidOperationException>(() => ParameterLayoutModel.ReconcileLatePadding(
                before, late.ToArray(), aligned.PaddingNames, Unobserved));
        }

        [TestCase(false)] [TestCase(true)]
        public void MobileSdkLayoutIsPreparedBelowBudgetAndAcceptsOnlyUnusedFillerReplacements(bool oldPaddingLeftLocal)
        {
            var descriptor = LayoutDescriptor();
            var source = Track(ScriptableObject.CreateInstance<VRCExpressionParameters>());
            source.parameters = new[] { LayoutParameter("Gate", ValueType.Bool), LayoutParameter("Face") };
            descriptor.expressionParameters = source;
            using var fixture = new LayoutTestStore();
            fixture.Store.Commit(LayoutRecord(LayoutParameter("Face"), LayoutParameter("Removed", ValueType.Bool), LayoutParameter("Gate", ValueType.Bool)));
            var context = new BuildContext(root, null);
            ParameterLayoutSdkSession.Begin(root);
            ParameterLayoutSdkSession.Defer(context, new ParameterLayoutSettings(), ParameterLayoutPlatform.Android, new OptimizationBuildReport(), fixture.Store);
            Assert.That(descriptor.expressionParameters, Is.SameAs(source));
            ParameterLayoutSdkSession.Prepare(root, value => Track(value), fixture.Store);
            var prepared = Track(descriptor.expressionParameters);
            Assert.That(prepared.parameters.Select(p => p.valueType), Is.EqualTo(new[] { ValueType.Float, ValueType.Bool, ValueType.Bool }));
            var late = Track(UnityEngine.Object.Instantiate(prepared));
            late.parameters = late.parameters.Select(ParameterLayoutModel.Copy).ToArray();
            string originalPadding = late.parameters[1].name;
            late.parameters[1].name = "GeneratedByAnotherLayoutTool";
            if (oldPaddingLeftLocal) late.parameters = late.parameters.Concat(new[] { LayoutParameter(originalPadding, ValueType.Bool, false) }).ToArray();
            descriptor.expressionParameters = late;
            string unchanged = EditorJsonUtility.ToJson(late);
            ParameterLayoutSdkSession.Complete(root, value => Track(value), fixture.Store);
            var final = descriptor.expressionParameters.parameters;
            Assert.That(final.Where(p => p.networkSynced).Select(p => p.name), Is.EqualTo(prepared.parameters.Select(p => p.name)));
            Assert.That(EditorJsonUtility.ToJson(late), Is.EqualTo(unchanged));
            Assert.That(source.parameters.Select(p => p.name), Is.EqualTo(new[] { "Gate", "Face" }));
        }

        [TestCase("Declaration")] [TestCase("Curve")] [TestCase("Condition")] [TestCase("Tree")]
        [TestCase("Time")] [TestCase("Driver")] [TestCase("Sensor")] [TestCase("Menu")]
        [TestCase("Event")] [TestCase("ChildAnimator")] [TestCase("UnknownBehaviour")]
        public void LatePaddingUsageFindsNativeAndUndeclaredConsumers(string kind)
        {
            var descriptor = LayoutDescriptor();
            var clip = Track(new AnimationClip());
            var controller = Track(new AnimatorController());
            controller.AddLayer("Layer"); Track(controller.layers[0].stateMachine);
            var state = Track(controller.layers[0].stateMachine.AddState("State")); state.motion = clip;
            descriptor.baseAnimationLayers = new[] { new VRCAvatarDescriptor.CustomAnimLayer
                { type = VRCAvatarDescriptor.AnimLayerType.FX, animatorController = controller, isDefault = false } };
            const string name = "PotentialFiller";
            if (kind == "Declaration") controller.AddParameter(name, AnimatorControllerParameterType.Float);
            if (kind == "Curve") AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), name), AnimationCurve.Constant(0, 1, 1));
            if (kind == "Condition") Track(state.AddExitTransition()).AddCondition(AnimatorConditionMode.If, 0, name);
            if (kind == "Tree")
            {
                var tree = Track(new BlendTree { blendType = BlendTreeType.Simple1D, blendParameter = name });
                tree.AddChild(clip); state.motion = tree;
            }
            if (kind == "Time") { state.timeParameterActive = true; state.timeParameter = name; }
            if (kind == "Driver") Track(state.AddStateMachineBehaviour<VRCAvatarParameterDriver>()).parameters.Add(
                new VRC.SDKBase.VRC_AvatarParameterDriver.Parameter { name = name, value = 1 });
            if (kind == "Sensor") PolicyChild("Sensor").AddComponent<VRCContactReceiver>().parameter = name;
            if (kind == "Menu")
            {
                var menu = Track(ScriptableObject.CreateInstance<VRCExpressionsMenu>());
                menu.controls.Add(new VRCExpressionsMenu.Control { parameter = new VRCExpressionsMenu.Control.Parameter { name = name } });
                descriptor.expressionsMenu = menu;
            }
            if (kind == "Event") AnimationUtility.SetAnimationEvents(clip, new[] { new AnimationEvent { functionName = "UnknownObserver" } });
            if (kind == "ChildAnimator")
            {
                descriptor.baseAnimationLayers = Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>();
                PolicyChild("Child Animator").AddComponent<Animator>().runtimeAnimatorController = controller;
                controller.AddParameter(name, AnimatorControllerParameterType.Float);
            }
            if (kind == "UnknownBehaviour") state.behaviours = new[] { Track(ScriptableObject.CreateInstance<UnsupportedReachabilityBehaviour>()) };
            var usage = new ParameterPaddingUsage(root);
            Assert.That(usage.IsUnobserved(name), Is.False);
            if (kind != "Event" && kind != "UnknownBehaviour") Assert.That(usage.IsUnobserved("Unused"), Is.True);
        }
    }
}
