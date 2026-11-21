Shader "Hidden/Noname/ALTO/MeshInputsGamma"
{
    Properties
    {
        [Gamma] _Gate ("Gate", Float) = 0.5
        _IndexEffect ("Index Effect", Float) = 1
    }
    SubShader
    {
        Pass
        {
            Cull Off ZWrite Off ZTest Always
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            float _Gate;
            float _IndexEffect;
            struct Input { float4 position : POSITION; uint id : SV_VertexID; };
            float4 vert(Input v) : SV_POSITION
            {
                if (_IndexEffect != 0 && _Gate != 0 && _Gate != 1 && abs(_Gate - 0.5) > 0.01)
                    v.position.x += (v.id % 2) * 0.1;
                return v.position;
            }
            float4 frag() : SV_Target { return float4(_Gate, 0, 0, 1); }
            ENDHLSL
        }
    }
}
