using UnityEngine;

namespace Noname.AvatarTools
{
    [System.Flags]
    public enum NumericalOptimization
    {
        None = 0,
        AnimationOutputs = 1,
        BlendShapes = 2,
        TransformMerging = 4,
        ComponentHoisting = 8,
        Constraints = 16,
        MeshMerging = 32,
        Skinning = 64,
        All = AnimationOutputs | BlendShapes | TransformMerging | ComponentHoisting | Constraints | MeshMerging | Skinning
    }

    [AddComponentMenu("Noname/ALTO Optimization Policy")]
    public sealed class ALTOOptimizationPolicy : ALTOPlatformOptimizationComponent
    {
        public ALTOOptimizationPolicy() : base(OptimizationPlatforms.All) { }

        [Tooltip("Allow small floating-point differences at each selected feature's continuous-output boundary. All features use the same numerical criterion. Clear a feature for stricter proofs. Parameter, control-flow, index and invalid-value analysis remain exact.")]
        public NumericalOptimization AllowedNumericalDifferences = NumericalOptimization.All;

        [Tooltip("Allow proven constant visual outputs to replace authored starting values before Animator evaluation. This can change the appearance during initialization. It does not allow changes to persistent physics, parameter feedback, native writers or later animation output. Baking still requires the corresponding optimization option.")]
        public bool AllowInitialValueChanges = true;

        [Tooltip("Allow the shader compiler to fold proven constant material inputs. This permits the compiler's floating-point rounding and reassociation changes; it does not assume unknown inputs are constant. Clear this option to require strict native equivalence before replacing a shader.")]
        public bool AllowShaderConstantFolding = true;
    }
}
