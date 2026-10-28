Shader "Hidden/Noname/ALTO/MaterialOutputPlatform"
{
    SubShader
    {
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma exclude_renderers metal
            float4 vert(float4 p : POSITION) : SV_POSITION { return p; }
            float4 frag() : SV_Target { return 1; }
            ENDCG
        }
    }
    SubShader
    {
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma only_renderers metal
            float4 _StrippedBackendUniform;
            float4 vert(float4 p : POSITION) : SV_POSITION { return p; }
            float4 frag() : SV_Target { return _StrippedBackendUniform; }
            ENDCG
        }
    }
    Fallback Off
}
