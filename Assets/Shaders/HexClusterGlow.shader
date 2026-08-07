// Shader-based replacement for HexHighlightMarker.ShowPolygon: renders the comb+glow border
// (same visual family as HexSelectionGlow.shader) around an arbitrary SET of hex cells,
// tracing only the true outer boundary — internal edges between two cells that are both in
// the set stay blank, same rule as HexGridMath.BuildOuterBoundary, just evaluated per pixel
// on the GPU instead of pre-baked into line geometry. See HexClusterHighlight.cs.
Shader "Custom/HexClusterGlow"
{
    Properties
    {
        _Color("Color", Color) = (1, 0.35, 0.1, 1)
        _LineThickness("Line Thickness", Range(0.001, 0.1)) = 0.03
        _NoiseReach("Noise Reach", Range(0.0, 0.5)) = 0.22
        _NoiseScale("Noise Scale", Range(0.1, 10)) = 3
        _NoiseSpeed("Noise Speed", Range(0, 5)) = 1.2
        _GlowIntensity("Glow Intensity", Range(0, 2)) = 0.7
        _GlowWidth("Glow Width", Range(0.0, 0.5)) = 0.25
        _RadiusScale("Radius Scale", Range(0.5, 1.5)) = 1.0
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

            #define MAX_CLUSTER_HEXES 32

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
            float _LineThickness;
            float _NoiseReach;
            float _NoiseScale;
            float _NoiseSpeed;
            float _GlowIntensity;
            float _GlowWidth;

            // Set from HexClusterHighlight.cs via a MaterialPropertyBlock — _ClusterHexes
            // isn't exposed in the Properties block above (arrays aren't supported there).
            // _OuterRadius is the TRUE hex grid spacing — used for axial rounding/neighbour
            // math below, and must never be scaled, or cells get misidentified.
            float _OuterRadius;
            int _ClusterHexCount;
            float4 _ClusterHexes[MAX_CLUSTER_HEXES];

            // Visual-only fudge factor (see HexHighlightStyle.radiusScale) — HexBlend.shader's
            // vertex-alpha fade makes each terrain tile read as smaller than its true
            // outerRadius, so the ring is drawn at outerRadius * this instead of outerRadius.
            // Only affects where the ring is drawn within a cell, never the grid math above.
            float _RadiusScale;

            // Matches HexGridMath.NeighborDirectionsByEdge exactly — direction[i] is the
            // neighbour across the edge between corners i and (i+1)%6.
            static const float2 kNeighborDirs[6] = {
                float2(1, 0), float2(0, 1), float2(-1, 1),
                float2(-1, 0), float2(0, -1), float2(1, -1)
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                // This object always sits at world identity (see HexClusterHighlight.cs) —
                // object space position already is world space.
                OUT.worldXZ = IN.positionOS.xz;
                return OUT;
            }

            // Inigo Quilez's regular-hexagon SDF — vertex along +X, matching this project's
            // hex corner convention (HexGridMath corners at angle 60*i).
            float hexSDF(float2 p, float r)
            {
                const float3 k = float3(-0.8660254, 0.5, 0.5773503);
                p = abs(p);
                p -= 2.0 * min(dot(k.xy, p), 0.0) * k.xy;
                p -= float2(clamp(p.x, -k.z * r, k.z * r), r);
                return length(p) * sign(p.y);
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

            // Mirrors HexGridMath.WorldToAxial (cube-coordinate rounding) exactly, so the
            // shader's idea of "which hex is this pixel in" always agrees with the C# side.
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

            bool isClusterMember(float2 qr)
            {
                for (int i = 0; i < _ClusterHexCount; i++)
                {
                    if (abs(_ClusterHexes[i].x - qr.x) < 0.5 && abs(_ClusterHexes[i].y - qr.y) < 0.5)
                        return true;
                }
                return false;
            }

            // Ring + ragged-noise "shape" and glow for point p relative to a hex centered at
            // the origin — the same per-pixel math the single-hex shader uses, factored out so
            // it can be evaluated against whichever member hex's frame actually owns a given
            // edge (see frag). p is always cell-relative, never an absolute world coordinate —
            // see the noise-precision note in ApplyStyle's HexSelectionGlow.shader counterpart.
            void computeEdgeEffect(float2 p, out float shape, out float glow)
            {
                float dist = hexSDF(p, _OuterRadius * _RadiusScale);

                float ring = 1.0 - smoothstep(0.0, _LineThickness, abs(dist));

                float edgeNoise = valueNoise(p * _NoiseScale + _Time.y * _NoiseSpeed * 0.3);
                float raggedReach = _NoiseReach * edgeNoise;
                float ragged = smoothstep(0.0, 0.01, dist) * (1.0 - smoothstep(raggedReach, raggedReach + 0.02, dist));
                shape = saturate(ring + ragged);

                // Pure gradient peaking on the boundary line, fading smoothly to 0 by
                // _GlowWidth on either side — no flat plateau (see HexSelectionGlow.shader).
                float glowBand = 1.0 - smoothstep(0.0, _GlowWidth, abs(dist));
                float glowNoise = valueNoise(p * _NoiseScale * 1.7 + _Time.y * _NoiseSpeed);
                glow = glowBand * glowNoise * _GlowIntensity;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float2 worldXZ = IN.worldXZ;
                float2 qr = worldToAxialRounded(worldXZ, _OuterRadius);

                float shape = 0.0;
                float glow = 0.0;

                if (isClusterMember(qr))
                {
                    // Home cell is a member — same as before: find which of its 6 edges this
                    // pixel is nearest to, and only draw if the cell across THAT edge is not
                    // also in the set (a true outer edge). This alone only ever draws inward,
                    // toward this cell's own centre — see the else branch below for the part
                    // that was missing entirely: the same effect bleeding outward, into the
                    // non-member neighbour on the other side of that edge.
                    float2 center = axialToWorld(qr, _OuterRadius);
                    float2 p = worldXZ - center;

                    float angle = atan2(p.y, p.x);
                    if (angle < 0.0)
                        angle += 2.0 * PI;
                    float rawEdgeIndex = angle / (PI / 3.0);
                    int edgeIdx = (int) floor(rawEdgeIndex) % 6;
                    float withinEdge = frac(rawEdgeIndex);

                    bool drawHere = !isClusterMember(qr + kNeighborDirs[edgeIdx]);

                    // Bucket boundaries (every 60°) land exactly ON the hex's corners, not on
                    // edge midpoints — so right at a corner, this pixel could be assigned to
                    // either of the two edges meeting there, and if only one of them is a true
                    // outer edge, the draw/no-draw decision used to flip abruptly right at that
                    // point (a visible notch/seam), even though hexSDF's own distance is
                    // perfectly continuous through the corner. Within a small angular margin of
                    // either side of a corner, also check the other edge sharing it, so the
                    // decision only flips near the corner if BOTH adjacent edges agree, not one.
                    const float cornerBlend = 0.06;
                    if (withinEdge < cornerBlend)
                        drawHere = drawHere || !isClusterMember(qr + kNeighborDirs[(edgeIdx + 5) % 6]);
                    else if (withinEdge > 1.0 - cornerBlend)
                        drawHere = drawHere || !isClusterMember(qr + kNeighborDirs[(edgeIdx + 1) % 6]);

                    if (drawHere)
                        computeEdgeEffect(p, shape, glow);
                }
                else
                {
                    // Home cell is NOT a member, but the ring/ragged/glow band around a
                    // neighbouring member hex can still reach into it — the single-hex shader
                    // has no such restriction at all (nothing stops it drawing outside its own
                    // hex), and this cluster version needs the equivalent: check every
                    // neighbour of this cell, and for any that IS a member, that specific edge
                    // (facing this, non-member, cell) is necessarily a true outer edge — so
                    // just evaluate the effect relative to that member's own centre. A pixel
                    // near a shared corner can be close to two such member neighbours at once,
                    // so combine with max rather than stopping at the first hit.
                    for (int i = 0; i < 6; i++)
                    {
                        float2 memberQR = qr + kNeighborDirs[i];
                        if (!isClusterMember(memberQR))
                            continue;

                        float2 memberCenter = axialToWorld(memberQR, _OuterRadius);
                        float2 pRelative = worldXZ - memberCenter;
                        float candidateShape, candidateGlow;
                        computeEdgeEffect(pRelative, candidateShape, candidateGlow);
                        shape = max(shape, candidateShape);
                        glow = max(glow, candidateGlow);
                    }
                }

                float alpha = saturate(shape + glow) * _Color.a;
                return half4(_Color.rgb, alpha);
            }
            ENDHLSL
        }
    }
}
