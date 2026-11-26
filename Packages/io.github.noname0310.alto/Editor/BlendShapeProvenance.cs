using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Noname.AvatarTools.Analysis;
using UnityEngine;

namespace Noname.AvatarTools.BlendShapeOptimization
{
    // A surviving renderer is not evidence that every shape came from its scope.
    // Keep authoring permissions on shapes. A complete source-name map or an
    // explicit whole-avatar policy can authorize generated inputs before the
    // first optimization boundary; later rewrites require their precise map.
    internal sealed class BlendShapeProvenance
    {
        internal sealed class Rule : IEquatable<Rule>
        {
            internal readonly HashSet<ScopeSettings> Scopes;
            private readonly HashSet<Transform> origins;
            internal readonly bool AuthoredProtected;
            internal bool AllowInitialChanges { get; private set; }
            internal NumericalOptimization AllowedNumericalDifferences { get; private set; }
            internal bool AllowsNumericalDifferences(NumericalOptimization feature) => capturedNumericalPermission &&
                (AllowedNumericalDifferences & feature) == feature;
            private bool capturedInitialPermission;
            private bool capturedNumericalPermission;
            internal bool ZeroReset => Scopes.All(s => s.ZeroReset);
            internal bool IgnoreUnreachable => Scopes.All(s => s.IgnoreUnreachableStates);
            internal bool RemoveIneffective => Scopes.All(s => s.RemoveIneffective);
            internal bool FreezeConstants => Scopes.All(s => s.FreezeConstants);
            internal bool PreserveMmdNames => Scopes.Any(s => s.Mmd);

            internal Rule(ScopeSettings scope, Transform origin, bool protect)
            {
                Scopes = new HashSet<ScopeSettings> { scope };
                origins = new HashSet<Transform> { origin };
                AuthoredProtected = protect;
            }

            internal Rule(IEnumerable<ScopeSettings> scopes, IEnumerable<Transform> allOrigins)
            { Scopes = new HashSet<ScopeSettings>(scopes); origins = new HashSet<Transform>(allOrigins); }

            private Rule(Rule a, Rule b)
            {
                Scopes = new HashSet<ScopeSettings>(a.Scopes); Scopes.UnionWith(b.Scopes);
                origins = new HashSet<Transform>(a.origins); origins.UnionWith(b.origins);
                AuthoredProtected = a.AuthoredProtected || b.AuthoredProtected;
                capturedInitialPermission = a.capturedInitialPermission && b.capturedInitialPermission;
                AllowInitialChanges = capturedInitialPermission && a.AllowInitialChanges && b.AllowInitialChanges;
                capturedNumericalPermission = a.capturedNumericalPermission && b.capturedNumericalPermission;
                AllowedNumericalDifferences = capturedNumericalPermission
                    ? a.AllowedNumericalDifferences & b.AllowedNumericalDifferences : NumericalOptimization.None;
            }

            private Rule(Rule source)
            {
                Scopes = new HashSet<ScopeSettings>(source.Scopes); origins = new HashSet<Transform>(source.origins);
                AuthoredProtected = true;
                capturedInitialPermission = source.capturedInitialPermission;
                AllowInitialChanges = source.AllowInitialChanges;
                capturedNumericalPermission = source.capturedNumericalPermission;
                AllowedNumericalDifferences = source.AllowedNumericalDifferences;
            }

            internal Rule WithAuthoredProtection() => AuthoredProtected ? this : new Rule(this);

            internal static Rule Intersect(Rule a, Rule b) => a == null || b == null ? null : new Rule(a, b);

            internal void CaptureInitialPermission(Func<Transform, bool> allows)
            {
                if (capturedInitialPermission) return;
                AllowInitialChanges = origins.All(t => t != null && allows?.Invoke(t) == true);
                capturedInitialPermission = true;
            }

            internal void CaptureNumericalPermission(Func<Transform, NumericalOptimization, bool> allows)
            {
                if (capturedNumericalPermission) return;
                foreach (var feature in new[] { NumericalOptimization.AnimationOutputs, NumericalOptimization.BlendShapes })
                    if (origins.All(t => t != null && allows?.Invoke(t, feature) == true)) AllowedNumericalDifferences |= feature;
                capturedNumericalPermission = true;
            }

