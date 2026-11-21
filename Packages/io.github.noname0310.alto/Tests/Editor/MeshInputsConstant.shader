Shader "Hidden/Noname/ALTO/MeshInputsConstant"
{
    Properties
    {
        _IndexEffect ("Index Effect", Float) = 0
        _IntegerValue ("Integer Value", Integer) = 0
        _RepeatedValue ("First Declaration", Float) = 0
        _RepeatedValue ("Second Declaration", Float) = 1
    }
    SubShader
    {
        Pass
        {
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            float _IndexEffect;
            struct Input { float4 position : POSITION; uint id : SV_VertexID; };
            float4 vert(Input v) : SV_POSITION
            {
                if (_IndexEffect != 0) v.position.x += (v.id % 2) * 0.01;
                return v.position;
            }
            float4 frag() : SV_Target { return 1; }
            ENDHLSL
        }
    }
}
