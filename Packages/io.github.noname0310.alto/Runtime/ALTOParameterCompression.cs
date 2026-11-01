using System;
using System.Collections.Generic;
using nadena.dev.ndmf;
using UnityEngine;

namespace Noname.AvatarTools
{
    public enum ParameterMultiplexingMode { Disabled, AutomaticMenuInputs, SelectedInputs, AllSynchronizedInputs, AutomaticByPriority }
    public enum ParameterTransmissionMode { Guarded, LowLatency }
    [Serializable]
    public sealed class CompressedIntegerParameter
    {
        [Tooltip("Optional owning object or controller. Without a source, Name uses the final avatar namespace.")]
        public UnityEngine.Object Source;
        public string Name;
        [Range(-128, 255)] public int Minimum;
        [Range(-128, 255)] public int Maximum = 7;
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
        [Tooltip("Keep these inputs and their upstream computations in their original network representation.")]
        public AnimatorParameterSelection KeepNativeParameters = new();
        [Tooltip("Reduce the wire cost of selected Ints. Full signed or unsigned ranges use the SDK bit width directly; other ranges use N Bool parameters and a decoder. Remote values are clamped to the specified range. Packed bits can briefly decode an intermediate value during delivery.")]
        public List<CompressedIntegerParameter> Integers = new();
    }
}
