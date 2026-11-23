using System;
using System.Collections.Generic;

namespace Noname.AvatarTools.Analysis
{
    internal sealed class ExternalParameterAccess
    {
        private static readonly Dictionary<string, Action<ExternalParameterAccess>> Providers = new();
        internal readonly List<Func<string, bool>> Readers = new(), Writers = new();
        internal readonly List<string> Diagnostics = new();
        internal bool Uncertain => Diagnostics.Count != 0;
        internal bool IsRead(string name)
        {
            if (Uncertain) return true;
            foreach (var reader in Readers) if (reader(name)) return true;
            return false;
        }
        internal bool IsWritten(string name)
        {
            if (Uncertain) return true;
            foreach (var writer in Writers) if (writer(name)) return true;
            return false;
        }
        internal static void Register(string provider, Action<ExternalParameterAccess> collect) => Providers[provider] = collect;
        internal static ExternalParameterAccess Collect()
        {
            using var cost = AnalysisCost.Measure("External parameter descriptions");
            var result = new ExternalParameterAccess();
            foreach (var provider in Providers)
                try { provider.Value(result); }
                catch (Exception e) { result.Diagnostics.Add("Cannot verify external parameter access from " + provider.Key + ": " + e.Message); }
            return result;
        }
    }
}
