using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using UnityEditor.Rendering;
using UnityEngine;

namespace Noname.AvatarTools.Analysis
{
    // The Windows compiler performs HLSL parsing, type checking and dead-code
    // elimination. No shader asset or compiled avatar program is rewritten.
    // Reflection follows Microsoft's D3D11_SHADER_DESC and signature contracts.
    internal static class D3DShaderInputs
    {
        internal readonly struct Input
        {
            internal readonly string Semantic;
            internal readonly uint SemanticIndex;
            internal readonly byte ReadMask;
            internal Input(string semantic, byte readMask, uint semanticIndex = 0)
            { Semantic = semantic; ReadMask = readMask; SemanticIndex = semanticIndex; }
        }

        [DllImport("d3dcompiler_47.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        private static extern int D3DCompile(byte[] source, UIntPtr length, string name, IntPtr defines, IntPtr includes,
            string entry, string target, uint flags, uint effectFlags, out IntPtr code, out IntPtr errors);
        [DllImport("d3dcompiler_47.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern int D3DReflect(byte[] code, UIntPtr length, ref Guid iid, out IntPtr reflection);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr BlobPointer(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate UIntPtr BlobSize(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Description(IntPtr self, out ShaderDescription description);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Parameter(IntPtr self, uint index, out Signature description);

        [StructLayout(LayoutKind.Sequential)]
        private struct ShaderDescription
        {
            internal uint Version;
            internal IntPtr Creator;
            internal uint Flags, ConstantBuffers, BoundResources, InputParameters, OutputParameters;
            internal uint InstructionCount, TempRegisterCount, TempArrayCount, DefCount, DclCount;
            internal uint TextureNormalInstructions, TextureLoadInstructions, TextureCompInstructions, TextureBiasInstructions, TextureGradientInstructions;
            internal uint FloatInstructionCount, IntInstructionCount, UintInstructionCount, StaticFlowControlCount, DynamicFlowControlCount;
            internal uint MacroInstructionCount, ArrayInstructionCount, CutInstructionCount, EmitInstructionCount;
            internal uint GSOutputTopology, GSMaxOutputVertexCount, InputPrimitive, PatchConstantParameters;
            internal uint GSInstanceCount, ControlPoints, HSOutputPrimitive, HSPartitioning, TessellatorDomain;
            internal uint BarrierInstructions, InterlockedInstructions, TextureStoreInstructions;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Signature
        {
            internal IntPtr SemanticName;
            internal uint SemanticIndex, Register, SystemValueType, ComponentType;
            internal byte Mask, ReadWriteMask;
            internal uint Stream, MinPrecision;
        }

        private static T Method<T>(IntPtr obj, int index) where T : Delegate
            => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), index * IntPtr.Size));

        private static byte[] ReadBlob(IntPtr blob)
        {
            if (blob == IntPtr.Zero) return Array.Empty<byte>();
            ulong length = Method<BlobSize>(blob, 4)(blob).ToUInt64();
            if (length > 16 * 1024 * 1024) throw new InvalidOperationException("shader compiler output exceeds the analysis limit");
            var result = new byte[(int)length];
            Marshal.Copy(Method<BlobPointer>(blob, 3)(blob), result, 0, result.Length); return result;
        }

        internal static bool Compile(string source, string entry, ShaderType stage, out Input[] inputs, out string error)
        {
            inputs = null; error = null;
            if (Application.platform != RuntimePlatform.WindowsEditor) { error = "D3D reflection is unavailable on this editor platform"; return false; }
            return CompileNative(source, entry, stage, out inputs, out error);
        }

        // The caller checks the editor platform before scheduling. Every native
        // blob/reflection handle belongs to this invocation; no Unity API is used.
        internal static bool CompileNative(string source, string entry, ShaderType stage, out Input[] inputs, out string error)
        {
            inputs = null; error = null;
            string target = stage switch { ShaderType.Vertex => "vs_5_0", ShaderType.Fragment => "ps_5_0", ShaderType.Geometry => "gs_5_0",
                ShaderType.Hull => "hs_5_0", ShaderType.Domain => "ds_5_0", _ => null };
            if (target == null) { error = "unsupported shader stage"; return false; }
            IntPtr code = IntPtr.Zero, errors = IntPtr.Zero;
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(source);
                // Strict IEEE semantics keep unsafe x*0/NaN assumptions out of
                // the input-independence proof. Compatibility matches Unity's Cg globals.
                int hr = D3DCompile(bytes, (UIntPtr)bytes.Length, "ALTO Shader Input Analysis", IntPtr.Zero, IntPtr.Zero,
                    entry, target, (1u << 15) | (1u << 13) | (1u << 12), 0, out code, out errors);
                if (hr < 0)
                {
                    string diagnostic = Encoding.UTF8.GetString(ReadBlob(errors)).TrimEnd('\0').Trim();
                    error = "analysis program did not compile" + (diagnostic.Length == 0 ? "" : ": " + diagnostic.Substring(0, Math.Min(1024, diagnostic.Length)));
                    return false;
                }
                return Reflect(ReadBlob(code), stage, out inputs, out error);
            }
            catch (Exception exception) { error = "D3D input analysis failed: " + exception.GetType().Name; return false; }
            finally
            {
                if (code != IntPtr.Zero) Marshal.Release(code);
                if (errors != IntPtr.Zero) Marshal.Release(errors);
            }
        }

        internal static bool Reflect(byte[] bytes, ShaderType stage, out Input[] inputs, out string error)
        {
            inputs = null; error = null; IntPtr reflection = IntPtr.Zero;
            // Hull patch-constant functions have a separate interface. A regular
            // input signature alone is not a proof for the tessellation stages.
            if (stage != ShaderType.Vertex && stage != ShaderType.Fragment && stage != ShaderType.Geometry)
            { error = "compiled input coverage is unverified for this shader stage"; return false; }
            try
            {
                if (bytes == null || bytes.Length == 0 || bytes.Length > 16 * 1024 * 1024) { error = "empty or oversized shader bytecode"; return false; }
                var iid = new Guid("8d536ca1-0cca-4956-a837-786963755584"); // IID_ID3D11ShaderReflection
                int hr = D3DReflect(bytes, (UIntPtr)bytes.Length, ref iid, out reflection);
                if (hr < 0 || reflection == IntPtr.Zero) { error = "shader reflection is unavailable"; return false; }
                if (Method<Description>(reflection, 3)(reflection, out var description) < 0 || description.InputParameters > 256)
                { error = "unverified shader input signature"; return false; }
                uint expected = stage switch { ShaderType.Fragment => 0u, ShaderType.Vertex => 1u, ShaderType.Geometry => 2u,
                    ShaderType.Hull => 3u, ShaderType.Domain => 4u, _ => uint.MaxValue };
                if ((description.Version >> 16) != expected) { error = "unexpected compiled shader stage"; return false; }
                var result = new List<Input>(); var parameter = Method<Parameter>(reflection, 7);
                for (uint i = 0; i < description.InputParameters; i++)
                {
                    if (parameter(reflection, i, out var signature) < 0 || signature.SemanticName == IntPtr.Zero)
                    { error = "incomplete shader input signature"; return false; }
                    result.Add(new Input(Marshal.PtrToStringAnsi(signature.SemanticName),
                        (byte)(signature.Mask & signature.ReadWriteMask), signature.SemanticIndex));
                }
                inputs = result.ToArray(); return true;
            }
            catch (Exception exception) { error = "shader reflection failed: " + exception.GetType().Name; return false; }
            finally { if (reflection != IntPtr.Zero) Marshal.Release(reflection); }
        }
    }
}
