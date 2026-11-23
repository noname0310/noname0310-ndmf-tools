// Shader "Keep this comment" { Properties { }
Shader "Hidden/Noname/Vertex ID Preservation"
{
    Properties { _Unused ("Properties Shader text", Float) = 0 }
    SubShader
    {
        Pass
        {
            Cull Off
            CGPROGRAM
            /* An include in a comment is not a dependency.
            #include "This comment must not be resolved.cginc"
            */
            #pragma target 4.0
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            struct Varyings { float4 position : SV_POSITION; float value : TEXCOORD0; };
            Varyings vert(float4 position : POSITION, uint vertexId : SV_VertexID)
            {
                Varyings output;
                output.position = UnityObjectToClipPos(position);
                output.value = (vertexId % 17u + 1u) / 18.0;
                return output;
            }
            float4 frag(Varyings input) : SV_Target { return float4(input.value, 0.25, 0.5, 1); }
            ENDCG
        }
    }
}
