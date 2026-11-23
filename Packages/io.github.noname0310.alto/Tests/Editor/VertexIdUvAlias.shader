Shader "Hidden/Noname/Vertex ID UV Alias"
{
    SubShader
    {
        Pass
        {
            Cull Off
            CGPROGRAM
            #pragma target 4.0
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            struct Varyings { float4 position : SV_POSITION; float2 value : TEXCOORD0; };
            Varyings vert(float4 position : POSITION, uint vertexId : SV_VertexID, float2 uv : TEXCOORD7)
            {
                Varyings output;
                output.position = UnityObjectToClipPos(position);
                output.value = float2((vertexId % 17u + 1u) / 18.0, uv.x);
                return output;
            }
            float4 frag(Varyings input) : SV_Target { return float4(input.value, 0.5, 1); }
            ENDCG
        }
    }
}
