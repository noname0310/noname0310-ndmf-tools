using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Noname.AvatarTools.ParameterLayout;
using NUnit.Framework;
using UnityEngine;
using ValueType = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters.ValueType;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed class ParameterTransactionFuzzTests
    {
        private static readonly string[] AvatarIds =
        {
            "avtr_15c8b354-a30a-40f2-97c9-3b2a6a09c111",
            "avtr_15c8b354-a30a-40f2-97c9-3b2a6a09c222",
            "avtr_15c8b354-a30a-40f2-97c9-3b2a6a09c333"
        };

        private enum EventKind { Begin, Stage, Built, Cancel, Confirm, Reload }

        private sealed class UploadEvent
        {
            internal EventKind Kind;
            internal string Id;
            internal ParameterLayoutPlatform Platform;
            internal bool Publishing, InvalidPayload;
            internal ParameterLayoutRecord Payload;
            internal Contract Accepted;
            public override string ToString() => Kind switch
            {
                EventKind.Begin => $"Begin({Id ?? "new"}, {Platform}, publish={Publishing})",
                EventKind.Stage => $"Stage({Id ?? "new"}, {Platform}, invalid={InvalidPayload})",
                EventKind.Confirm => $"Confirm({Id ?? "null"})",
                _ => Kind.ToString()
            };
        }

        private sealed class Contract
        {
            internal ParameterLayoutPlatform Platform;
            internal (string Name, ValueType Type)[] Parameters;
            internal bool Invalidated;
            internal static Contract Capture(ParameterLayoutRecord record) => new()
            {
                Platform = record.ReferencePlatform,
                Parameters = record.Parameters.Select(p => (p.Name, p.Type)).ToArray()
            };
        }

        // The oracle is a history predicate: only an uninterrupted publishing attempt
        // with a completed build and matching confirmation may alter the server contract.
        // It does not call the production transaction, validation or alignment helpers.
        private sealed class HistoryModel
        {
            internal readonly List<UploadEvent> Events = new();
            internal readonly Dictionary<string, Contract> Stored = new(StringComparer.Ordinal);

            internal UploadEvent CurrentBegin
            {
                get
                {
                    var boundary = Events.LastOrDefault(e => e.Kind is EventKind.Begin or EventKind.Cancel or EventKind.Confirm or EventKind.Reload);
                    return boundary?.Kind == EventKind.Begin ? boundary : null;
                }
            }

            internal (bool Error, ParameterLayoutConfirmation Result, string ChangedId) Observe(UploadEvent next)
            {
                var begin = CurrentBegin;
                var attempt = begin == null ? Array.Empty<UploadEvent>() : Events.Skip(Events.IndexOf(begin) + 1).ToArray();
                var payload = attempt.Select(e => e.Accepted).FirstOrDefault(c => c != null);
                bool error = false;
                var result = ParameterLayoutConfirmation.None;
                string changed = null;
                if (next.Kind == EventKind.Stage && begin?.Publishing == true && next.Platform == begin.Platform)
                {
                    error = next.Id != begin.Id || payload != null || next.InvalidPayload;
                    if (!error) next.Accepted = Contract.Capture(next.Payload);
                }
                if (next.Kind == EventKind.Confirm && begin?.Publishing == true && attempt.Any(e => e.Kind == EventKind.Built))
                {
                    error = !AvatarIds.Contains(next.Id) || !string.IsNullOrEmpty(begin.Id) && begin.Id != next.Id;
                    if (!error && payload != null)
                    {
                        Stored[next.Id] = payload;
                        changed = next.Id; result = ParameterLayoutConfirmation.Recorded;
                    }
                    else if (!error && Stored.TryGetValue(next.Id, out var old) && old.Platform == begin.Platform && !old.Invalidated)
                    {
                        old.Invalidated = true;
                        changed = next.Id; result = ParameterLayoutConfirmation.Invalidated;
                    }
                }
                Events.Add(next);
                return (error, result, changed);
            }
        }

        private sealed class TemporaryStore : IDisposable
        {
            private readonly string path = Path.GetFullPath(Path.Combine("Temp", "ALTOParameterTransactionFuzz", Guid.NewGuid().ToString("N")));
            internal readonly ParameterLayoutStore Store;
            internal TemporaryStore() => Store = new ParameterLayoutStore(path);
            public void Dispose()
            {
                string parent = Path.GetFullPath(Path.Combine("Temp", "ALTOParameterTransactionFuzz")) + Path.DirectorySeparatorChar;
                if (!path.StartsWith(parent, StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected fixture directory.");
                if (Directory.Exists(path)) Directory.Delete(path, true);
            }
        }

        private static ParameterLayoutRecord Payload(string id, ParameterLayoutPlatform platform, int serial) => new()
        {
            BlueprintId = id, ReferencePlatform = platform,
            Parameters = Enumerable.Range(0, serial % 4).Select(i => new ParameterLayoutEntry
            { Name = "Input_" + serial + "_" + i, Type = (ValueType)((serial + i) % 3) }).ToList()
        };

        [TestCase(1937)] [TestCase(71021)] [TestCase(29017)] [TestCase(86731)]
        public void UploadTransactionMatchesConfirmedUploadHistory(int seed)
        {
            using var fixture = new TemporaryStore();
            var store = fixture.Store;
            var model = new HistoryModel();
            var transaction = new ParameterLayoutUploadTransaction();
            var random = new System.Random(seed);
            int serial = 1;
            foreach (var id in AvatarIds.Take(2))
            {
                var baseline = Payload(id, id == AvatarIds[0] ? ParameterLayoutPlatform.PC : ParameterLayoutPlatform.Android, serial++);
                baseline.UploadedUtc = "2026-01-01T00:00:00.0000000Z";
                store.Commit(baseline); model.Stored.Add(id, Contract.Capture(baseline));
            }

            void Begin(string id, ParameterLayoutPlatform platform, bool publishing = true) => Run(new UploadEvent
            { Kind = EventKind.Begin, Id = id, Platform = platform, Publishing = publishing });
            void Stage(string id, ParameterLayoutPlatform platform, bool invalid = false)
            {
                var payload = Payload(id, platform, serial++);
                if (invalid) payload.Version = 99;
                Run(new UploadEvent { Kind = EventKind.Stage, Id = id, Platform = platform, InvalidPayload = invalid, Payload = payload });
            }
            void Event(EventKind kind, string id = null) => Run(new UploadEvent { Kind = kind, Id = id });

            void Run(UploadEvent operation)
            {
                var before = AvatarIds.ToDictionary(id => id, id => File.Exists(store.PathFor(id)) ? File.ReadAllBytes(store.PathFor(id)) : null);
                var expected = model.Observe(operation);
                Exception exception = null;
                var result = ParameterLayoutConfirmation.None;
                try
                {
                    switch (operation.Kind)
                    {
                        case EventKind.Begin: transaction.Begin(operation.Id, operation.Platform, operation.Publishing); break;
                        case EventKind.Stage: transaction.Stage(operation.Payload); break;
                        case EventKind.Built: transaction.BuildSucceeded(); break;
                        case EventKind.Cancel: transaction.Cancel(); break;
                        case EventKind.Confirm: result = transaction.Confirm(operation.Id, store); break;
                        case EventKind.Reload: transaction = new ParameterLayoutUploadTransaction(); break;
                    }
                }
                catch (Exception caught) { exception = caught; }

                string trace = $"Seed {seed}, event {model.Events.Count - 1}: " + string.Join(" -> ", model.Events.TakeLast(12));
                Assert.That(exception?.GetType(), Is.EqualTo(expected.Error ? typeof(InvalidOperationException) : null), trace);
                Assert.That(result, Is.EqualTo(expected.Result), trace);
                foreach (var id in AvatarIds)
                {
                    string path = store.PathFor(id);
                    if (id != expected.ChangedId)
                        Assert.That(File.Exists(path) ? File.ReadAllBytes(path) : null, Is.EqualTo(before[id]), "Unconfirmed upload changed storage. " + trace);
                    if (!model.Stored.TryGetValue(id, out var contract))
                    { Assert.That(File.Exists(path), Is.False, trace); continue; }
                    var actual = JsonUtility.FromJson<ParameterLayoutRecord>(File.ReadAllText(path));
                    Assert.That(actual.BlueprintId, Is.EqualTo(id), trace);
                    Assert.That(actual.ReferencePlatform, Is.EqualTo(contract.Platform), trace);
                    Assert.That(actual.Parameters.Select(p => (p.Name, p.Type)), Is.EqualTo(contract.Parameters), trace);
                    Assert.That(!string.IsNullOrEmpty(actual.InvalidatedUtc), Is.EqualTo(contract.Invalidated), trace);
                    Assert.That(actual.UploadedUtc, Is.Not.Null.And.Not.Empty, trace);
                    if (contract.Invalidated) Assert.Throws<InvalidOperationException>(() => store.Read(id), trace);
                    else Assert.DoesNotThrow(() => store.Read(id), trace);
                }

                // The caller may reuse or mutate its record immediately after Stage.
                // Neither a stored baseline nor a pending accepted snapshot may alias it.
                if (operation.Payload != null)
                {
                    operation.Payload.BlueprintId = "changed-after-stage";
                    operation.Payload.ReferencePlatform = ParameterLayoutPlatform.IOS;
                    if (operation.Payload.Parameters.Count > 0) operation.Payload.Parameters[0].Name = "ChangedAfterStage";
                    operation.Payload.Parameters.Clear();
                }
            }

            // Guaranteed witnesses keep all safety properties exercised for every seed.
            // The random tail then interleaves them across avatar IDs and platforms.
            Begin(null, ParameterLayoutPlatform.PC); Stage(null, ParameterLayoutPlatform.PC);
            Event(EventKind.Built); Event(EventKind.Confirm, AvatarIds[2]); Event(EventKind.Confirm, AvatarIds[2]);
            foreach (var interruption in new[] { EventKind.Cancel, EventKind.Reload })
            {
                Begin(AvatarIds[0], ParameterLayoutPlatform.PC); Stage(AvatarIds[0], ParameterLayoutPlatform.PC);
                Event(EventKind.Built); Event(interruption); Event(EventKind.Confirm, AvatarIds[0]);
            }
            Begin(AvatarIds[0], ParameterLayoutPlatform.PC, false); Stage(AvatarIds[0], ParameterLayoutPlatform.PC);
            Event(EventKind.Built); Event(EventKind.Confirm, AvatarIds[0]);
            Begin(AvatarIds[0], ParameterLayoutPlatform.PC); Stage(AvatarIds[0], ParameterLayoutPlatform.PC);
            Event(EventKind.Confirm, AvatarIds[0]);
            Begin(AvatarIds[0], ParameterLayoutPlatform.PC); Stage(AvatarIds[0], ParameterLayoutPlatform.PC, true);
            Stage(AvatarIds[1], ParameterLayoutPlatform.PC); Stage(AvatarIds[0], ParameterLayoutPlatform.Android);
            Stage(AvatarIds[0], ParameterLayoutPlatform.PC); Stage(AvatarIds[0], ParameterLayoutPlatform.PC);
            Event(EventKind.Built); Event(EventKind.Confirm, "../invalid-avatar"); Event(EventKind.Confirm, AvatarIds[0]);
            Begin(AvatarIds[0], ParameterLayoutPlatform.PC); Stage(AvatarIds[0], ParameterLayoutPlatform.PC);
            Event(EventKind.Built); Event(EventKind.Confirm, AvatarIds[1]);
            Begin(AvatarIds[0], ParameterLayoutPlatform.PC); Event(EventKind.Built); Event(EventKind.Confirm, AvatarIds[0]);
            Begin(AvatarIds[0], ParameterLayoutPlatform.Android); Event(EventKind.Built); Event(EventKind.Confirm, AvatarIds[0]);
            Begin(AvatarIds[0], ParameterLayoutPlatform.PC); Stage(AvatarIds[0], ParameterLayoutPlatform.PC);
            Event(EventKind.Built); Event(EventKind.Confirm, AvatarIds[0]);

            for (int step = 0; step < 128; step++)
            {
                var begin = model.CurrentBegin;
                switch (random.Next(12))
                {
                    case 0: case 1:
                        Begin(random.Next(5) == 0 ? null : AvatarIds[random.Next(AvatarIds.Length)],
                            (ParameterLayoutPlatform)random.Next(3), random.Next(4) != 0);
                        break;
                    case 2: case 3: case 4:
                        Stage(begin != null && random.Next(4) != 0 ? begin.Id : AvatarIds[random.Next(AvatarIds.Length)],
                            begin != null && random.Next(4) != 0 ? begin.Platform : (ParameterLayoutPlatform)random.Next(3), random.Next(8) == 0);
                        break;
                    case 5: case 6: Event(EventKind.Built); break;
                    case 7: case 8: case 9:
                        Event(EventKind.Confirm, random.Next(8) == 0 ? "../invalid-avatar" :
                            begin != null && begin.Id != null && random.Next(4) != 0 ? begin.Id : AvatarIds[random.Next(AvatarIds.Length)]);
                        break;
                    case 10: Event(EventKind.Cancel); break;
                    case 11: Event(EventKind.Reload); break;
                }
            }
        }
    }
}
