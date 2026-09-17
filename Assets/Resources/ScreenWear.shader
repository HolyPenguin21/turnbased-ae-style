Shader "Custom/ScreenWear"
{
    Properties
    {
        _Color ("Scratch Color", Color) = (0.82, 0.76, 0.65, 1)
        _Intensity ("Intensity", Range(0, 0.2)) = 0.065
        _EdgeWidth ("Edge Width", Range(0.05, 0.35)) = 0.17
        _SpeckStrength ("Speck Strength", Range(0, 1)) = 0.22
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent+100"
            "IgnoreProjector" = "True"
            "RenderType" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
        }

        Cull Off
        Lighting Off
        ZWrite Off
        ZTest Always
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            float4 _Color;
            float _Intensity;
            float _EdgeWidth;
            float _SpeckStrength;

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = IN.uv;
                return OUT;
            }

            float hash21(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

            float2 aspectPoint(float2 uv)
            {
                float aspect = _ScreenParams.x / max(_ScreenParams.y, 1.0);
                return float2((uv.x - 0.5) * aspect, uv.y - 0.5);
            }

            float lineSegment(float2 uv, float2 a, float2 b, float width)
            {
                float2 p = aspectPoint(uv);
                float2 pa = p - aspectPoint(a);
                float2 ba = aspectPoint(b) - aspectPoint(a);
                float denom = max(dot(ba, ba), 1e-5);
                float h = saturate(dot(pa, ba) / denom);
                float d = length(pa - ba * h);
                float aa = max(fwidth(d), 0.0002);
                return 1.0 - smoothstep(width, width + aa * 1.5, d);
            }

            float brokenLine(float2 uv, float2 a, float2 b, float width, float seed)
            {
                float line = lineSegment(uv, a, b, width);
                float2 dir = normalize(b - a + 1e-5);
                float along = dot(uv - a, dir);
                float breakup = hash21(float2(floor(along * 95.0), seed));
                float keep = smoothstep(0.18, 0.42, breakup);
                return line * keep;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float2 uv = IN.uv;

                // Only the outer frame should carry visible wear. The centre of the board and
                // cards stays almost completely clean, preserving clarity during normal play.
                float edgeDistance = min(min(uv.x, 1.0 - uv.x), min(uv.y, 1.0 - uv.y));
                float edgeMask = 1.0 - smoothstep(_EdgeWidth * 0.28, _EdgeWidth, edgeDistance);

                // A small fixed set of long, thin scratches gives a physical screen/glass feel
                // without producing animated visual noise. The broken mask prevents ruler-straight
                // lines and keeps the marks intermittent like worn coating rather than cracks.
                float scratches = 0.0;
                scratches = max(scratches, brokenLine(uv, float2(0.018, 0.08), float2(0.028, 0.43), 0.00055, 1.0));
                scratches = max(scratches, brokenLine(uv, float2(0.031, 0.54), float2(0.049, 0.91), 0.00048, 2.0));
                scratches = max(scratches, brokenLine(uv, float2(0.965, 0.10), float2(0.982, 0.47), 0.00055, 3.0));
                scratches = max(scratches, brokenLine(uv, float2(0.948, 0.57), float2(0.986, 0.92), 0.00050, 4.0));
                scratches = max(scratches, brokenLine(uv, float2(0.08, 0.976), float2(0.31, 0.963), 0.00046, 5.0));
                scratches = max(scratches, brokenLine(uv, float2(0.69, 0.986), float2(0.91, 0.957), 0.00044, 6.0));
                scratches = max(scratches, brokenLine(uv, float2(0.11, 0.018), float2(0.30, 0.031), 0.00042, 7.0));
                scratches = max(scratches, brokenLine(uv, float2(0.74, 0.022), float2(0.92, 0.043), 0.00046, 8.0));

                // Sparse flecks are deliberately much weaker than the scratches. They are static
                // and edge-biased, so the overlay never turns into film grain over the playfield.
                float2 cell = floor(uv * float2(360.0, 210.0));
                float speckRnd = hash21(cell);
                float speck = smoothstep(0.994, 0.9995, speckRnd) * _SpeckStrength;

                float marks = saturate(scratches + speck * 0.35);
                float alpha = marks * edgeMask * _Intensity * _Color.a;

                return half4(_Color.rgb, alpha);
            }
            ENDHLSL
        }
    }
}
