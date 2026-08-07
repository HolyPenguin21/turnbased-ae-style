// Drifting cloud-SHADOW overlay for the strategic map — a single large flat quad (see
// Game.Map.MapCloudOverlay) rendered above the terrain, with soft blob shapes formed from
// layered value noise (fractal/fbm, same hash/valueNoise building blocks as
// HexSelectionGlow.shader's own ragged-edge noise) that scroll across world space over time.
// Not a texture — fully procedural, so it never tiles/repeats visibly regardless of map size.
//
// Multiplicative blend (Blend DstColor Zero), not the usual alpha-over-transparent — an alpha
// blend with a light colour ADDS a translucent haze on top of the terrain, which reads as fog,
// not shadow. Multiplying the terrain by a value <=1 wherever a cloud shape is present actually
// DARKENS it there and leaves every other pixel untouched (multiplying by white/1.0), which is
// what a shadow drifting over the ground actually looks like. _Color's RGB is the shadow tint
// (dark, not the cloud's own visible colour — you're seeing the ground darkened, never the
// cloud itself), its alpha is the maximum darkening strength at full coverage.
Shader "Custom/CloudDrift"
{
    Properties
    {
        _Color ("Shadow Tint", Color) = (0.15, 0.18, 0.25, 0.45)
        _Scale ("Noise Scale", Range(0.005, 0.5)) = 0.08
        _Coverage ("Coverage", Range(0, 1)) = 0.55
        _Softness ("Edge Softness", Range(0.01, 1)) = 0.25
        _Speed ("Drift Speed", Range(0, 1)) = 0.03
        _Direction ("Drift Direction", Vector) = (1, 0.4, 0, 0)
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }
        Blend DstColor Zero
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
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 worldXZ : TEXCOORD0;
            };

            float4 _Color;
            float _Scale;
            float _Coverage;
            float _Softness;
            float _Speed;
            float4 _Direction;

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                float3 worldPos = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.positionHCS = TransformWorldToHClip(worldPos);
                // World-space XZ, not UV — the noise pattern is evaluated directly in world
                // units so it stays correctly scaled/continuous regardless of how big the quad
                // itself is (see MapCloudOverlay, which sizes it generously to always cover the
                // camera's visible area).
                OUT.worldXZ = worldPos.xz;
                return OUT;
            }

            float hash(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

            float valueNoise(float2 p)
            {
                float2 i = floor(p);
                float2 f = frac(p);
                float a = hash(i);
                float b = hash(i + float2(1, 0));
                float c = hash(i + float2(0, 1));
                float d = hash(i + float2(1, 1));
                float2 u = f * f * (3.0 - 2.0 * f);
                return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
            }

            // 3-octave fractal sum — a single noise layer reads as a flat mottled grid at any
            // one scale; stacking a few progressively finer/fainter layers is what gives cloud
            // shapes their soft, organic (not obviously procedural) silhouette.
            float fbm(float2 p)
            {
                float total = 0.0;
                float amplitude = 0.55;
                [unroll]
                for (int i = 0; i < 3; i++)
                {
                    total += valueNoise(p) * amplitude;
                    p *= 2.05;
                    amplitude *= 0.5;
                }
                return total;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float2 dir = normalize(_Direction.xy + 1e-5);
                float2 drift = dir * _Time.y * _Speed;
                float2 p = IN.worldXZ * _Scale + drift;

                float n = fbm(p);
                // _Coverage picks how much of the sky has cloud in it (higher = more/denser
                // cover), _Softness is purely the edge falloff width, not a second coverage
                // knob — keeps the two independently tunable instead of fighting each other.
                float shape = smoothstep(_Coverage - _Softness, _Coverage + _Softness, n);

                // Multiplicative output: white (1,1,1) where there's no cloud — multiplying the
                // terrain by that leaves it completely unchanged — fading down toward
                // _Color.rgb (the shadow tint) as cloud coverage/darkening strength increases.
                // Alpha is unused by a DstColor/Zero blend, but every path needs SOME alpha
                // written (URP's default target expects RGBA).
                float3 tint = lerp(float3(1, 1, 1), _Color.rgb, shape * _Color.a);
                return half4(tint, 1.0);
            }
            ENDHLSL
        }
    }
}
