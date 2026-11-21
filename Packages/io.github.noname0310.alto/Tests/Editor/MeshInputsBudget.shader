Shader "Hidden/Noname/ALTO/MeshInputsBudget"
{
    SubShader
    {
        Pass
        {
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile __ ALTO_CASE_1
            #pragma multi_compile __ ALTO_CASE_2
            #pragma multi_compile __ ALTO_CASE_3
            #if 0
            uint _InactiveInput : SV_VertexID;
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
