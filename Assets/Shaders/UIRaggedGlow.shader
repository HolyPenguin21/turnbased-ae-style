// A UI-space (UGUI Image, screen space) counterpart to HexSelectionGlow.shader — same ragged
// noise-driven edge + animated glow technique (see that shader's own comments for the visual
// language this is matching), just drawn around a rectangle instead of a hexagon, and computed
// from the Image's UV (0..1 across its RectTransform) instead of an object-space world mesh.
// Structured like Unity's own built-in UI/Default.shader (stencil/clip-rect support included)
// so it's a safe drop-in Image material rather than a bespoke one-off that fights the Canvas/
// RectMask2D pipeline. See UIRaggedGlowUI.cs for the component that drives _TrueSize/_RectSize.
Shader "Custom/UIRaggedGlow"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1, 0.85, 0.1, 1)
        _LineThickness ("Line Thickness", Range(0.1, 20)) = 2
        _NoiseReach ("Noise Reach", Range(0, 20)) = 4
        _NoiseScale ("Noise Scale", Range(0.01, 1)) = 0.12
        _NoiseSpeed ("Noise Speed", Range(0, 5)) = 1.2
        _GlowIntensity ("Glow Intensity", Range(0, 2)) = 0.8
        _GlowWidth ("Glow Width", Range(0, 30)) = 6
        _RadiusScale ("Radius Scale", Range(0.5, 1.5)) = 1.0
        // The real cell size (x=width, y=height, local/UI units) the ring is drawn at.
        _TrueSize ("True Size", Vector) = (80, 100, 0, 0)
        // This object's own (inflated, margin-padded) rect size — how UV maps to local space.
        _RectSize ("Rect Size", Vector) = (92, 112, 0, 0)

        _StencilComp ("Stencil Comparison", Float) = 8
        _Stencil ("Stencil ID", Float) = 0
        _StencilOp ("Stencil Operation", Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask ("Stencil Read Mask", Float) = 255
        _ColorMask ("Color Mask", Float) = 15
    }

    SubShader
    {
        Tags
        {
            "Queue"="Transparent"
            "IgnoreProjector"="True"
            "RenderType"="Transparent"
            "PreviewType"="Plane"
            "CanUseSpriteAtlas"="True"
        }

        Stencil
        {
            Ref [_Stencil]
            Comp [_StencilComp]
            Pass [_StencilOp]
            ReadMask [_StencilReadMask]
            WriteMask [_StencilWriteMask]
        }

        Cull Off
        Lighting Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend SrcAlpha OneMinusSrcAlpha
        ColorMask [_ColorMask]

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            #include "UnityUI.cginc"

            struct appdata_t
            {
                float4 vertex   : POSITION;
                float4 color    : COLOR;
                float2 texcoord : TEXCOORD0;
            };

            struct v2f
            {
                float4 vertex   : SV_POSITION;
                fixed4 color    : COLOR;
                float2 texcoord : TEXCOORD0;
                float4 worldPosition : TEXCOORD1;
            };

            fixed4 _Color;
            float4 _ClipRect;

            v2f vert(appdata_t v)
            {
                v2f OUT;
                OUT.worldPosition = v.vertex;
                OUT.vertex = UnityObjectToClipPos(OUT.worldPosition);
                OUT.texcoord = v.texcoord;
                OUT.color = v.color * _Color;
                return OUT;
            }

            sampler2D _MainTex;
            float _LineThickness;
            float _NoiseReach;
            float _NoiseScale;
            float _NoiseSpeed;
            float _GlowIntensity;
            float _GlowWidth;
            float _RadiusScale;
            float4 _TrueSize;
            float4 _RectSize;

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

            // Axis-aligned box SDF (Inigo Quilez) — negative inside, positive outside,
            // magnitude = distance to the nearest edge. Same role as HexSelectionGlow's hexSDF,
            // just for a rectangle instead of a hexagon.
            float boxSDF(float2 p, float2 halfSize)
            {
                float2 d = abs(p) - halfSize;
                return length(max(d, 0.0)) + min(max(d.x, d.y), 0.0);
            }

            fixed4 frag(v2f IN) : SV_Target
            {
                // texcoord spans 0..1 across this object's own (margin-inflated) rect — convert
                // to local units centered on the cell, then test against the TRUE cell half-size
                // so the crisp ring lands exactly on the real cell edge, with the extra rect
                // space beyond it only used as room for the ragged edge/glow to bleed into.
                float2 p = (IN.texcoord - 0.5) * _RectSize.xy;
                float2 halfSize = _TrueSize.xy * 0.5 * _RadiusScale;
                float dist = boxSDF(p, halfSize);

                float ring = 1.0 - smoothstep(0.0, _LineThickness, abs(dist));

                float edgeNoise = valueNoise(p * _NoiseScale + _Time.y * _NoiseSpeed * 0.3);
                float raggedReach = _NoiseReach * edgeNoise;
                float ragged = smoothstep(0.0, 1.0, dist) * (1.0 - smoothstep(raggedReach, raggedReach + 1.0, dist));

                float shape = saturate(ring + ragged);

                float glowBand = 1.0 - smoothstep(0.0, _GlowWidth, abs(dist));
                float glowNoise = valueNoise(p * _NoiseScale * 1.7 + _Time.y * _NoiseSpeed);
                float glow = glowBand * glowNoise * _GlowIntensity;

                float alpha = saturate(shape + glow) * IN.color.a;

                #ifdef UNITY_UI_CLIP_RECT
                alpha *= UnityGet2DClipping(IN.worldPosition.xy, _ClipRect);
                #endif

                return fixed4(IN.color.rgb, alpha);
            }
            ENDCG
        }
    }
}
