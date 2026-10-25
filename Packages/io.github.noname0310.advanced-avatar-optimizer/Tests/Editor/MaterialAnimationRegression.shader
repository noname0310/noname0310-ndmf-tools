Shader "Hidden/Noname/AdvancedAvatarOptimizer/MaterialAnimationRegression"
{
    Properties
    {
        _Vector ("Vector", Vector) = (0, 1, 1, 1)
        _Color ("Color", Color) = (0, 1, 1, 1)
    }
    SubShader
    {
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            float4 _Vector;
            float4 vert(float4 vertex : POSITION) : SV_POSITION { return UnityObjectToClipPos(vertex); }
            float4 frag() : SV_Target { return _Vector; }
            ENDCG
        }
    }
}
