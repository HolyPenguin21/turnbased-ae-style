// Shared rounded amber selection outline for collection cards and acting battle units.
// Retains the shader name and style properties used by existing serialized components.
// Uses the UGUI stencil and RectMask2D clipping pipeline.
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
            #pragma multi_compile_local _ UNITY_UI_CLIP_RECT
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
                // Rounded amber frame follows card corners; no noisy tendrils.
                float radius = min(3.5, min(halfSize.x, halfSize.y) * 0.1);
                float dist = boxSDF(p, halfSize - radius) - radius;

                float aa = max(fwidth(dist), 0.35);
                float ring = 1.0 - smoothstep(_LineThickness * 0.5,
                    _LineThickness * 0.5 + aa, abs(dist));
                float glow = exp(-abs(dist) * 3.0 / max(_GlowWidth, 0.01)) * _GlowIntensity;
                float alpha = saturate(ring + glow) * IN.color.a;

                #ifdef UNITY_UI_CLIP_RECT
                alpha *= UnityGet2DClipping(IN.worldPosition.xy, _ClipRect);
                #endif

                return fixed4(IN.color.rgb, alpha);
            }
            ENDCG
        }
    }
}

