// Total War-style movement arrow: a flat ribbon, one flat colour across the whole shape,
// with per-vertex alpha (baked by MoveArrowMarker from distance along the curve) giving the
// tip -> tail longitudinal fade. Geometry (MoveArrowMarker) is what gives it its actual
// silhouette — this shader is deliberately just a solid unlit fill modulated by vertex alpha.
Shader "Custom/MoveArrowGlow"
{
    Properties
    {
        _Color("Color", Color) = (0.8, 0.25, 0.25, 0.9)
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float4 color : COLOR;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float4 color : COLOR;
            };

            float4 _Color;

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.color = IN.color;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                return half4(_Color.rgb, _Color.a * IN.color.a);
            }
            ENDHLSL
        }
    }
}
