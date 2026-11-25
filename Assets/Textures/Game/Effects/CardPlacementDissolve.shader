Shader "Custom/CardPlacementDissolve"
{
    Properties
    {
        [PerRendererData] _MainTex ("Card Art", 2D) = "white" {}
        _ArtUVRect ("Art UV Origin and Size", Vector) = (0, 0, 1, 1)
        _Tint ("Tint", Color) = (1, 1, 1, 1)
        [HideInInspector] _ZTest ("Depth Test", Float) = 4
        _Progress ("Dissolve Progress", Range(0, 1)) = 0
        _DustColor ("Dust Edge Tint", Color) = (0.48, 0.30, 0.15, 0.8)
    }
    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "CanUseSpriteAtlas" = "True"
        }
        Blend SrcAlpha OneMinusSrcAlpha
        ZTest [_ZTest]
        ZWrite Off
        Cull Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            CBUFFER_START(UnityPerMaterial)
                float4 _ArtUVRect;
                float4 _Tint;
                float4 _DustColor;
                float _Progress;
            CBUFFER_END

            struct Attributes
            {
                float3 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 artUV : TEXCOORD0;
                float2 cardUV : TEXCOORD1;
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                output.positionHCS = TransformObjectToHClip(input.positionOS);
                output.artUV = input.uv;
                output.cardUV = (input.uv - _ArtUVRect.xy) / _ArtUVRect.zw;
                return output;
            }

            float Hash(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

            float ValueNoise(float2 p)
            {
                float2 cell = floor(p);
                float2 blend = frac(p);
                blend = blend * blend * (3.0 - 2.0 * blend);
                return lerp(
                    lerp(Hash(cell), Hash(cell + float2(1, 0)), blend.x),
                    lerp(Hash(cell + float2(0, 1)), Hash(cell + 1.0), blend.x),
                    blend.y);
            }

            half4 frag(Varyings input) : SV_Target
            {
                const float featherWidth = 0.075;
                const float dustWidth = 0.06;
                float2 p = input.cardUV * float2(5.0, 7.0);
                float broad = ValueNoise(p + float2(9.2, 3.7));
                float detail = ValueNoise(p * 3.4 + float2(17.1, 5.8));
                float diagonal = dot(input.cardUV, float2(0.72, 0.28));
                float erosion = saturate(diagonal + (broad - 0.5) * 0.46 + (detail - 0.5) * 0.14);
                float front = lerp(-featherWidth - dustWidth, 1.0, saturate(_Progress));
                float distanceToFront = erosion - front;
                float coverage = smoothstep(0.0, featherWidth, distanceToFront);
                float edge = 1.0 - smoothstep(featherWidth, featherWidth + dustWidth, distanceToFront);

                half4 card = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.artUV) * _Tint;
                card.rgb = lerp(card.rgb, _DustColor.rgb, edge * _DustColor.a);
                card.a *= coverage;
                return card;
            }
            ENDHLSL
        }
    }
}
