Shader "Hidden/Noname/ALTO/ConstantMaterialOutputs"
{
    Properties
    {
        _Scalar ("Scalar", Float) = 0.8
        _Vector ("Vector", Vector) = (0.8, 0.7, 0.6, 1)
        _Color ("Color", Color) = (0.8, 0.7, 0.6, 1)
        _MainTex ("Texture", 2D) = "white" {}
        _Output ("Output", Float) = 0
    }
    SubShader
    {
        Pass
        {
            Cull Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            float _Scalar, _Output;
            float4 _Vector, _Color, _MainTex_ST;
            float4 vert(float4 vertex : POSITION) : SV_POSITION { return UnityObjectToClipPos(vertex); }
            float4 frag() : SV_Target
            {
                if (_Output == 1) return _Vector;
                if (_Output == 2) return _Color;
                if (_Output == 3) return _MainTex_ST;
                return float4(_Scalar, _Scalar, _Scalar, 1);
            }
            ENDCG
        }
    }
}
