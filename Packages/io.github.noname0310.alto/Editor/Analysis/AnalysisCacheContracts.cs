namespace Noname.AvatarTools.Analysis
{
    // Persistent facts are versioned by their producer contract, not by the
    // entire editor assembly. Inspector changes and unrelated optimization passes
    // must not discard compiler artifacts. Bump the relevant revision whenever
    // its algorithm, assumptions or serialized representation changes; dependent
    // certificates include those revisions transitively.
    internal static class AnalysisCacheContracts
    {
        private const string Lexical = "lexical/1";
        private const string Imported = Lexical + "/imported-programs/1";
        private const string Declarations = Lexical + "/declarations/2";
        private const string Preprocessing = Imported + "/preprocessing/1";
        private const string NativeEnvironment = "native-environment/1";
        private const string NativeDependencies = Imported + "/" + NativeEnvironment + "/dependencies/1";
        private const string NativeProgram = NativeDependencies + "/native-artifact/1";
        internal const string CompiledInputs = "compiled-input-inventory/1";
        private const string NativeInputs = NativeProgram + "/" + Declarations + "/" + CompiledInputs + "/native-expression-proof/2";
        private const string Slices = Preprocessing + "/" + Declarations + "/" + CompiledInputs + "/input-slices/1";
        private const string Coverage = NativeInputs + "/" + Slices + "/keyword-coverage/1";
        private const string Rewrite = Preprocessing + "/" + Declarations + "/source-rewrite/2";

        // Unknown producers are deliberately not persistent. Adding a cache
        // requires choosing its invalidation dependencies here.
        internal static string Revision(string kind) => kind switch
        {
            "shader lexical summary" => Lexical,
            "shaderlab summary" => Lexical + "/shaderlab/1",
            "shader declarations" => Declarations,
            "shader specialization" => Declarations + "/specialization/1",
            "shader preprocessing" => Preprocessing,
            "native shader environment" => NativeEnvironment,
            "native shader dependencies" => NativeDependencies,
            "native shader preprocessing" => NativeDependencies + "/" + Preprocessing,
            "native shader program" => NativeProgram,
            "native shader input evidence" => NativeInputs,
            "shader input slices" => Slices,
            "native shader build inputs" => NativeDependencies + "/" + Coverage,
            "complete shader input coverage" => Coverage,
            "completed shader unit coverage" => Coverage,
            "shader source rewrite dependencies" => Rewrite,
            "source shader constants" => Rewrite,
            "runtime shared shader constants" => Rewrite + "/" + Coverage + "/runtime-constants/1",
            "runtime shared shader constants retained" => Rewrite + "/" + Coverage + "/runtime-constants/1",
            _ => null
        };
    }
}
