using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using nadena.dev.ndmf;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.ParameterLayout;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using VRC.Core;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using Parameter = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters.Parameter;
using ValueType = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters.ValueType;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private const string LayoutId = "avtr_15c8b354-a30a-40f2-97c9-3b2a6a09c111";
        private const string OtherLayoutId = "avtr_15c8b354-a30a-40f2-97c9-3b2a6a09c222";
        private static Parameter LayoutParameter(string name, ValueType type = ValueType.Float, bool synced = true) => new()
        { name = name, valueType = type, networkSynced = synced, defaultValue = 0.375f, saved = true };
        private static ParameterLayoutRecord LayoutRecord(params Parameter[] parameters) => new()
        { BlueprintId = LayoutId, ReferencePlatform = ParameterLayoutPlatform.PC, Parameters = ParameterLayoutModel.Capture(parameters) };

        private sealed class LayoutTestStore : IDisposable
        {
            internal readonly ParameterLayoutStore Store;
            private readonly string path;
            internal LayoutTestStore()
            {
                path = Path.GetFullPath(Path.Combine("Temp", "AdvancedParameterLayoutTests", Guid.NewGuid().ToString("N")));
                Store = new ParameterLayoutStore(path);
            }
            public void Dispose()
            {
                var allowed = Path.GetFullPath(Path.Combine("Temp", "AdvancedParameterLayoutTests")) + Path.DirectorySeparatorChar;
                if (!path.StartsWith(allowed, StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected fixture directory.");
                if (Directory.Exists(path)) Directory.Delete(path, true);
            }
        }

        [TestCase(0)] [TestCase(1)] [TestCase(2)] [TestCase(3)]
        [TestCase(4)] [TestCase(5)] [TestCase(6)] [TestCase(7)]
        public void LayoutPreservesNetworkSlotsAcrossDeletionsAndReordering(int mask)
        {
            var original = new[] { LayoutParameter("Face", ValueType.Float), LayoutParameter("Clothes", ValueType.Int), LayoutParameter("Light", ValueType.Bool) };
            var record = LayoutRecord(original);
            var retained = original.Where((_, index) => (mask & (1 << index)) != 0).ToArray();
            foreach (var mobile in new[] { retained, retained.Reverse().ToArray() })
            {
                var input = mobile.Concat(new[] { LayoutParameter("Local", synced: false) }).ToArray();
                var before = input.Select(p => (p.name, p.valueType, p.networkSynced, p.saved, p.defaultValue)).ToArray();
                var output = ParameterLayoutModel.Align(record, input, AdditionalSyncedParameterPolicy.Error);
                var network = output.Parameters.Where(p => p.networkSynced).ToArray();
                Assert.That(network.Select(p => p.valueType), Is.EqualTo(original.Select(p => p.valueType)));
                Assert.That(network.Sum(p => VRCExpressionParameters.TypeCost(p.valueType)), Is.EqualTo(17));
                for (int slot = 0; slot < original.Length; slot++)
                {
                    bool exists = (mask & (1 << slot)) != 0;
                    Assert.That(network[slot].name == original[slot].name, Is.EqualTo(exists));
                    Assert.That(network[slot].saved, Is.EqualTo(exists));
                    Assert.That(network[slot].defaultValue, Is.EqualTo(exists ? 0.375f : 0));
                }
                Assert.That(output.Padding, Is.EqualTo(3 - retained.Length));
                Assert.That(output.Parameters.Last().name, Is.EqualTo("Local"));
                Assert.That(input.Select(p => (p.name, p.valueType, p.networkSynced, p.saved, p.defaultValue)), Is.EqualTo(before));
                Assert.That(output.Parameters.Intersect(input), Is.Empty, "Output declarations must not alias source declarations");
            }
        }

        [Test]
        public void LayoutRandomizedSubsetsKeepEverySurvivingInputAtItsOriginalSlot()
        {
            var random = new System.Random(1937);
            for (int iteration = 0; iteration < 100; iteration++)
            {
                var pc = Enumerable.Range(0, random.Next(1, 25)).Select(i => LayoutParameter("P" + i, (ValueType)random.Next(3))).ToArray();
                var mobile = pc.Where(_ => random.Next(2) != 0).OrderBy(_ => random.Next()).ToArray();
                var output = ParameterLayoutModel.Align(LayoutRecord(pc), mobile, AdditionalSyncedParameterPolicy.Error).Parameters;
                Assert.That(output.Length, Is.EqualTo(pc.Length));
                foreach (var parameter in mobile)
                    Assert.That(Array.FindIndex(output, p => p.name == parameter.name), Is.EqualTo(Array.IndexOf(pc, parameter)));
                Assert.That(output.Select(p => p.valueType), Is.EqualTo(pc.Select(p => p.valueType)));
            }
        }

        [TestCase(AdditionalSyncedParameterPolicy.Error)] [TestCase(AdditionalSyncedParameterPolicy.KeepLocal)]
        public void NewMobileParametersRequireAnExplicitPolicy(AdditionalSyncedParameterPolicy policy)
        {
            var input = new[] { LayoutParameter("Extra"), LayoutParameter("Face") }; var record = LayoutRecord(input[1]);
            if (policy == AdditionalSyncedParameterPolicy.Error)
                Assert.Throws<InvalidOperationException>(() => ParameterLayoutModel.Align(record, input, policy));
            else
            {
                var result = ParameterLayoutModel.Align(record, input, policy);
                Assert.That(result.MadeLocal, Is.EqualTo(new[] { "Extra" }));
                Assert.That(result.Parameters.Select(p => (p.name, p.networkSynced)), Is.EqualTo(new[] { ("Face", true), ("Extra", false) }));
            }
            Assert.That(input[0].networkSynced, Is.True);
        }

        [Test]
        public void PaddingDoesNotReactivateLocalInputsOrCollideWithOtherConsumers()
        {
            var record = LayoutRecord(LayoutParameter("Face"));
            var result = ParameterLayoutModel.Align(record, new[] { LayoutParameter("Face", synced: false),
                LayoutParameter("__AdvancedParameterPadding_0", synced: false) }, AdditionalSyncedParameterPolicy.Error,
                new[] { "__AdvancedParameterPadding_0_1", "__AdvancedParameterPadding_0_2" });
            Assert.That(result.Parameters[0].name, Is.EqualTo("__AdvancedParameterPadding_0_3"));
            Assert.That(result.Parameters.Single(p => p.name == "Face").networkSynced, Is.False);
        }

        [Test]
        public void EmptyReferenceLayoutRemainsAnExplicitEmptyContract()
        {
            var record = LayoutRecord();
            Assert.That(ParameterLayoutModel.Align(record, null, AdditionalSyncedParameterPolicy.Error).Parameters, Is.Empty);
            Assert.Throws<InvalidOperationException>(() => ParameterLayoutModel.Align(record, new[] { LayoutParameter("New") }, AdditionalSyncedParameterPolicy.Error));
            Assert.That(ParameterLayoutModel.Align(record, new[] { LayoutParameter("New") }, AdditionalSyncedParameterPolicy.KeepLocal).Parameters[0].networkSynced, Is.False);
        }

        [TestCase("type")] [TestCase("duplicate")] [TestCase("invalidType")] [TestCase("nonfinite")]
        public void InvalidMobileDeclarationsAreRejectedBeforeWritingAnything(string invalid)
        {
            var record = LayoutRecord(LayoutParameter("Face")); var input = new[] { LayoutParameter("Face") };
            if (invalid == "type") input[0].valueType = ValueType.Bool;
            if (invalid == "duplicate") input = new[] { LayoutParameter("Face"), LayoutParameter("Face", synced: false) };
            if (invalid == "invalidType") input[0].valueType = (ValueType)88;
            if (invalid == "nonfinite") input[0].defaultValue = float.NaN;
            Assert.Throws<InvalidOperationException>(() => ParameterLayoutModel.Align(record, input, AdditionalSyncedParameterPolicy.Error));
            Assert.That(input[0].name, Is.EqualTo("Face"));
        }

        [TestCase("version")] [TestCase("duplicate")] [TestCase("null")] [TestCase("platform")]
        public void InvalidStoredContractsCannotBeApplied(string invalid)
        {
            var record = LayoutRecord(LayoutParameter("Face"));
            if (invalid == "version") record.Version++;
            if (invalid == "duplicate") record.Parameters.Add(record.Parameters[0]);
            if (invalid == "null") record.Parameters = null;
            if (invalid == "platform") record.ReferencePlatform = (ParameterLayoutPlatform)99;
            Assert.Throws<InvalidOperationException>(() => ParameterLayoutModel.Align(record, null, AdditionalSyncedParameterPolicy.Error));
        }

        [TestCase("added")] [TestCase("removed")] [TestCase("renamed")] [TestCase("type")]
        public void LateTransportDeclarationChangesAreRejectedWithoutACompressorDependency(string change)
        {
            var before = new[] { LayoutParameter("Face"), LayoutParameter("Toggle", ValueType.Bool) };
            var after = before.Select(ParameterLayoutModel.Copy).ToList();
            if (change == "added") after.Add(LayoutParameter("Transport"));
            if (change == "removed") after.RemoveAt(0);
            if (change == "renamed") after[0].name = "Transport";
            if (change == "type") after[0].valueType = ValueType.Int;
            Assert.Throws<InvalidOperationException>(() => ParameterLayoutModel.ValidateLateDeclarations(ParameterLayoutModel.Capture(before), after.ToArray()));
        }

        [Test]
        public void LateReorderingAndLocalOnlyChangesDoNotPretendToBeCompression()
        {
            var input = new[] { LayoutParameter("Face"), LayoutParameter("Toggle", ValueType.Bool) };
            var after = input.Reverse().Concat(new[] { LayoutParameter("Local", synced: false) }).ToArray();
            Assert.DoesNotThrow(() => ParameterLayoutModel.ValidateLateDeclarations(ParameterLayoutModel.Capture(input), after));
        }

        [TestCase("analysis")] [TestCase("failedBuild")] [TestCase("cancelledUpload")] [TestCase("wrongPlatform")]
        public void FailedOrNonUploadRunsNeverReplaceAConfirmedRecord(string mode)
        {
            using var fixture = new LayoutTestStore(); var store = fixture.Store;
            store.Commit(LayoutRecord(LayoutParameter("Old"))); string before = File.ReadAllText(store.PathFor(LayoutId));
            var transaction = new ParameterLayoutUploadTransaction();
            transaction.Begin(LayoutId, mode == "wrongPlatform" ? ParameterLayoutPlatform.Android : ParameterLayoutPlatform.PC, mode != "analysis");
            transaction.Stage(LayoutRecord(LayoutParameter("New")));
            if (mode != "failedBuild") transaction.BuildSucceeded();
            if (mode == "cancelledUpload") transaction.Cancel();
            Assert.That(transaction.Confirm(LayoutId, store), Is.EqualTo(ParameterLayoutConfirmation.None));
            Assert.That(File.ReadAllText(store.PathFor(LayoutId)), Is.EqualTo(before));
        }

        [TestCase(false)] [TestCase(true)]
        public void OnlyMatchingSuccessfulUploadsCommitIndependentSnapshots(bool firstUpload)
        {
            using var fixture = new LayoutTestStore(); var transaction = new ParameterLayoutUploadTransaction();
            transaction.Begin(firstUpload ? null : LayoutId, ParameterLayoutPlatform.PC, true);
            var candidate = LayoutRecord(LayoutParameter("Face")); if (firstUpload) candidate.BlueprintId = null;
            transaction.Stage(candidate); candidate.Parameters[0].Name = "MutatedAfterCapture";
            transaction.BuildSucceeded(); Assert.That(transaction.Confirm(LayoutId, fixture.Store), Is.EqualTo(ParameterLayoutConfirmation.Recorded));
            var record = fixture.Store.Read(LayoutId);
            Assert.That(record.Parameters[0].Name, Is.EqualTo("Face")); Assert.That(record.UploadedUtc, Is.Not.Empty);
            Assert.That(transaction.Confirm(LayoutId, fixture.Store), Is.EqualTo(ParameterLayoutConfirmation.None), "A duplicate upload event cannot commit twice");
        }

        [Test]
        public void WrongBlueprintConfirmationAndRepeatedBuildsDoNotLeakCandidates()
        {
            using var fixture = new LayoutTestStore(); var transaction = new ParameterLayoutUploadTransaction();
            transaction.Begin(LayoutId, ParameterLayoutPlatform.PC, true); transaction.Stage(LayoutRecord(LayoutParameter("Face"))); transaction.BuildSucceeded();
            Assert.Throws<InvalidOperationException>(() => transaction.Confirm(OtherLayoutId, fixture.Store));
            Assert.That(fixture.Store.Read(LayoutId), Is.Null); Assert.That(fixture.Store.Read(OtherLayoutId), Is.Null);
            transaction.Begin(LayoutId, ParameterLayoutPlatform.PC, true); transaction.Stage(LayoutRecord(LayoutParameter("Old")));
            transaction.Begin(OtherLayoutId, ParameterLayoutPlatform.PC, true); transaction.BuildSucceeded();
            Assert.That(transaction.Confirm(OtherLayoutId, fixture.Store), Is.EqualTo(ParameterLayoutConfirmation.None));
        }

        [TestCase(ParameterLayoutPlatform.PC, ParameterLayoutPlatform.PC)]
        [TestCase(ParameterLayoutPlatform.PC, ParameterLayoutPlatform.Android)]
        [TestCase(ParameterLayoutPlatform.PC, ParameterLayoutPlatform.IOS)]
        [TestCase(ParameterLayoutPlatform.Android, ParameterLayoutPlatform.PC)]
        [TestCase(ParameterLayoutPlatform.Android, ParameterLayoutPlatform.Android)]
        [TestCase(ParameterLayoutPlatform.Android, ParameterLayoutPlatform.IOS)]
        [TestCase(ParameterLayoutPlatform.IOS, ParameterLayoutPlatform.PC)]
        [TestCase(ParameterLayoutPlatform.IOS, ParameterLayoutPlatform.Android)]
        [TestCase(ParameterLayoutPlatform.IOS, ParameterLayoutPlatform.IOS)]
        public void UploadWithoutLayoutInvalidatesOnlyTheRecordedReferencePlatform(ParameterLayoutPlatform reference, ParameterLayoutPlatform uploaded)
        {
            using var fixture = new LayoutTestStore(); var record = LayoutRecord(LayoutParameter("Old"));
            record.ReferencePlatform = reference; fixture.Store.Commit(record);
            string before = File.ReadAllText(fixture.Store.PathFor(LayoutId));
            var transaction = new ParameterLayoutUploadTransaction();
            transaction.Begin(LayoutId, uploaded, true); transaction.BuildSucceeded();
            Assert.That(transaction.Confirm(LayoutId, fixture.Store), Is.EqualTo(reference == uploaded
                ? ParameterLayoutConfirmation.Invalidated : ParameterLayoutConfirmation.None));
            if (reference != uploaded)
                Assert.That(File.ReadAllText(fixture.Store.PathFor(LayoutId)), Is.EqualTo(before));
            else
            {
                Assert.Throws<InvalidOperationException>(() => fixture.Store.Read(LayoutId));
                var tombstone = JsonUtility.FromJson<ParameterLayoutRecord>(File.ReadAllText(fixture.Store.PathFor(LayoutId)));
                Assert.That(tombstone.Parameters.Single().Name, Is.EqualTo("Old"));
                Assert.That(tombstone.InvalidatedUtc, Is.Not.Empty);
                Assert.Throws<InvalidOperationException>(() => ParameterLayoutModel.Align(tombstone, null, AdditionalSyncedParameterPolicy.Error));
                string invalidated = File.ReadAllText(fixture.Store.PathFor(LayoutId));
                transaction.Begin(LayoutId, uploaded, true); transaction.BuildSucceeded();
                Assert.That(transaction.Confirm(LayoutId, fixture.Store), Is.EqualTo(ParameterLayoutConfirmation.None));
                Assert.That(File.ReadAllText(fixture.Store.PathFor(LayoutId)), Is.EqualTo(invalidated));
            }
        }

        [TestCase("analysis")] [TestCase("failedBuild")] [TestCase("cancelledUpload")]
        public void FailedUploadWithoutComponentDoesNotInvalidateTheServerLayout(string mode)
        {
            using var fixture = new LayoutTestStore(); fixture.Store.Commit(LayoutRecord(LayoutParameter("Old")));
            string before = File.ReadAllText(fixture.Store.PathFor(LayoutId));
            var transaction = new ParameterLayoutUploadTransaction(); transaction.Begin(LayoutId, ParameterLayoutPlatform.PC, mode != "analysis");
            if (mode != "failedBuild") transaction.BuildSucceeded();
            if (mode == "cancelledUpload") transaction.Cancel();
            Assert.That(transaction.Confirm(LayoutId, fixture.Store), Is.EqualTo(ParameterLayoutConfirmation.None));
            Assert.That(File.ReadAllText(fixture.Store.PathFor(LayoutId)), Is.EqualTo(before));
        }

        [Test]
        public void InvalidatedRecordRejectsMobileBuildsUntilAnotherReferenceUploadIsConfirmed()
        {
            var descriptor = LayoutDescriptor();
            using var fixture = new LayoutTestStore(); fixture.Store.Commit(LayoutRecord(LayoutParameter("Old")));
            Assert.That(fixture.Store.InvalidateReference(LayoutId, ParameterLayoutPlatform.PC), Is.True);
            Assert.Throws<InvalidOperationException>(() => ParameterLayoutPass.ApplyFinal(root, new ParameterLayoutSettings(),
                ParameterLayoutPlatform.Android, new OptimizationBuildReport(), _ => Assert.Fail("Invalid records cannot write assets"), fixture.Store));
            Assert.That(descriptor.expressionParameters, Is.Null);
            var transaction = new ParameterLayoutUploadTransaction(); transaction.Begin(LayoutId, ParameterLayoutPlatform.PC, true);
            transaction.Stage(LayoutRecord(LayoutParameter("New"))); transaction.BuildSucceeded();
            Assert.That(transaction.Confirm(LayoutId, fixture.Store), Is.EqualTo(ParameterLayoutConfirmation.Recorded));
            Assert.That(fixture.Store.Read(LayoutId).Parameters.Single().Name, Is.EqualTo("New"));
        }

        [Test]
        public void UploadsWithoutAnyRecordedLayoutDoNotCreateUnrequestedFiles()
        {
            using var fixture = new LayoutTestStore(); var transaction = new ParameterLayoutUploadTransaction();
            transaction.Begin(null, ParameterLayoutPlatform.PC, true); transaction.BuildSucceeded();
            Assert.That(transaction.Confirm(LayoutId, fixture.Store), Is.EqualTo(ParameterLayoutConfirmation.None));
            Assert.That(File.Exists(fixture.Store.PathFor(LayoutId)), Is.False);
            fixture.Store.Commit(LayoutRecord(LayoutParameter("Old")));
            transaction.Begin(OtherLayoutId, ParameterLayoutPlatform.PC, true); transaction.BuildSucceeded();
            Assert.Throws<InvalidOperationException>(() => transaction.Confirm(LayoutId, fixture.Store));
            Assert.That(fixture.Store.Read(LayoutId).Parameters.Single().Name, Is.EqualTo("Old"));
        }

        [TestCase("../escape")] [TestCase("")] [TestCase(null)]
        public void LayoutStoreRejectsInvalidIdentifiers(string id)
        {
            using var fixture = new LayoutTestStore();
            Assert.Throws<InvalidOperationException>(() => fixture.Store.PathFor(id));
        }

        [Test]
        public void StoredContractsRejectCorruptionAndForeignBlueprints()
        {
            using var fixture = new LayoutTestStore(); var store = fixture.Store;
            store.Commit(LayoutRecord()); var path = store.PathFor(LayoutId);
            File.WriteAllText(path, "{broken"); Assert.Throws<InvalidOperationException>(() => store.Read(LayoutId));
            var other = LayoutRecord(); other.BlueprintId = OtherLayoutId;
            File.WriteAllText(path, JsonUtility.ToJson(other)); Assert.Throws<InvalidOperationException>(() => store.Read(LayoutId));
        }

        private VRCAvatarDescriptor LayoutDescriptor()
        {
            var descriptor = root.AddComponent<VRCAvatarDescriptor>(); descriptor.customExpressions = true;
            descriptor.baseAnimationLayers = Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>();
            descriptor.specialAnimationLayers = Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>();
            root.AddComponent<PipelineManager>().blueprintId = LayoutId;
            return descriptor;
        }

        [Test]
        public void SdkLayoutFinalizationCopiesAssetsAndNeverCreatesARecordDuringAnalysis()
        {
            var descriptor = LayoutDescriptor(); var source = Track(ScriptableObject.CreateInstance<VRCExpressionParameters>());
            source.parameters = new[] { LayoutParameter("Face"), LayoutParameter("Local", synced: false) }; descriptor.expressionParameters = source;
            string before = EditorJsonUtility.ToJson(source);
            using var fixture = new LayoutTestStore(); fixture.Store.Commit(LayoutRecord(LayoutParameter("Removed", ValueType.Bool), LayoutParameter("Face")));
            string recorded = File.ReadAllText(fixture.Store.PathFor(LayoutId));
            var context = new BuildContext(root, null); var settings = new ParameterLayoutSettings { ReferencePlatform = ParameterLayoutPlatform.PC };
            ParameterLayoutSdkSession.Begin(root);
            Assert.That(ParameterLayoutSdkSession.Defer(context, settings, ParameterLayoutPlatform.Android, new OptimizationBuildReport(), fixture.Store), Is.True);
            ParameterLayoutSdkSession.Complete(root, value => Track(value), fixture.Store);
            Assert.That(descriptor.expressionParameters, Is.Not.SameAs(source));
            Assert.That(descriptor.expressionParameters.parameters[1].name, Is.EqualTo("Face"));
            Assert.That(EditorJsonUtility.ToJson(source), Is.EqualTo(before));
            Assert.That(File.ReadAllText(fixture.Store.PathFor(LayoutId)), Is.EqualTo(recorded));
            Assert.DoesNotThrow(() => ParameterLayoutSdkSession.Complete(root, value => Track(value), fixture.Store));
            var candidate = ParameterLayoutPass.ApplyFinal(root, settings, ParameterLayoutPlatform.PC,
                new OptimizationBuildReport(), value => Track(value), fixture.Store);
            Assert.That(candidate, Is.Not.Null); Assert.That(File.ReadAllText(fixture.Store.PathFor(LayoutId)), Is.EqualTo(recorded));
        }

        [Test]
        public void SdkLayoutRejectsLateCompressionBeforeMutatingAssets()
        {
            var descriptor = LayoutDescriptor(); var source = Track(ScriptableObject.CreateInstance<VRCExpressionParameters>());
            source.parameters = new[] { LayoutParameter("Face") }; descriptor.expressionParameters = source;
            var context = new BuildContext(root, null); ParameterLayoutSdkSession.Begin(root);
            ParameterLayoutSdkSession.Defer(context, new ParameterLayoutSettings(), ParameterLayoutPlatform.PC, new OptimizationBuildReport());
            source.parameters = new[] { LayoutParameter("PackedChannel", ValueType.Int) }; string before = EditorJsonUtility.ToJson(source);
            Assert.Throws<InvalidOperationException>(() => ParameterLayoutSdkSession.Complete(root, value => Track(value)));
            Assert.That(descriptor.expressionParameters, Is.SameAs(source)); Assert.That(EditorJsonUtility.ToJson(source), Is.EqualTo(before));
        }

        [TestCase(false)] [TestCase(true)]
        public void MissingOrWrongPlatformRecordsDoNotGuessAMobileLayout(bool wrongPlatform)
        {
            LayoutDescriptor(); using var fixture = new LayoutTestStore();
            if (wrongPlatform) { var record = LayoutRecord(); record.ReferencePlatform = ParameterLayoutPlatform.IOS; fixture.Store.Commit(record); }
            Assert.Throws<InvalidOperationException>(() => ParameterLayoutPass.ApplyFinal(root, new ParameterLayoutSettings(), ParameterLayoutPlatform.Android,
                new OptimizationBuildReport(), value => Track(value), fixture.Store));
        }
    }
}
