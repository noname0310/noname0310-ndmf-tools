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
using Parameter = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters.Parameter;
using ValueType = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters.ValueType;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private static ResolvedFloatPrecisionRule PrecisionRule(string name, int bits, bool individual = false,
            FloatPrecisionMode mode = FloatPrecisionMode.Minimum)
        {
            var rule = new ResolvedFloatPrecisionRule { Bits = bits, Precision = mode, Individual = individual };
            rule.Names.Add(name); return rule;
        }

        private static ResolvedParameterPrecisionRule IntegerPrecisionRule(string name, int minimum, int maximum)
        {
            var rule = new ResolvedParameterPrecisionRule { Individual = true,
                Integers = new() { Range = IntegerRangeMode.LimitRange, Minimum = minimum, Maximum = maximum } };
            rule.Names.Add(name); return rule;
        }

        [Test]
        public void PrecisionUntouchedSourceTypesDoNotShadowIndependentOverrides()
        {
            var floats = new ResolvedParameterPrecisionRule { Floats = new() { Bits = 6 } };
            var integers = new ResolvedParameterPrecisionRule { Integers = new() { Range = IntegerRangeMode.LimitRange, Maximum = 7 } };
            floats.Names.UnionWith(new[] { "Float", "Int" }); integers.Names.UnionWith(floats.Names);
            var result = ParameterPrecisionRules.Resolve(new[] { floats, integers }, new[] { LayoutParameter("Float"), LayoutParameter("Int", ValueType.Int) });
            Assert.That(result.Floats.Single().Cost, Is.EqualTo(6));
            Assert.That(result.Integers.Single().Maximum, Is.EqualTo(7));
        }

        [TestCase(ValueType.Int)] [TestCase(ValueType.Float)]
        public void PrecisionIndividualOverridesIgnoreSettingsForTheOtherType(ValueType type)
        {
            var descriptor = LayoutDescriptor();
            descriptor.expressionParameters = Track(ScriptableObject.CreateInstance<VRCExpressionParameters>());
            var input = LayoutParameter("Input", type); input.defaultValue = 0;
            descriptor.expressionParameters.parameters = new[] { input };
            var component = root.AddComponent<ALTOParameterCompression>(); component.RemoveUnusedSynchronization = false;
            var rule = new ParameterPrecisionOverride { Name = "Input", Floats = new() { Bits = type == ValueType.Float ? 6 : -1 },
                Integers = new() { Range = IntegerRangeMode.LimitRange, Minimum = 0, Maximum = type == ValueType.Int ? 7 : -1 } };
            component.PrecisionOverrides.Add(rule);
            var context = new BuildContext(root, null); var settings = ParameterCompressionPass.Capture(context, true);
            var result = ParameterCompressionPass.Apply(context, settings, null, new OptimizationBuildReport(), o => Track(o));
            Assert.That(result.Integers.Count, Is.EqualTo(type == ValueType.Int ? 1 : 0));
            Assert.That(result.FloatPrecisions.Count, Is.EqualTo(type == ValueType.Float ? 1 : 0));
        }

        [Test]
        public void PrecisionUnifiedSourceRulesDispatchByTypeAndIndividualRulesOverrideThem()
        {
            var descriptor = LayoutDescriptor();
            var parameters = Track(ScriptableObject.CreateInstance<VRCExpressionParameters>());
            parameters.parameters = new[] { LayoutParameter("Float"), LayoutParameter("Int", ValueType.Int), LayoutParameter("Bool", ValueType.Bool) };
            parameters.parameters[1].defaultValue = 1;
            descriptor.expressionParameters = parameters;
            var component = root.AddComponent<ALTOParameterCompression>(); component.RemoveUnusedSynchronization = false;
            var source = new ParameterPrecisionSourceOverride { Floats = new() { Bits = 5 },
                Integers = new() { Range = IntegerRangeMode.LimitRange, Minimum = 0, Maximum = 7 } };
            source.Parameters.Sources.Add(new AnimatorParameterSource { Source = parameters });
            component.PrecisionSourceOverrides.Add(source);
            component.PrecisionOverrides.Add(new() { Name = "Int", Integers = new() { Range = IntegerRangeMode.LimitRange, Minimum = 0, Maximum = 1 } });
            var context = new BuildContext(root, null);
            var settings = ParameterCompressionPass.Capture(context, true);
            var result = ParameterCompressionPass.Apply(context, settings, null, new OptimizationBuildReport(), o => Track(o));
            Assert.That(result.FloatPrecisions.Single().Cost, Is.EqualTo(5));
            Assert.That(result.Integers.Single().Bits.Count, Is.EqualTo(1));
            Assert.That(result.Integers.Single().Maximum, Is.EqualTo(1));
            Assert.That(descriptor.expressionParameters.parameters.Single(p => p.name == "Bool").valueType, Is.EqualTo(ValueType.Bool));
            Assert.That(parameters.parameters.Select(p => p.GetBitWidthByType()), Is.EqualTo(new[] { 8, 8, 1 }));
        }

        [TestCase("Conflict", true)] [TestCase("Individual", false)] [TestCase("KeepExisting", false)] [TestCase("WrongType", true)]
        public void PrecisionUnifiedIntegerRulesRejectAmbiguityAndSupportIndividualExceptions(string kind, bool fails)
        {
            var first = new ResolvedParameterPrecisionRule { Integers = new() { Range = IntegerRangeMode.LimitRange, Maximum = 7 } };
            var second = new ResolvedParameterPrecisionRule { Individual = kind != "Conflict",
                Type = kind == "WrongType" ? ParameterPrecisionType.Float : ParameterPrecisionType.Auto,
                Integers = new() { Range = kind == "KeepExisting" ? IntegerRangeMode.KeepExisting : IntegerRangeMode.LimitRange, Maximum = 3 } };
            first.Names.Add("Mode"); second.Names.Add("Mode");
            var inputs = new[] { LayoutParameter("Mode", ValueType.Int) };
            if (fails) Assert.Throws<InvalidOperationException>(() => ParameterPrecisionRules.Resolve(new[] { first, second }, inputs));
            else
            {
                var result = ParameterPrecisionRules.Resolve(new[] { first, second }, inputs);
                Assert.That(result.SelectedIntegers, Does.Contain("Mode"));
                if (kind == "KeepExisting") Assert.That(result.Integers, Is.Empty);
                else Assert.That(result.Integers.Single().Maximum, Is.EqualTo(3));
            }
        }

        [Test]
        public void PrecisionInspectorInfersNumericTypesWithoutModifyingTheAvatar()
        {
            var descriptor = LayoutDescriptor();
            var parameters = Track(ScriptableObject.CreateInstance<VRCExpressionParameters>());
            parameters.parameters = new[] { LayoutParameter("Float"), LayoutParameter("Int", ValueType.Int), LayoutParameter("Bool", ValueType.Bool) };
            descriptor.expressionParameters = parameters;
            var before = root.GetComponentsInChildren<Component>(true);
            Assert.That(ParameterPrecisionPreview.Find(root, parameters, "Float"), Is.EqualTo(AnimatorControllerParameterType.Float));
            Assert.That(ParameterPrecisionPreview.Find(root, parameters, "Int"), Is.EqualTo(AnimatorControllerParameterType.Int));
            Assert.That(ParameterPrecisionPreview.Find(root, parameters, "Bool"), Is.EqualTo(AnimatorControllerParameterType.Bool));
            Assert.That(ParameterPrecisionPreview.Find(root, parameters, "GeneratedLater"), Is.Null);
            Assert.That(root.GetComponentsInChildren<Component>(true), Is.EqualTo(before));
        }

        [Test]
        public void PrecisionOverridesRespectArtistDefaultsAndIndividualSourcePrecedence()
        {
            var descriptor = LayoutDescriptor();
            var parameters = Track(ScriptableObject.CreateInstance<VRCExpressionParameters>());
            parameters.parameters = new[] { LayoutParameter("Face"), LayoutParameter("Eye"), LayoutParameter("Mode", ValueType.Int) };
            parameters.parameters[0].flippedRepresentation = true;
            parameters.parameters[1].bitWidth = 5;
            descriptor.expressionParameters = parameters;
            string before = EditorJsonUtility.ToJson(parameters);
            var marker = root.AddComponent<ALTOParameterCompression>(); marker.RemoveUnusedSynchronization = false;
            var sourceRule = new ParameterPrecisionSourceOverride { Floats = new() { Bits = 6 } };
            sourceRule.Parameters.Sources.Add(new AnimatorParameterSource { Source = parameters });
            marker.PrecisionSourceOverrides.Add(sourceRule);
            marker.PrecisionOverrides.Add(new ParameterPrecisionOverride { Source = parameters, Name = "Eye" });
            marker.InputAssumption = ParameterInputAssumption.AvatarDrivenExceptListed;
            var keep = new ParameterKeepRule(); keep.Parameters.Names.Add("Face"); marker.KeepParameters.Add(keep);
            var context = new BuildContext(root, null);
            var settings = ParameterCompressionPass.Capture(context, true);
            var result = ParameterCompressionPass.Apply(context, settings, null, new OptimizationBuildReport(), o => Track(o));
            var output = descriptor.expressionParameters.parameters;
            Assert.That(output.Select(p => p.GetBitWidthByType()), Is.EqualTo(new[] { 6, 5, 8 }));
            Assert.That(output[0].flippedRepresentation, Is.True);
            Assert.That(result.FloatPrecisions.Select(p => p.Name), Is.EquivalentTo(new[] { "Face", "Eye" }));
            Assert.That(output.All(p => p.networkSynced && p.saved), Is.True);
            Assert.That(result.Multiplexing, Is.Null);
            Assert.That(descriptor.baseAnimationLayers.All(l => l.animatorController == null), Is.True);
            Assert.That(EditorJsonUtility.ToJson(parameters), Is.EqualTo(before));
        }

        [Test]
        public void PrecisionOverridesDoNotApplyImplicitlyAndConflictingRulesAreRejected()
        {
            var parameters = new[] { LayoutParameter("Face"), LayoutParameter("Eye") };
            parameters[0].bitWidth = 3; parameters[0].flippedRepresentation = true;
            Assert.That(FloatPrecisionOverrides.Resolve(Array.Empty<ResolvedFloatPrecisionRule>(), parameters), Is.Empty);
            Assert.That(JsonUtility.ToJson(FloatPrecisionOverrides.Apply(parameters, Array.Empty<FloatPrecisionEncoding>())[0]),
                Is.EqualTo(JsonUtility.ToJson(parameters[0])));
            var rules = new[] { PrecisionRule("Face", 4), PrecisionRule("Face", 6) };
            Assert.Throws<InvalidOperationException>(() => FloatPrecisionOverrides.Resolve(rules, parameters));
            var resolved = FloatPrecisionOverrides.Resolve(rules.Append(PrecisionRule("Face", 7, true)), parameters);
            Assert.That(resolved.Single().Cost, Is.EqualTo(7));
            Assert.Throws<InvalidOperationException>(() => FloatPrecisionOverrides.Resolve(new[] { PrecisionRule("Missing", 7, true) }, parameters));
            var unsigned = PrecisionRule("Face", 7); unsigned.Representation = FloatPrecisionRepresentation.Unsigned;
            parameters[0].defaultValue = -.5f;
            Assert.Throws<InvalidOperationException>(() => FloatPrecisionOverrides.Apply(parameters,
                FloatPrecisionOverrides.Resolve(new[] { unsigned }, parameters)));
        }

        [TestCase(false, 47)] [TestCase(true, 40)]
        public void PrecisionMinimumSharesWiderSlotsWhileExactQuantizationStaysSeparate(bool minimum, int costAtTwoBatches)
        {
            var input = MultiplexInputs(8); input[7].bitWidth = 7;
            var flexible = minimum ? new HashSet<string> { input[7].name } : new HashSet<string>();
            var planner = new MultiplexSlotPlanner(input, flexible);
            Assert.That(planner.Cost(2) + 8, Is.EqualTo(costAtTwoBatches));
            var plan = MultiplexEncoding.Plan(input, input.Select(p => p.name).ToHashSet(), 40, .1f, new HashSet<string>(), flexible);
            Assert.That(plan.Batches, Is.EqualTo(minimum ? 2 : 3));
            foreach (var member in plan.Inputs) Assert.That(member.Accepts(plan.Slots[member.Slot]), Is.True);
            var seven = plan.Inputs.Single(p => p.Name == input[7].name);
            Assert.That(plan.Slots[seven.Slot].Cost, Is.EqualTo(minimum ? 8 : 7));
            var record = LayoutRecord(plan.WireDeclarations()); record.Multiplexing = plan;
            record.FloatPrecisionOverrides.Add(new FloatPrecisionEncoding { Name = seven.Name, Type = ValueType.Float,
                BitWidth = 7, MinimumPrecision = minimum });
            ParameterLayoutModel.Validate(JsonUtility.FromJson<ParameterLayoutRecord>(JsonUtility.ToJson(record)));
            seven.AllowWiderPrecision = false;
            if (minimum) Assert.Throws<InvalidOperationException>(() => plan.Validate());
        }

        [Test]
        public void PrecisionMinimumSplitsOnlyWhenNarrowSlotsReduceCost()
        {
            var input = MultiplexInputs(4);
            foreach (var p in input.Skip(1)) p.bitWidth = 7;
            var flexible = input.Skip(1).Select(p => p.name).ToHashSet();
            var plan = MultiplexEncoding.Plan(input, input.Select(p => p.name).ToHashSet(), 23, .1f, new HashSet<string>(), flexible);
            Assert.That(plan.Batches, Is.EqualTo(2));
            Assert.That(plan.Slots.Select(p => p.Cost), Is.EquivalentTo(new[] { 8, 7 }));
            Assert.That(plan.WireCost, Is.EqualTo(23));
            Assert.That(plan.Inputs.Count(p => p.AllowWiderPrecision && plan.Slots[p.Slot].Cost == 8), Is.EqualTo(1));
        }

        [Test]
        public void PrecisionSlotCostsMatchExhaustivePartitionsAcrossRandomInputs()
        {
            var random = new System.Random(76123);
            for (int iteration = 0; iteration < 500; iteration++)
            {
                int batches = random.Next(1, 5);
                var input = MultiplexInputs(random.Next(1, 8)); var flexible = new HashSet<string>();
                foreach (var p in input)
                {
                    p.bitWidth = (byte)random.Next(2, 9);
                    if (random.Next(4) != 0) flexible.Add(p.name);
                }
                var buckets = new List<(int Width, bool Exact, int Count)>(); int best = int.MaxValue;
                void Search(int index, int cost)
                {
                    if (cost >= best) return;
                    if (index == input.Length) { best = cost; return; }
                    int bits = input[index].bitWidth; bool exact = !flexible.Contains(input[index].name);
                    for (int i = 0; i < buckets.Count; i++)
                    {
                        var old = buckets[i]; int width = Math.Max(old.Width, bits);
                        if (old.Count == batches || old.Exact && width != old.Width || exact && width != bits) continue;
                        buckets[i] = (width, old.Exact || exact, old.Count + 1);
                        Search(index + 1, cost + width - old.Width); buckets[i] = old;
                    }
                    buckets.Add((bits, exact, 1)); Search(index + 1, cost + bits); buckets.RemoveAt(buckets.Count - 1);
                }
                Search(0, 0);
                var planner = new MultiplexSlotPlanner(input, flexible);
                Assert.That(planner.Cost(batches), Is.EqualTo(best), "Iteration " + iteration);
                var allocated = planner.Allocate(batches);
                Assert.That(allocated.Sum(s => s.Encoding.Cost), Is.EqualTo(best));
                Assert.That(allocated.SelectMany(s => s.Inputs).Select(p => p.name), Is.EquivalentTo(input.Select(p => p.name)));
                foreach (var slot in allocated)
                {
                    Assert.That(slot.Inputs.Count, Is.LessThanOrEqualTo(batches));
                    foreach (var p in slot.Inputs) Assert.That(slot.Encoding.Cost >= p.bitWidth &&
                        (flexible.Contains(p.name) || slot.Encoding.Cost == p.bitWidth), Is.True);
                }
            }
        }

        [TestCase(false)] [TestCase(true)]
        public void PrecisionReferenceOverridesAreReusedOrPaddedOnMobile(bool removed)
        {
            var descriptor = LayoutDescriptor();
            var original = LayoutParameter("Face"); original.bitWidth = 7; original.flippedRepresentation = true;
            var record = LayoutRecord(original);
            record.FloatPrecisionOverrides.Add(new FloatPrecisionEncoding { Name = "Face", Type = ValueType.Float, BitWidth = 7,
                FlippedRepresentation = true, MinimumPrecision = true });
            var source = Track(ScriptableObject.CreateInstance<VRCExpressionParameters>());
            source.parameters = removed ? Array.Empty<Parameter>() : new[] { LayoutParameter("Face") };
            descriptor.expressionParameters = source;
            var settings = new ParameterCompressionSettings { RemoveUnused = false };
            var result = ParameterCompressionPass.Apply(new BuildContext(root, null), settings, record, new OptimizationBuildReport(), o => Track(o));
            var aligned = ParameterLayoutModel.Align(record, descriptor.expressionParameters.parameters, AdditionalSyncedParameterPolicy.Error);
            var wire = aligned.Parameters.Single(p => p.networkSynced);
            Assert.That(wire.GetBitWidthByType(), Is.EqualTo(7)); Assert.That(wire.flippedRepresentation, Is.True);
            Assert.That(aligned.Padding, Is.EqualTo(removed ? 1 : 0));
            Assert.That(result.FloatPrecisions.Single().MinimumPrecision, Is.True);
            if (!removed) Assert.That(source.parameters[0].GetBitWidthByType(), Is.EqualTo(8));
        }

        [TestCase(0, 1, false, false)] [TestCase(0, 7, true, false)]
        [TestCase(10, 17, false, true)] [TestCase(0, 5, false, true)] [TestCase(-3, 1, false, true)]
        public void PrecisionIntegerCodecChoosesNativeOrSingleBoolAutomatically(int minimum, int maximum, bool direct, bool remapped)
        {
            var input = LayoutParameter("Mode", ValueType.Int); input.flippedRepresentation = minimum < 0; input.defaultValue = minimum;
            var encoding = IntegerEncoding.CreateForSdk(input, minimum, maximum, new HashSet<string>());
            Assert.That(encoding.IsNative, Is.EqualTo(direct)); Assert.That(encoding.IsRemapped, Is.EqualTo(remapped));
            Assert.That(encoding.Bits.Count, Is.EqualTo(direct || remapped ? 0 : 1));
            var record = LayoutRecord(encoding.Declarations(input)); record.IntegerEncodings.Add(encoding);
            ParameterLayoutModel.Validate(JsonUtility.FromJson<ParameterLayoutRecord>(JsonUtility.ToJson(record)));
            for (int value = minimum - 5; value < maximum + 5; value++)
                Assert.That(encoding.Decode(encoding.Encode(value)), Is.EqualTo(Math.Max(minimum, Math.Min(maximum, value))));
        }

        [TestCase(10, 17)] [TestCase(0, 5)] [TestCase(-3, 1)]
        public void PrecisionRemappedIntegersRunNativeAnimatorEncodeAndDecode(int minimum, int maximum)
        {
            var declaration = LayoutParameter("Mode", ValueType.Int); declaration.flippedRepresentation = minimum < 0; declaration.defaultValue = minimum;
            var encoding = IntegerEncoding.CreateForSdk(declaration, minimum, maximum, new HashSet<string>());
            var clone = new CloneContext(VRChatPlatformAnimatorBindings.Instance);
            var controller = VirtualAnimatorController.Create(clone, "Remapped integer fixture");
            IntegerEncodingAnimator.Append(clone, controller, encoding, minimum);
            var native = CommitAnimator(controller, root);
            var states = native.layers.Single().stateMachine.states.Select(s => s.state).ToDictionary(s => Animator.StringToHash(s.name));
            var graph = PlayableGraph.Create("Remapped integer fixture"); graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            try
            {
                var local = AnimatorControllerPlayable.Create(graph, native); var remote = AnimatorControllerPlayable.Create(graph, native);
                AnimationPlayableOutput.Create(graph, "Local", PolicyChild("Local").AddComponent<Animator>()).SetSourcePlayable(local);
                AnimationPlayableOutput.Create(graph, "Remote", PolicyChild("Remote").AddComponent<Animator>()).SetSourcePlayable(remote);
                graph.Play(); local.SetBool("IsLocal", true); remote.SetBool("IsLocal", false);
                int previousLocal = 0, previousRemote = 0;
                void Drivers(AnimatorControllerPlayable peer, ref int previous)
                {
                    int current = peer.GetCurrentAnimatorStateInfo(0).shortNameHash;
                    if (current == previous) return;
                    previous = current;
                    foreach (var driver in states[current].behaviours.OfType<VRC_AvatarParameterDriver>())
                        foreach (var op in driver.parameters) peer.SetInteger(op.name, (int)op.value);
                }
                void Tick() { graph.Evaluate(.1f); Drivers(local, ref previousLocal); Drivers(remote, ref previousRemote); }
                foreach (int value in new[] { minimum - 3, minimum, minimum + 1, maximum, maximum + 3, minimum })
                {
                    local.SetInteger("Mode", value); Tick(); Tick();
                    Assert.That(local.GetInteger("Mode"), Is.EqualTo(value));
                    Assert.That(local.GetInteger(encoding.WireName), Is.EqualTo(encoding.Encode(value)));
                    remote.SetInteger(encoding.WireName, local.GetInteger(encoding.WireName)); Tick(); Tick();
                    Assert.That(remote.GetInteger("Mode"), Is.EqualTo(Math.Max(minimum, Math.Min(maximum, value))));
                }
            }
            finally { graph.Destroy(); }
        }
    }
}
