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
            #pragma multi_compile __ ALTO_CASE_4
            #pragma multi_compile __ ALTO_CASE_5
            #pragma multi_compile __ ALTO_CASE_6
            #pragma multi_compile __ ALTO_CASE_7
            #pragma multi_compile __ ALTO_CASE_8
            #pragma multi_compile __ ALTO_CASE_9
            #if 0
            uint _InactiveInput : SV_VertexID;
            #endif
            struct Input
            {
                float4 vertex : POSITION;
                #if ALTO_CASE_1
                uint id : SV_VertexID;
                #endif
            };
            float4 vert(Input v) : SV_POSITION
            {
                v.vertex.x += ALTO_CASE_1 + ALTO_CASE_2 + ALTO_CASE_3 + ALTO_CASE_4 + ALTO_CASE_5
                    + ALTO_CASE_6 + ALTO_CASE_7 + ALTO_CASE_8 + ALTO_CASE_9;
                #if ALTO_CASE_1
                v.vertex.x += (v.id % 2) * 0.01;
                #endif
                return v.vertex;
            }
            float4 frag() : SV_Target { return float4(1, 1, 1, 1); }
            ENDHLSL
        }
    }
}
