Shader "Hidden/ALTO Tests/Material Contact"
{
    SubShader
    {
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            #include "Packages/com.vrchat.base/ShaderLibrary/VRCContacts.cginc"
            VRC_CONTACT_DECLARE(Touch);
            float4 vert(float4 vertex : POSITION) : SV_POSITION { return UnityObjectToClipPos(vertex); }
            float4 frag() : SV_Target { return float4(VRC_CONTACT_WORLDPOS_SELF(Touch), 1); }
            ENDCG
        }
    }
}
