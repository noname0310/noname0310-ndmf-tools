Shader "Hidden/Noname/Tests/OcclusionUsePass"
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
        UsePass "Hidden/Noname/Tests/OcclusionPolicy/SURFACE"
        UsePass "Hidden/Noname/Tests/OcclusionPolicy/SHADOW"
    }
}