            public bool Equals(Rule other) => other != null && AuthoredProtected == other.AuthoredProtected &&
                AllowInitialChanges == other.AllowInitialChanges && capturedInitialPermission == other.capturedInitialPermission &&
                AllowedNumericalDifferences == other.AllowedNumericalDifferences && capturedNumericalPermission == other.capturedNumericalPermission &&
                Scopes.SetEquals(other.Scopes) && (capturedInitialPermission && capturedNumericalPermission || origins.SetEquals(other.origins));
            public override bool Equals(object other) => other is Rule rule && Equals(rule);
            public override int GetHashCode()
            {
                int hash = (AuthoredProtected ? 1 : 0) | (AllowInitialChanges ? 4 : 0) |
                    (capturedInitialPermission ? 8 : 0) | ((int)AllowedNumericalDifferences << 8) | (capturedNumericalPermission ? 32 : 0);
                foreach (var scope in Scopes) hash ^= RuntimeHelpers.GetHashCode(scope);
                if (!capturedInitialPermission || !capturedNumericalPermission) foreach (var origin in origins) hash ^= RuntimeHelpers.GetHashCode(origin);
                return hash;
            }
        }

        private sealed class Entry
        {
            internal Mesh Mesh;
            internal string Signature;
            internal Dictionary<string, Rule> Shapes;
            internal BlendShapeOptimizationExclude ExportedMarker;
            internal readonly HashSet<string> ExportedProtections = new(StringComparer.Ordinal);
            internal int ValidatedBoundary;
            internal bool Unknown;
            internal bool NeedsSignature;
        }

        private readonly Dictionary<SkinnedMeshRenderer, Entry> entries = new();
        // An audited rewrite can remove a restricted source shape before a
        // later external pass copies an older mesh. Current entries describe
        // surviving outputs; they must not erase capture-time input policy.
        private readonly List<Rule> capturedInputRules = new();
        private Transform avatarRoot;
        private Rule wholeAvatarRule;
        private bool authoredExclusions;
        private int markerBoundary = -1;
        private readonly Dictionary<Mesh, string> boundarySignatures = new();
        private int boundary, boundaryDepth;
        private bool earlyLocked;
        internal long SignatureComputations { get; private set; }
        internal long SignatureFrameReads { get; private set; }
        internal long SignatureMilliseconds { get; private set; }
        private long reportedComputations, reportedFrames, reportedMilliseconds;
        private ICollection<string> metricTarget;
        private readonly HashSet<string> mappingDiagnostics = new(StringComparer.Ordinal);
        private readonly HashSet<string> reportedDiagnostics = new(StringComparer.Ordinal);
        internal bool Captured { get; private set; }
        internal bool HasScopedShapes => (!earlyLocked && wholeAvatarRule != null && !authoredExclusions) ||
            entries.Any(e => e.Key != null && e.Value.Shapes.Values.Any(r => r != null));

        // Worklists contain only synchronous package-owned rewrites. Validate
        // native mesh contents once on entry, then let explicit rewrite hooks
        // carry the proof through their changes. A new external/SDK boundary
        // checks the full buffers again, including edits made in place.
        internal IDisposable BeginBoundary()
        {
            if (boundaryDepth++ == 0) { boundary++; boundarySignatures.Clear(); }
            return new Boundary(this);
        }

        private sealed class Boundary : IDisposable
        {
            private BlendShapeProvenance owner;
            internal Boundary(BlendShapeProvenance owner) => this.owner = owner;
            public void Dispose()
            {
                if (owner == null) return;
                if (--owner.boundaryDepth == 0) owner.CompleteBoundary();
                owner = null;
            }
        }

        private void CompleteBoundary()
        {
            // Intermediate package-owned copies need no fingerprint. Establish
            // one baseline for each final surviving mesh before external code can
            // run again; the next untrusted boundary still validates all bytes.
            foreach (var pair in entries)
            {
                var entry = pair.Value;
                if (!entry.NeedsSignature) continue;
                entry.NeedsSignature = false;
                if (pair.Key == null || entry.Mesh == null || pair.Key.sharedMesh != entry.Mesh)
                { entry.Unknown = true; continue; }
                entry.Signature = Signature(entry.Mesh); entry.Unknown = entry.Signature == null;
            }
            if (metricTarget != null) ReportSignatures(metricTarget);
        }

