Shader "Hidden/Noname/ALTO/MeshInputsDead"
{
    SubShader
    {
        Pass
        {
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            struct Input { float4 position : POSITION; uint id : SV_VertexID; };
            float unused(Input v) { return v.id; }
            float4 vert(Input v) : SV_POSITION { return v.position; }
            float4 frag() : SV_Target { return 1; }
            ENDHLSL
        }
    }
}
