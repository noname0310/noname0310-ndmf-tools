Shader "Hidden/Noname/ALTO/MeshInputsSliced"
{
    Properties
    {
        _Gate ("Input gate", Float) = 0
        _Unrelated ("Unrelated color", Float) = 0
    }
    SubShader
    {
        Pass
        {
            Name "VISIBLE"
            Tags { "LightMode" = "Always" }
            Cull Off
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile __ ALTO_UNRELATED_A
            #pragma multi_compile __ ALTO_UNRELATED_B
            #pragma multi_compile __ ALTO_UNRELATED_C
            #pragma multi_compile __ ALTO_UNRELATED_D
            #pragma multi_compile __ ALTO_UNRELATED_E
            #pragma multi_compile __ ALTO_UNRELATED_F
            #pragma multi_compile __ ALTO_UNRELATED_G
            #pragma multi_compile __ ALTO_UNRELATED_H
            #pragma multi_compile __ ALTO_UNRELATED_I
            #pragma multi_compile __ ALTO_UNRELATED_J
            float _Gate;
            float _Unrelated;
            float ReadInput(float value, float gate) { return gate == 0 ? 0 : value * gate; }
            float4 vert(float4 position : POSITION, float custom : TEXCOORD3) : SV_POSITION
            {
                position.x += ReadInput(custom, _Gate);
                return position;
            }
            float4 frag() : SV_Target
            {
                float color = _Unrelated;
                #ifdef ALTO_UNRELATED_A
                color += 0.01;
                #endif
                #ifdef ALTO_UNRELATED_B
                color += 0.02;
                #endif
                #ifdef ALTO_UNRELATED_C
                color += 0.03;
                #endif
                #ifdef ALTO_UNRELATED_D
                color += 0.04;
                #endif
                #ifdef ALTO_UNRELATED_E
                color += 0.05;
                #endif
                #ifdef ALTO_UNRELATED_F
                color += 0.06;
                #endif
                #ifdef ALTO_UNRELATED_G
                color += 0.07;
                #endif
                #ifdef ALTO_UNRELATED_H
                color += 0.08;
                #endif
                #ifdef ALTO_UNRELATED_I
                color += 0.09;
                #endif
                #ifdef ALTO_UNRELATED_J
                color += 0.10;
                #endif
                return float4(1, color, 0, 1);
            }
            ENDHLSL
        }
        Pass
        {
            Name "METADATA_ONLY"
            Tags { "LightMode" = "ALTO_Test_NotScheduled" }
            Cull Off ZTest Always ZWrite Off
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            float4 vert(float4 position : POSITION, uint id : SV_VertexID) : SV_POSITION
            {
                position.x += id * 0.001;
                return position;
            }
            float4 frag() : SV_Target { return float4(0, 1, 0, 1); }
            ENDHLSL
        }
    }
    Fallback Off
}