        internal void Capture(GameObject avatar, IReadOnlyList<ScopeSettings> scopes)
        {
            if (Captured) return;
            Captured = true;
            avatarRoot = avatar.transform;
            // An explicit root scope grants permission to generated avatar
            // outputs too. Meet every scope, including currently empty ones;
            // this proves permission without guessing a generated shape's origin.
            // Authored exclusions still require precise source-name lineage.
            authoredExclusions = HasAuthoredExclusions();
            if (!authoredExclusions && scopes.Any(s => s.Root == avatarRoot))
                wholeAvatarRule = new Rule(scopes, avatar.GetComponentsInChildren<Transform>(true));
            using var capture = BeginBoundary();
            var renderers = avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (!renderers.Any(r => r.sharedMesh != null && r.sharedMesh.blendShapeCount != 0 &&
                scopes.Any(s => s.OriginalRenderers.Contains(r) || s.Root != null && r.transform.IsChildOf(s.Root)))) return;
            foreach (var renderer in renderers)
            {
                var mesh = renderer.sharedMesh;
                if (mesh == null || ALTOPlugin.IsEditorOnly(renderer.transform)) continue;
                var scope = scopes.Where(s => s.OriginalRenderers.Contains(renderer) ||
                    s.Root != null && renderer.transform.IsChildOf(s.Root)).OrderByDescending(s => s.Depth).FirstOrDefault();
                var protectedNames = new HashSet<string>(StringComparer.Ordinal);
                var marker = renderer.GetComponent<BlendShapeOptimizationExclude>();
                if (marker != null && marker.enabled) protectedNames.UnionWith(marker.BlendShapes);
                var shapes = new Dictionary<string, Rule>(StringComparer.Ordinal);
                for (int i = 0; i < mesh.blendShapeCount; i++)
                {
                    string name = mesh.GetBlendShapeName(i);
                    shapes[name] = scope == null ? null : new Rule(scope, renderer.transform, protectedNames.Contains(name));
                    capturedInputRules.Add(shapes[name]);
                }
                entries[renderer] = CreateEntry(mesh, shapes, reuseSignature: true);
            }
        }

