// Strategic-map visibility overlay. Terrain stays readable beneath fog, while hidden territory
// becomes a darker, drier material state with a visibly eroded boundary. Gameplay visibility
// remains driven by VisionSystem/FogOfWarController; this shader changes presentation only.
Shader "Custom/FogOfWar"
{
    Properties
    {
        _Color ("Fog Tint", Color) = (0.32, 0.24, 0.14, 0.88)
        _EdgeSoftness ("Edge Irregularity", Range(0, 1)) = 0.42
        _EdgeSharpness ("Edge Sharpness", Range(0, 1)) = 0.92
        _NoiseScale ("Patina Noise Scale", Range(0.01, 1)) = 0.10
        _NoiseSpeed ("Edge Drift Speed", Range(0, 1)) = 0.0
        _NoiseTex ("Detail Texture (optional)", 2D) = "white" {}
        _NoiseTexScale ("Detail Texture Scale", Range(0.001, 1)) = 0.11
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

            float hash21(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

            float valueNoise(float2 p)
            {
                float2 i = floor(p);
                float2 f = frac(p);
                float a = hash21(i);
                float b = hash21(i + float2(1, 0));
                float c = hash21(i + float2(0, 1));
                float d = hash21(i + float2(1, 1));
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

                // Pick the neighbour whose centre lies behind the nearest side. The previous code
                // divided angle into sectors starting at 0 degrees, which is half a sector off for
                // a flat-top axial hex and often sampled the wrong neighbour. That made the reveal
                // contour fall back to a clean geometric hex instead of receiving erosion.
                float angle = atan2(p.y, p.x);
                if (angle < 0.0)
                    angle += 2.0 * PI;
                float rawEdgeIndex = (angle + PI / 6.0) / (PI / 3.0);
                int edgeIdx = (int) floor(rawEdgeIndex) % 6;
                float withinSector = frac(rawEdgeIndex);
                float neighborFog = sampleHexFog(qr + kNeighborDirs[edgeIdx]);

                // At hex vertices two neighbours are equally valid. Blend the alternate sample in
                // a very narrow corner region to prevent a hard visibility pin at the vertex.
                const float cornerBlend = 0.09;
                float cornerProximity = smoothstep(_OuterRadius * 0.80, _OuterRadius * 0.99, length(p));
                if (withinSector < cornerBlend)
                {
                    float altFog = sampleHexFog(qr + kNeighborDirs[(edgeIdx + 5) % 6]);
                    float weight = (1.0 - withinSector / cornerBlend) * cornerProximity;
                    neighborFog = lerp(neighborFog, altFog, weight);
                }
                else if (withinSector > 1.0 - cornerBlend)
                {
                    float altFog = sampleHexFog(qr + kNeighborDirs[(edgeIdx + 1) % 6]);
                    float weight = ((withinSector - (1.0 - cornerBlend)) / cornerBlend) * cornerProximity;
                    neighborFog = lerp(neighborFog, altFog, weight);
                }

                // High-frequency world-anchored erosion. The old frequency was so low that each
                // side received almost one constant offset and still looked ruler-straight. This
                // varies several times along one hex side, creating a dry torn edge without blur.
                float edgeFrequency = max(_NoiseScale * 26.0, 2.4);
                float2 drift = float2(0.7, 0.23) * _Time.y * (_NoiseSpeed * 0.006);
                float edgeCoarse = fbm(worldXZ * edgeFrequency + drift + float2(4.7, -2.9));
                float edgeFine = valueNoise(worldXZ * (edgeFrequency * 2.7) + float2(-11.3, 6.4));
                float edgeSignal = saturate(edgeCoarse * 0.72 + edgeFine * 0.28);
                float erosion = (edgeSignal - 0.50)
                    * _OuterRadius
                    * lerp(0.08, 0.24, saturate(_EdgeSoftness));

                float dist = hexSDF(p, _OuterRadius) + erosion;
                float effectiveSharpness = lerp(0.92, 0.992, saturate(_EdgeSharpness));
                float band = lerp(_OuterRadius * 0.050, _OuterRadius * 0.008, effectiveSharpness);
                float edgeBlend = smoothstep(-band, band, dist);
                float fog = lerp(ownFog, neighborFog, edgeBlend);

                // Procedural dry material inside fog. Three scales are mixed on purpose: broad
                // stains give mass, medium breakup creates readable mottling, and fine grain keeps
                // the overlay from collapsing into a single flat colour at gameplay zoom.
                float coarsePatina = fbm(worldXZ * max(_NoiseScale * 5.2, 0.52) + float2(13.7, -8.1));
                float midPatina = fbm(worldXZ * max(_NoiseScale * 21.0, 2.10) + float2(-5.4, 19.2));
                float finePatina = valueNoise(worldXZ * max(_NoiseScale * 73.0, 7.30) + float2(31.1, -17.6));
                float materialNoise = saturate(coarsePatina * 0.48 + midPatina * 0.38 + finePatina * 0.14);

                // Irregular thin patches reveal slightly more terrain underneath, similar to dry
                // abrasion/dust loss rather than translucent cloudy smoke.
                float abrasionNoise = fbm(worldXZ * max(_NoiseScale * 34.0, 3.40) + float2(2.3, 27.8));
                float abrasion = smoothstep(0.58, 0.82, abrasionNoise);

                float density = lerp(0.66, 1.20, materialNoise);
                density *= lerp(1.0, 0.78, abrasion);

                // Optional authored detail remains secondary. A default white texture changes the
                // density only minimally, so procedural breakup is always present even when the
                // GameConfig has no detail texture assigned.
                float detailA = SAMPLE_TEXTURE2D(_NoiseTex, sampler_NoiseTex, worldXZ * _NoiseTexScale).r;
                float detailB = SAMPLE_TEXTURE2D(
                    _NoiseTex,
                    sampler_NoiseTex,
                    worldXZ * (_NoiseTexScale * 1.73) + float2(17.31, -9.73)).r;
                float detail = saturate(detailA * 0.60 + detailB * 0.40);
                float detailDensity = lerp(0.94, 1.06, detail);
                density *= lerp(1.0, detailDensity, saturate(_NoiseTexStrength));

                fog = saturate(fog * density);

                // Keep a warm charcoal/earth tint but vary it enough that the fog itself has a
                // visible material texture. Terrain classification still comes from the original
                // map below; this overlay never replaces it with an opaque texture.
                float3 baseTint = lerp(_Color.rgb * 0.34, float3(0.052, 0.039, 0.028), 0.34);
                float tintVariation = (materialNoise - 0.5) * 0.30 - abrasion * 0.05;
                float3 finalTint = saturate(baseTint * (1.0 + tintVariation));

                // Roughly half the source terrain remains visible even in the densest fog; local
                // density variation then creates the promised dry/broken texture on top of it.
                float readableAlpha = min(_Color.a * 0.62, 0.54);
                return half4(finalTint, fog * readableAlpha);
            }
            ENDHLSL
        }
    }
}
