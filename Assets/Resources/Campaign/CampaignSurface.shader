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
        _MapViewport ("Map viewport in overlay", Vector) = (0,0,1,1)
        _CornerRadius ("Panel corner radius", Float) = 0
        _UVRect ("Overlay UV rect", Vector) = (0,0,1,1)
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
            #pragma target 2.0
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
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct v2f
            {
                float4 vertex : SV_POSITION;
                fixed4 color : COLOR;
                float2 texcoord : TEXCOORD0;
                float4 localPosition : TEXCOORD1;
                float4 surface : TEXCOORD2;
                float3 selection : TEXCOORD3;
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
            float4 _MapViewport;
            float _CornerRadius;
            float4 _UVRect;
            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.localPosition = v.vertex;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.texcoord = v.texcoord;
                o.surface = v.surface;
                o.selection = float3(v.selection.x, v.selection.zw);
                o.color = v.color * _Color;
                return o;
            }
            fixed4 frag(v2f i) : SV_Target
            {
                // Borders and markers remain solid; only terrain triangles sample the map texture.
                fixed4 sample = tex2D(_MainTex, i.texcoord) + _TextureSampleAdd;
                // Preserve terrain detail while washing its warm paper palette with faction color.
                // A common reference brightness preserves the deliberately muted olive palette.
                fixed luminance = dot(sample.rgb, fixed3(.299,.587,.114));
                fixed3 terrain = lerp(sample.rgb, luminance * i.color.rgb / .65, .60);
                float radiusSquared = dot(i.surface.zw, i.surface.zw);
                float limb = smoothstep(.38, 1.0, radiusSquared);
                terrain *= (1.04 + .07 * i.surface.w - .34 * limb) * i.surface.y;
                // A restrained dusty atmospheric rim follows the same continuous map coordinates.
                terrain = lerp(terrain, fixed3(.49,.46,.40), smoothstep(.72, 1.0, radiusSquared) * .22);
                // Contour glow fades inward; its distance weights are cached on the region mesh.
                terrain = lerp(terrain, fixed3(1,.72,.28), i.selection.x * .64);
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
                float overMap = (1 - smoothstep(.99, 1.075, mapRadius)) * _CloudOverlay;
                color.a *= lerp(1, .10, overMap);
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
