Shader "Hidden/Noname/ALTO/MeshInputsPartialProof"
{
    Properties
    {
        _FirstGate ("First gate", Float) = 0
        _LastGate ("Last gate", Float) = 1
        _Unrelated ("Unrelated output", Float) = 0
    }
    SubShader
    {
        Pass
        {
            Name "FIRST"
            Tags { "LightMode" = "Always" }
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            float _FirstGate;
            float _Unrelated;
            float InputEffect(float value, float gate) { return gate == 0 ? 0 : value * gate; }
            float4 vert(float4 p : POSITION, uint id : SV_VertexID) : SV_POSITION
            {
                p.x += InputEffect(id, _FirstGate);
                return p;
            }
            float4 frag() : SV_Target { return float4(_Unrelated, 0, 0, 1); }
            ENDHLSL
        }
        Pass
        {
            Name "LAST"
            Tags { "LightMode" = "Always" }
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            float _LastGate;
            float _Unrelated;
            float InputEffect(float value, float gate) { return gate == 0 ? 0 : value * gate; }
            float4 vert(float4 p : POSITION, uint id : SV_VertexID) : SV_POSITION
            {
                p.x += InputEffect(id, _LastGate);
                return p;
            }
            float4 frag() : SV_Target { return float4(0, _Unrelated, 0, 1); }
            ENDHLSL
        }
    }
    Fallback Off
}
