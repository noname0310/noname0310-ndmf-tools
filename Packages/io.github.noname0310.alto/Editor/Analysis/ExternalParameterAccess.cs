using System;
using System.Collections.Generic;
using System.Linq;

namespace Noname.AvatarTools.Analysis
{
    internal sealed class ExternalParameterAccess
    {
        private static readonly Dictionary<string, Action<ExternalParameterAccess>> Providers = new();
        internal readonly List<Func<string, bool>> Readers = new(), Writers = new();
        internal readonly List<string> Diagnostics = new();
        internal bool Uncertain => Diagnostics.Count != 0;
        internal bool IsRead(string name) => Uncertain || Readers.Any(p => p(name));
        internal bool IsWritten(string name) => Uncertain || Writers.Any(p => p(name));
        internal static void Register(string provider, Action<ExternalParameterAccess> collect) => Providers[provider] = collect;
        internal static ExternalParameterAccess Collect()
        {
            var result = new ExternalParameterAccess();
            foreach (var provider in Providers)
                try { provider.Value(result); }
                catch (Exception e) { result.Diagnostics.Add("Cannot verify external parameter access from " + provider.Key + ": " + e.Message); }
            return result;
        }
    }
}
