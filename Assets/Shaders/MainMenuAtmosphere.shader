Shader "Game/UI/MainMenuAtmosphere"
{
    Properties
    {
        [PerRendererData] _MainTex ("Dust atlas", 2D) = "white" {}
        _OcclusionTex ("Architecture alpha mask", 2D) = "black" {}
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
        Tags { "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" "PreviewType"="Plane" }
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
            #pragma target 2.0
            #pragma multi_compile_local _ UNITY_UI_CLIP_RECT
            #pragma multi_compile_local _ UNITY_UI_ALPHACLIP
            #include "UnityCG.cginc"
            #include "UnityUI.cginc"
            struct appdata
            {
                float4 vertex : POSITION;
                fixed4 color : COLOR;
                float2 uv : TEXCOORD0;
                float2 imageUV : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct v2f
            {
                float4 vertex : SV_POSITION;
                fixed4 color : COLOR;
                float2 uv : TEXCOORD0;
                float2 imageUV : TEXCOORD1;
                float4 localPosition : TEXCOORD2;
                UNITY_VERTEX_OUTPUT_STEREO
            };
            sampler2D _MainTex;
            sampler2D _OcclusionTex;
            float4 _ClipRect;
            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.localPosition = v.vertex;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.color = v.color;
                o.uv = v.uv;
                o.imageUV = v.imageUV;
                return o;
            }
            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 result = tex2D(_MainTex, i.uv) * i.color;
                // The mask uses the background's UVs, not each cloud's animated atlas UVs.
                result.a *= 1.0 - tex2D(_OcclusionTex, i.imageUV).a;
                // Rotating debris may reach the viewport edge; clip it without a UI Mask object.
                result.a *= step(0.0, i.imageUV.x) * step(i.imageUV.x, 1.0)
                    * step(0.0, i.imageUV.y) * step(i.imageUV.y, 1.0);
                #ifdef UNITY_UI_CLIP_RECT
                result.a *= UnityGet2DClipping(i.localPosition.xy, _ClipRect);
                #endif
                #ifdef UNITY_UI_ALPHACLIP
                clip(result.a - .001);
                #endif
                return result;
            }
            ENDCG
        }
    }
}
