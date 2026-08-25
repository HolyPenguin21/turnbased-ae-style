// Content-visibility overlay for the strategic map (see Game.Map.FogOfWarController /
// Game.Map.VisionSystem) — a single flat quad covering the whole hex grid, darkening every hex
// the current viewer (VisionSystem.CurrentViewer) doesn't presently have vision of. Terrain
// itself is never hidden by this, only content (armies/buildings/resource yield, gated
// separately in C# — see HexSelectionController/MapResourceDisplay) — this shader only draws
// the dimming tint, it has no say in what's actually shown/hidden underneath it.
//
// Per the project owner's own call, the boundary reads as a slightly soft drifting haze, not a
// hard binary cutout — but the SEAM itself must trace the true hex edge, not a rounded blob.
// Earlier this sampled the mask with continuous (unrounded) axial coordinates through a
// bilinear-filtered texture, on the assumption that adjacent mask texels being adjacent hexes
// (axial IS a valid skewed lattice) would make that blend read as hex-shaped. It doesn't: a
// bilinear filter's own iso-contours are curved/elliptical near each texel-square's diagonal,
// not hexagonal, which was hidden by the wide _EdgeSoftness haze at first but became an obvious
// wrong-shaped curve once the boundary was sharpened. Fixed the same way HexClusterGlow.shader
// already draws exact hex-shaped edges: worldToAxialRounded (cube-coordinate rounding) finds
// the TRUE owning hex for this pixel (its Voronoi cell, correct even at corners), and hexSDF
// (Inigo Quilez's regular-hexagon distance field) gives the real geometric distance to that
// hex's boundary — see frag()'s own comment for how the blend against the correct neighbour
// hex is picked. fbm noise (same hash/valueNoise building blocks as CloudDrift.shader) still
// roughens the edge on top (_EdgeSoftness, 0 by default in the project's own tuning), and an
// optional hand-picked detail texture (_NoiseTex, left "white" — a no-op — until the project
// owner assigns and tunes one) can add further texture over everything.
Shader "Custom/FogOfWar"
{
    Properties
    {
        _Color ("Fog Tint", Color) = (0.03, 0.04, 0.07, 0.75)
        _EdgeSoftness ("Edge Softness", Range(0, 1)) = 0.35
        _EdgeSharpness ("Edge Sharpness", Range(0, 1)) = 0.8
        _NoiseScale ("Haze Noise Scale", Range(0.01, 1)) = 0.12
        _NoiseSpeed ("Haze Drift Speed", Range(0, 1)) = 0.04
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

            // Set from FogOfWarController via a MaterialPropertyBlock — a plain Texture2D
            // property still needs declaring here (unlike an array, this one CAN sit in the
            // Properties block, but it's omitted there since nothing needs to expose it as an
            // Inspector swatch).
            TEXTURE2D(_VisibilityMask);
            SAMPLER(sampler_VisibilityMask);
            // TRUE hex grid spacing (never scaled) — same role as HexClusterGlow's _OuterRadius.
            float _OuterRadius;
            // Axial coordinate of the mask texture's (0,0) texel, and its (width, height) in
            // texels — together these convert a continuous (q, r) into a mask UV.
            float2 _MaskMinQR;
            float2 _MaskSize;

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                // This object always sits at world identity (see FogOfWarController), same
                // convention as HexClusterHighlight — object space position already is world.
                OUT.worldXZ = IN.positionOS.xz;
                return OUT;
            }

            // Mirrors HexGridMath.WorldToAxial (cube-coordinate rounding) exactly, same as
            // HexClusterGlow.shader's own copy — gives the TRUE owning hex (Voronoi cell) for
            // any world position, correct even right at a corner shared by three hexes.
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

            // Mirrors HexGridMath.AxialToWorld exactly.
            float2 axialToWorld(float2 qr, float outerRadius)
            {
                float x = outerRadius * 1.5 * qr.x;
                float z = outerRadius * sqrt(3.0) * (qr.y + qr.x * 0.5);
                return float2(x, z);
            }

            // Inigo Quilez's regular-hexagon SDF, same copy HexClusterGlow.shader uses — vertex
            // along +X, matching this project's hex corner convention (HexGridMath corners at
            // angle 60*i). Negative inside the hex, 0 exactly on its boundary, positive outside.
            float hexSDF(float2 p, float r)
            {
                const float3 k = float3(-0.8660254, 0.5, 0.5773503);
                p = abs(p);
                p -= 2.0 * min(dot(k.xy, p), 0.0) * k.xy;
                p -= float2(clamp(p.x, -k.z * r, k.z * r), r);
                return length(p) * sign(p.y);
            }

            // Matches HexGridMath.NeighborDirectionsByEdge exactly, same copy HexClusterGlow.
            // shader uses — direction[i] is the neighbour across the edge between corners i and
            // (i+1)%6.
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

            // 3-octave fractal sum — same shape-softening role as CloudDrift.shader's own fbm.
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

            // Point-samples ONE hex's own mask value — 0 visible / 1 fogged — by looking up its
            // exact texel centre. Sampling precisely at a texel centre returns that texel alone
            // even through a Bilinear-filtered texture (the interpolation weight collapses to
            // 1 for the sampled texel, 0 for its neighbours right at that point), so this needs
            // no separate Point-filtered copy of the mask.
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

                // Which of the 6 edges this pixel sits nearest to, within its own hex — same
                // angle-bucket convention as HexClusterGlow.shader's own edge lookup, so
                // kNeighborDirs[edgeIdx] is guaranteed the hex sharing THAT edge.
                float angle = atan2(p.y, p.x);
                if (angle < 0.0)
                    angle += 2.0 * PI;
                float rawEdgeIndex = angle / (PI / 3.0);
                int edgeIdx = (int) floor(rawEdgeIndex) % 6;
                float withinEdge = frac(rawEdgeIndex);

                float neighborFog = sampleHexFog(qr + kNeighborDirs[edgeIdx]);

                // Right near a corner, the OTHER edge meeting there can matter just as much —
                // same fix HexClusterGlow.shader's own outer-boundary tracing needs and for the
                // same reason (see its cornerBlend comment): the 60°-wide angle bucket boundary
                // doesn't line up with which neighbour is actually closest once you're that
                // close to a vertex three hexes share.
                const float cornerBlend = 0.08;
                // The angular corner bucket spans all the way from the hex centre to its
                // vertex. Blending the alternate neighbour from angle alone therefore paints
                // a long triangular wedge through the cell. Gate that blend by radial
                // proximity so the alternate neighbour participates only near the actual
                // shared vertex, while pixels farther inward keep their nearest edge's value.
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

                // True geometric distance to this hex's own boundary (negative inside) — unlike
                // the old bilinear-texture blend, this traces the REAL hex edge, so blending
                // ownFog -> neighborFog against it can't bulge into a rounded, wrong-shaped seam.
                // _EdgeSharpness=0 blends across most of the hex (wide, soft); 1 narrows the band
                // down to a few percent of the hex radius, hugging the true edge tightly.
                float dist = hexSDF(p, _OuterRadius);
                float band = lerp(_OuterRadius * 0.75, _OuterRadius * 0.03, _EdgeSharpness);
                float blend = smoothstep(-band, band, dist);
                float fog = lerp(ownFog, neighborFog, blend);

                float2 dir = float2(1.0, 0.4);
                float2 drift = dir * _Time.y * _NoiseSpeed;
                float haze = fbm(worldXZ * _NoiseScale + drift);

                // Only perturbs near the actual boundary (blend close to 0.5) — deep fog stays
                // fully opaque and clear ground stays fully clean, only the seam between them
                // gets the organic, drifting roughness that reads as haze rather than a hard
                // line or a uniformly noisy wash over everything.
                float edgeFactor = 1.0 - abs(blend * 2.0 - 1.0);
                fog = saturate(fog + (haze - 0.5) * _EdgeSoftness * edgeFactor);

                // Two differently-scaled, differently-directed samples keep the repeated dust
                // texture from reading as one flat image sliding over the board. The density
                // modulation stays centred close to 1, so terrain remains readable instead of
                // opening transparent holes in the fog. Since it only multiplies `fog`, fully
                // visible cells (fog == 0) remain completely clean.
                float detailA = SAMPLE_TEXTURE2D(
                    _NoiseTex,
                    sampler_NoiseTex,
                    worldXZ * _NoiseTexScale + drift * 0.55).r;
                float2 crossDrift = float2(-drift.y, drift.x);
                float detailB = SAMPLE_TEXTURE2D(
                    _NoiseTex,
                    sampler_NoiseTex,
                    worldXZ * (_NoiseTexScale * 1.73) + crossDrift * 0.8).r;
                float dustDensity = saturate(detailA * 0.62 + detailB * 0.38);
                float stormModulation = lerp(0.72, 1.18, dustDensity);
                fog *= lerp(1.0, stormModulation, _NoiseTexStrength);

                return half4(_Color.rgb, fog * _Color.a);
            }
            ENDHLSL
        }
    }
}
