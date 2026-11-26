Shader "Hidden/Noname/ALTO/SharedShaderConstants"
{
    Properties
    {
        _Shared ("Shared", Float) = 0.25
        _Different ("Different", Float) = 0.2
        _Animated ("Animated", Float) = 0.1
        _Included ("Included", Float) = 0.5
        _UnsupportedArray ("Array", Float) = 0
        _UnsupportedBuffer ("Buffer", Float) = 0
        _Writable ("Writable", Float) = 0
        _InputTexture ("Input texture", 2D) = "white" {}
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        CGINCLUDE
        #include "UnityCG.cginc"
        #include "SharedShaderConstants.cginc"
        float _Shared;
        float _Different;
        float _Animated;
        float _UnsupportedArray[2];
        cbuffer ExplicitLayout { float _UnsupportedBuffer; };
        float _Writable;
        float WritableValue() { _Writable += 1; return _Writable; }
        float4 frag0(v2f_img input) : SV_Target
        {
            float different = _Different;
            #if defined(ALTO_TEST_SHARED_ALTERNATE)
                different = 1 - different;
            #endif
            return float4(_Shared, different, _Animated + IncludedValue() * 0.1, 1);
        }
        float4 frag1(v2f_img input) : SV_Target
        { return float4(IncludedValue(), _Shared, _Different, (asuint(_Shared) & 0x80000000u) != 0 ? 1 : 0); }
        ENDCG
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag0
            #pragma multi_compile_local _ ALTO_TEST_SHARED_ALTERNATE
            ENDCG
        }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag1
            ENDCG
        }
    }
    Fallback Off
}
