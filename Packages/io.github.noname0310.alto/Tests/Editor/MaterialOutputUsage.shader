Shader "Hidden/Noname/ALTO/MaterialOutputUsage"
{
    Properties
    {
        _Color ("Color", Color) = (1,1,1,1)
        _DeclaredUnused ("Declared", Float) = 0
    }
    SubShader
    {
        Pass
        {
            Name "TEST"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile __ AOO_ALTERNATE_VARIANT
            #include "UnityCG.cginc"
            #include "MaterialOutputUsage.cginc"
            // _RemovedLightingControl is not a shader input.
            float4 _Color;
            float4 vert(float4 p : POSITION) : SV_POSITION { return UnityObjectToClipPos(p); }
            float4 frag() : SV_Target
            {
                float4 value = _Color + _HiddenUniform + AOO_PROPERTY(_Macro);
                #if defined(AOO_ALTERNATE_VARIANT)
                value += _AlternateUniform;
                #endif
                return value;
            }
            ENDCG
        }
    }
    Fallback Off
}
