Shader "Hidden/Noname/ALTO/SharedShaderNumericBits"
{
    Properties
    {
        _Value ("Value", Float) = 0
        _Stable ("Stable", Float) = 0.25
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma target 4.0
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            float _Value;
            float _Stable;
            v2f_img vert(appdata_img input)
            {
                v2f_img output = vert_img(input);
                return output;
            }
            float4 frag(v2f_img input) : SV_Target
            {
                uint bits = asuint(_Value);
                return float4(bits & 255u, (bits >> 8) & 255u,
                    (bits >> 16) & 255u, (bits >> 24) & 255u) / 255.0;
            }
            ENDCG
        }
        // An independent direct output remains eligible when the arithmetic
        // encoding above cannot be specialized without changing native folding.
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment stable
            #include "UnityCG.cginc"
            float _Stable;
            float4 stable(v2f_img input) : SV_Target { return _Stable; }
            ENDCG
        }
    }
    Fallback Off
}
