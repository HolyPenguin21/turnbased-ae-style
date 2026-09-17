Shader "Custom/ScreenWear"
{
    Properties
    {
        _Color ("Scratch Color", Color) = (0.84, 0.78, 0.67, 1)
        _Intensity ("Intensity", Range(0, 0.25)) = 0.13
        _EdgeWidth ("Edge Width", Range(0.05, 0.35)) = 0.20
        _SpeckStrength ("Speck Strength", Range(0, 1)) = 0.28
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
                float aa = max(fwidth(d), 0.00035);
                return 1.0 - smoothstep(width, width + aa * 1.4, d);
            }

            float brokenLine(float2 uv, float2 a, float2 b, float width, float seed)
            {
                float line = lineSegment(uv, a, b, width);
                float2 dir = normalize(b - a + 1e-5);
                float along = dot(uv - a, dir);
                float breakup = hash21(float2(floor(along * 78.0), seed));
                float keep = smoothstep(0.16, 0.38, breakup);
                return line * keep;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float2 uv = IN.uv;

                // Wear belongs to the protective frame/glass, not the centre of the tactical
                // view. Marks fade rapidly away from the outer 15-20% of the screen.
                float edgeDistance = min(min(uv.x, 1.0 - uv.x), min(uv.y, 1.0 - uv.y));
                float edgeMask = 1.0 - smoothstep(_EdgeWidth * 0.30, _EdgeWidth, edgeDistance);

                // Widths are deliberately around a pixel at normal game resolutions. The first
                // version used ~0.0005, which rasterised below one pixel and was effectively
                // invisible on the actual 1000px-wide gameplay capture.
                float scratches = 0.0;
                scratches = max(scratches, brokenLine(uv, float2(0.016, 0.06), float2(0.030, 0.43), 0.00120, 1.0));
                scratches = max(scratches, brokenLine(uv, float2(0.030, 0.51), float2(0.052, 0.91), 0.00100, 2.0));
                scratches = max(scratches, brokenLine(uv, float2(0.963, 0.08), float2(0.981, 0.46), 0.00116, 3.0));
                scratches = max(scratches, brokenLine(uv, float2(0.946, 0.55), float2(0.985, 0.93), 0.00102, 4.0));
                scratches = max(scratches, brokenLine(uv, float2(0.07, 0.976), float2(0.32, 0.961), 0.00092, 5.0));
                scratches = max(scratches, brokenLine(uv, float2(0.67, 0.984), float2(0.92, 0.956), 0.00092, 6.0));
                scratches = max(scratches, brokenLine(uv, float2(0.10, 0.020), float2(0.31, 0.034), 0.00090, 7.0));
                scratches = max(scratches, brokenLine(uv, float2(0.72, 0.021), float2(0.93, 0.045), 0.00094, 8.0));

                // A few short secondary strokes stop the pattern reading as four long ruler marks.
                scratches = max(scratches, brokenLine(uv, float2(0.010, 0.70), float2(0.085, 0.76), 0.00078, 9.0));
                scratches = max(scratches, brokenLine(uv, float2(0.912, 0.74), float2(0.992, 0.68), 0.00080, 10.0));
                scratches = max(scratches, brokenLine(uv, float2(0.18, 0.991), float2(0.26, 0.944), 0.00072, 11.0));
                scratches = max(scratches, brokenLine(uv, float2(0.80, 0.010), float2(0.87, 0.068), 0.00072, 12.0));

                // Sparse static flecks, weaker than the scratches and still edge-masked.
                float2 cell = floor(uv * float2(330.0, 190.0));
                float speckRnd = hash21(cell);
                float speck = smoothstep(0.9915, 0.9988, speckRnd) * _SpeckStrength;

                float marks = saturate(scratches + speck * 0.32);
                float alpha = marks * edgeMask * _Intensity * _Color.a;
                return half4(_Color.rgb, alpha);
            }
            ENDHLSL
        }
    }
}
