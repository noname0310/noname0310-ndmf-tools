Shader "Hidden/Noname/OcclusionPreview"
{
    Properties { _Color ("Color", Color) = (1,1,1,1) }
    SubShader
    {
        Cull Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            float4 _Color;
            struct Varyings { float4 position : SV_POSITION; float3 world : TEXCOORD0; };
            Varyings vert(float4 vertex : POSITION)
            {
                Varyings o;
                o.position = UnityObjectToClipPos(vertex);
                o.world = mul(unity_ObjectToWorld, vertex).xyz;
                return o;
            }
            float4 frag(Varyings i) : SV_Target
            {
                float3 normal = normalize(cross(ddx(i.world), ddy(i.world)));
                return float4(_Color.rgb * (0.4 + 0.6 * abs(dot(normal, normalize(float3(0.5,1,0.3))))), 1);
            }
            ENDCG
        }
    }
}
