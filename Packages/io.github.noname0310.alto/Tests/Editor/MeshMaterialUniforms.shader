Shader "Hidden/Noname/Material Uniform Proof"
{
    Properties
    {
        _Gate ("Gate", Float) = 0
        _Target ("Target", Float) = 0
        _Self ("Self", Float) = 0
        _GlobalTarget ("Global target", Float) = 0
        _Cull ("Cull", Float) = 0
    }
    SubShader
    {
        Cull [_Cull]
        Pass
        {
            HLSLPROGRAM
            #pragma target 4.0
            #pragma vertex vert
            #pragma fragment frag
            #pragma shader_feature_local __ ALTO_FORCE_TARGET
            #pragma multi_compile __ ALTO_GLOBAL_READ
            float _Gate;
            float _Target;
            float _Self;
            float _GlobalTarget;
            float _HelperTarget;
            float HelperGate()
            {
                #if defined(ALTO_GLOBAL_READ)
                return 1;
                #else
                return 0;
                #endif
            }
            float4 vert(float4 position : POSITION) : SV_POSITION { return position; }
            float4 frag() : SV_Target
            {
                float value = 0.25;
                if (_Gate > 0) value += _Target;
                if (_Self > 0) value += _Self;
                if (HelperGate() > 0) value += _HelperTarget;
                #if defined(ALTO_FORCE_TARGET)
                value += _Target;
                #endif
                #if defined(ALTO_GLOBAL_READ)
                value += _GlobalTarget;
                #endif
                return float4(value, value, value, 1);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
