// Content-visibility overlay for the strategic map (see Game.Map.FogOfWarController /
// Game.Map.VisionSystem). The overlay deliberately keeps terrain readable: fog-of-war marks a
// visibility STATE, it does not paint an opaque atmospheric layer over the board. Armies,
// buildings and resource markers are gated separately in C#; this shader only changes how the
// underlying terrain is visually de-emphasised.
//
// The boundary still follows the true hex geometry. worldToAxialRounded finds the owning hex,
// hexSDF provides the geometric distance to its border, and neighbouring mask values determine
// which side is visible/fogged. Low-frequency world-space noise only breaks the otherwise sterile
// edge and adds a very small static patina across fogged ground. There is intentionally no blur,
// screen-space haze, or strong moving texture: terrain silhouettes and texture features must stay
// sharp enough to identify the hex type at a glance.
Shader "Custom/FogOfWar"
{
    Properties
    {
        _Color ("Fog Tint", Color) = (0.32, 0.24, 0.14, 0.88)
        _EdgeSoftness ("Edge Irregularity", Range(0, 1)) = 0.18
        _EdgeSharpness ("Edge Sharpness", Range(0, 1)) = 0.9
        _NoiseScale ("Patina Noise Scale", Range(0.01, 1)) = 0.10
        _NoiseSpeed ("Edge Drift Speed", Range(0, 1)) = 0.0
        _NoiseTex ("Detail Texture (optional)", 2D) = "white" {}
        _NoiseTexScale ("Detail Texture Scale", Range(0.001, 1)) = 0.05
        _NoiseTexStrength ("Detail Texture Strength", Range(0, 1)) = 0
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
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 worldXZ : TEXCOORD0;
            };

            float4 _Color;
            float _EdgeSoftness;
            float _EdgeSharpness;
            float _NoiseScale;
            float _NoiseSpeed;
            TEXTURE2D(_NoiseTex);
            SAMPLER(sampler_NoiseTex);
            float _NoiseTexScale;
            float _NoiseTexStrength;

            TEXTURE2D(_VisibilityMask);
            SAMPLER(sampler_VisibilityMask);
            float _OuterRadius;
            float2 _MaskMinQR;
            float2 _MaskSize;

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.worldXZ = IN.positionOS.xz;
                return OUT;
            }

            float2 worldToAxialRounded(float2 worldXZ, float outerRadius)
            {
                float q = worldXZ.x / (1.5 * outerRadius);
                float r = worldXZ.y / (sqrt(3.0) * outerRadius) - q * 0.5;
                float s = -q - r;

                float rq = round(q);
                float rr = round(r);
                float rs = round(s);

                float qDiff = abs(rq - q);
                float rDiff = abs(rr - r);
                float sDiff = abs(rs - s);

                if (qDiff > rDiff && qDiff > sDiff)
                    rq = -rr - rs;
                else if (rDiff > sDiff)
                    rr = -rq - rs;

                return float2(rq, rr);
            }

            float2 axialToWorld(float2 qr, float outerRadius)
            {
                float x = outerRadius * 1.5 * qr.x;
                float z = outerRadius * sqrt(3.0) * (qr.y + qr.x * 0.5);
                return float2(x, z);
            }

            float hexSDF(float2 p, float r)
            {
                const float3 k = float3(-0.8660254, 0.5, 0.5773503);
                p = abs(p);
                p -= 2.0 * min(dot(k.xy, p), 0.0) * k.xy;
                p -= float2(clamp(p.x, -k.z * r, k.z * r), r);
                return length(p) * sign(p.y);
            }

            static const float2 kNeighborDirs[6] = {
                float2(1, 0), float2(0, 1), float2(-1, 1),
                float2(-1, 0), float2(0, -1), float2(1, -1)
            };

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

            float sampleHexFog(float2 qr)
            {
                float2 uv = (qr - _MaskMinQR + 0.5) / _MaskSize;
                return 1.0 - SAMPLE_TEXTURE2D(_VisibilityMask, sampler_VisibilityMask, uv).r;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float2 worldXZ = IN.worldXZ;
                float2 qr = worldToAxialRounded(worldXZ, _OuterRadius);
                float2 center = axialToWorld(qr, _OuterRadius);
                float2 p = worldXZ - center;

                float ownFog = sampleHexFog(qr);

                float angle = atan2(p.y, p.x);
                if (angle < 0.0)
                    angle += 2.0 * PI;
                float rawEdgeIndex = angle / (PI / 3.0);
                int edgeIdx = (int) floor(rawEdgeIndex) % 6;
                float withinEdge = frac(rawEdgeIndex);

                float neighborFog = sampleHexFog(qr + kNeighborDirs[edgeIdx]);

                const float cornerBlend = 0.08;
                float cornerProximity = smoothstep(_OuterRadius * 0.82, _OuterRadius * 0.98, length(p));
                if (withinEdge < cornerBlend)
                {
                    float altFog = sampleHexFog(qr + kNeighborDirs[(edgeIdx + 5) % 6]);
                    float leftCornerWeight = (1.0 - withinEdge / cornerBlend) * cornerProximity;
                    neighborFog = lerp(neighborFog, altFog, leftCornerWeight);
                }
                else if (withinEdge > 1.0 - cornerBlend)
                {
                    float altFog = sampleHexFog(qr + kNeighborDirs[(edgeIdx + 1) % 6]);
                    float rightCornerWeight = ((withinEdge - (1.0 - cornerBlend)) / cornerBlend) * cornerProximity;
                    neighborFog = lerp(neighborFog, altFog, rightCornerWeight);
                }

                // Keep the transition narrow enough that the terrain texture remains crisp. The
                // existing project style may still carry older, softer values, so remap the
                // effective sharpness into a deliberately tighter range instead of letting a
                // legacy value turn the seam back into a broad haze.
                float effectiveSharpness = lerp(0.82, 0.97, saturate(_EdgeSharpness));
                float dist = hexSDF(p, _OuterRadius);
                float band = lerp(_OuterRadius * 0.18, _OuterRadius * 0.025, effectiveSharpness);
                float blend = smoothstep(-band, band, dist);
                float fog = lerp(ownFog, neighborFog, blend);

                // Organic edge breakup, but intentionally restrained. Even an older style asset
                // with edgeSoftness=1 now produces only a small boundary perturbation and cannot
                // wash detail out across the whole cell.
                float2 drift = float2(1.0, 0.4) * _Time.y * (_NoiseSpeed * 0.12);
                float edgeNoise = fbm(worldXZ * _NoiseScale + drift);
                float edgeFactor = 1.0 - abs(blend * 2.0 - 1.0);
                fog = saturate(fog + (edgeNoise - 0.5) * (_EdgeSoftness * 0.18) * edgeFactor);

                // Static, world-anchored low-frequency patina breaks up large uniform fogged
                // regions without behaving like weather or a lens effect. Its amplitude is
                // intentionally tiny: terrain type and local texture remain the dominant signal.
                float patina = fbm(worldXZ * max(_NoiseScale * 0.55, 0.025));
                float patinaDensity = lerp(0.94, 1.06, saturate(patina));

                // Optional authored texture is also static in world space. The old implementation
                // slid two samples over the map and strongly modulated both opacity and colour;
                // here it only contributes a few percent of dry/grimy variation.
                float detailA = SAMPLE_TEXTURE2D(
                    _NoiseTex,
                    sampler_NoiseTex,
                    worldXZ * _NoiseTexScale).r;
                float detailB = SAMPLE_TEXTURE2D(
                    _NoiseTex,
                    sampler_NoiseTex,
                    worldXZ * (_NoiseTexScale * 1.73) + float2(17.31, -9.73)).r;
                float detail = saturate(detailA * 0.62 + detailB * 0.38);
                float detailDensity = lerp(0.96, 1.04, detail);

                fog *= patinaDensity;
                fog *= lerp(1.0, detailDensity, _NoiseTexStrength);
                fog = saturate(fog);

                // Keep the tint close to a single dry earth/charcoal wash. Tiny value variation
                // gives the fog some material character without creating bright streaks that
                // compete with resource markers or the terrain art.
                float tintVariation = (patina - 0.5) * 0.08;
                float3 finalTint = saturate(_Color.rgb * (1.0 + tintVariation));

                // Most important readability rule: cap the effective overlay opacity. Existing
                // serialized styles currently use alpha close to 0.9; scaling it here keeps
                // roughly two thirds of the underlying terrain contribution visible while still
                // making the visibility state immediately obvious.
                float readableAlpha = min(_Color.a * 0.40, 0.38);
                return half4(finalTint, fog * readableAlpha);
            }
            ENDHLSL
        }
    }
}
