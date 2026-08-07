// The hex outline and an animated noise-driven ragged edge + glow are all computed per-pixel
// here instead of being baked into geometry. Renders on a flat quad (see
// HexShaderHighlight.cs) built in the object's own local space, centered on the hex and sized
// with a generous fixed world-space margin — not a UV-space budget, which previously left too
// little room for the ragged edge/glow and clipped them at the quad's boundary.
Shader "Custom/HexSelectionGlow"
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

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 localXZ : TEXCOORD0;
            };

            float4 _Color;
            float _LineThickness;
            float _NoiseReach;
            float _NoiseScale;
            float _NoiseSpeed;
            float _GlowIntensity;
            float _GlowWidth;

            // The real hex radius (world units), set from HexShaderHighlight.cs to match
            // whatever the map's actual hex size is — not a fixed/guessed constant.
            float _OuterRadius;
            // Visual-only fudge factor (see HexHighlightStyle.radiusScale) — HexBlend.shader's
            // vertex-alpha fade makes each terrain tile read as smaller than its true
            // outerRadius, so the ring is drawn at outerRadius * this instead of outerRadius.
            float _RadiusScale;

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                // The mesh is built in local space centered on the hex (see BuildQuad in
                // HexShaderHighlight.cs) — object-space position is already hex-relative,
                // regardless of where this object's transform currently sits in the world.
                OUT.localXZ = IN.positionOS.xz;
                return OUT;
            }

            // Inigo Quilez's regular-hexagon SDF — hexagon with a vertex along +X, matching
            // this project's own hex corner convention (HexGridMath corners at angle 60*i,
            // corner 0 along +X). Negative inside, positive outside, magnitude = distance to
            // the nearest edge/corner.
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

            half4 frag(Varyings IN) : SV_Target
            {
                float2 p = IN.localXZ;
                float dist = hexSDF(p, _OuterRadius * _RadiusScale);

                // Crisp base ring right on the hex boundary.
                float ring = 1.0 - smoothstep(0.0, _LineThickness, abs(dist));

                // Ragged noise edge instead of geometric teeth: how far outward the pattern
                // reaches varies continuously with noise (spatial + a slow time drift), giving
                // an organic, irregular border rather than a mechanical comb.
                float edgeNoise = valueNoise(p * _NoiseScale + _Time.y * _NoiseSpeed * 0.3);
                float raggedReach = _NoiseReach * edgeNoise;
                float ragged = smoothstep(0.0, 0.01, dist) * (1.0 - smoothstep(raggedReach, raggedReach + 0.02, dist));

                float shape = saturate(ring + ragged);

                // Soft animated glow layered just outside the crisp shape, its own faster
                // noise so it doesn't just track the ragged edge 1:1. A pure gradient peaking
                // right on the boundary line and fading smoothly to 0 by _GlowWidth on either
                // side — no flat plateau in the middle (the previous two-smoothstep band held
                // full brightness out to _NoiseReach before fading, which read as a hard-ish
                // cutoff rather than a continuous falloff).
                float glowBand = 1.0 - smoothstep(0.0, _GlowWidth, abs(dist));
                float glowNoise = valueNoise(p * _NoiseScale * 1.7 + _Time.y * _NoiseSpeed);
                float glow = glowBand * glowNoise * _GlowIntensity;

                float alpha = saturate(shape + glow) * _Color.a;
                return half4(_Color.rgb, alpha);
            }
            ENDHLSL
        }
    }
}
