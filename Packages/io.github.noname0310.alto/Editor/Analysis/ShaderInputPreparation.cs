using System;
using System.Threading.Tasks;

namespace Noname.AvatarTools.Analysis
{
    // One bounded pair of parses per consumed shader sample. Only immutable text
    // crosses the worker boundary; Unity preprocessing stays on the main thread.
    // Native reflection and logical compile budgets are consumed in their
    // original order. An early native result can discard these structural facts.
    internal sealed class ShaderInputPreparation : IDisposable
    {
        private readonly string sourceId;
        internal string SourceId => sourceId;
        private readonly Func<string, string, ShaderInputProgram> parse;
        private Task<ShaderInputProgram> original, specialized;
        internal bool Started => original != null;

        internal ShaderInputPreparation(string sourceId, Func<string, string, ShaderInputProgram> parse = null)
        { this.sourceId = sourceId; this.parse = parse ?? ((text, file) => new ShaderInputProgram(text, file)); }

        internal void Start(string originalSource, string specializedSource)
        {
            if (original != null) throw new InvalidOperationException("A shader preparation already owns a sample");
            // These programs have independent symbol tables. Parsing one after
            // the other can outlast the native compile and stall its consumer.
            original = Task.Run(() => parse(originalSource, sourceId));
            specialized = Task.Run(() => parse(specializedSource, ""));
        }

        private (ShaderInputProgram Original, ShaderInputProgram Specialized) Parse(string original, string specialized)
            => (parse(original, sourceId), parse(specialized, ""));

        internal (ShaderInputProgram Original, ShaderInputProgram Specialized) Get(string originalSource, string specializedSource)
            => original == null ? Parse(originalSource, specializedSource) :
                (original.GetAwaiter().GetResult(), specialized.GetAwaiter().GetResult());

        public void Dispose()
        {
            Join(original); Join(specialized);
        }

        private static void Join(Task<ShaderInputProgram> task)
        {
            if (task == null) return;
            try { task.GetAwaiter().GetResult(); }
            catch (Exception)
            {
                // A consumed parse reports its exception through Get. An unused
                // speculative parse must not replace the native proof's result.
            }
        }
    }
}
