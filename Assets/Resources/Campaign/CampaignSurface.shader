Shader "Game/UI/CampaignSurface"
{
    Properties
    {
        [PerRendererData] _MainTex ("Terrain", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _SurfaceSize ("Surface size", Vector) = (1360,740,0,0)
        _EdgeFeather ("Edge feather (canvas units)", Float) = 32
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
                fixed4 color = lerp(i.color, fixed4(terrain, sample.a * i.color.a), i.surface.x);
                // UV-carried viewport coordinates survive Canvas batching and parent transforms.
                // Never compare batched POSITION with a graphic-local rectangle.
                float2 edge = min(i.selection.yz, 1 - i.selection.yz) * _SurfaceSize.xy;
                color.a *= smoothstep(0, _EdgeFeather, min(edge.x, edge.y));
                // The elliptical limb has a soft map-space horizon.
                // Original map coordinates keep the horizon independent of terrain UV scale.
                float horizon = 1 - smoothstep(.91, 1.0, length(i.surface.zw));
                color.a *= horizon;
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
