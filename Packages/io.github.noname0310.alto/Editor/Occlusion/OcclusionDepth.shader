Shader "Hidden/Noname/Occlusion Depth"
{
    Properties { _Cull ("Cull", Float) = 2 }
    SubShader
    {
        Pass
        {
            Cull [_Cull] ZWrite Off ZTest Always
            Blend One One BlendOp Min ColorMask R
            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            float4x4 _ClipFromWorld, _ViewFromWorld;
            struct Input { float4 vertex : POSITION; };
            struct Output { float4 vertex : SV_POSITION; float depth : TEXCOORD0; };
            Output vert(Input input)
            {
                Output output;
                output.vertex = mul(_ClipFromWorld, input.vertex);
                output.depth = -mul(_ViewFromWorld, input.vertex).z;
                return output;
            }
            float frag(Output input) : SV_Target { return input.depth; }
            ENDCG
        }
    }
}
