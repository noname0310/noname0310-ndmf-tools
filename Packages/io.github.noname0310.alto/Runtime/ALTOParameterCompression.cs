using System;
using System.Collections.Generic;
using nadena.dev.ndmf;
using UnityEngine;

namespace Noname.AvatarTools
{
    public enum ParameterMultiplexingMode { Disabled, AutomaticMenuInputs, SelectedInputs, AllSynchronizedInputs, AutomaticByPriority }
    [Serializable]
    public sealed class CompressedIntegerParameter
    {
        [Tooltip("Optional owning object or controller. Without a source, Name uses the final avatar namespace.")]
        public UnityEngine.Object Source;
        public string Name;
        [Range(0, 255)] public int Minimum;
        [Range(0, 255)] public int Maximum = 7;
    }

    [AddComponentMenu("Noname/ALTO Parameter Compression")]
    [DisallowMultipleComponent]
    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, "Noname.AvatarTools", "Noname.AdvancedAvatarOptimizer", "AdvancedParameterCompression")]
    public sealed class ALTOParameterCompression : MonoBehaviour, INDMFEditorOnly
    {
        [Tooltip("Automatically share network slots when the reference avatar exceeds the budget, or explicitly select eligible inputs. Applies to PC and other platforms using the recorded contract.")]
        public ParameterMultiplexingMode TimeMultiplexing = ParameterMultiplexingMode.AutomaticByPriority;
        [Range(16, 256)] public int TargetBitBudget = 256;
        [Tooltip("Seconds to hold each payload preparation and publication phase. Longer holds improve tolerance of slow delivery but increase synchronization delay.")]
        [Range(0.1f, 1f)] public float PacketHoldSeconds = 0.15f;
        [Tooltip("Eligible inputs in Selected Inputs mode. Empty selection selects nothing. Momentary inputs may be missed between snapshots.")]
        public AnimatorParameterSelection MultiplexedParameters = new();
        [Tooltip("Stop synchronizing parameters with no surviving avatar consumer. Logical local declarations remain available to OSC. Explicit preservation takes priority.")]
        public bool RemoveUnusedSynchronization = true;
        [Tooltip("Keep these inputs and their upstream computations in their original network representation.")]
        public AnimatorParameterSelection KeepNativeParameters = new();
        [Tooltip("Transmit each selected Int as N Bool parameters. Remote values are clamped to the specified inclusive range. Local values and names are unchanged. Independent bit delivery can briefly decode an intermediate value.")]
        public List<CompressedIntegerParameter> Integers = new();
    }
}
