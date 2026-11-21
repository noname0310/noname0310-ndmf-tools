using System;
using System.Collections.Generic;
using nadena.dev.ndmf;
using UnityEngine;

namespace Noname.AvatarTools
{
    public enum ParameterMultiplexingMode { Disabled, AutomaticMenuInputs, SelectedInputs, AllSynchronizedInputs, AutomaticByPriority }
    public enum ParameterTransmissionMode { Guarded, LowLatency }
    public enum ParameterInputAssumption { ExternalWithCompression, AvatarDrivenExceptListed }
    [Serializable]
    public sealed class ParameterKeepRule
    {
        public AnimatorParameterSelection Parameters = new();
        [Tooltip("Higher priorities are multiplexed later. Used only when all inputs are treated as external. In Avatar-Driven mode every listed input is protected regardless of priority.")]
        [Min(1)] public int Priority = 100;
    }
    public enum FloatPrecisionMode { KeepExisting, Minimum, Exact }
    public enum FloatPrecisionRepresentation { KeepExisting, Signed, Unsigned }

    [Serializable]
    public class FloatPrecisionSettings
    {
        [Tooltip("Minimum permits wider shared slots when they reduce total transport cost. Exact retains this quantization and can require separate slots. Keep Existing retains the source bit width.")]
        public FloatPrecisionMode Precision = FloatPrecisionMode.Minimum;
        [Range(2, 8)] public int Bits = 6;
        public FloatPrecisionRepresentation Representation = FloatPrecisionRepresentation.KeepExisting;
    }

    [Serializable]
    public class ParameterPrecisionSettings
    {
        public FloatPrecisionSettings Floats = new() { Precision = FloatPrecisionMode.KeepExisting };
        public IntegerRangeSettings Integers = new();
    }

    public enum IntegerRangeMode { KeepExisting, LimitRange }
    public enum ParameterPrecisionType { Auto, Float, Int }

    [Serializable]
    public sealed class IntegerRangeSettings
    {
        public IntegerRangeMode Range = IntegerRangeMode.KeepExisting;
        [Range(-128, 255)] public int Minimum;
        [Range(-128, 255)] public int Maximum = 7;
    }

    [Serializable]
    public sealed class ParameterPrecisionSourceOverride : ParameterPrecisionSettings
    {
        [Tooltip("Apply the Int and Float settings independently to inputs selected by sources, final names or prefixes. Bool inputs are unchanged. Empty selection selects nothing.")]
        public AnimatorParameterSelection Parameters = new();
    }

    [Serializable]
    public sealed class ParameterPrecisionOverride : ParameterPrecisionSettings
    {
        [Tooltip("Optional owning object or controller. Without a source, Name uses the final avatar namespace.")]
        public UnityEngine.Object Source;
        public string Name;
        [Tooltip("Auto uses the parameter's type. Select a type to edit settings for an input which is generated only during the build; a type mismatch stops the build.")]
        public ParameterPrecisionType Type = ParameterPrecisionType.Auto;
    }
    [AddComponentMenu("Noname/ALTO Parameter Compression")]
    [DisallowMultipleComponent]
    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, "Noname.AvatarTools", "Noname.AdvancedAvatarOptimizer", "AdvancedParameterCompression")]
    public sealed class ALTOParameterCompression : MonoBehaviour, INDMFEditorOnly
    {
        [Tooltip("Automatically share network slots when the reference avatar exceeds the budget, or explicitly select eligible inputs. Applies to PC and other platforms using the recorded contract.")]
        public ParameterMultiplexingMode TimeMultiplexing = ParameterMultiplexingMode.AutomaticByPriority;
        [Tooltip("Guarded separates field updates to tolerate bounded independent delivery delays. Low Latency assumes a packet's fields are observed together and adds periodic longer sweeps for slow observers. Other platforms follow the reference upload's recorded mode.")]
        public ParameterTransmissionMode TransmissionMode = ParameterTransmissionMode.Guarded;
        [Range(16, 256)] public int TargetBitBudget = 256;
        [Tooltip("Guarded: hold each preparation phase for this duration. Low Latency: hold ordinary publications for this duration, with longer recovery sweeps. Longer holds increase synchronization latency.")]
        [Range(0.1f, 1f)] public float PacketHoldSeconds = 0.15f;
        [Tooltip("Eligible inputs in Selected Inputs mode. Empty selection selects nothing. Momentary inputs may be missed between snapshots.")]
        public AnimatorParameterSelection MultiplexedParameters = new();
        [Tooltip("Stop synchronizing parameters with no surviving avatar consumer. Logical local declarations remain available to OSC. Explicit preservation takes priority.")]
        public bool RemoveUnusedSynchronization = true;
        [Tooltip("External With Compression assumes every input may be externally controlled, but permits delayed multiplexing. Avatar Driven Except Listed assumes unlisted inputs have no OSC or other unregistered external writer; listed inputs are strictly protected.")]
        public ParameterInputAssumption InputAssumption = ParameterInputAssumption.ExternalWithCompression;
        public List<ParameterKeepRule> KeepParameters = new();
        [Tooltip("For proven nonnegative avatar-driven values with default SDK encodings, allow one fewer network bit. Float quantization may change slightly. Artist precision overrides and external inputs are excluded.")]
        public bool AllowUnsignedBitReduction = true;
        [Tooltip("Apply independent Int range and Float precision settings to selected sources. Bool inputs are unchanged. Unselected inputs retain the artist's encoding.")]
        public List<ParameterPrecisionSourceOverride> PrecisionSourceOverrides = new();
        [Tooltip("Individual settings override source rules. Keep Existing restores the source width even when a source rule selects this input. Conflicting rules at the same priority stop the build.")]
        public List<ParameterPrecisionOverride> PrecisionOverrides = new();
    }
}
