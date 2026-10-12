// Content-visibility overlay for the strategic map (see Game.Map.FogOfWarController /
// Game.Map.VisionSystem) — a single flat quad covering the whole hex grid, darkening every hex
// the current viewer (VisionSystem.CurrentViewer) doesn't presently have vision of. Terrain
// itself is never hidden by this, only content (armies/buildings/resource yield, gated
// separately in C# — see HexSelectionController/MapResourceDisplay) — this shader only draws
// the dimming tint, it has no say in what's actually shown/hidden underneath it.
//
// The seam traces the TRUE hex edge (worldToAxialRounded + a per-edge signed distance, see
// `edgeSignedDistance` — sampling _VisibilityMask continuously across raw (q, r) instead would
// blend along the mask texture's own skewed axial axes, a parallelogram rather than a hexagon).
// A shared edge is still owned by the FOGGED hex, never the
// clearer one — every "which side owns this edge" decision below is a flat threshold on the RAW
// per-hex fog sample, never a blended one. Both the main fog wave and the scorch rim are unioned
// multi-edge fields: a pixel considers ALL of its own hex's up-to-6 real fog<->visible edges (not
// one single-angle-bucket edge, and not one "winning" neighbour) and combines their independent,
// noise-warped contributions — clearing via min() on the fogged side, painting via max() on the
// clear side, scorch via max() on both — so neither field can develop an internal seam or kink at
// a hex vertex just because one particular edge happened to be picked over another there.
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
        _ScorchColor ("Scorch Rim Color", Color) = (0.015, 0.009, 0.005, 1)
        _ScorchWidth ("Scorch Rim Width", Range(0, 0.5)) = 0.34
        _ScorchStrength ("Scorch Rim Strength", Range(0, 1)) = 0.88
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

            // Restricts a fog OR scorch candidate to pixels actually near ONE specific edge of the
            // fogged hex it's evaluated in (`edgeIdx`), not near any of that hex's 6 edges equally.
            // `edgeSignedDistance` below gives the candidate's raw distance/ramp value against
            // THIS edge specifically, but says nothing about tangential position along it — without
            // this separate mask, a candidate meant for one real fog<->visible edge would keep
            // contributing along its own edge's infinite line far past that edge's actual span,
            // smearing into territory that should belong to its OTHER edges instead (which may
            // face a different, fog<->fog neighbour). `p` is in the fogged hex's own local space
            // (pixel position minus ITS centre).
            // `feather` intentionally reaches past the edge's true half-length so two real edges
            // meeting at a shared vertex overlap there instead of both hard-cutting right at it —
            // used by both frag() branches below with the same `supportFeather`, so the fog wave
            // and the scorch rim overlap vertices identically.
            float edgeSupport(float2 p, int edgeIdx, float outerRadius, float feather)
            {
                float edgeAngle = (edgeIdx + 0.5) * (PI / 3.0);
                float2 normal = float2(cos(edgeAngle), sin(edgeAngle));
                float2 tangent = float2(-normal.y, normal.x);
                float along = abs(dot(p, tangent));
                float halfEdge = outerRadius * 0.5;
                return 1.0 - smoothstep(halfEdge, halfEdge + feather, along);
            }

            // Signed distance to ONE specific edge's own infinite line (negative inside, 0 on that
            // edge, positive past it) — NOT a "whichever of the hex's 6 edges is nearest" distance.
            // Without this, a per-edge loop iteration would keep reading the hex's closest side
            // regardless of which `edgeIdx` it's actually supposed to be evaluating, so a pixel
            // near one real fog<->visible edge could pick up FOW/scorch erosion meant for a
            // DIFFERENT, unrelated edge just because that other edge happened to be geometrically
            // closer (e.g. near a corner). `edgeSupport` alone only masks the RESULT by tangential
            // position; it doesn't fix what distance the ramp itself is measured against, which is
            // what this does.
            float edgeSignedDistance(float2 p, int edgeIdx, float apothem)
            {
                float angle = (edgeIdx + 0.5) * (PI / 3.0);
                float2 normal = float2(cos(angle), sin(angle));
                return dot(p, normal) - apothem;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float2 worldXZ = IN.worldXZ;

                float2 dir = float2(1.0, 0.4);
                float2 drift = dir * _Time.y * _NoiseSpeed;

                // The pixel's own hex (true Voronoi cell) and its position relative to that hex's
                // own centre — fed into `edgeSignedDistance` per-edge further below, not a single
                // whichever-edge-is-nearest distance (that would let a pixel near one real boundary
                // erode against a different, unrelated edge of the same hex).
                float2 qr = worldToAxialRounded(worldXZ, _OuterRadius);
                float2 center = axialToWorld(qr, _OuterRadius);
                float2 localP = worldXZ - center;

                float ownFog = sampleHexFogQR(qr);

                // Two-octave coastline-style noise (a broad sweep plus a higher-frequency layer
                // riding on top) used to warp a hex's distance field before the threshold —
                // warping the distance makes the smoothstep's zero-crossing wander in world space
                // (a broken/torn silhouette), rather than jittering opacity within a fixed-shape
                // band after the fact (which only ever reads as grain). Sampled once here at this
                // pixel's own world position (not per-hex), so every branch below — this hex's own
                // retreat, or a neighbour's boundary reaching forward — tears with the same noise.
                float2 hexUV = worldXZ / max(_OuterRadius, 0.001);
                float erosionBroad = fbm(hexUV * lerp(1.0, 3.0, _NoiseScale) + drift);
                float erosionFine = fbm(hexUV * lerp(4.5, 13.5, _NoiseScale) - drift * 1.4 + float2(41.2, -17.7));
                // One-sided (0..1, not centred on 0.5): erosion only ever pulls a fogged hex's own
                // edge FURTHER back into itself (see `tornDistOwn`/`tornDistFog` below), never
                // forward past its true boundary from that hex's OWN point of view — so the torn
                // look is always a retreat into fogged territory, never a bulge past it, even
                // though (per the clear-hex branch below) that retreat can now be evaluated from a
                // neighbouring hex's centre and so reach into what is, from THIS pixel's own hex,
                // a bulge.
                float erosionNoise = saturate(erosionBroad * 0.62 + erosionFine * 0.38);
                // Upper end raised from 0.6 to 1.5 so _EdgeSoftness can carve much deeper teeth —
                // see `midPoint`'s own comment below for the clamp that keeps this from reaching
                // past a hex's own centre regardless of how this combines with _EdgeSharpness.
                float erosionAmplitude = _OuterRadius * lerp(0.05, 1.5, _EdgeSoftness);
                float erosion = erosionNoise * erosionAmplitude;

                float band = lerp(_OuterRadius * 0.3, _OuterRadius * 0.02, _EdgeSharpness);
                // The APOTHEM (centre-to-edge-midpoint), not the circumradius (_OuterRadius,
                // centre-to-vertex — see HexGridMath.AxialToWorld) — same constant for every hex on
                // a uniform grid, so it's reused below by `edgeSignedDistance` for both this pixel's
                // own hex and any neighbour's frame.
                float apothem = _OuterRadius * 0.8660254;
                // Clamped so `midPoint + band` can never exceed a hex's own apothem (the greatest
                // possible depth of ANY point inside it) — without this, raising _EdgeSoftness's
                // 1.5x amplitude together with a wide _EdgeSharpness band could push the ramp's
                // un-saturated zone past a fogged hex's own centre, carving a hole clear through it
                // on an unlucky noise sample and exposing whatever it's supposed to still be
                // hiding — not a cosmetic wide edge. Same clamp value serves the fogged hex whether
                // it's this pixel's own (below) or a neighbour's (further below).
                float midPoint = min(erosionAmplitude * 0.5, max(0.0, apothem - band));

                // FOW and scorch are now driven off the exact same edge topology, the only
                // difference being which candidate function reads that topology (`candidateFog`
                // vs `candidateScorch` below) — see each branch's own comment for why. Only
                // `ownFog` decides which of the two branches this pixel takes: no more single
                // angle-bucket edge, blended corner neighbour, or one-neighbour-wins select — every
                // real fog<->visible boundary this pixel could be near contributes independently,
                // gated by a flat 0.5 threshold on the RAW per-hex sample (never a blended one,
                // which would read as a second, fainter fog<->fog "boundary" of its own and paint a
                // false line down the middle of two fogged hexes).
                float scorchWidth = max(_ScorchWidth * _OuterRadius, 0.0001);
                float supportFeather = max(_OuterRadius * 0.08, scorchWidth * 1.5);

                float fog;
                float scorch = 0.0;

                if (ownFog > 0.5)
                {
                    // This pixel's own hex is fogged: start fully fogged, and let every one of its
                    // REAL (nFog <= 0.5) visible neighbours locally erode it on its own edge only —
                    // min() rather than a single erosion, so interior pixels far from any real
                    // boundary stay exactly 1, and several real edges near a shared vertex each
                    // just clear their own patch without one of them "winning" and overriding the
                    // others (which is what caused the old single-`edgeIdx` scheme's internal
                    // seams/kinks: a pixel could sit near a real boundary that its OWN angle bucket
                    // didn't happen to point at, and render fully fogged despite being right next
                    // to open ground).
                    fog = 1.0;
                    [unroll]
                    for (int edge = 0; edge < 6; edge++)
                    {
                        float2 neighborQR = qr + kNeighborDirs[edge];
                        float nFog = sampleHexFogQR(neighborQR);
                        if (nFog > 0.5)
                            continue; // fog<->fog, not a real boundary — no contribution at all

                        // Distance to THIS specific edge's own line, not a "whichever edge is
                        // nearest overall" distance — otherwise a pixel near one real boundary could
                        // erode against a completely different, unrelated edge just because that
                        // other edge happened to be geometrically closer (e.g. near a corner).
                        // `edgeSupport` further below only masks the RESULT by tangential position;
                        // it doesn't by itself fix what the ramp is measured against.
                        float edgeDistOwn = edgeSignedDistance(localP, edge, apothem);
                        float tornDistOwn = edgeDistOwn - erosion;
                        float inwardOwn = -tornDistOwn;
                        float rampOwn = smoothstep(midPoint - band, midPoint + band, inwardOwn);
                        // Re-centred on the ramp's actual 50% crossing (`inward == midPoint`), not
                        // on `tornDistOwn == 0` — that's where the fog visually transitions, and
                        // scorch below must line up with it exactly.
                        float seamDist = inwardOwn - midPoint;

                        float support = edgeSupport(localP, edge, _OuterRadius, supportFeather);

                        // Away from this edge (support -> 0), revert to the "no boundary here"
                        // default of 1.0 (fully fogged) rather than 0 — a candidate that goes quiet
                        // must fall back to what this pixel already believed, not to "clear".
                        float candidateFog = lerp(1.0, rampOwn, support);
                        fog = min(fog, candidateFog);

                        float scorchCore = 1.0 - smoothstep(0.0, scorchWidth * 0.55, abs(seamDist));
                        float scorchHalo = 1.0 - smoothstep(scorchWidth * 0.35, scorchWidth * 1.4, abs(seamDist));
                        float candidateScorch = saturate(scorchCore + scorchHalo * 0.45) * _ScorchStrength;
                        scorch = max(scorch, candidateScorch * support);
                    }
                }
                else
                {
                    // This pixel's own hex is clear: start fully clear, and let every one of its
                    // REAL (nFog > 0.5) fogged neighbours paint forward onto it from ITS OWN centre
                    // — max() so several fogged neighbours near a shared vertex all get to
                    // contribute without one hiding the others, the same "collect from every
                    // neighbour, take the strongest" principle as the fogged branch above, just
                    // mirrored (clearing needs min() against a fogged default; painting needs
                    // max() against a clear default).
                    fog = 0.0;
                    [unroll]
                    for (int i = 0; i < 6; i++)
                    {
                        float2 fogQR = qr + kNeighborDirs[i];
                        float nFog = sampleHexFogQR(fogQR);
                        if (nFog < 0.5)
                            continue; // not a real fog owner — no contribution at all

                        // `i` here is a raw kNeighborDirs index around THIS hex; `i + 3` is that
                        // fogged neighbour's OPPOSITE edge, the one facing back at this hex, i.e.
                        // the shared edge itself.
                        int fogEdge = (i + 3) % 6;
                        float2 fogCenter = axialToWorld(fogQR, _OuterRadius);
                        float2 pFog = worldXZ - fogCenter;

                        // Same reasoning as `edgeDistOwn` in the fogged branch above: this must be
                        // distance to `fogEdge` specifically (the shared edge facing back at this
                        // pixel's own hex), not "whichever of the fogged hex's 6 edges is nearest"
                        // — which could be a different, unrelated edge of that same hex.
                        float edgeDistFog = edgeSignedDistance(pFog, fogEdge, apothem);
                        float tornDistFog = edgeDistFog - erosion;
                        float inwardFog = -tornDistFog;
                        float rampFog = smoothstep(midPoint - band, midPoint + band, inwardFog);
                        float seamDist = inwardFog - midPoint;

                        float support = edgeSupport(pFog, fogEdge, _OuterRadius, supportFeather);

                        // Away from this edge (support -> 0), revert to "no boundary here" = 0.0
                        // (clear), the mirror image of the fogged branch's lerp(1.0, ...) above.
                        float candidateFog = rampFog * support;
                        fog = max(fog, candidateFog);

                        float scorchCore = 1.0 - smoothstep(0.0, scorchWidth * 0.55, abs(seamDist));
                        float scorchHalo = 1.0 - smoothstep(scorchWidth * 0.35, scorchWidth * 1.4, abs(seamDist));
                        float candidateScorch = saturate(scorchCore + scorchHalo * 0.45) * _ScorchStrength;
                        scorch = max(scorch, candidateScorch * support);
                    }
                }

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

                // Burnt-paper rim: `scorch` (the unioned multi-edge field computed above) composited
                // as its own layer on TOP of the fog fill rather than mixed into its tint — so it
                // reads as scorching along the seam, visible on the clear side too, not just a
                // darker version of the fog colour itself. Colour strength and alpha are scaled
                // down independently (0.665 / 0.35) rather than letting `scorch` drive a full-
                // strength `_ScorchColor.a` mix outright — a near-black `_ScorchColor` at strength 1
                // otherwise reads as an opaque black hole rather than a thin charred line.
                float scorchColorMix = scorch * _ScorchColor.a * 0.665;
                float scorchAlpha = scorch * _ScorchColor.a * 0.35;

                float3 outColor = lerp(fogColor, _ScorchColor.rgb, scorchColorMix);
                // Light extra darkening on top of the colour mix — kept far weaker than an opaque
                // blend would need, since this stacks with `scorchColorMix` above rather than
                // replacing it.
                outColor *= lerp(1.0, 0.895, scorch);

                float outAlpha = fogAlpha + scorchAlpha * (1.0 - fogAlpha);

                return half4(outColor, outAlpha);
            }
            ENDHLSL
        }
    }
}