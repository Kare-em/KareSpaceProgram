// Полярные сияния (GDD §2.7, §9.5): вертикальные «занавесы» вдоль овала вокруг магнитного полюса (AuroraView).
// Меш — кольцо из нескольких параллельных лент в осях овала (ось +Y = магнитная ось), UV.x — азимут (0..1),
// UV.y — высота в полосе (0 — нижняя кромка, 1 — верх), UV2.x — номер ленты (зерно шума).
// Свечение — только эмиссия, аддитивно; тонкий слой газа: яркость ∝ пути взгляда сквозь лист ∝ 1/|N·V|
// (с ребра лист ярче, чем в лоб). Складки — смещение вершин поперёк листа шумом по окружности (без шва по U).
// Лучи — вертикальные штрихи по азимуту, мерцание — быстрый шум. Дневная сторона гасится по _SunDirOS (в осях
// меша, ставит AuroraView): днём сияние есть, но тусклое, а экспозиция дневного неба его и так съедает.
// Выход — нит × экспозиция HDRP: ночью (экспозиция ~×50) видно, днём (×1e-4) — нет, без отдельной логики.
Shader "Kare/Aurora HDRP"
{
    Properties
    {
        [HDR] _ColorLow ("Low Edge (нит)", Color) = (0.3, 1, 0.45, 1)
        [HDR] _ColorHigh ("High Edge (нит)", Color) = (1, 0.15, 0.2, 1)
        // x — доля высоты на нарастание нижней кромки, y — спад нижнего цвета (доля высоты),
        // z — центр верхнего цвета, w — его ширина.
        _Profile ("Profile", Vector) = (0.04, 0.3, 0.7, 0.25)
        // x — амплитуда складок (ед. меша = радиус тела), y — крупная волна, z — частота лучей (на окружность), w — скорость.
        _Fold ("Fold", Vector) = (0.004, 0.012, 300, 1)
        _SunDirOS ("Sun Dir (OS)", Vector) = (1, 0, 0, 0)
        _DaySide ("Day Side Factor", Float) = 0.2
        _EdgeMax ("Edge-on Max", Float) = 6
        _NearFade ("Near Fade", Float) = 2000
    }

    HLSLINCLUDE
    #pragma target 4.5
    #pragma only_renderers d3d11 vulkan metal

    #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"

    float4 _ColorLow, _ColorHigh, _Profile, _Fold, _SunDirOS;
    float _DaySide, _EdgeMax, _NearFade;

    struct Attributes
    {
        float3 positionOS : POSITION;
        float3 normalOS : NORMAL;
        float2 uv : TEXCOORD0;
        float2 uv2 : TEXCOORD1;
    };

    struct Varyings
    {
        float4 positionCS : SV_POSITION;
        float3 posRWS : TEXCOORD0;
        float3 normalWS : TEXCOORD1;
        float4 data : TEXCOORD2;     // x — азимут (рад), y — высота 0..1, z — зерно ленты, w — день/ночь (0..1)
        float depth : TEXCOORD3;
    };

    float Hash13(float3 p)
    {
        p = frac(p * 0.1031);
        p += dot(p, p.zyx + 31.32);
        return frac((p.x + p.y) * p.z);
    }

    float ValueNoise(float3 p)
    {
        float3 i = floor(p), f = frac(p);
        f = f * f * (3 - 2 * f);
        float a = lerp(Hash13(i), Hash13(i + float3(1, 0, 0)), f.x);
        float b = lerp(Hash13(i + float3(0, 1, 0)), Hash13(i + float3(1, 1, 0)), f.x);
        float c = lerp(Hash13(i + float3(0, 0, 1)), Hash13(i + float3(1, 0, 1)), f.x);
        float d = lerp(Hash13(i + float3(0, 1, 1)), Hash13(i + float3(1, 1, 1)), f.x);
        return lerp(lerp(a, b, f.y), lerp(c, d, f.y), f.z);
    }

    // Шум по окружности: точка (cos φ, sin φ)·freq — без шва на φ = 2π.
    float RingNoise(float phi, float freq, float t, float seed)
    {
        return ValueNoise(float3(cos(phi) * freq + seed, sin(phi) * freq - seed, t));
    }

    Varyings Vert(Attributes v)
    {
        Varyings o;
        float phi = v.uv.x * 6.2831853;
        float seed = v.uv2.x * 7.31;
        float tm = _Time.y * _Fold.w;
        // Складки: крупная волна (вихри овала) + мелкие завитки, медленно ползут — смещение поперёк листа.
        float fold = (RingNoise(phi, 6, tm * 0.03, seed) - 0.5) * 2 * _Fold.y
                   + (RingNoise(phi, 45, tm * 0.15, seed + 3) - 0.5) * 2 * _Fold.x;
        float3 pos = v.positionOS + v.normalOS * fold;
        o.posRWS = TransformObjectToWorld(pos);
        o.positionCS = TransformWorldToHClip(o.posRWS);
        o.normalWS = TransformObjectToWorldDir(v.normalOS);
        // День/ночь — по точке под сиянием: косинус зенитного угла Солнца.
        float mu = dot(normalize(v.positionOS), _SunDirOS.xyz);
        o.data = float4(phi, v.uv.y, seed, smoothstep(-0.12, 0.15, mu));
        o.depth = -TransformWorldToView(o.posRWS).z;
        return o;
    }

    float4 Frag(Varyings i) : SV_Target
    {
        float phi = i.data.x, y = i.data.y, seed = i.data.z;
        float tm = _Time.y * _Fold.w;

        // Вертикальный профиль: резкая нижняя кромка (кислород 557,7 нм у Земли), выше — спад; вверху — широкий
        // слабый пояс второго цвета (630 нм). Верх листа размыт, чтобы не было видно края меша.
        float rise = smoothstep(0, _Profile.x, y);
        float low = rise * exp(-y / max(_Profile.y, 1e-3));
        float hz = (y - _Profile.z) / max(_Profile.w, 1e-3);
        float high = exp(-hz * hz) * smoothstep(1, 0.8, y);
        float3 col = _ColorLow.rgb * low + _ColorHigh.rgb * high;

        // Лучи: частые вертикальные штрихи по азимуту, медленно бегут вдоль дуги; к верху вытягиваются и бледнеют.
        float rays = RingNoise(phi, _Fold.z, tm * 0.4 + y * 0.6, seed);
        rays = lerp(0.35, 1.6, rays * rays);
        // Мерцание и пятна: дуги рвутся на куски, куски медленно перетекают.
        float shimmer = 0.75 + 0.5 * RingNoise(phi, _Fold.z * 0.15, tm * 2.5, seed + 9);
        float patches = smoothstep(0.3, 0.75, RingNoise(phi, 3.5, tm * 0.02, seed + 5));
        float bright = rays * shimmer * patches;

        // Тонкий лист: путь взгляда ∝ 1/|N·V|, потолок — когда лист виден с ребра (толщина и складки ограничивают путь).
        float3 V = GetWorldSpaceNormalizeViewDir(i.posRWS);
        float nv = abs(dot(normalize(i.normalWS), V));
        float edge = min(1 / max(nv, 1e-3), _EdgeMax) / 2;

        float day = lerp(1, _DaySide, i.data.w);
        float nearFade = saturate(i.depth / max(_NearFade, 1e-3) - 0.5);
        float3 c3 = col * (bright * edge * day * nearFade * GetCurrentExposureMultiplier());
        return float4(c3, 0);
    }
    ENDHLSL

    SubShader
    {
        Tags { "RenderPipeline" = "HDRenderPipeline" "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Pass
        {
            Name "ForwardOnly"
            Tags { "LightMode" = "ForwardOnly" }
            Blend One One
            ZWrite Off
            ZTest LEqual
            Cull Off
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            ENDHLSL
        }
    }
}
