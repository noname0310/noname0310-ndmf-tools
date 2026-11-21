Shader "Hidden/Noname/ALTO/MeshInputsIndexed"
{
    Properties { _IndexEffect ("Index Effect", Float) = 1 }
    SubShader
    {
        Pass
        {
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            float _IndexEffect;
            struct Input { float4 position : POSITION; uint id : SV_VertexID0; };
            float4 vert(Input v) : SV_POSITION
            {
                if (_IndexEffect != 0) v.position.x += (v.id % 2) * 0.1;
                return v.position;
            }
            float4 frag() : SV_Target { return 1; }
            ENDHLSL
        }
    }
}
