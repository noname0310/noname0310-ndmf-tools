Shader "Hidden/Noname/MaterialAtlasInput"
{
    Properties
    {
        _AtlasChannelCount ("Channel count", Float) = 1
        _AtlasSampleST ("Source sampling transform", Vector) = (1,1,0,0)
        _AtlasClampUv ("Clamp atlas padding", Float) = 0
        _MainTex ("Channel 0", 2D) = "white" {}
        _AtlasTexture1 ("Channel 1", 2D) = "white" {}
        _AtlasTexture2 ("Channel 2", 2D) = "white" {}
        _AtlasTexture3 ("Channel 3", 2D) = "white" {}
        _AtlasTexture4 ("Channel 4", 2D) = "white" {}
        _AtlasTexture5 ("Channel 5", 2D) = "white" {}
        _AtlasTexture6 ("Channel 6", 2D) = "white" {}
        _AtlasTexture7 ("Channel 7", 2D) = "white" {}
    }
    SubShader
    {
        Pass
        {
            Cull Off ZWrite Off ZTest Always
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 _AtlasSampleST;
            float4 _MainTex_TexelSize;
            float _AtlasClampUv;
            float4 frag(v2f_img input) : SV_Target
            {
                float2 uv = input.uv * _AtlasSampleST.xy + _AtlasSampleST.zw;
                if (_AtlasClampUv > 0.5) uv = clamp(uv, abs(_MainTex_TexelSize.xy) * 0.5, 1 - abs(_MainTex_TexelSize.xy) * 0.5);
                return tex2D(_MainTex, uv);
            }
            ENDCG
        }
    }
    Fallback Off
}
