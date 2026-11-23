Shader "Hidden/Noname/Independent Material B"
{
    Properties { _OnlyB ("B", Float) = 0 }
    SubShader
    {
        Pass
        {
            Cull Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            #define OPTIONAL_MATERIAL_LIBRARY 0
            #if OPTIONAL_MATERIAL_LIBRARY
                #include "AbsentOptionalMaterialLibrary.cginc"
            #endif
            float _OnlyB;
            float4 vert(float4 position : POSITION) : SV_POSITION { return UnityObjectToClipPos(position); }
            float4 frag() : SV_Target { return float4(0.2, 0.1 + _OnlyB * 0.7, 0.3, 1); }
            ENDCG
        }
    }
}
