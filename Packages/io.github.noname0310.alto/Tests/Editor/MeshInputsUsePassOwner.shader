Shader "Hidden/Noname/ALTO/MeshInputsUsePassOwner"
{
    SubShader
    {
        Pass
        {
            Name "CLEAN"
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            float4 vert(float4 p : POSITION) : SV_POSITION { return p; }
            float4 frag() : SV_Target { return 1; }
            ENDHLSL
        }
        Pass
        {
            Name "INDEX"
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            float4 vert(float4 p : POSITION, uint id : SV_VertexID) : SV_POSITION { p.x += id; return p; }
            float4 frag() : SV_Target { return 1; }
            ENDHLSL
        }
    }
    Fallback Off
}