        // Resolving captures authoring intent before markers disappear. Ordinary
        // generators can subsequently copy/rebind a mesh or remove vertices. The
        // first ALTO pass recognizes an old interface only from an intact source
        // and exact vertex/morph correspondence. That correspondence does NOT
        // prove origin: a generator can copy, rebase or rename a foreign morph
        // into identical bytes. Without universal output permission or a source
        // map, all captured input permissions therefore constrain the result.
        internal void PrepareEarly()
        {
            if (!Captured || earlyLocked) return;
            using var preparation = BeginBoundary();
            authoredExclusions |= HasAuthoredExclusions(); markerBoundary = boundary;
            var before = entries.ToArray();
            Rule possibleInputs = capturedInputRules.Count == 0 ? null : capturedInputRules.Aggregate(Rule.Intersect);
            foreach (var pair in before)
            {
                var renderer = pair.Key; var entry = pair.Value;
                if (renderer == null || renderer.sharedMesh == null || !entry.Shapes.Values.Any(r => r != null)) continue;
                var output = renderer.sharedMesh;
                if (entry.Mesh == output && entry.NeedsSignature && entry.ValidatedBoundary == boundary) continue;
                string current = Contents(output);
                if (entry.Mesh == output && current == entry.Signature) continue;
                if (TryAuthorizeWholeAvatar(renderer, output, out _)) continue;
                // A missing-name marker can describe a shape generated after
                // capture. Only an explicit source map can exclude that origin.
                if (authoredExclusions) { MissingMapping(renderer); continue; }
                if (entry.Mesh == null || entry.Signature == null || Contents(entry.Mesh) != entry.Signature ||
                    !TryVertexMap(entry.Mesh, output, out var vertices))
                { MissingMapping(renderer); continue; }
                mappingDiagnostics.Add("BlendShapes on '" + renderer.name + "': the generator supplied no complete contributor/name map; " +
                    "using the intersection of all captured source permissions and protections. New or changed morphs remain retained.");
                var shapes = new Dictionary<string, Rule>(StringComparer.Ordinal);
                var original = new ShapeDelta(entry.Mesh.vertexCount); var copied = new ShapeDelta(output.vertexCount);
                for (int shape = 0; shape < output.blendShapeCount; shape++)
                {
                    string name = output.GetBlendShapeName(shape);
                    int sourceShape = entry.Mesh.GetBlendShapeIndex(name);
                    bool same = sourceShape >= 0 && entry.Mesh.GetBlendShapeFrameCount(sourceShape) == output.GetBlendShapeFrameCount(shape);
                    for (int frame = 0; same && frame < output.GetBlendShapeFrameCount(shape); frame++)
                    {
                        if (!MotionAnalysis.Same(entry.Mesh.GetBlendShapeFrameWeight(sourceShape, frame), output.GetBlendShapeFrameWeight(shape, frame)))
                        { same = false; break; }
                        original.Read(entry.Mesh, sourceShape, frame); copied.Read(output, shape, frame);
                        for (int vertex = 0; same && vertex < vertices.Length; vertex++)
                            same = Exact(original.Vertices[vertices[vertex]], copied.Vertices[vertex]) &&
                                Exact(original.Normals[vertices[vertex]], copied.Normals[vertex]) &&
                                Exact(original.Tangents[vertices[vertex]], copied.Tangents[vertex]);
                    }
                    shapes[name] = same && entry.Shapes.TryGetValue(name, out var rule) ? Rule.Intersect(rule, possibleInputs) : null;
                }
                var replacement = CreateEntry(output, shapes, reuseSignature: true);
                CopyExportSnapshot(entry, replacement);
                entries[renderer] = replacement;
            }
            // A generator can add its first renderer after Resolving. No old
            // mesh or shape is required by the explicit whole-avatar permission.
            if (avatarRoot != null && wholeAvatarRule != null)
                foreach (var renderer in avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    if (!entries.TryGetValue(renderer, out var entry) || !entry.Shapes.Values.Any(r => r != null))
                        TryAuthorizeWholeAvatar(renderer, renderer.sharedMesh, out _);
        }

        private bool HasAuthoredExclusions()
        {
            if (avatarRoot == null) return true;
            foreach (var marker in avatarRoot.GetComponentsInChildren<BlendShapeOptimizationExclude>(true))
            {
                if (!marker.enabled || ALTOPlugin.IsEditorOnly(marker.transform)) continue;
                var renderer = marker.GetComponent<SkinnedMeshRenderer>();
                var exported = renderer != null && entries.TryGetValue(renderer, out var entry) &&
                    ReferenceEquals(entry.ExportedMarker, marker) ? entry.ExportedProtections : null;
                if (marker.BlendShapes == null || marker.BlendShapes.Any(n => exported == null || !exported.Contains(n))) return true;
            }
            return false;
        }

        private bool TryAuthorizeWholeAvatar(SkinnedMeshRenderer renderer, Mesh mesh, out Entry replacement)
        {
            replacement = null;
            if (earlyLocked || wholeAvatarRule == null || authoredExclusions || renderer == null || mesh == null || mesh.blendShapeCount == 0 ||
                avatarRoot == null || !renderer.transform.IsChildOf(avatarRoot) || ALTOPlugin.IsEditorOnly(renderer.transform)) return false;
            if (boundaryDepth == 0 || markerBoundary != boundary)
            {
                markerBoundary = boundary;
                authoredExclusions |= HasAuthoredExclusions();
            }
            if (authoredExclusions) return false;
            var shapes = Enumerable.Range(0, mesh.blendShapeCount).ToDictionary(mesh.GetBlendShapeName,
                _ => wholeAvatarRule, StringComparer.Ordinal);
            replacement = CreateEntry(mesh, shapes, reuseSignature: true);
            if (replacement.Unknown) { replacement = null; return false; }
            if (entries.TryGetValue(renderer, out var previous)) CopyExportSnapshot(previous, replacement);
            entries[renderer] = replacement;
            mappingDiagnostics.Add("BlendShapes on '" + renderer.name +
                "': generated mesh permissions use the explicit whole-avatar scope intersection; current runtime interfaces remain protected.");
            return true;
        }

        internal void LockEarly() => earlyLocked = true;

        internal void ReportSignatures(ICollection<string> messages)
        {
            metricTarget = messages;
            foreach (string diagnostic in mappingDiagnostics)
                if (reportedDiagnostics.Add(diagnostic)) messages.Add(diagnostic);
            if (SignatureComputations == reportedComputations) return;
            messages.Add("BlendShape provenance: " + (SignatureComputations - reportedComputations) + " mesh signatures, " +
                (SignatureFrameReads - reportedFrames) + " morph frame reads, " +
                (SignatureMilliseconds - reportedMilliseconds) + " ms.");
            reportedComputations = SignatureComputations; reportedFrames = SignatureFrameReads; reportedMilliseconds = SignatureMilliseconds;
        }

        // Ordered subsets cover generator vertex removal without guessing a
        // spatial tolerance. UVs and skin palettes may change independently;
        // original names and complete morph deltas are checked separately above.
        private static bool TryVertexMap(Mesh source, Mesh output, out int[] mapping)
        {
            mapping = null;
            if (!source.isReadable || !output.isReadable || output.vertexCount > source.vertexCount) return false;
            var a = source.vertices; var b = output.vertices;
            var an = source.normals; var bn = output.normals;
            var at = source.tangents; var bt = output.tangents;
            if ((an.Length == a.Length) != (bn.Length == b.Length) || (at.Length == a.Length) != (bt.Length == b.Length)) return false;
            var result = new int[b.Length]; int previous = 0;
            for (int vertex = 0; vertex < b.Length; vertex++)
            {
                while (previous < a.Length && (!Exact(a[previous], b[vertex]) ||
                    an.Length != 0 && !Exact(an[previous], bn[vertex]) || at.Length != 0 && !Exact(at[previous], bt[vertex]))) previous++;
                if (previous == a.Length) return false;
                result[vertex] = previous++;
            }
            mapping = result; return true;
        }

        private static bool Exact(Vector3 a, Vector3 b) => MotionAnalysis.Same(a.x, b.x) && MotionAnalysis.Same(a.y, b.y) && MotionAnalysis.Same(a.z, b.z);
        private static bool Exact(Vector4 a, Vector4 b) => MotionAnalysis.Same(a.x, b.x) && MotionAnalysis.Same(a.y, b.y) &&
            MotionAnalysis.Same(a.z, b.z) && MotionAnalysis.Same(a.w, b.w);

        internal void CaptureInitialPermissions(Func<Transform, bool> allows)
        {
            foreach (var rule in capturedInputRules.Concat(entries.Values.SelectMany(e => e.Shapes.Values)).Where(r => r != null))
                rule.CaptureInitialPermission(allows);
            wholeAvatarRule?.CaptureInitialPermission(allows);
        }

        internal void CaptureNumericalPermissions(Func<Transform, NumericalOptimization, bool> allows)
        {
            foreach (var rule in capturedInputRules.Concat(entries.Values.SelectMany(e => e.Shapes.Values)).Where(r => r != null))
                rule.CaptureNumericalPermission(allows);
            wholeAvatarRule?.CaptureNumericalPermission(allows);
        }

        internal bool TryGet(SkinnedMeshRenderer renderer, out IReadOnlyDictionary<string, Rule> shapes)
        {
            shapes = null;
            if (renderer == null) return false;
            if (!entries.TryGetValue(renderer, out var entry))
            {
                if (!TryAuthorizeWholeAvatar(renderer, renderer.sharedMesh, out entry)) return false;
            }
            if (!Current(entry, renderer.sharedMesh))
            {
                if (!TryAuthorizeWholeAvatar(renderer, renderer.sharedMesh, out var replacement))
                { if (entry.Shapes.Values.Any(r => r != null)) MissingMapping(renderer); return false; }
                entry = replacement;
            }
            shapes = entry.Shapes;
            return true;
        }

        private void MissingMapping(SkinnedMeshRenderer renderer)
            => mappingDiagnostics.Add("Retained BlendShapes on '" + renderer.name +
                "': the mesh rewrite has no complete contributor/name map or verified source interface.");

        internal IEnumerable<string> CaptureAdditionalProtections(SkinnedMeshRenderer renderer)
        {
            var marker = renderer.GetComponent<BlendShapeOptimizationExclude>();
            if (marker == null || !marker.enabled) return Array.Empty<string>();
            entries.TryGetValue(renderer, out var entry);
            bool ownedMarker = entry != null && ReferenceEquals(entry.ExportedMarker, marker);
            var names = (marker.BlendShapes ?? new List<string>()).Where(n => n != null &&
                (!ownedMarker || !entry.ExportedProtections.Contains(n))).ToArray();
            // Generators may add explicit markers after Resolving. Capture them
            // before exporting runtime-interface protections into the same marker;
            // otherwise a later pass could mistake the authored names for ours.
            authoredExclusions |= names.Length != 0;
            if (entry != null)
                foreach (string name in names)
                    if (entry.Shapes.TryGetValue(name, out var rule) && rule != null && !rule.AuthoredProtected)
                    {
                        var protectedRule = rule.WithAuthoredProtection();
                        entry.Shapes[name] = protectedRule; capturedInputRules.Add(protectedRule);
                    }
            return names;
        }

        internal void ExportProtections(SkinnedMeshRenderer renderer, BlendShapeOptimizationExclude writtenMarker)
        {
            if (writtenMarker == null || !entries.TryGetValue(renderer, out var entry)) return;
            // Ownership belongs to this component and the names actually written.
            // A replacement marker, or a new name added later, is authored even
            // when it matches an interface that a previous marker once protected.
            entry.ExportedMarker = writtenMarker;
            entry.ExportedProtections.Clear();
            entry.ExportedProtections.UnionWith(writtenMarker.BlendShapes);
        }

        private static void CopyExportSnapshot(Entry source, Entry destination)
        {
            destination.ExportedMarker = source.ExportedMarker;
            destination.ExportedProtections.UnionWith(source.ExportedProtections);
        }

        // Call before assignment, or provide the exact previous mesh afterwards.
        // This contract is only for audited rewrites which preserve shape names
        // and their source identity (index compaction, reduction, skin rebinding).
        internal void RecordMeshRewrite(SkinnedMeshRenderer renderer, Mesh previous, Mesh output)
        {
            if (!Captured || renderer == null || output == null) return;
            var shapes = new Dictionary<string, Rule>(StringComparer.Ordinal);
            bool known = entries.TryGetValue(renderer, out var old) && Current(old, previous);
            for (int i = 0; i < output.blendShapeCount; i++)
            {
                string name = output.GetBlendShapeName(i);
                shapes[name] = known && old.Shapes.TryGetValue(name, out var rule) ? rule : null;
            }
            var entry = CreateEntry(output, shapes);
            if (old != null) CopyExportSnapshot(old, entry);
            entries[renderer] = entry;
        }

        // Must run while every source still has its previous mesh and before
        // source renderers are destroyed. Missing or unknown input names retain
        // the entire combined output shape, regardless of destination scope.
        internal void RecordMerge(SkinnedMeshRenderer destination, Mesh output,
            IReadOnlyDictionary<SkinnedMeshRenderer, Dictionary<string, string>> mappings)
        {
            if (!Captured || destination == null || output == null) return;
            var shapes = new Dictionary<string, Rule>(StringComparer.Ordinal);
            foreach (var input in mappings)
            {
                bool known = TryGet(input.Key, out var original);
                foreach (var mapping in input.Value)
                {
                    Rule rule = null;
                    if (known) original.TryGetValue(mapping.Key, out rule);
                    shapes[mapping.Value] = shapes.TryGetValue(mapping.Value, out var previous) ? Rule.Intersect(previous, rule) : rule;
                }
            }
            for (int i = 0; i < output.blendShapeCount; i++)
            {
                string name = output.GetBlendShapeName(i);
                if (!shapes.ContainsKey(name)) shapes.Add(name, null);
            }
            var entry = CreateEntry(output, shapes);
            // Merging does not rewrite the surviving marker component. Its
            // ownership snapshot stays exact; authored source rules were mapped
            // above, and runtime interfaces are checked on the resulting mesh.
            if (entries.TryGetValue(destination, out var oldDestination)) CopyExportSnapshot(oldDestination, entry);
            foreach (var source in mappings.Keys) entries.Remove(source);
            entries[destination] = entry;
        }

        internal void RecordOptimization(OptimizationPlan plan, Mesh output)
        {
            if (!entries.TryGetValue(plan.Renderer, out var old) || !Current(old, plan.Source)) return;
            var shapes = new Dictionary<string, Rule>(StringComparer.Ordinal);
            var groups = plan.Groups.ToDictionary(g => g.Name);
            for (int i = 0; i < output.blendShapeCount; i++)
            {
                string name = output.GetBlendShapeName(i);
                if (groups.TryGetValue(name, out var group))
                {
                    var rules = group.Inputs.Select(s => old.Shapes.TryGetValue(s.Name, out var rule) ? rule : null).ToArray();
                    shapes[name] = rules.Aggregate(Rule.Intersect);
                }
                else shapes[name] = old.Shapes.TryGetValue(name, out var rule) ? rule : null;
            }
            var entry = CreateEntry(output, shapes);
            CopyExportSnapshot(old, entry);
            entries[plan.Renderer] = entry;
        }

        private Entry CreateEntry(Mesh mesh, Dictionary<string, Rule> shapes, bool reuseSignature = false)
        {
            if (!shapes.Values.Any(rule => rule != null))
                return new Entry { Mesh = mesh, Shapes = shapes, Unknown = true };
            if (!reuseSignature && boundaryDepth != 0)
            {
                if (mesh != null) boundarySignatures.Remove(mesh);
                return new Entry { Mesh = mesh, Shapes = shapes, Unknown = mesh == null || !mesh.isReadable,
                    NeedsSignature = mesh != null && mesh.isReadable, ValidatedBoundary = boundary };
            }
            string signature = reuseSignature ? Contents(mesh) : Signature(mesh);
            if (boundaryDepth != 0 && mesh != null) boundarySignatures[mesh] = signature;
            return new Entry { Mesh = mesh, Signature = signature, Shapes = shapes, Unknown = signature == null,
                ValidatedBoundary = boundaryDepth == 0 ? -1 : boundary };
        }

        private bool Current(Entry entry, Mesh mesh)
        {
            if (entry.Unknown) return false;
            if (mesh == null || entry.Mesh != mesh)
            { entry.Unknown = true; return false; }
            if (boundaryDepth != 0 && entry.ValidatedBoundary == boundary) return true;
            string signature = Contents(mesh);
            if (entry.Signature != signature) { entry.Unknown = true; return false; }
            entry.ValidatedBoundary = boundaryDepth == 0 ? -1 : boundary;
            return true;
        }

        private string Contents(Mesh mesh)
        {
            if (mesh == null) return null;
            if (boundaryDepth != 0 && boundarySignatures.TryGetValue(mesh, out var signature)) return signature;
            signature = Signature(mesh);
            if (boundaryDepth != 0) boundarySignatures[mesh] = signature;
            return signature;
        }

        // Object identity alone misses external in-place assembly. The topology,
        // base coordinates, skin palette and complete morph buffers establish the
        // vertex/shape identity used by this provenance, independently of curves.
        private string Signature(Mesh mesh)
        {
            if (mesh == null || !mesh.isReadable) return null;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            SignatureComputations++;
            try { return ContentHash.Compute(writer =>
            {
                writer.Write(mesh.vertexCount); writer.Write(mesh.subMeshCount);
                Write(writer, mesh.vertices); Write(writer, mesh.normals);
                Write(writer, mesh.tangents); Write(writer, mesh.bindposes);
                using (var counts = mesh.GetBonesPerVertex())
                    Write(writer, counts.ToArray());
                using (var weights = mesh.GetAllBoneWeights())
                    Write(writer, weights.ToArray());
                for (int sub = 0; sub < mesh.subMeshCount; sub++)
                {
                    writer.Write((int)mesh.GetTopology(sub));
                    Write(writer, mesh.GetIndices(sub, true));
                }
                writer.Write(mesh.blendShapeCount);
                var buffer = new ShapeDelta(mesh.vertexCount);
                for (int shape = 0; shape < mesh.blendShapeCount; shape++)
                {
                    writer.Write(mesh.GetBlendShapeName(shape));
                    int frames = mesh.GetBlendShapeFrameCount(shape); writer.Write(frames);
                    for (int frame = 0; frame < frames; frame++)
                    {
                        SignatureFrameReads++;
                        writer.Write(mesh.GetBlendShapeFrameWeight(shape, frame)); buffer.Read(mesh, shape, frame);
                        Write(writer, buffer.Vertices); Write(writer, buffer.Normals); Write(writer, buffer.Tangents);
                    }
                }
            }); }
            finally { SignatureMilliseconds += watch.ElapsedMilliseconds; }
        }

        private static void Write<T>(BinaryWriter writer, T[] values) where T : unmanaged
        {
            writer.Write(values.Length);
            writer.Write(Unity.Collections.LowLevel.Unsafe.UnsafeUtility.SizeOf<T>());
            writer.Write(ContentDigest.BlittableArray(values));
        }
    }
}
