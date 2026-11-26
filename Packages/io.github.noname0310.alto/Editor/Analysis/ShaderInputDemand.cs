using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Noname.AvatarTools.Analysis
{
    // The observed part of a program for one input-use proof. Whole aggregate
    // values are distinct from member reads so unrelated fields do not expand a
    // query to the entire vertex structure.
    internal sealed class ShaderInputDemand
    {
        internal readonly ShaderInputQuery Query;
        internal readonly HashSet<string> Names = new(StringComparer.Ordinal);
        internal readonly HashSet<string> WholeValues = new(StringComparer.Ordinal);
        private readonly HashSet<(string Name, string File, int First, int Last)> wholeValueScopes = new();
        private readonly Dictionary<string, List<(string File, int First, int Last)>> wholeScopesByName = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<(int First, int Last)>> regions = new(
            Path.DirectorySeparatorChar == '\\' ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        private readonly Dictionary<string, string> paths = new(StringComparer.Ordinal);
        private readonly Dictionary<string, bool> queryMatches = new(StringComparer.Ordinal);
        private string lastRegionFile;
        private int lastRegionLine;
        private bool lastRegionMatch;
        internal bool LocationsComplete { get; private set; } = true;
        internal bool UsesLocations => regions.Count != 0 || wholeValueScopes.Count != 0;
        internal ShaderInputDemand(ShaderInputQuery query) { Query = query; }
        internal string ContentSignature
        {
            get
            {
                using var data = new MemoryStream();
                using (var writer = new BinaryWriter(data, System.Text.Encoding.UTF8, true))
                {
                    writer.Write(Query.Signature); writer.Write(LocationsComplete);
                    writer.Write(Names.Count); foreach (string name in Names.OrderBy(n => n, StringComparer.Ordinal)) writer.Write(name);
                    writer.Write(WholeValues.Count); foreach (string name in WholeValues.OrderBy(n => n, StringComparer.Ordinal)) writer.Write(name);
                    writer.Write(regions.Count);
                    foreach (var pair in regions.OrderBy(p => p.Key, StringComparer.Ordinal))
                    {
                        writer.Write(pair.Key); writer.Write(pair.Value.Count);
                        foreach (var region in pair.Value.OrderBy(r => r.First).ThenBy(r => r.Last))
                        { writer.Write(region.First); writer.Write(region.Last); }
                    }
                    writer.Write(wholeValueScopes.Count);
                    foreach (var scope in wholeValueScopes.OrderBy(s => s.Name, StringComparer.Ordinal).ThenBy(s => s.File, StringComparer.Ordinal)
                                 .ThenBy(s => s.First).ThenBy(s => s.Last))
                    { writer.Write(scope.Name); writer.Write(scope.File); writer.Write(scope.First); writer.Write(scope.Last); }
                }
                return ShaderAnalysisCache.Digest(data.ToArray());
            }
        }
        internal string Signature => Query.Signature + "|" + string.Join(",", Names.OrderBy(n => n, StringComparer.Ordinal)) + "|" +
            string.Join(",", WholeValues.OrderBy(n => n, StringComparer.Ordinal)) + "|" + string.Join(";", regions.OrderBy(p => p.Key, StringComparer.Ordinal)
                .SelectMany(p => p.Value.OrderBy(r => r.First).ThenBy(r => r.Last).Select(r => p.Key + ":" + r.First + ":" + r.Last))) + "|scopes:" +
            string.Join(";", wholeValueScopes.OrderBy(s => s.Name, StringComparer.Ordinal).ThenBy(s => s.File, StringComparer.Ordinal).ThenBy(s => s.First).ThenBy(s => s.Last));

        internal bool Include(ShaderInputUseSlice slice)
        {
            lastRegionFile = null;
            int previousNames = Names.Count;
            Names.UnionWith(slice.Names);
            bool changed = previousNames != Names.Count;
            foreach (var scope in slice.WholeValueScopes)
            {
                string file = PathKey(scope.File);
                if (!wholeValueScopes.Add((scope.Name, file, scope.First, scope.Last))) continue;
                changed = true;
                if (!wholeScopesByName.TryGetValue(scope.Name, out var scopes)) wholeScopesByName.Add(scope.Name, scopes = new());
                scopes.Add((file, scope.First, scope.Last));
            }
            LocationsComplete &= slice.LocationsComplete;
            foreach (var region in slice.Regions)
            {
                string path = PathKey(region.File);
                if (!regions.TryGetValue(path, out var ranges)) regions.Add(path, ranges = new List<(int, int)>());
                if (ranges.Any(r => r.First <= region.First && r.Last >= region.Last)) continue;
                ranges.RemoveAll(r => region.First <= r.First && region.Last >= r.Last);
                ranges.Add((region.First, region.Last));
                changed = true;
            }
            return changed;
        }

        internal bool Matches(PpToken token, PpToken next, PpToken invocation = null)
        {
            if (InRegion(token)) return true;
            if (token.Kind != PpTokenKind.Identifier && token.Kind != PpTokenKind.Symbol) return false;
            if (!queryMatches.TryGetValue(token.Text, out bool matches))
                queryMatches.Add(token.Text, matches = Query.MayDeclareToken(token.Text));
            if (matches || Names.Contains(token.Text)) return true;
            if (next?.Text == ".") return false;
            if (WholeValues.Contains(token.Text)) return true;
            if (wholeScopesByName.TryGetValue(token.Text, out var scopes))
                for (int i = 0; i < scopes.Count; i++)
                {
                    var scope = scopes[i];
                    if (Inside(scope.File, scope.First, scope.Last, token) ||
                        invocation != null && Inside(scope.File, scope.First, scope.Last, invocation)) return true;
                }
            return false;
        }

        private bool InRegion(PpToken token)
        {
            if (regions.Count == 0 || string.IsNullOrEmpty(token.File)) return false;
            if (lastRegionLine == token.Line && lastRegionFile == token.File) return lastRegionMatch;
            lastRegionFile = token.File; lastRegionLine = token.Line; lastRegionMatch = false;
            if (regions.TryGetValue(PathKey(token.File), out var spans))
                for (int i = 0; i < spans.Count; i++)
                    if (spans[i].First <= token.Line && token.Line <= spans[i].Last) return lastRegionMatch = true;
            return false;
        }

        private bool Inside(string file, int first, int last, PpToken token) =>
            token.Line >= first && token.Line <= last && !string.IsNullOrEmpty(token.File) &&
            string.Equals(file, PathKey(token.File), Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

        private string PathKey(string path)
        {
            if (paths.TryGetValue(path, out string known)) return known;
            string result;
            try { result = Path.GetFullPath(path).Replace('\\', '/'); }
            catch (Exception) { result = path.Replace('\\', '/'); }
            paths.Add(path, result); return result;
        }
    }
}
