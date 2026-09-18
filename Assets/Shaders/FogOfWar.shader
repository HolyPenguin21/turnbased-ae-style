// Content-visibility overlay for the strategic map (see Game.Map.FogOfWarController /
// Game.Map.VisionSystem) — a single flat quad covering the whole hex grid, darkening every hex
// the current viewer (VisionSystem.CurrentViewer) doesn't presently have vision of. Terrain
// itself is never hidden by this, only content (armies/buildings/resource yield, gated
// separately in C# — see HexSelectionController/MapResourceDisplay) — this shader only draws
// the dimming tint, it has no say in what's actually shown/hidden underneath it.
//
// The seam traces the TRUE hex edge (worldToAxialRounded + hexSDF, same technique
// HexClusterGlow.shader uses for exact hex-shaped edges — sampling _VisibilityMask continuously
// across raw (q, r) instead would blend along the mask texture's own skewed axial axes, a
// parallelogram rather than a hexagon). A shared edge is owned by exactly one side (the
// MORE-FOGGED hex, see frag()'s own `fogContrast` comment) — a hard binary select, not an
// approximate gate, so the clearer hex's alpha is structurally just its own flat value with zero
// dependency on distance/erosion, and there's nothing left to double-draw from both hexes'
// perspectives. A drifting fbm haze then domain-warps that owning hex's own distance field
// (always retreating further into its own territory, never past its true edge) before the
// threshold, so the seam reads as broken/torn rather than as grain sitting on a fixed-shape band.
// A second, static "storm dust" detail texture (_NoiseTex, assigned on
// Assets/Materials/FogOfWar.mat) modulates both density and tint so fogged territory reads as
// textured dry terrain, not a flat colour fill. All of this shader's artistic tunables live on
// that material asset, not in code — see
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
        // FIX: Queue shifted to Transparent-100 so the fog overlay renders BEFORE units and effects
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent-100" "RenderPipeline" = "UniversalPipeline" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        // FIX: ZTest changed from Always to LEqual so it doesn't forcefully overwrite and clip visuals on top
        ZTest LEqual
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

            // Mirrors HexGridMath.WorldToAxial (cube-coordinate rounding) exactly — gives the
            // TRUE owning hex (Voronoi cell) for any world position, correct even right at a
            // corner shared by three hexes.
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

            // Inigo Quilez's regular-hexagon SDF — vertex along +X, matching this project's hex
            // corner convention. Negative inside the hex, 0 exactly on its boundary, positive
            // outside. Used (instead of sampling _VisibilityMask continuously across raw (q, r),
            // which blends along the mask texture's own skewed axial axes — a parallelogram, not
            // a hexagon) so the seam traces the REAL hex shape and doesn't round off at corners.
            float hexSDF(float2 p, float r)
            {
                const float3 k = float3(-0.8660254, 0.5, 0.5773503);
                p = abs(p);
                p -= 2.0 * min(dot(k.xy, p), 0.0) * k.xy;
                p -= float2(clamp(p.x, -k.z * r, k.z * r), r);
                return length(p) * sign(p.y);
            }

            // Matches HexGridMath.NeighborDirectionsByEdge — direction[i] is the neighbour across
            // the edge between corners i and (i+1)%6.
            static const float2 kNeighborDirs[6] = {
                float2(1, 0), float2(0, 1), float2(-1, 1),
                float2(-1, 0), float2(0, -1), float2(1, -1)
            };

            // Point-samples ONE hex's own mask value — 0 visible / 1 fogged — by its exact texel
            // centre. Sampling precisely at a texel centre returns that texel alone even through
            // a Bilinear-filtered texture (the interpolation weight collapses to 1 for the
            // sampled texel, 0 for its neighbours right at that point).
            float sampleHexFogQR(float2 qr)
            {
                float2 uv = (qr - _MaskMinQR + 0.5) / _MaskSize;
                return 1.0 - SAMPLE_TEXTURE2D(_VisibilityMask, sampler_VisibilityMask, uv).r;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float2 worldXZ = IN.worldXZ;

                float2 dir = float2(1.0, 0.4);
                float2 drift = dir * _Time.y * _NoiseSpeed;

                // The pixel's own hex (true Voronoi cell) and its exact geometric distance to
                // that hex's boundary — negative inside, 0 on the true edge.
                float2 qr = worldToAxialRounded(worldXZ, _OuterRadius);
                float2 center = axialToWorld(qr, _OuterRadius);
                float2 localP = worldXZ - center;
                // hexSDF's `r` is the APOTHEM (centre-to-edge-midpoint), not the circumradius
                // (_OuterRadius, centre-to-vertex — see HexGridMath.AxialToWorld).
                float distToEdge = hexSDF(localP, _OuterRadius * 0.8660254);

                // Which of the 6 edges this pixel sits nearest to, within its own hex.
                float angle = atan2(localP.y, localP.x);
                if (angle < 0.0)
                    angle += 2.0 * PI;
                float rawEdgeIndex = angle / (PI / 3.0);
                int edgeIdx = (int) floor(rawEdgeIndex) % 6;
                float withinEdge = frac(rawEdgeIndex);

                float ownFog = sampleHexFogQR(qr);
                float neighborFog = sampleHexFogQR(qr + kNeighborDirs[edgeIdx]);

                // Right near a corner, the OTHER edge meeting there can matter just as much — the
                // 60°-wide angle bucket boundary doesn't line up with which neighbour is actually
                // closest once you're that close to a vertex three hexes share.
                const float cornerBlend = 0.08;
                float cornerProximity = smoothstep(_OuterRadius * 0.82, _OuterRadius * 0.98, length(localP));
                if (withinEdge < cornerBlend)
                {
                    float altFog = sampleHexFogQR(qr + kNeighborDirs[(edgeIdx + 5) % 6]);
                    neighborFog = lerp(neighborFog, altFog, (1.0 - withinEdge / cornerBlend) * cornerProximity);
                }
                else if (withinEdge > 1.0 - cornerBlend)
                {
                    float altFog = sampleHexFogQR(qr + kNeighborDirs[(edgeIdx + 1) % 6]);
                    neighborFog = lerp(neighborFog, altFog, ((withinEdge - (1.0 - cornerBlend)) / cornerBlend) * cornerProximity);
                }

                // Signed (own MINUS neighbour, floored at 0), not abs() — a shared edge must be
                // owned by exactly one side. The MORE-FOGGED hex (own more fogged than its
                // neighbour) is the one that erodes — retreats inward, back toward its own centre
                // — revealing clear ground along its own ragged edge. The clearer hex on the
                // other side sees a negative (floored to 0) contrast and renders flat at its own
                // ownFog, with NO dependency on distance/erosion at all: `isOwningSide` below is a
                // hard binary select, so `fog` on that side is algebraically just `ownFog` — not
                // approximately protected by some epsilon-width gate, but structurally incapable
                // of ever depending on `distToEdge`/erosion in the first place. Nothing left to
                // double-draw, and nothing for noise to leak past.
                float fogContrast = max(ownFog - neighborFog, 0.0);
                float isOwningSide = fogContrast > 0.0 ? 1.0 : 0.0;

                // Two-octave coastline-style noise (a broad sweep plus a higher-frequency layer
                // riding on top) used to warp `distToEdge` itself before the threshold — warping
                // the distance makes the smoothstep's zero-crossing wander in world space (a
                // broken/torn silhouette), rather than jittering opacity within a fixed-shape band
                // after the fact (which only ever reads as grain).
                float2 hexUV = worldXZ / max(_OuterRadius, 0.001);
                float erosionBroad = fbm(hexUV * lerp(1.0, 3.0, _NoiseScale) + drift);
                float erosionFine = fbm(hexUV * lerp(4.5, 13.5, _NoiseScale) - drift * 1.4 + float2(41.2, -17.7));
                // One-sided (0..1, not centred on 0.5): erosion only ever pulls the owning hex's
                // own edge FURTHER back into itself (see `perturbedDist` below), never forward
                // past its true boundary — so the torn look is a retreat into the fogged hex's own
                // territory, not a bulge into its neighbour's.
                float erosionNoise = saturate(erosionBroad * 0.62 + erosionFine * 0.38);
                // Upper end raised from 0.6 to 1.5 so _EdgeSoftness can carve much deeper teeth —
                // see `midPoint`'s own comment below for the clamp that keeps this from reaching
                // past a hex's own centre regardless of how this combines with _EdgeSharpness.
                float erosionAmplitude = _OuterRadius * lerp(0.05, 1.5, _EdgeSoftness);
                float erosion = erosionNoise * erosionAmplitude;

                float band = lerp(_OuterRadius * 0.3, _OuterRadius * 0.02, _EdgeSharpness);

                // `distToEdge` is negative inside the owning (more-fogged) hex; subtracting
                // `erosion` (>= 0) only ever makes it MORE negative — i.e. the perturbed boundary
                // can retreat deeper into the owning hex's own territory, never advance past its
                // true edge into the neighbour's. Combined with `isOwningSide` above (which
                // already makes the clearer neighbour's `fog` independent of this entirely), there
                // is no path for erosion to ever paint on guaranteed-open ground — no separate
                // epsilon-gate needed on top.
                float perturbedDist = isOwningSide > 0.5 ? distToEdge - erosion : distToEdge;

                // `inward` is always >= 0 by construction on both branches (`perturbedDist` is
                // itself always <= 0: unperturbed it's `distToEdge`, itself <= 0 inside any hex,
                // and the owning branch only ever subtracts more), so the old max(0.0, ...) clamp
                // here was a no-op — dropped rather than kept as dead ballast.
                //
                // The ramp's own threshold is centred on `midPoint` (half the erosion amplitude),
                // not on 0 — right at the true edge (distToEdge = 0), `inward` on the owning side
                // is just `erosion` itself, which straddles `midPoint` as noise varies per pixel:
                // some points there land solidly below the threshold (ramp -> 0, reading as clear)
                // and others solidly above it (ramp -> 1, reading as fogged), instead of every
                // point starting from the same ramp=0 baseline. That's what gives the "torn paper"
                // zero-crossing itself a wavering, uneven silhouette along the seam, rather than a
                // uniform band that only ever erodes inward by the same amount everywhere.
                float inward = -perturbedDist;
                // Clamped so `midPoint + band` can never exceed the hex's own apothem (the
                // greatest possible depth of ANY point inside it, see hexSDF's own `r` argument
                // below) — without this, raising _EdgeSoftness's 1.5x amplitude together with a
                // wide _EdgeSharpness band could push the ramp's un-saturated zone past a border
                // hex's own centre. That's not just a wider seam: any pixel whose angle bucket
                // faces a clearer neighbour reaches this branch regardless of radial depth (see
                // `edgeIdx` above, chosen purely by angle), so an unclamped `midPoint` could carve
                // a hole clear through to the hex's centre on an unlucky noise sample, exposing
                // whatever it's supposed to still be hiding — not a cosmetic wide edge.
                float apothem = _OuterRadius * 0.8660254;
                float midPoint = min(erosionAmplitude * 0.5, max(0.0, apothem - band));
                float ramp = smoothstep(midPoint - band, midPoint + band, inward);
                float fog = lerp(ownFog, lerp(neighborFog, ownFog, ramp), isOwningSide);

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
                // Centred on `ramp` itself (0.5 = the actual torn seam, wherever the smoothstep
                // crosses it for THIS pixel's noise), not on `inward`/`midPoint` distance — the rim
                // then inherits `ramp`'s own smoothstep continuity, so it can't detach from the
                // fog boundary regardless of how deep _EdgeSoftness pushes the erosion.
                // `rampDeviation` is bounded to [0, 1] by construction (`ramp` itself never leaves
                // [0, 1]), unlike the old world-space `distToRippedEdge` which grew without limit
                // away from the seam — so `widthFactor` MUST stay below 1.0, or `scorchProximity`
                // can never reach exactly 0 anywhere and the "rim" smears across the whole eroding
                // hex interior instead of staying a bounded ring. _ScorchWidth's own Inspector
                // range goes up to 0.5 (well past the *4 = 2.0 that would trigger this), so this is
                // clamped rather than left to the material's own tuning to avoid by luck.
                float rampDeviation = abs(ramp - 0.5) * 2.0;
                float widthFactor = clamp(_ScorchWidth * 4.0, 0.01, 0.98);
                float scorchProximity = saturate(1.0 - (rampDeviation / widthFactor));
                float scorch = pow(scorchProximity, 1.2) * _ScorchStrength * fogContrast;

                float3 outColor = lerp(fogColor, _ScorchColor.rgb, scorch * _ScorchColor.a);
                float outAlpha = fogAlpha + scorch * _ScorchColor.a * (1.0 - fogAlpha);

                return half4(outColor, outAlpha);
            }
            ENDHLSL
        }
    }
}