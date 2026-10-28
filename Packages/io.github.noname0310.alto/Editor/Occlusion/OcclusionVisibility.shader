Shader "Hidden/Noname/Occlusion Visibility"
{
    SubShader
    {
        Pass
        {
            Cull Off ZWrite Off ZTest Always ColorMask 0
            CGPROGRAM
            #pragma target 5.0
            #pragma vertex vert
            #pragma fragment frag
            float4x4 _ClipFromWorld, _ViewFromWorld;
            float _DepthTolerance;
            Texture2D<float> _OccluderDepth;
            RWStructuredBuffer<uint> _VisibilityFlags : register(u1);
            struct Input { float4 vertex : POSITION; float2 id : TEXCOORD0; };
            struct Output { float4 vertex : SV_POSITION; float depth : TEXCOORD0; nointerpolation uint id : TEXCOORD1; };
            Output vert(Input input)
            {
                Output output;
                output.vertex = mul(_ClipFromWorld, input.vertex);
                output.depth = -mul(_ViewFromWorld, input.vertex).z;
                output.id = (uint)input.id.x;
                return output;
            }
            float4 frag(Output input) : SV_Target
            {
                float occluder = _OccluderDepth.Load(int3((int2)input.vertex.xy, 0));
                uint bits = input.depth <= occluder + _DepthTolerance ? 3 : 1;
                InterlockedOr(_VisibilityFlags[input.id], bits);
                return 0;
            }
            ENDCG
        }
    }
}
