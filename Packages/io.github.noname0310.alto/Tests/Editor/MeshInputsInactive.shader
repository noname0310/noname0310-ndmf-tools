Shader "Hidden/Noname/ALTO/MeshInputsInactive"
{
    SubShader
    {
        Pass
        {
            Name "MAIN"
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #define OPTIONAL_INPUTS 0
            #if OPTIONAL_INPUTS
            #include "ALTO_INTENTIONALLY_MISSING_INACTIVE_INCLUDE.hlsl"
            uint _InactiveInput : SV_VertexID;
            #endif
            float _UnusedButDeclared;
            struct Input
            {
                float4 vertex : POSITION;
                #if OPTIONAL_INPUTS
                uint id : SV_VertexID;
                #endif
            };
            float4 vert(Input v) : SV_POSITION
            {
                #if OPTIONAL_INPUTS
                v.vertex.x += (v.id % 2) * 0.01;
                #endif
                return v.vertex;
            }
            float4 frag() : SV_Target { return float4(1, 1, 1, 1); }
            ENDHLSL
        }
    }
}
