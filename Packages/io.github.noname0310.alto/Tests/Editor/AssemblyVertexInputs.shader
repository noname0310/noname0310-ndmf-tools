Shader "Hidden/Noname/Assembly Vertex Inputs"
{
    Properties { _UvProbe ("UV Component Probe", Float) = 0 }
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
            float _UvProbe;
            struct Input { float4 vertex : POSITION; float4 color : COLOR; float4 tangent : TANGENT; float4 uv : TEXCOORD1; };
            struct Output { float4 vertex : SV_POSITION; float4 color : TEXCOORD0; };
            Output vert(Input v)
            {
                Output o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.color = float4(v.uv.x, v.color.g, (v.tangent.x + v.tangent.w) * 0.25, 1);
                if (_UvProbe > 0.5) o.color = float4(v.uv.x, v.uv.z, v.uv.w, 1);
                return o;
            }
            float4 frag(Output v) : SV_Target { return v.color; }
            ENDCG
        }
    }
}
