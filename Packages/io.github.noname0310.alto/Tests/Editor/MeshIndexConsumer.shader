Shader "Hidden/Noname/Mesh Index Consumer"
{
    SubShader
    {
        Pass
        {
            CGPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            struct Input { float4 vertex : POSITION; uint id : SV_VertexID; uint4 bones : BLENDINDICES; };
            struct Output { float4 vertex : SV_POSITION; float4 color : TEXCOORD0; };
            Output vert(Input v)
            {
                Output o; o.vertex = UnityObjectToClipPos(v.vertex);
                o.color = float4((v.id % 4) * 0.25, (v.bones.x % 4) * 0.25, 0.5, 1); return o;
            }
            float4 frag(Output v) : SV_Target { return v.color; }
            ENDCG
        }
    }
}
