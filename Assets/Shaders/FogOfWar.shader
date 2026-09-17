// Content-visibility overlay for the strategic map (see Game.Map.FogOfWarController /
// Game.Map.VisionSystem). The terrain remains readable under fog, but fogged territory must still
// read immediately as a separate visual state: darker, drier and less visually active than the
// visible area. Armies, buildings and resource markers are gated separately in C#.
//
// The boundary follows the true hex geometry, then receives a static world-space erosion offset.
// This produces a broken dry edge without blur or a moving atmospheric haze. Large-scale patina
// breaks up flat areas inside the fog while preserving terrain silhouettes and texture detail.
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

                // Blend alternate neighbours very close to corners so the erosion stays stable
                // when three different visibility states meet at one vertex.
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

                // Broken dry edge: distort the signed hex boundary itself rather than adding a
                // translucent haze after the fact. The noise is world-anchored, therefore the
                // edge never swims when the camera moves. A tiny optional drift remains for
                // backwards compatibility with the existing style value, but is almost static.
                float2 drift = float2(0.7, 0.23) * _Time.y * (_NoiseSpeed * 0.025);
                float erosionNoise = fbm(worldXZ * max(_NoiseScale * 2.4, 0.08) + drift);
                float erosion = (erosionNoise - 0.5)
                    * _OuterRadius
                    * lerp(0.025, 0.16, saturate(_EdgeSoftness));

                float dist = hexSDF(p, _OuterRadius) + erosion;
                float effectiveSharpness = lerp(0.86, 0.985, saturate(_EdgeSharpness));
                float band = lerp(_OuterRadius * 0.11, _OuterRadius * 0.016, effectiveSharpness);
                float edgeBlend = smoothstep(-band, band, dist);
                float fog = lerp(ownFog, neighborFog, edgeBlend);

                // Large, static patina patches make fogged territory read as a coherent mass
                // rather than a flat transparent colour. The range is intentionally noticeable,
                // but does not erase the underlying terrain type.
                float patina = fbm(worldXZ * max(_NoiseScale * 0.62, 0.035) + float2(13.7, -8.1));
                float patinaDensity = lerp(0.78, 1.18, saturate(patina));

                // Optional authored detail remains static in world space and contributes only
                // secondary texture. It never blurs the map or moves independently of terrain.
                float detailA = SAMPLE_TEXTURE2D(
                    _NoiseTex,
                    sampler_NoiseTex,
                    worldXZ * _NoiseTexScale).r;
                float detailB = SAMPLE_TEXTURE2D(
                    _NoiseTex,
                    sampler_NoiseTex,
                    worldXZ * (_NoiseTexScale * 1.71) + float2(17.31, -9.73)).r;
                float detail = saturate(detailA * 0.62 + detailB * 0.38);
                float detailDensity = lerp(0.88, 1.12, detail);

                fog *= patinaDensity;
                fog *= lerp(1.0, detailDensity, saturate(_NoiseTexStrength));
                fog = saturate(fog);

                // The original serialized tint is rather close to the sand colour. Darken it in
                // shader space so existing GameConfig assets immediately produce the intended
                // charcoal/earth shadow without requiring a migration of serialized data.
                float tintVariation = (patina - 0.5) * 0.16;
                float3 finalTint = saturate(_Color.rgb * 0.46 * (1.0 + tintVariation));

                // Strong enough to read at game scale, but still transparent enough to classify
                // dunes, ruins, mountains and ordinary desert through the fog.
                float readableAlpha = min(_Color.a * 0.62, 0.56);
                return half4(finalTint, fog * readableAlpha);
            }
            ENDHLSL
        }
    }
}
