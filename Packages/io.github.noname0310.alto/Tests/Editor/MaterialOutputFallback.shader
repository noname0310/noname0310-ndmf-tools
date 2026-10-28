Shader "Hidden/Noname/ALTO/MaterialOutputFallback"
{
    SubShader
    {
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            float4 vert(float4 p : POSITION) : SV_POSITION { return UnityObjectToClipPos(p); }
            float4 frag() : SV_Target { return 1; }
            ENDCG
        }
    }
    Fallback "Hidden/Noname/ALTO/MaterialOutputUsage"
}
