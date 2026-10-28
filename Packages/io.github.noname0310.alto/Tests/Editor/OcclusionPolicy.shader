Shader "Hidden/Noname/Tests/OcclusionPolicy"
{
    Properties
    {
        _Cull ("Cull", Float) = 2
        _StencilComp ("Stencil Comparison", Float) = 8
        _StencilPass ("Stencil Pass", Float) = 0
        _ZWrite ("Depth Write", Float) = 1
        _ColorMask ("Color Mask", Float) = 15
        _Color ("Color", Color) = (1,1,1,1)
        _SourceBlend ("Source Blend", Float) = 1
        _DestinationBlend ("Destination Blend", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        Cull [_Cull]
        ZWrite [_ZWrite]
        ColorMask [_ColorMask]
        Stencil { Comp [_StencilComp] Pass [_StencilPass] }
        Pass
        {
            Name "SURFACE"
            Tags { "LightMode" = "ForwardBase" }
            Blend [_SourceBlend] [_DestinationBlend]
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            float4 _Color;
            float4 vert(float4 vertex : POSITION) : SV_POSITION { return UnityObjectToClipPos(vertex); }
            float4 frag() : SV_Target { return _Color; }
            ENDCG
        }
        Pass
        {
            Name "SHADOW"
            Tags { "LightMode" = "ShadowCaster" }
            ColorMask 0
            Cull Off
        }
    }
}
