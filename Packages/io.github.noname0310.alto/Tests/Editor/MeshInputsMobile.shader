Shader "Hidden/Noname/ALTO/MeshInputsMobile"
{
    SubShader
    {
        Pass
        {
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #if defined(SHADER_API_MOBILE) || defined(SHADER_API_GLES3) || defined(SHADER_API_METAL)
            #define OPTIONAL_INPUTS 1
            #endif
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
