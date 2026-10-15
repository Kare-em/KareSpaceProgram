// Кольца планет (GDD §9.4): плоский слой частиц с оптической толщиной τ(r) из RingProfiles, свет считается здесь.
// Освещённая сторона — однократное рассеяние в слое (Chandrasekhar): I/F = S·μ0/(μ0+μ)·(1 − e^(−τ(1/μ0+1/μ))).
// Теневая сторона — свет, прошедший сквозь слой: I/F = S·μ0/(μ0−μ)·(e^(−τ/μ0) − e^(−τ/μ)) — плотное B тёмное,
//   тонкие C и деление Кассини светятся.
// S — рассеяние частицы: крупные ледяные глыбы рассеивают назад (Хеньи—Гринстейн g < 0, всплеск в оппозиции),
//   пыль — резко вперёд (g ≈ 0,9): против Солнца загораются D, F, щели Энке и деление Роша.
// Тень планеты на кольцах — цилиндр радиуса планеты с полутенью от углового размера Солнца.
// Непрозрачность 1 − e^(−τ/μ): у B любой угол — стена, у C с ребра ярче, сверху — прозрачнее.
// Дальняя плоскость 1e7: дальний край колец у сжатого Сатурна выходит за неё — глубина вершин прижимается к ней,
// а заслонение телами проверяется по буферу глубины вручную (ZTest Always).
Shader "Kare/Planet Rings HDRP"
{
    Properties
    {
        _TauTex ("Tau (R, нормирована)", 2D) = "black" {}
        _ColorTex ("Albedo (RGB) + доля пыли (A)", 2D) = "white" {}
        _TauScale ("Tau Scale", Float) = 1
        // Пределы текстуры, в радиусах сферы (единица меша).
        _RIn ("R In", Float) = 1
        _ROut ("R Out", Float) = 2
        _PlanetR ("Planet Radius", Float) = 1
        // Направление на Солнце в осях меша (xyz) и угловой радиус Солнца, рад (w).
        _SunDirOS ("Sun Dir (local) + angular radius", Vector) = (0, 1, 0, 0.0005)
        // Освещённость, лк × цвет 6500 K (SunLight.ColorTemperature) — без затмения: кольца в тени борта горят.
        _SunE ("Sun Illuminance (lux, rgb)", Vector) = (1400, 1400, 1400, 0)
        // x — g крупных частиц, y — g пыли, z — альбедо однократного рассеяния пыли, w — сила оппозиции.
        _Phase ("Phase", Vector) = (-0.25, 0.9, 0.6, 0.35)
    }

    HLSLINCLUDE
    #pragma target 4.5
    #pragma only_renderers d3d11 vulkan metal

    #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"

    TEXTURE2D(_TauTex); SAMPLER(sampler_TauTex);
    TEXTURE2D(_ColorTex); SAMPLER(sampler_ColorTex);
    float _TauScale, _RIn, _ROut, _PlanetR;
    float4 _SunDirOS, _SunE, _Phase;

    // Ширина оппозиционного всплеска, рад: у колец Сатурна он узкий, ≈1–2° (French 2007).
    #define OppositionWidth 0.03
    // Нижний предел μ: с ребра 1/μ взлетает, τ/μ — к бесконечности, а нужна просто «стена» в ноль пикселей.
    #define MuMin 1e-4
    // Минимальная полутень тени планеты, в радиусах сферы: иначе край — лесенка в пиксель.
    #define ShadowSoftMin 0.002

    struct Attributes { float3 positionOS : POSITION; };

    struct Varyings
    {
        float4 positionCS : SV_POSITION;
        float3 posOS : TEXCOORD0;
        float viewZ : TEXCOORD1;
    };

    Varyings Vert(Attributes v)
    {
        Varyings o;
        float3 rws = TransformObjectToWorld(v.positionOS);
        o.positionCS = TransformWorldToHClip(rws);
        // Не обрезать дальней плоскостью: вершину прижимаем к ней, заслонение решает Frag.
    #if UNITY_REVERSED_Z
        o.positionCS.z = max(o.positionCS.z, o.positionCS.w * 1e-7);
    #else
        o.positionCS.z = min(o.positionCS.z, o.positionCS.w * (1 - 1e-7));
    #endif
        o.posOS = v.positionOS;
        o.viewZ = -TransformWorldToView(rws).z;
        return o;
    }

    // Хеньи—Гринстейн, нормированная так, что среднее по сфере = 1.
    float HG(float g, float cosTheta)
    {
        float d = 1 + g * g - 2 * g * cosTheta;
        return (1 - g * g) / (d * sqrt(d));
    }

    float4 Frag(Varyings i) : SV_Target
    {
        float r = length(i.posOS.xz);
        if (r < _RIn || r > _ROut) discard;

        // Заслонение телами (планета, луны, борт) — по глубине непрозрачных; 0 — пустое небо.
        float raw = LoadCameraDepth(uint2(i.positionCS.xy));
    #if UNITY_REVERSED_Z
        bool sky = raw <= 0;
    #else
        bool sky = raw >= 1;
    #endif
        if (!sky && i.viewZ > LinearEyeDepth(raw, _ZBufferParams) * 1.0001) discard;

        float u = (r - _RIn) / (_ROut - _RIn);
        float tau = SAMPLE_TEXTURE2D(_TauTex, sampler_TauTex, float2(u, 0.5)).r * _TauScale;
        float4 alb = SAMPLE_TEXTURE2D(_ColorTex, sampler_ColorTex, float2(u, 0.5));
        if (tau <= 0) discard;

        float3 camOS = mul(UNITY_MATRIX_I_M, float4(0, 0, 0, 1)).xyz;
        float3 V = normalize(camOS - i.posOS);
        float3 L = normalize(_SunDirOS.xyz);
        float mu = max(abs(V.y), MuMin), mu0 = abs(L.y);
        float alpha = 1 - exp(-tau / mu);

        // Рассеяние частиц. cosθ — угол рассеяния: назад (на Солнце за спиной) θ = 180°.
        float cosA = dot(L, V), cosT = -cosA;
        float phaseAngle = acos(clamp(cosA, -1, 1));
        float big = HG(_Phase.x, cosT) / HG(_Phase.x, -1) * (1 + _Phase.w * exp(-phaseAngle / OppositionWidth));
        float3 Sbig = 2 * alb.rgb * big;
        float lumA = max(dot(alb.rgb, float3(0.2126, 0.7152, 0.0722)), 1e-3);
        float3 Sdust = _Phase.z * HG(_Phase.y, cosT) * 0.25 * (alb.rgb / lumA);
        float3 S = lerp(Sbig, Sdust, alb.a);

        float IF = 0;
        if (mu0 > 1e-5)
        {
            if (V.y * L.y > 0)
                IF = mu0 / (mu0 + mu) * (1 - exp(-tau * (1 / mu0 + 1 / mu)));
            else
            {
                float d = mu0 - mu;
                // При μ ≈ μ0 разность экспонент делится на ноль — предел (τ/μ0)·e^(−τ/μ0).
                IF = abs(d) > 1e-4 ? mu0 * (exp(-tau / mu0) - exp(-tau / mu)) / d : tau / mu0 * exp(-tau / mu0);
            }
        }

        // Тень планеты: точка за планетой (b < 0) и ближе её радиуса к оси Солнце—планета.
        float3 P = i.posOS;
        float b = dot(P, L), lit = 1;
        if (b < 0)
        {
            float dist = length(P - b * L);
            float w = max(_SunDirOS.w * -b, ShadowSoftMin);
            lit = smoothstep(_PlanetR - w, _PlanetR + w, dist);
        }

        // I/F → яркость: L = I/F · E / π, в нитах; буфер HDRP предэкспонирован.
        float3 c = S * (IF * lit / PI) * _SunE.rgb * GetCurrentExposureMultiplier();
        return float4(c, alpha);
    }
    ENDHLSL

    SubShader
    {
        Tags { "RenderPipeline" = "HDRenderPipeline" "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Pass
        {
            Name "ForwardOnly"
            Tags { "LightMode" = "ForwardOnly" }
            // Предумноженная альфа: цвет — свет самого слоя, альфа — ослабление того, что за ним.
            Blend One OneMinusSrcAlpha
            ZWrite Off
            ZTest Always
            Cull Off
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            ENDHLSL
        }
    }
}
