// Content-visibility overlay for the strategic map (see Game.Map.FogOfWarController /
// Game.Map.VisionSystem). Terrain remains readable under fog, but hidden territory must read
// immediately as a separate, darker visual state. Armies/buildings/resources are gated in C#.
//
// The overlay can be coplanar with generated terrain, so this pass deliberately ignores depth.
// Its alpha is zero on visible cells and map content in fog is already hidden separately; this
// removes the depth/z-fighting failure mode without changing gameplay visibility.
Shader "Custom/FogOfWar"
{
    Properties
    {
        _Color ("Fog Tint", Color) = (0.32, 0.24, 0.14, 0.88)
        _EdgeSoftness ("Edge Irregularity", Range(0, 1)) = 0.35
        _EdgeSharpness ("Edge Sharpness", Range(0, 1)) = 0.92
        _NoiseScale ("Patina Noise Scale", Range(0.01, 1)) = 0.10
        _NoiseSpeed ("Edge Drift Speed", Range(0, 1)) = 0.0
        _NoiseTex ("Detail Texture (optional)", 2D) = "white" {}
        _NoiseTexScale ("Detail Texture Scale", Range(0.001, 1)) = 0.05
        _NoiseTexStrength ("Detail Texture Strength", Range(0, 1)) = 0.45
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        ZTest Always
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
                for (int i = 0; i < 4; i++)
                {
                    total += valueNoise(p) * amplitude;
                    p *= 2.03;
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
                    float weight = (1.0 - withinEdge / cornerBlend) * cornerProximity;
                    neighborFog = lerp(neighborFog, altFog, weight);
                }
                else if (withinEdge > 1.0 - cornerBlend)
                {
                    float altFog = sampleHexFog(qr + kNeighborDirs[(edgeIdx + 1) % 6]);
                    float weight = ((withinEdge - (1.0 - cornerBlend)) / cornerBlend) * cornerProximity;
                    neighborFog = lerp(neighborFog, altFog, weight);
                }

                // Erode the actual visibility boundary. Serialized projects may still have the
                // older edgeSoftness=1 value, so the useful range is bounded here instead of
                // allowing the seam to become a broad translucent haze.
                float2 drift = float2(0.7, 0.23) * _Time.y * (_NoiseSpeed * 0.01);
                float erosionNoise = fbm(worldXZ * max(_NoiseScale * 2.8, 0.10) + drift);
                float erosion = (erosionNoise - 0.5)
                    * _OuterRadius
                    * lerp(0.05, 0.19, saturate(_EdgeSoftness));

                float dist = hexSDF(p, _OuterRadius) + erosion;
                float effectiveSharpness = lerp(0.90, 0.988, saturate(_EdgeSharpness));
                float band = lerp(_OuterRadius * 0.085, _OuterRadius * 0.014, effectiveSharpness);
                float edgeBlend = smoothstep(-band, band, dist);
                float fog = lerp(ownFog, neighborFog, edgeBlend);

                // Large-scale, static breakup. This is intentionally stronger than the first
                // implementation: at normal camera scale it must be visible as dry material
                // variation rather than disappearing into the source terrain texture.
                float patina = fbm(worldXZ * max(_NoiseScale * 0.70, 0.045) + float2(13.7, -8.1));
                float patinaDensity = lerp(0.72, 1.24, saturate(patina));

                float detailA = SAMPLE_TEXTURE2D(
                    _NoiseTex,
                    sampler_NoiseTex,
                    worldXZ * _NoiseTexScale).r;
                float detailB = SAMPLE_TEXTURE2D(
                    _NoiseTex,
                    sampler_NoiseTex,
                    worldXZ * (_NoiseTexScale * 1.71) + float2(17.31, -9.73)).r;
                float detail = saturate(detailA * 0.62 + detailB * 0.38);
                float detailDensity = lerp(0.84, 1.16, detail);

                fog *= patinaDensity;
                fog *= lerp(1.0, detailDensity, saturate(_NoiseTexStrength));
                fog = saturate(fog);

                // Existing GameConfig assets are already serialized with a warm sand-brown tint,
                // so code defaults in FogOfWarStyle do not migrate them. Convert that authored
                // tint into the darker earth/charcoal treatment here, where it applies reliably
                // to both old and new serialized configs.
                float tintVariation = (patina - 0.5) * 0.18;
                float3 baseTint = lerp(_Color.rgb * 0.30, float3(0.045, 0.034, 0.024), 0.30);
                float3 finalTint = saturate(baseTint * (1.0 + tintVariation));

                // About 38% of the original terrain remains at full fog. Dunes, ruins and
                // mountains stay classifiable, while fogged territory now reads immediately.
                float readableAlpha = min(_Color.a * 0.72, 0.62);
                return half4(finalTint, fog * readableAlpha);
            }
            ENDHLSL
        }
    }
}
