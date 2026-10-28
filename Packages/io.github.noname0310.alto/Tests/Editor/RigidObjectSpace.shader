Shader "Hidden/Noname/Rigid Object Space"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" "DisableBatching"="True" }
        Pass
        {
            Cull Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            struct Input { float4 vertex : POSITION; };
            struct Output { float4 vertex : SV_POSITION; float3 color : TEXCOORD0; };
            Output vert(Input v)
            {
                Output o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.color = v.vertex.xyz * 0.15 + unity_ObjectToWorld._m03_m13_m23 * 0.2
                    + unity_ObjectToWorld._m00_m10_m20 * 0.2 + 0.3;
                return o;
            }
            float4 frag(Output v) : SV_Target { return float4(v.color, 1); }
            ENDCG
        }
    }
}
