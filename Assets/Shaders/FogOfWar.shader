// Content-visibility overlay for the strategic map (see Game.Map.FogOfWarController /
// Game.Map.VisionSystem) — a single flat quad covering the whole hex grid, darkening every hex
// the current viewer (VisionSystem.CurrentViewer) doesn't presently have vision of. Terrain
// itself is never hidden by this, only content (armies/buildings/resource yield, gated
// separately in C# — see HexSelectionController/MapResourceDisplay) — this shader only draws
// the dimming tint, it has no say in what's actually shown/hidden underneath it.
//
// The seam traces the TRUE hex edge (worldToAxialRounded + hexSDF, same technique
// HexClusterGlow.shader uses for exact hex-shaped edges — a plain bilinear mask blend curves at
// texel diagonals, not hexagonally), then a drifting fbm haze roughens only that seam into a
// broken, organic edge rather than a hard cutout or a uniformly noisy wash over everything. A
// second, static "storm dust" detail texture (_NoiseTex, assigned on Assets/Materials/FogOfWar.mat)
// modulates both density and tint so fogged territory reads as textured dry terrain, not a flat
// colour fill. All of this shader's tunables live on that material asset, not in code — see
// FogOfWarController.overlayMaterial's own comment.
Shader "Custom/FogOfWar"
{
    Properties
    {
        _Color ("Fog Tint", Color) = (0.10, 0.09, 0.075, 0.85)
        _EdgeSoftness ("Edge Softness", Range(0, 1)) = 0.45
        _EdgeSharpness ("Edge Sharpness", Range(0, 1)) = 0.97
        _NoiseScale ("Haze Noise Scale", Range(0.01, 1)) = 0.12
        _NoiseSpeed ("Haze Drift Speed", Range(0, 1)) = 0.04
        _NoiseTex ("Detail Texture (optional)", 2D) = "white" {}
        _NoiseTexScale ("Detail Texture Scale", Range(0.001, 1)) = 0.05
        _NoiseTexStrength ("Detail Texture Strength", Range(0, 1)) = 0.6
        _ScorchColor ("Scorch Rim Color", Color) = (0.03, 0.02, 0.015, 1)
        _ScorchWidth ("Scorch Rim Width", Range(0, 0.5)) = 0.14
        _ScorchStrength ("Scorch Rim Strength", Range(0, 1)) = 0.85
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
            float4 _ScorchColor;
            float _ScorchWidth;
            float _ScorchStrength;

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

            // 3-octave fractal sum softens the otherwise geometric fog boundary.
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
                // Tracks the STRONGEST fog contrast seen against any neighbour considered here
                // (primary edge, and either corner-adjacent alternate below). Used for the scorch
                // rim gate instead of |ownFog - neighborFog| after blending, because blending
                // neighborFog toward a partial value near a corner would otherwise dilute that
                // difference and fade the rim out right at hex vertices — even when one of the
                // actual neighbours there IS a real visible/fogged boundary.
                float fogContrast = abs(ownFog - neighborFog);

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
                    fogContrast = max(fogContrast, abs(ownFog - altFog));
                }
                else if (withinEdge > 1.0 - cornerBlend)
                {
                    float altFog = sampleHexFog(qr + kNeighborDirs[(edgeIdx + 1) % 6]);
                    float rightCornerWeight = ((withinEdge - (1.0 - cornerBlend)) / cornerBlend) * cornerProximity;
                    neighborFog = lerp(neighborFog, altFog, rightCornerWeight);
                    fogContrast = max(fogContrast, abs(ownFog - altFog));
                }

                float2 dir = float2(1.0, 0.4);
                float2 drift = dir * _Time.y * _NoiseSpeed;

                // True geometric distance to this hex's own boundary (negative inside) — unlike
                // a bilinear-texture blend, this traces the REAL hex edge, so blending
                // ownFog -> neighborFog against it can't bulge into a rounded, wrong-shaped seam.
                // The boundary itself is then displaced by noise BEFORE the threshold, not after:
                // perturbing the resulting alpha post-hoc (the previous approach here) only
                // jitters opacity within a fixed-shape band — it never actually moves the seam,
                // so it reads as grain, not a torn edge. Warping `dist` makes the smoothstep's
                // zero-crossing itself wander in world space, which is what actually produces a
                // broken/torn silhouette. _EdgeSharpness=0 blends across most of the hex (wide,
                // soft); 1 narrows the band down to a few percent of the hex radius, hugging the
                // (now jagged) edge tightly.
                // Noise frequency expressed in hex-relative units (worldXZ / _OuterRadius), not
                // raw world units: at the old world-space scale, one noise cycle spanned several
                // hexes, so the seam only ever bowed in one broad, smooth sweep — never actually
                // torn at the scale of an individual hex edge. Several cycles per hex radius is
                // what makes each edge segment notch independently.
                // Coastline-style raggedness: a broad, slow sweep (bays/headlands) plus a much
                // higher-frequency layer riding on top of it (small inlets/notches), rather than
                // one single noise scale — a single fbm call reads as smoothly wavy, never as
                // genuinely torn/organic at multiple sizes at once.
                float2 hexUV = worldXZ / max(_OuterRadius, 0.001);
                float erosionBroad = fbm(hexUV * lerp(1.0, 3.0, _NoiseScale) + drift);
                float erosionFine = fbm(hexUV * lerp(4.5, 13.5, _NoiseScale) - drift * 1.4 + float2(41.2, -17.7));
                float erosionNoise = saturate(erosionBroad * 0.62 + erosionFine * 0.38);
                float erosion = (erosionNoise - 0.5) * _OuterRadius * lerp(0.10, 1.1, _EdgeSoftness);
                // hexSDF's `r` is the APOTHEM (centre-to-edge-midpoint distance), not the
                // circumradius (_OuterRadius, centre-to-vertex — this project's own convention,
                // see HexGridMath.AxialToWorld). Passing _OuterRadius straight in put the SDF's
                // zero-crossing ~13% of a hex radius past the true edge (into the neighbour),
                // since apothem = circumradius * cos(30 deg). Erosion had to close that gap
                // before it could ever push `dist` past the true boundary, which is why no amount
                // of tuning produced a visibly torn edge.
                float dist = hexSDF(p, _OuterRadius * 0.8660254) + erosion;
                float band = lerp(_OuterRadius * 0.75, _OuterRadius * 0.03, _EdgeSharpness);
                float blend = smoothstep(-band, band, dist);
                float fog = lerp(ownFog, neighborFog, blend);

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
                // The source map deliberately has a narrow, soft grayscale range, so it's widened
                // around its midpoint after the two samples are combined — otherwise their
                // weighted average compresses the already-small contrast to an imperceptible
                // 1-2% alpha change even with strength set to 1. The previous ×4 expansion pushed
                // most of that range all the way to 0 or 1, effectively posterising the source
                // texture's own soft shapes (dune shadows etc.) into sharp, unnaturally crisp
                // patches inside the fog. ×1.8 keeps it as visible material variation without
                // flattening its gradients into hard-edged shapes.
                float contrastDust = saturate((dustDensity - 0.5) * 1.8 + 0.5);

                float stormAlpha = lerp(0.65, 1.05, contrastDust);
                fog *= lerp(1.0, stormAlpha, _NoiseTexStrength);

                // Density changes the fog tint as well as its opacity, so wind streaks remain
                // visible over terrain with similar brightness. This colour modulation is still
                // multiplied by `fog` in the returned alpha: visible cells stay clean.
                float3 darkDust = _Color.rgb * 0.72;
                float3 lightDust = saturate(_Color.rgb * 1.35 + float3(0.05, 0.035, 0.015));
                float3 stormTint = lerp(darkDust, lightDust, contrastDust);
                float3 finalTint = lerp(_Color.rgb, stormTint, _NoiseTexStrength);

                float3 fogColor = finalTint;
                float fogAlpha = fog * _Color.a;

                // Burnt-paper rim: a distinct charred colour hugging the (already eroded/torn)
                // boundary, composited as its own layer on TOP of the fog fill rather than mixed
                // into its tint — so it reads as scorching along the seam, visible even on the
                // fully-clear side, not just a darker version of the fog colour itself.
                // `dist` alone is purely geometric — it sits near zero at EVERY hex edge, fogged
                // or not, so without gating this painted a scorch mark on every hex seam on the
                // map, including two fully-visible neighbours. `fogContrast` (computed above,
                // alongside the corner-neighbour lookups) gates it to real visible/fogged seams.
                float scorchWidth = max(_OuterRadius * _ScorchWidth, 0.0001);
                float scorchProximity = 1.0 - saturate(abs(dist) / scorchWidth);
                float scorch = pow(scorchProximity, 1.6) * _ScorchStrength * fogContrast;

                float3 outColor = lerp(fogColor, _ScorchColor.rgb, scorch * _ScorchColor.a);
                float outAlpha = fogAlpha + scorch * _ScorchColor.a * (1.0 - fogAlpha);

                return half4(outColor, outAlpha);
            }
            ENDHLSL
        }
    }
}
