// Факел и струи РСУ (GDD §9.5): аддитивная светящаяся оболочка тела вращения (ProcMesh.Plume: ось −Y, UV.y = доля длины t).
// Раньше — HDRP/Unlit: яркость стенки не зависела от угла, и края конуса читались жёсткой кромкой, факел — плоской
// картинкой. Здесь объём изображается тремя приёмами, без лишних мешей и частиц:
//   край: свечение тонкой стенки ∝ пути взгляда сквозь столб, т. е. ∝ |cos| прицельного угла к оси (R·Vp). Две стенки
//         (Cull Off) в центре дают ту же яркость, что раньше, к силуэту — плавный спад в ноль. Взгляд вдоль оси
//         (из-под сопла) — путь длинный, спад отключается;
//   клочья: трёхмерный шум в осях меша, сносится вдоль струи — у среза ровно, к хвосту струя рвётся;
//   «бочки» (ударные ромбы) у земли: яркие узлы на оси с шагом _ShockStep (доля длины), видны в ромбовидной области
//         вокруг оси; гаснут с номером узла и с высотой (_Shock ставит VesselView по давлению).
// В пустоте струя недорасширена: вершины раздвигаются от оси на (1 + _Expand·√t) — основание остаётся на срезе,
// хвост раскрывается куполом; яркость делится на тот же множитель (столб тот же, площадь больше).
// Эмиссия в нитах × экспозиция (_ExposureWeight = 1, факел) или в долях экрана (0, струи РСУ — RcsJets).
Shader "Kare/Plume HDRP"
{
    Properties
    {
        _EmissiveColorMap ("Gradient (V = t)", 2D) = "white" {}
        [HDR] _EmissiveColor ("Emissive (нит)", Color) = (1, 1, 1, 1)
        _ExposureWeight ("Exposure Weight", Float) = 1
        // Степень спада к силуэту: 1 — путь сквозь цилиндр, больше — плотнее к оси.
        _Limb ("Limb Power", Float) = 1.2
        // Клочья: сила (0 — гладко), ячеек шума на радиус поперёк и вдоль (_Aspect = длина/радиус ставит код), снос ячеек/с.
        _Turb ("Turbulence", Float) = 0.6
        _Aspect ("Length / Radius", Float) = 12
        _Scroll ("Scroll", Float) = 6
        _Seed ("Seed", Float) = 0
        _Expand ("Vacuum Expand", Float) = 0
        // x — сила ромбов, y — шаг (доля длины), z — число узлов до затухания.
        _Shock ("Shock (strength, step, count)", Vector) = (0, 0.2, 5, 0)
        _SoftDist ("Soft Dist", Float) = 0.5
        _NearFade ("Near Fade", Float) = 0.5
    }

    HLSLINCLUDE
    #pragma target 4.5
    #pragma only_renderers d3d11 vulkan metal

    #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"

    TEXTURE2D(_EmissiveColorMap);
    SAMPLER(sampler_EmissiveColorMap);
    float4 _EmissiveColor, _Shock;
    float _ExposureWeight, _Limb, _Turb, _Aspect, _Scroll, _Seed, _Expand, _SoftDist, _NearFade;

    struct Attributes
    {
        float3 positionOS : POSITION;
        float2 uv : TEXCOORD0;
    };

    struct Varyings
    {
        float4 positionCS : SV_POSITION;
        float3 posRWS : TEXCOORD0;
        float3 radialWS : TEXCOORD1;
        float3 axisWS : TEXCOORD2;
        float4 noiseP : TEXCOORD3;   // xyz — точка шума, w — t
        float2 extra : TEXCOORD4;    // x — 1/расширение, y — глубина вида
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

    Varyings Vert(Attributes v)
    {
        Varyings o;
        float t = saturate(v.uv.y);
        float grow = 1 + _Expand * sqrt(t);
        float3 pos = v.positionOS;
        float2 rad = pos.xz;
        pos.xz *= grow;
        o.posRWS = TransformObjectToWorld(pos);
        o.positionCS = TransformWorldToHClip(o.posRWS);
        o.radialWS = TransformObjectToWorldDir(float3(rad.x, 0, rad.y));
        o.axisWS = TransformObjectToWorldDir(float3(0, 1, 0));
        // Шум в осях меша: по окружности — направление от оси (без шва по U), вдоль — t в радиусах, снос — вниз по струе.
        float r = max(length(rad), 1e-4);
        o.noiseP = float4(rad / r * 1.6, t * _Aspect * 0.5 - _Time.y * _Scroll, t);
        o.noiseP.xy += _Seed;
        o.extra = float2(1 / grow, -TransformWorldToView(o.posRWS).z);
        return o;
    }

    float4 Frag(Varyings i) : SV_Target
    {
        float t = i.noiseP.w;
        float3 V = GetWorldSpaceNormalizeViewDir(i.posRWS);
        float3 A = normalize(i.axisWS), R = normalize(i.radialWS);
        float3 Vp = V - A * dot(V, A);
        float lp = length(Vp);
        // |cos| прицельного угла: 1 на оси столба (если смотреть сбоку), 0 у силуэта.
        float c = abs(dot(R, Vp / max(lp, 1e-4)));
        float limb = lerp(1, c, saturate(lp * 1.5));
        float body = pow(limb, _Limb);

        float n = ValueNoise(i.noiseP.xyz) * 0.65 + ValueNoise(i.noiseP.xyz * 2.3 + 17) * 0.35;
        float turb = _Turb * smoothstep(0.04, 0.6, t);
        float mod = lerp(1, 0.3 + 1.4 * n, turb);
        // Хвост рвётся клочьями: порог по шуму сдвигается к срезу с ростом t.
        mod *= saturate((1.1 - t) * 2.5 + (n - 0.5) * 1.6 * _Turb);

        float3 col = SAMPLE_TEXTURE2D_LOD(_EmissiveColorMap, sampler_EmissiveColorMap, float2(0.5, t), 0).rgb;
        float bright = body * mod;

        // Ударные ромбы: k-й узел — на (k + 0,5)·шаг, треугольный профиль ширины вдоль, вокруг оси (b = sin прицельного угла).
        if (_Shock.x > 0)
        {
            float k = t / max(_Shock.y, 1e-3);
            float f = frac(k);
            float tri = 1 - abs(2 * f - 1);
            float b = sqrt(saturate(1 - limb * limb));
            float inside = saturate((tri * 0.55 - b) / 0.08);
            float fade = exp(-floor(k) / max(_Shock.z, 0.5)) * step(k, _Shock.z * 2);
            // Между узлами струя чуть темнее (зона расширения за ударной волной).
            bright *= 1 - 0.35 * _Shock.x * (1 - tri) * fade;
            bright += _Shock.x * inside * tri * fade * 2.5;
        }

        float sceneZ = LinearEyeDepth(LoadCameraDepth(uint2(i.positionCS.xy)), _ZBufferParams);
        float soft = saturate((sceneZ - i.extra.y) / max(_SoftDist, 1e-3));
        float nearFade = saturate((i.extra.y - _NearFade) / max(_NearFade, 1e-3));
        float exposure = lerp(1, GetCurrentExposureMultiplier(), _ExposureWeight);
        float3 c3 = _EmissiveColor.rgb * col * (bright * i.extra.x * soft * nearFade * exposure);
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
