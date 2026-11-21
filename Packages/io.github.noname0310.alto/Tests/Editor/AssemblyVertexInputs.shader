Shader "Hidden/Noname/Assembly Vertex Inputs"
{
    Properties
    {
        _UvProbe ("UV Component Probe", Float) = 0
        _Color ("Material Color Probe", Color) = (1,1,1,1)
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Pass
        {
            Cull Off
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            // Keep this vertex-buffer fixture independent of unused stereo
            // semantic macros in UnityCG and shader variant-analysis budgets.
            float4x4 unity_ObjectToWorld;
            float4x4 unity_MatrixVP;
            float _UvProbe;
            struct Input { float4 vertex : POSITION; float4 color : COLOR; float4 tangent : TANGENT; float4 uv : TEXCOORD1; };
            struct Output { float4 vertex : SV_POSITION; float4 color : TEXCOORD0; };
            Output vert(Input v)
            {
                Output o;
                o.vertex = mul(unity_MatrixVP, mul(unity_ObjectToWorld, float4(v.vertex.xyz, 1)));
                o.color = float4(v.uv.x, v.color.g, (v.tangent.x + v.tangent.w) * 0.25, 1);
                if (_UvProbe > 0.5) o.color = float4(v.uv.x, v.uv.z, v.uv.w, 1);
                return o;
            }
            float4 frag(Output v) : SV_Target { return v.color; }
            ENDHLSL
        }
    }
}
