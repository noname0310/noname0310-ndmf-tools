Shader "Hidden/Noname/ALTO/MeshInputsLocalKeyword"
{
    Properties { [Toggle(ALTO_LOCAL_INDEX)] _Switch ("Switch", Float) = 0 }
    SubShader
    {
        Pass
        {
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_local __ ALTO_LOCAL_INDEX
            #pragma shader_feature_local __ ALTO_LOCAL_OPTION_A
            #pragma shader_feature_local __ ALTO_LOCAL_OPTION_B
            #pragma shader_feature_local __ ALTO_LOCAL_OPTION_C
            #pragma shader_feature_local __ ALTO_LOCAL_OPTION_D
            struct Input
            {
                float4 vertex : POSITION;
                #ifdef ALTO_LOCAL_INDEX
                uint id : SV_VertexID;
                #endif
            };
            float4 vert(Input v) : SV_POSITION
            {
                #ifdef ALTO_LOCAL_INDEX
                v.vertex.x += (v.id % 2) * 0.01;
                #endif
                return v.vertex;
            }
            float4 frag() : SV_Target { return float4(1, 1, 1, 1); }
            ENDHLSL
        }
    }
}
