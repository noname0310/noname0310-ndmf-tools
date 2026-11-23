Shader "Hidden/Noname/Independent Material A"
{
    Properties { _OnlyA ("A", Float) = 0 _TintA ("Tint A", Color) = (0,0,0,1) }
    SubShader
    {
        Pass
        {
            Cull Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            float _OnlyA; float4 _TintA;
            float4 vert(float4 position : POSITION) : SV_POSITION { return UnityObjectToClipPos(position); }
            float4 frag() : SV_Target { return float4(0.1 + _OnlyA * 0.7, 0.2 + _TintA.g * 0.5, 0.3, 1); }
            ENDCG
        }
    }
}
