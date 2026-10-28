Shader "Hidden/Noname/Assembly World Frame"
{
    Properties { _Mode ("Mode", Float) = 1 }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Pass
        {
            Cull Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            float _Mode;
            struct Input { float4 vertex : POSITION; float3 normal : NORMAL; float4 tangent : TANGENT; };
            struct Output { float4 vertex : SV_POSITION; float3 color : TEXCOORD0; };
            Output vert(Input v)
            {
                Output o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                float3 normal = UnityObjectToWorldNormal(v.normal);
                float3 tangent = UnityObjectToWorldDir(v.tangent.xyz);
                float3 bitangent = cross(normal, tangent) * (v.tangent.w * unity_WorldTransformParams.w);
                float3 value = _Mode < 1.5 ? normal : _Mode < 2.5 ? tangent : bitangent;
                o.color = normalize(value) * 0.4 + 0.5;
                return o;
            }
            float4 frag(Output v) : SV_Target { return float4(v.color, 1); }
            ENDCG
        }
    }
}
