using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.ParameterLayout;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDKBase;
using ValueType = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters.ValueType;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private IntegerEncoding IntegerContract(int minimum = 0, int maximum = 7) =>
            IntegerEncoding.Create("Mode", minimum, maximum, new HashSet<string> { "Mode", "__AdvancedInt/Mode/0" });

        [TestCase(0, 1, 1)] [TestCase(0, 3, 2)] [TestCase(0, 7, 3)]
        [TestCase(10, 15, 3)] [TestCase(128, 255, 7)]
        public void IntegerCompressionUsesOnlyTheRequiredBitsAndRetainsSavedLogicalInput(int minimum, int maximum, int width)
        {
            var encoding = IntegerContract(minimum, maximum);
            var input = LayoutParameter("Mode", ValueType.Int); input.defaultValue = maximum;
            var output = encoding.Declarations(input);
            Assert.That(encoding.Bits.Count, Is.EqualTo(width));
            Assert.That(encoding.Bits[0], Is.EqualTo("__AdvancedInt/Mode/0_1"));
            Assert.That(input.networkSynced, Is.True, "Source declarations must be immutable");
            Assert.That(output.Last().saved, Is.True);
            Assert.That(output.Last().networkSynced, Is.False);
            Assert.That(output.Last().defaultValue, Is.EqualTo(maximum));
            Assert.That(output.Take(width).All(p => !p.saved && p.valueType == ValueType.Bool && p.networkSynced), Is.True);
            for (int value = -10; value <= 265; value++)
                Assert.That(encoding.Decode(encoding.Encode(value)), Is.EqualTo(Math.Max(minimum, Math.Min(maximum, value))));
        }

        [TestCase(-1, 7)] [TestCase(1, 1)] [TestCase(8, 7)] [TestCase(0, 128)] [TestCase(0, 256)]
        public void IntegerCompressionRejectsInvalidOrNonSavingDomains(int minimum, int maximum) =>
            Assert.Throws<InvalidOperationException>(() => IntegerContract(minimum, maximum));

        [TestCase("missing")] [TestCase("shared")] [TestCase("logical")]
        [TestCase("type")] [TestCase("width")] [TestCase("version")]
        public void IntegerCompressionRejectsCorruptRecordedWireContracts(string damage)
        {
            var encoding = IntegerContract();
            var input = LayoutParameter("Mode", ValueType.Int); input.defaultValue = 2;
            var record = LayoutRecord(encoding.Declarations(input)); record.IntegerEncodings.Add(encoding);
            ParameterLayoutModel.Validate(record);
            if (damage == "missing") record.Parameters.RemoveAt(0);
            if (damage == "shared") record.IntegerEncodings.Add(new IntegerEncoding
                { Name = "Other", Minimum = 0, Maximum = 7, Bits = encoding.Bits.ToList() });
            if (damage == "logical") record.Parameters.Add(new ParameterLayoutEntry { Name = "Mode", Type = ValueType.Int });
            if (damage == "type") record.Parameters[0].Type = ValueType.Int;
            if (damage == "width") encoding.Bits.RemoveAt(0);
            if (damage == "version") record.Version = 1;
            Assert.Throws<InvalidOperationException>(() => ParameterLayoutModel.Validate(record));
        }

        [TestCase(false)] [TestCase(true)]
        public void IntegerCompressionLayoutKeepsExactWireSlotsWhenAMobileFeatureDisappears(bool frozenLocal)
        {
            var encoding = IntegerContract();
            var logical = LayoutParameter("Mode", ValueType.Int); logical.defaultValue = 5;
            var record = LayoutRecord(encoding.Declarations(logical)); record.IntegerEncodings.Add(encoding);
            var mobile = frozenLocal ? new[] { ParameterLayoutModel.Copy(logical) } : Array.Empty<VRCExpressionParameters.Parameter>();
            if (mobile.Length > 0) mobile[0].networkSynced = false;
            var aligned = ParameterLayoutModel.Align(record, mobile, AdditionalSyncedParameterPolicy.Error);
            Assert.That(aligned.Padding, Is.EqualTo(3));
            Assert.That(aligned.Parameters.Where(p => p.networkSynced).All(p => p.valueType == ValueType.Bool && p.name != "Mode"), Is.True);
            if (frozenLocal) Assert.That(aligned.Parameters.Last().defaultValue, Is.EqualTo(5));
            using var fixture = new LayoutTestStore();
            fixture.Store.Commit(record);
            Assert.That(JsonUtility.ToJson(fixture.Store.Read(LayoutId).IntegerEncodings[0]), Is.EqualTo(JsonUtility.ToJson(encoding)));
        }

        [TestCase(0, 7, AnimatorControllerParameterType.Bool, false)] [TestCase(10, 15, AnimatorControllerParameterType.Bool, false)]
        [TestCase(0, 1, AnimatorControllerParameterType.Bool, false)] [TestCase(128, 255, AnimatorControllerParameterType.Bool, false)]
        [TestCase(0, 7, AnimatorControllerParameterType.Int, false)] [TestCase(0, 7, AnimatorControllerParameterType.Float, false)]
        [TestCase(0, 7, AnimatorControllerParameterType.Bool, true)] [TestCase(10, 15, AnimatorControllerParameterType.Bool, true)]
        [TestCase(0, 7, AnimatorControllerParameterType.Int, true)] [TestCase(0, 7, AnimatorControllerParameterType.Float, true)]
        public void IntegerCompressionNativeStatesEncodeRestoreAndConvergeAfterStaggeredBitDelivery(int minimum, int maximum,
            AnimatorControllerParameterType identityType, bool writeDefaults)
        {
            var encoding = IntegerContract(minimum, maximum);
            var clone = new CloneContext(VRChatPlatformAnimatorBindings.Instance);
            var controller = VirtualAnimatorController.Create(clone, "Transport fixture");
            controller.Parameters = controller.Parameters.Add("IsLocal",
                new AnimatorControllerParameter { name = "IsLocal", type = identityType, defaultBool = true, defaultInt = 7, defaultFloat = .25f });
            IntegerEncodingAnimator.Append(clone, controller, encoding, minimum);
            if (writeDefaults)
                foreach (var state in controller.AllReachableNodes().OfType<VirtualState>()) state.WriteDefaultValues = true;
            var native = CommitAnimator(controller, root);
            var states = native.layers.Single().stateMachine.states.Select(s => s.state).ToDictionary(s => Animator.StringToHash(s.name));
            var graph = PlayableGraph.Create("Native integer transport fixture");
            var localObject = PolicyChild("Local"); var remoteObject = PolicyChild("Remote");
            var localAnimator = localObject.AddComponent<Animator>(); var remoteAnimator = remoteObject.AddComponent<Animator>();
            graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            var local = AnimatorControllerPlayable.Create(graph, native);
            var remote = AnimatorControllerPlayable.Create(graph, native);
            AnimationPlayableOutput.Create(graph, "Local", localAnimator).SetSourcePlayable(local);
            AnimationPlayableOutput.Create(graph, "Remote", remoteAnimator).SetSourcePlayable(remote);
            int previousLocal = 0, previousRemote = 0;
            try
            {
                graph.Play();
                void Identity(AnimatorControllerPlayable playable, bool owner)
                {
                    if (identityType == AnimatorControllerParameterType.Bool) playable.SetBool("IsLocal", owner);
                    else if (identityType == AnimatorControllerParameterType.Int) playable.SetInteger("IsLocal", owner ? 1 : 0);
                    else playable.SetFloat("IsLocal", owner ? 1 : 0);
                }
                Identity(local, true); Identity(remote, false);
                // This executes Unity's real state transitions. Driver Set operations are
                // modeled explicitly; the test does not emulate VRChat's network scheduler.
                void Tick()
                {
                    graph.Evaluate(1f / 60);
                    Drive(local, ref previousLocal, true); Drive(remote, ref previousRemote, false);
                }
                void Drive(AnimatorControllerPlayable playable, ref int previous, bool owner)
                {
                    int state = playable.GetCurrentAnimatorStateInfo(0).shortNameHash;
                    if (previous == state) return;
                    previous = state;
                    foreach (var driver in states[state].behaviours.OfType<VRC_AvatarParameterDriver>())
                    {
                        if (driver.localOnly && !owner) continue;
                        foreach (var operation in driver.parameters)
                        {
                            Assert.That(operation.type, Is.EqualTo(VRC_AvatarParameterDriver.ChangeType.Set));
                            if (operation.name == encoding.Name) playable.SetInteger(operation.name, (int)operation.value);
                            else playable.SetBool(operation.name, operation.value != 0);
                        }
                    }
                }
                foreach (int value in Enumerable.Range(minimum, maximum - minimum + 1).Reverse().Concat(new[] { -5, 260, minimum, maximum }))
                {
                    local.SetInteger("Mode", value); Tick(); Tick();
                    Assert.That(local.GetInteger("Mode"), Is.EqualTo(value), "The codec must never clamp the local OSC/Saved input");
                    int code = encoding.Bits.Select((bit, index) => local.GetBool(bit) ? 1 << index : 0).Sum();
                    Assert.That(code, Is.EqualTo(encoding.Encode(value)));
                    foreach (var bit in encoding.Bits) { remote.SetBool(bit, local.GetBool(bit)); Tick(); Tick(); }
                    Assert.That(remote.GetInteger("Mode"), Is.EqualTo(Math.Max(minimum, Math.Min(maximum, value))));
                }
            }
            finally { graph.Destroy(); }
        }

        [TestCase(false)] [TestCase(true)]
        public void IntegerCompressionRunsAtFinalizationAndLeavesSourceAssetsUntouched(bool sdk)
        {
            var descriptor = LayoutDescriptor();
            var source = Track(ScriptableObject.CreateInstance<VRCExpressionParameters>());
            source.parameters = new[] { LayoutParameter("Mode", ValueType.Int), LayoutParameter("Unused") };
            source.parameters[0].defaultValue = 3; descriptor.expressionParameters = source;
            string before = EditorJsonUtility.ToJson(source);
            var layout = root.AddComponent<ALTOParameterLayout>();
            var marker = root.AddComponent<ALTOParameterCompression>();
            marker.Integers.Add(new CompressedIntegerParameter { Name = "Mode", Maximum = 7 });
            var context = new BuildContext(root, null); ParameterLayoutPass.Capture(context);
            var settings = context.GetState<ParameterLayoutState>().Settings;
            Assert.That(marker == null && layout == null, Is.True);
            using var fixture = new LayoutTestStore();
            if (sdk)
            {
                ParameterLayoutSdkSession.Begin(root);
                ParameterLayoutSdkSession.Defer(context, settings, ParameterLayoutPlatform.PC, new OptimizationBuildReport());
                ParameterLayoutSdkSession.Complete(root, value => Track(value), fixture.Store);
            }
            else ParameterLayoutPass.ApplyFinal(root, settings, ParameterLayoutPlatform.PC,
                new OptimizationBuildReport(), value => Track(value), fixture.Store, context);
            Assert.That(descriptor.expressionParameters.CalcTotalCost(), Is.EqualTo(3));
            Assert.That(descriptor.expressionParameters.parameters.Single(p => p.name == "Mode").saved, Is.True);
            Assert.That(descriptor.expressionParameters.parameters.Single(p => p.name == "Unused").networkSynced, Is.False);
            Assert.That(EditorJsonUtility.ToJson(source), Is.EqualTo(before));
            Assert.That(fixture.Store.Read(LayoutId), Is.Null, "Analysis must not confirm an upload contract");
            var fx = descriptor.baseAnimationLayers.Single(l => l.type == VRCAvatarDescriptor.AnimLayerType.FX).animatorController as AnimatorController;
            Assert.That(fx, Is.Not.Null); TrackControllerAssets(fx);
        }

        [TestCase(false)] [TestCase(true)]
        public void IntegerCompressionMobileUsesRecordedEncodingAndBuildsOnlyNeededDecoders(bool frozen)
        {
            var descriptor = LayoutDescriptor();
            var encoding = IntegerContract(10, 15);
            var logical = LayoutParameter("Mode", ValueType.Int); logical.defaultValue = 11;
            var record = LayoutRecord(encoding.Declarations(logical)); record.IntegerEncodings.Add(encoding);
            var source = Track(ScriptableObject.CreateInstance<VRCExpressionParameters>());
            source.parameters = new[] { ParameterLayoutModel.Copy(logical) }; source.parameters[0].networkSynced = !frozen;
            source.parameters[0].defaultValue = 14; descriptor.expressionParameters = source;
            string original = EditorJsonUtility.ToJson(source);
            var settings = new ParameterLayoutSettings { Compression = new ParameterCompressionSettings { RemoveUnused = false } };
            using var fixture = new LayoutTestStore(); fixture.Store.Commit(record);
            string confirmed = JsonUtility.ToJson(fixture.Store.Read(LayoutId));
            var context = new BuildContext(root, null);
            ParameterLayoutPass.ApplyFinal(root, settings, ParameterLayoutPlatform.Android, new OptimizationBuildReport(),
                value => Track(value), fixture.Store, context);
            var parameters = descriptor.expressionParameters.parameters;
            Assert.That(parameters.Where(p => p.networkSynced).Select(p => p.valueType), Is.EqualTo(record.Parameters.Select(p => p.Type)));
            Assert.That(parameters.Single(p => p.name == "Mode").defaultValue, Is.EqualTo(14));
            Assert.That(parameters.Single(p => p.name == "Mode").networkSynced, Is.False);
            if (!frozen)
            {
                Assert.That(parameters.Take(3).Select(p => p.name), Is.EqualTo(encoding.Bits));
                Assert.That(parameters.Take(3).Select(p => p.defaultValue), Is.EqualTo(new[] { 0f, 0f, 1f }));
                var fx = (AnimatorController)descriptor.baseAnimationLayers.Single(l => l.type == VRCAvatarDescriptor.AnimLayerType.FX).animatorController;
                TrackControllerAssets(fx);
                Assert.That(fx.layers.Last().stateMachine.states.Any(s => s.state.name == "Decode 4"), Is.True);
            }
            else Assert.That(descriptor.baseAnimationLayers.All(l => l.animatorController == null), Is.True);
            Assert.That(EditorJsonUtility.ToJson(source), Is.EqualTo(original));
            Assert.That(JsonUtility.ToJson(fixture.Store.Read(LayoutId)), Is.EqualTo(confirmed));
        }

        [TestCase("preserved")] [TestCase("type")] [TestCase("default")] [TestCase("wire collision")] [TestCase("identity")]
        public void IntegerCompressionFailsBeforeMutatingControllersOnConflictingInputs(string conflict)
        {
            var descriptor = LayoutDescriptor();
            var source = Track(ScriptableObject.CreateInstance<VRCExpressionParameters>());
            var logical = LayoutParameter("Mode", ValueType.Int); logical.defaultValue = conflict == "default" ? 8 : 2;
            if (conflict == "type") logical.valueType = ValueType.Float;
            var encoding = IntegerContract();
            source.parameters = conflict == "wire collision" ? new[] { logical, LayoutParameter(encoding.Bits[0]) } : new[] { logical };
            descriptor.expressionParameters = source;
            var controller = Controller(Clip()); controller.AddParameter("Mode", AnimatorControllerParameterType.Int);
            if (conflict == "identity") controller.AddParameter("IsLocal", AnimatorControllerParameterType.Trigger);
            descriptor.baseAnimationLayers = new[] { new VRCAvatarDescriptor.CustomAnimLayer
                { type = VRCAvatarDescriptor.AnimLayerType.FX, isDefault = false, animatorController = controller } };
            string original = EditorJsonUtility.ToJson(source), originalController = EditorJsonUtility.ToJson(controller);
            var settings = new ParameterCompressionSettings();
            settings.Integers.Add(new CompressedIntegerParameter { Name = "Mode", Maximum = 7 });
            if (conflict == "preserved") settings.KeepNative.Add("Mode");
            var record = conflict == "wire collision" ? new ParameterLayoutRecord { IntegerEncodings = new() { encoding } } : null;
            var context = new BuildContext(root, null);
            Assert.Throws<InvalidOperationException>(() => ParameterCompressionPass.Apply(context, settings, record,
                new OptimizationBuildReport(), _ => Assert.Fail("No asset may be saved before validation succeeds")));
            Assert.That(descriptor.expressionParameters, Is.SameAs(source));
            Assert.That(EditorJsonUtility.ToJson(source), Is.EqualTo(original));
            Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(originalController));
        }

        [TestCase(false)] [TestCase(true)]
        public void IntegerCompressionUnusedSynchronizationFollowsComputedOutputsAndPreservation(bool preserveDead)
        {
            var descriptor = LayoutDescriptor(); Mesh("Face");
            var source = TreeController(("Output", StaticCurve(Binding("Face"), 100)));
            source.AddParameter("Input", AnimatorControllerParameterType.Float);
            source.AddParameter("DeadInput", AnimatorControllerParameterType.Float);
            source.AddParameter("DeadOutput", AnimatorControllerParameterType.Float);
            var driver = Track(source.layers[0].stateMachine.defaultState.AddStateMachineBehaviour<VRCAvatarParameterDriver>());
            driver.parameters = new() { CopyOperation("Output", "Input"), CopyOperation("DeadOutput", "DeadInput") };
            var parameters = Track(ScriptableObject.CreateInstance<VRCExpressionParameters>());
            parameters.parameters = new[] { LayoutParameter("Input"), LayoutParameter("DeadInput"), LayoutParameter("DeadOutput") };
            descriptor.expressionParameters = parameters;
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var entries = new Dictionary<object, VirtualAnimatorController> { [VRCAvatarDescriptor.AnimLayerType.FX] = controller };
            var preserve = preserveDead ? new HashSet<string> { "DeadOutput" } : new HashSet<string>();
            var unused = ParameterCompressionPass.FindUnused(root, entries, preserve, new OptimizationBuildReport());
            Assert.That(unused, Is.EquivalentTo(preserveDead ? Array.Empty<string>() : new[] { "DeadInput", "DeadOutput" }));
        }

        [Test]
        public void IntegerCompressionSdkPersistsControllerAndDriversAfterNdmfSaverDisposal()
        {
            string folderName = "AdvancedIntegerSdkFixture_" + Guid.NewGuid().ToString("N"), folder = "Assets/" + folderName;
            AssetDatabase.CreateFolder("Assets", folderName);
            try
            {
                var descriptor = LayoutDescriptor();
                var input = Track(ScriptableObject.CreateInstance<VRCExpressionParameters>());
                input.parameters = new[] { LayoutParameter("Mode", ValueType.Int) }; input.parameters[0].defaultValue = 5;
                descriptor.expressionParameters = input;
                root.AddComponent<ALTOParameterLayout>();
                root.AddComponent<ALTOParameterCompression>().Integers.Add(new CompressedIntegerParameter { Name = "Mode", Maximum = 7 });
                var context = new BuildContext(root, folder);
                ParameterLayoutPass.Capture(context);
                ParameterLayoutSdkSession.Begin(root);
                ParameterLayoutSdkSession.Defer(context, context.GetState<ParameterLayoutState>().Settings,
                    ParameterLayoutPlatform.PC, new OptimizationBuildReport());
                context.AssetSaver.Dispose();
                using var fixture = new LayoutTestStore();
                ParameterLayoutSdkSession.Complete(root, storeForTests: fixture.Store);
                var output = descriptor.expressionParameters;
                Assert.That(EditorUtility.IsPersistent(output), Is.True);
                Assert.That(output.CalcTotalCost(), Is.EqualTo(3));
                var controller = (AnimatorController)descriptor.baseAnimationLayers.Single(l => l.type == VRCAvatarDescriptor.AnimLayerType.FX).animatorController;
                Assert.That(EditorUtility.IsPersistent(controller), Is.True);
                var drivers = controller.layers.SelectMany(l => l.stateMachine.states).SelectMany(s => s.state.behaviours).ToArray();
                Assert.That(drivers, Has.Length.EqualTo(16));
                Assert.That(drivers.All(EditorUtility.IsPersistent), Is.True);
                Assert.That(AssetDatabase.GetAssetPath(controller), Does.StartWith(folder + "/"));
                Assert.That(fixture.Store.Read(LayoutId), Is.Null);
                ParameterLayoutSdkSession.Complete(root, storeForTests: fixture.Store);
                Assert.That(descriptor.expressionParameters, Is.SameAs(output), "A completed SDK session cannot double-encode parameters");
            }
            finally { AssetDatabase.DeleteAsset(folder); }
        }
    }
}
