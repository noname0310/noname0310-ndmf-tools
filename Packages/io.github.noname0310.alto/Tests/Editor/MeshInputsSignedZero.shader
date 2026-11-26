Shader "Hidden/Noname/ALTO/MeshInputsSignedZero"
{
    Properties { _Zero ("Zero", Float) = 0 }
    SubShader
    {
        Pass
        {
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            float _Zero;
            struct Input { float4 position : POSITION; uint id : SV_VertexID; };
            float4 vert(Input input) : SV_POSITION
            {
                if (asuint(_Zero) != 0u) input.position.x += (input.id % 2) * 0.01;
                return input.position;
            }
            float4 frag() : SV_Target { return 1; }
            ENDHLSL
        }
    }
    Fallback Off
}
