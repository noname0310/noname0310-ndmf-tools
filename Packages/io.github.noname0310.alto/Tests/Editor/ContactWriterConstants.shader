Shader "Hidden/Noname/ALTO/ContactWriterConstants"
{
    Properties
    {
        _VRChatContact_Data_Touch ("Contact data", Float) = 0.2
        _Unrelated ("Unrelated", Float) = 0.3
    }
    SubShader
    {
        Pass
        {
            Cull Off ZWrite Off ZTest Always
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float _VRChatContact_Data_Touch;
            float _Unrelated;
            float4 vert(float4 position : POSITION) : SV_POSITION { return position; }
            float4 frag() : SV_Target { return float4(_VRChatContact_Data_Touch, _Unrelated, 0, 1); }
            ENDCG
        }
    }
    Fallback Off
}
