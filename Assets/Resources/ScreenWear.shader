Shader "Custom/ScreenWear"
{
    Properties
    {
        _Color ("Scratch Color", Color) = (0.84, 0.78, 0.67, 1)
        _Intensity ("Intensity", Range(0, 0.25)) = 0.085
        _EdgeWidth ("Edge Width", Range(0.05, 0.35)) = 0.22
        _SpeckStrength ("Speck Strength", Range(0, 1)) = 0.18
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

            float shortScratch(float2 uv, float2 a, float2 b, float width, float seed)
            {
                float2 p = aspectPoint(uv);
                float2 pa = p - aspectPoint(a);
                float2 ba = aspectPoint(b) - aspectPoint(a);
                float denom = max(dot(ba, ba), 1e-5);
                float t = saturate(dot(pa, ba) / denom);
                float d = length(pa - ba * t);
                float aa = max(fwidth(d), 0.00028);
                float stroke = 1.0 - smoothstep(width, width + aa * 1.35, d);

                // Fade both ends and introduce slight intermittent wear so every mark reads as a
                // scuff/scratch rather than a perfectly continuous ruler line.
                float taper = smoothstep(0.02, 0.16, t) * (1.0 - smoothstep(0.78, 0.98, t));
                float breakup = lerp(0.55, 1.0, hash21(float2(floor(t * 9.0), seed)));
                return stroke * taper * breakup;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float2 uv = IN.uv;

                // Keep wear peripheral. The middle of the tactical view remains effectively clean.
                float edgeDistance = min(min(uv.x, 1.0 - uv.x), min(uv.y, 1.0 - uv.y));
                float edgeMask = 1.0 - smoothstep(_EdgeWidth * 0.24, _EdgeWidth, edgeDistance);

                // Deliberately short, irregular marks. None of these spans enough screen space to
                // read as the long white stripes from the previous version.
                float scratches = 0.0;
                scratches = max(scratches, shortScratch(uv, float2(0.018, 0.12), float2(0.036, 0.18), 0.00062, 1.0));
                scratches = max(scratches, shortScratch(uv, float2(0.026, 0.29), float2(0.052, 0.34), 0.00055, 2.0));
                scratches = max(scratches, shortScratch(uv, float2(0.015, 0.63), float2(0.043, 0.69), 0.00058, 3.0));
                scratches = max(scratches, shortScratch(uv, float2(0.038, 0.81), float2(0.064, 0.85), 0.00050, 4.0));

                scratches = max(scratches, shortScratch(uv, float2(0.958, 0.10), float2(0.982, 0.15), 0.00058, 5.0));
                scratches = max(scratches, shortScratch(uv, float2(0.944, 0.31), float2(0.974, 0.36), 0.00054, 6.0));
                scratches = max(scratches, shortScratch(uv, float2(0.951, 0.59), float2(0.981, 0.65), 0.00060, 7.0));
                scratches = max(scratches, shortScratch(uv, float2(0.936, 0.79), float2(0.968, 0.83), 0.00050, 8.0));

                scratches = max(scratches, shortScratch(uv, float2(0.10, 0.978), float2(0.16, 0.955), 0.00050, 9.0));
                scratches = max(scratches, shortScratch(uv, float2(0.31, 0.969), float2(0.37, 0.944), 0.00046, 10.0));
                scratches = max(scratches, shortScratch(uv, float2(0.66, 0.975), float2(0.72, 0.948), 0.00048, 11.0));
                scratches = max(scratches, shortScratch(uv, float2(0.84, 0.972), float2(0.90, 0.945), 0.00046, 12.0));

                scratches = max(scratches, shortScratch(uv, float2(0.12, 0.028), float2(0.18, 0.050), 0.00048, 13.0));
                scratches = max(scratches, shortScratch(uv, float2(0.39, 0.036), float2(0.45, 0.060), 0.00044, 14.0));
                scratches = max(scratches, shortScratch(uv, float2(0.70, 0.030), float2(0.76, 0.054), 0.00048, 15.0));
                scratches = max(scratches, shortScratch(uv, float2(0.86, 0.044), float2(0.91, 0.070), 0.00044, 16.0));

                // Tiny paired scuffs make the wear feel abraded instead of like isolated vector
                // strokes, while still staying far below the salience of gameplay markers.
                scratches = max(scratches, shortScratch(uv, float2(0.030, 0.465), float2(0.071, 0.490), 0.00038, 17.0));
                scratches = max(scratches, shortScratch(uv, float2(0.034, 0.474), float2(0.066, 0.503), 0.00034, 18.0));
                scratches = max(scratches, shortScratch(uv, float2(0.930, 0.455), float2(0.970, 0.480), 0.00038, 19.0));
                scratches = max(scratches, shortScratch(uv, float2(0.935, 0.468), float2(0.965, 0.495), 0.00034, 20.0));

                // Sparse, faint edge flecks. These should be perceived as worn coating only when
                // looking for them, not as film grain over the image.
                float2 cell = floor(uv * float2(420.0, 240.0));
                float speckRnd = hash21(cell);
                float speck = smoothstep(0.9960, 0.9996, speckRnd) * _SpeckStrength;

                float marks = saturate(scratches + speck * 0.24);
                float alpha = marks * edgeMask * _Intensity * _Color.a;
                return half4(_Color.rgb, alpha);
            }
            ENDHLSL
        }
    }
}
