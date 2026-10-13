Shader "Game/UI/CampaignSurface"
{
    Properties
    {
        [PerRendererData] _MainTex ("Terrain", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _SurfaceSize ("Surface size", Vector) = (1360,740,0,0)
        _EdgeFeather ("Edge feather (canvas units)", Float) = 28
        _Atmosphere ("Cloud overlay", Float) = 0
        _CloudOverlay ("Cloud footprint attenuation", Float) = 0
        _CloudInteriorOpacity ("Cloud opacity over regions", Range(0,1)) = .10
        _CloudCornerOpacity ("Cloud opacity at map corners", Range(0,1)) = .10
        _MapViewport ("Map viewport in overlay", Vector) = (0,0,1,1)
        _CornerRadius ("Panel corner radius", Float) = 0
        _UVRect ("Overlay UV rect", Vector) = (0,0,1,1)
        _CaptureProgress ("Capture progress", Range(0,1)) = 1
        _CaptureOrigin ("Capture origin in viewport", Vector) = (.5,.5,0,0)
        _CaptureRadius ("Capture radius in viewport height", Float) = 0
        _CaptureFeather ("Capture feather", Float) = .018
        _CapturePreviousColor ("Previous owner", Color) = (1,1,1,1)
        // White = no borders nearby: materials without a field get no highlight.
        _RegionField ("Region distance field", 2D) = "white" {}
        _FieldRange ("Field range (canvas units)", Float) = 64
        _TerrainContrast ("Terrain contrast", Float) = 1.4
        _TerrainBrightness ("Terrain brightness", Float) = .8
        _TintAmount ("Faction tint at borders", Float) = .55
        _TintCenter ("Faction tint at centers", Float) = .15
        _TintEdgeWidth ("Faction tint fade width", Float) = 48
        _TintGain ("Faction tint gain", Float) = 1.1
        _LineWidth ("Edge line inset", Float) = 1.6
        _LineStrength ("Edge line strength", Float) = .8
        _SeamWidth ("Dark seam width", Float) = 1.2
        _SeamStrength ("Dark seam strength", Float) = .7
        _RimWidth ("Faction rim width", Float) = 20
        _RimFalloff ("Faction rim falloff", Float) = 1.6
        _RimStrength ("Faction rim strength", Float) = .45
        _RimBrightness ("Faction rim brightness", Float) = 1.15
        _RimLift ("Faction rim lift", Float) = .22
        _HoverBrighten ("Hover brighten", Float) = .12
        _HoverRimBoost ("Hover rim boost", Float) = 1
        _SelectionColor ("Selection glow", Color) = (1,.60,.20,1)
        _SelectionRimStrength ("Selection rim strength", Float) = 1.4
        _SelectionInnerWidth ("Selection inner width", Float) = 44
        _SelectionInnerStrength ("Selection inner strength", Float) = .85
        _SelectionOuterWidth ("Selection outer width", Float) = 12
        _SelectionOuterStrength ("Selection outer strength", Float) = 1.1
        _SelectionFill ("Selection fill", Float) = .5
        _AttackColor ("Attack rim", Color) = (1,.22,.10,1)
        _AttackStrength ("Attack rim strength", Float) = 1.6
        _AttackFill ("Attack fill", Float) = .32
        _AttackPulseSpeed ("Attack pulse speed", Float) = 3.2
        _StencilComp ("Stencil Comparison", Float) = 8
        _Stencil ("Stencil ID", Float) = 0
        _StencilOp ("Stencil Operation", Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask ("Stencil Read Mask", Float) = 255
        _ColorMask ("Color Mask", Float) = 15
        [Toggle(UNITY_UI_ALPHACLIP)] _UseUIAlphaClip ("Use Alpha Clip", Float) = 0
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" "PreviewType"="Plane" "CanUseSpriteAtlas"="False" }
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
            Name "CampaignSurface"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #pragma multi_compile_local _ UNITY_UI_CLIP_RECT
            #pragma multi_compile_local _ UNITY_UI_ALPHACLIP
            #include "UnityCG.cginc"
            #include "UnityUI.cginc"
            struct appdata
            {
                float4 vertex : POSITION;
                fixed4 color : COLOR;
                float2 texcoord : TEXCOORD0;
                float4 surface : TEXCOORD1;
                float4 selection : TEXCOORD2;
                float4 state : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct v2f
            {
                float4 vertex : SV_POSITION;
                fixed4 color : COLOR;
                float2 texcoord : TEXCOORD0;
                float4 localPosition : TEXCOORD1;
                float4 surface : TEXCOORD2;
                float4 selection : TEXCOORD3;
                float4 state : TEXCOORD4;
                UNITY_VERTEX_OUTPUT_STEREO
            };
            sampler2D _MainTex;
            fixed4 _Color;
            fixed4 _TextureSampleAdd;
            float4 _ClipRect;
            float4 _SurfaceSize;
            float _EdgeFeather;
            float _Atmosphere;
            float _CloudOverlay;
            float _CloudInteriorOpacity;
            float _CloudCornerOpacity;
            float4 _MapViewport;
            float _CornerRadius;
            float4 _UVRect;
            float _CaptureProgress, _CaptureRadius, _CaptureFeather;
            float4 _CaptureOrigin;
            fixed4 _CapturePreviousColor;
            sampler2D _RegionField;
            float _FieldRange, _RimWidth, _RimFalloff, _RimStrength, _RimBrightness, _RimLift, _HoverBrighten, _HoverRimBoost;
            float _TerrainContrast, _TerrainBrightness, _TintAmount, _TintCenter, _TintEdgeWidth, _TintGain, _LineWidth, _LineStrength, _SeamWidth, _SeamStrength;
            float _SelectionRimStrength, _SelectionInnerWidth, _SelectionInnerStrength, _SelectionOuterWidth, _SelectionOuterStrength, _SelectionFill;
            float _AttackStrength, _AttackPulseSpeed, _AttackFill;
            fixed4 _SelectionColor, _AttackColor;
            fixed3 Screen(fixed3 color, fixed3 light) { return 1 - (1 - color) * (1 - saturate(light)); }
            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.localPosition = v.vertex;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.texcoord = v.texcoord;
                o.surface = v.surface;
                o.selection = float4(v.selection.x, v.selection.zw, v.selection.y);
                o.state = v.state;
                o.color = v.color * _Color;
                return o;
            }
            fixed4 frag(v2f i) : SV_Target
            {
                // Borders and markers remain solid; only terrain triangles sample the map texture.
                fixed4 sample = tex2D(_MainTex, i.texcoord) + _TextureSampleAdd;
                // Only the confirmed target's terrain vertices carry the capture flag. Shared
                // map/stencil materials leave every other region, border and marker untouched.
                float2 captureOffset = (i.selection.yz - _CaptureOrigin.xy) * _SurfaceSize.xy
                    / max(1, min(_SurfaceSize.x, _SurfaceSize.y));
                float reveal = 1 - smoothstep(_CaptureRadius - _CaptureFeather, _CaptureRadius, length(captureOffset));
                reveal = _CaptureProgress <= 0 ? 0 : _CaptureProgress >= 1 ? 1 : reveal;
                fixed3 ownerTint = lerp(i.color.rgb, lerp(_CapturePreviousColor.rgb * _Color.rgb, i.color.rgb, reveal), i.selection.w);
                // Region highlight from the distance field (R: own border, G: selected region's border).
                float4 field = tex2D(_RegionField, i.selection.yz);
                float ownDistance = field.r * _FieldRange, selectedDistance = field.g * _FieldRange;
                // Faction color is strongest along the borders and fades toward each region's center.
                float borderWeight = 1 - smoothstep(0, max(.001, _TintEdgeWidth), ownDistance);
                float fillWeight = .45 + .55 * borderWeight;
                // Contrast-boosted terrain multiplied by the faction color keeps relief crisp under the tint.
                fixed luminance = dot(sample.rgb, fixed3(.299,.587,.114));
                float contrastLuminance = saturate((luminance - .45) * _TerrainContrast + .45);
                fixed3 terrain = lerp(saturate((sample.rgb - .45) * _TerrainContrast + .45), contrastLuminance * ownerTint * _TintGain, lerp(_TintCenter, _TintAmount, borderWeight));
                float radiusSquared = dot(i.surface.zw, i.surface.zw);
                float limb = smoothstep(.38, 1.0, radiusSquared);
                terrain *= (1.04 + .07 * i.surface.w - .34 * limb) * i.surface.y * _TerrainBrightness;
                // A restrained dusty atmospheric rim follows the same continuous map coordinates.
                terrain = lerp(terrain, fixed3(.49,.46,.40), smoothstep(.72, 1.0, radiusSquared) * .22);
                // state: x selected, y hovered, z legal attack target, w pending operation target.
                float chosen = i.state.x, hovered = i.state.y, threatened = saturate(i.state.z + i.state.w);
                float pulse = lerp(.55, 1, (.5 + .5 * sin(_Time.y * _AttackPulseSpeed)) * (1 - i.state.w) + i.state.w);
                // Selected: amber wash over the same contrast terrain.
                terrain = lerp(terrain, contrastLuminance * _SelectionColor.rgb * _TintGain * 1.1, chosen * _SelectionFill * fillWeight);
                // Legal targets: a pulsing red wash so they read at a glance.
                terrain = lerp(terrain, contrastLuminance * _AttackColor.rgb * _TintGain, threatened * _AttackFill * pulse * fillWeight * (1 - chosen));
                // Every region: a crisp light line just inside its border and a soft inward glow in its own color;
                // legal targets switch to the pulsing attack color, the selected region to the selection color.
                fixed3 rimColor = lerp(saturate(ownerTint * _RimBrightness + _RimLift), _AttackColor.rgb, threatened);
                rimColor = lerp(rimColor, _SelectionColor.rgb, chosen);
                float rimStrength = lerp(_RimStrength * (1 + _HoverRimBoost * hovered) * (1 + threatened * _AttackStrength * pulse), _SelectionRimStrength, chosen);
                float glow = exp(-ownDistance / max(.001, _RimWidth) * _RimFalloff);
                float edgeLine = (1 - smoothstep(_LineWidth, _LineWidth + 1.2, ownDistance)) * (1 + chosen * .6);
                terrain = Screen(terrain, rimColor * (glow * rimStrength * .6 + edgeLine * _LineStrength * (.55 + .45 * min(rimStrength, 1.4))));
                terrain = Screen(terrain, _SelectionColor.rgb * exp(-ownDistance / max(.001, _SelectionInnerWidth) * 2.5) * _SelectionInnerStrength * chosen);
                // Neighbours carry the selected region's bloom outward across the shared border.
                terrain = Screen(terrain, _SelectionColor.rgb * exp(-selectedDistance / max(.001, _SelectionOuterWidth) * 2.5) * _SelectionOuterStrength * (1 - chosen));
                // A dark hairline seam separates neighbours; the selected contour stays bright.
                terrain *= 1 - _SeamStrength * (1 - smoothstep(0, max(.001, _SeamWidth), ownDistance)) * (1 - chosen);
                terrain *= 1 + _HoverBrighten * hovered;
                fixed4 color = lerp(i.color, fixed4(terrain, sample.a * i.color.a), saturate(i.surface.x));
                color = lerp(color, sample * i.color, _Atmosphere);
                // UV-carried viewport coordinates survive Canvas batching and parent transforms.
                // Never compare batched POSITION with a graphic-local rectangle.
                float2 viewport = lerp(i.selection.yz, (i.texcoord - _UVRect.xy) / _UVRect.zw, _Atmosphere);
                // Inverse radius of the rounded map projection; avoid a rectangular cloud cutout.
                float2 mapPosition = abs(((viewport - _MapViewport.xy) / _MapViewport.zw * 2 - 1) / .98);
                float2 squared = mapPosition * mapPosition;
                float2 fourth = squared * squared;
                float mapRadius = sqrt(sqrt(sqrt(dot(fourth * fourth, float2(1, 1)))));
                float overMap = (1 - smoothstep(.93, 1.075, mapRadius)) * _CloudOverlay;
                float cornerCloud = smoothstep(.65, .95, min(mapPosition.x, mapPosition.y));
                float cloudOpacity = lerp(_CloudInteriorOpacity, _CloudCornerOpacity, cornerCloud);
                color.a *= lerp(1, cloudOpacity, overMap);
                color.rgb = lerp(color.rgb, dot(color.rgb, fixed3(.299,.587,.114)), overMap * .70);
                float2 edge = min(viewport, 1 - viewport) * _SurfaceSize.xy;
                color.a *= smoothstep(0, _EdgeFeather, min(edge.x, edge.y));
                // Fade terrain and the depth silhouette, keeping borders and markers readable.
                float horizon = 1 - smoothstep(.94, 1.015, length(i.surface.zw));
                color.a *= lerp(lerp(1, horizon, abs(i.surface.x)), 1, _Atmosphere);
                // Optional rounded backing/mask uses the same batching-safe viewport coordinates.
                float2 corner = abs((viewport - .5) * _SurfaceSize.xy) - (_SurfaceSize.xy * .5 - _CornerRadius);
                float distance = length(max(corner, 0)) + min(max(corner.x, corner.y), 0) - _CornerRadius;
                color.a *= 1 - smoothstep(-1, 0, distance);
                #ifdef UNITY_UI_CLIP_RECT
                color.a *= UnityGet2DClipping(i.localPosition.xy, _ClipRect);
                #endif
                #ifdef UNITY_UI_ALPHACLIP
                clip(color.a - 0.001);
                #endif
                return color;
            }
            ENDCG
        }
    }
}

