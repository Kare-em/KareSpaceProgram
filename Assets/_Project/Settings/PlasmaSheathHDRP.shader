// Ударный слой входа в атмосферу (GDD §4.6): тонкая светящаяся оболочка, снятая с деталей борта (VesselView.BuildSheathMesh):
// силуэт корпуса и крыльев, раздутый по нормалям. Меш статичный, от направления полёта зависит только эта вершинная
// стадия: _Flow — курс борта относительно воздуха в осях меша. Поэтому один и тот же меш годится для капсулы носом вперёд,
// «Старшипа» брюхом вперёд и ускорителя хвостом вперёд — раньше меш пересобирался под поток и был телом вращения от лба.
//   наветренная сторона (N·F > 0): слой раздут, ярче всего там, где поверхность смотрит в поток (лоб, брюхо, кромка);
//   подветренная (N·F < 0): вершины утянуты назад по потоку — оболочка вытягивается в след и гаснет в ноль.
// Яркость и цвет считаются здесь, а не в вершинах: они зависят от потока. В цвете вершины остаётся только вес гашения.
// Свечение тонкого слоя пропорционально пути взгляда сквозь него, 1/|N·V|: к силуэту ярче, лоб в фас — тусклее.
// У самого края спад в ноль, иначе силуэт оболочки читается жёсткой кромкой «мыльного пузыря».
Shader "Kare/Plasma Sheath HDRP"
{
    Properties
    {
        [HDR] _EmissiveColor ("Emissive (нит)", Color) = (1, 0.5, 0.3, 1)
        // Касание корпуса и подход камеры, м (VesselView ставит в радиусах борта).
        _SoftDist ("Soft Dist", Float) = 0.2
        _NearFade ("Near Fade", Float) = 2
        // Курс борта в осях меша (xyz): оболочка — на стороне, куда летит борт; след — в обратную.
        _Flow ("Flow (local)", Vector) = (0, 1, 0, 0)
        // Общий множитель толщины слоя (в вершинах — толщина в м у этой детали) и длина растяжки в след, м.
        _Inflate ("Inflate", Float) = 1
        _WakeLen ("Wake Length", Float) = 3
        // x — доля толщины на лбу (у сферы Δ ≈ 0,14 r против 0,35 r у плеча), y — на подветренной стороне,
        // z — степень наветренности для яркости, w — степень, с которой подветренная сторона уходит в след.
        _Shape ("Shape", Vector) = (0.4, 0.6, 2, 2)
        // x — яркость поверхности вдоль потока (плечо), y — общий множитель по площади наветренной стороны:
        // у плоского брюха площадь слоя в разы больше, чем у капсулы того же радиуса, и оно иначе выбеливает кадр.
        _Glow ("Glow", Vector) = (0.2, 1, 0, 0)
        // Цвета слоя: у лба горячий (светлее), на плече и в следе остывший — Vector, чтобы блок свойств не гнал их гамма→линейка.
        _HotTint ("Hot Tint", Vector) = (1, 0.78, 0.58, 1)
        _CoolTint ("Cool Tint", Vector) = (1, 0.45, 0.27, 1)
    }

    HLSLINCLUDE
    #pragma target 4.5
    #pragma only_renderers d3d11 vulkan metal

    #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"

    float4 _EmissiveColor;
    float _SoftDist, _NearFade;
    float4 _Flow, _Shape, _Glow, _HotTint, _CoolTint;
    float _Inflate, _WakeLen;

    // Пик свечения у силуэта, |N·V| = LimbEdge: 1/LimbEdge ≈ 3,3 — толщина слоя к радиусу ≈ 0,1 (пара: ShockStandoff).
    #define LimbEdge 0.3

    struct Attributes
    {
        float3 positionOS : POSITION;
        float3 normalOS : NORMAL;
        float4 color : COLOR;
        float2 uv : TEXCOORD0;   // x — толщина слоя у этой детали, м
    };

    struct Varyings
    {
        float4 positionCS : SV_POSITION;
        float3 tint : COLOR;
        float3 normalWS : TEXCOORD0;
        float3 posRWS : TEXCOORD1;
        float viewZ : TEXCOORD2;
    };

    Varyings Vert(Attributes v)
    {
        Varyings o;
        float3 F = normalize(_Flow.xyz);
        float3 N = normalize(v.normalOS);
        float nf = dot(N, F);
        float wind = saturate(nf), lee = saturate(-nf);
        // Слой: на лбу тоньше (ударная волна ближе к телу), на плече — полный, с подветренной стороны — ужимается.
        float layer = v.uv.x * _Inflate * lerp(lerp(1, _Shape.y, lee), _Shape.x, wind);
        // След: нормаль смотрит назад по потоку — вершина уезжает назад. Плечо (N ⟂ F) остаётся на месте,
        // торец кормы получает полную длину и сходится в хвост.
        float3 pos = v.positionOS + N * layer - F * (_WakeLen * pow(lee, _Shape.w));
        // Яркость: у наветренной стороны растёт от «плеча» к 1 в точке торможения, с подветренной гаснет в ноль.
        float bright = nf >= 0 ? lerp(_Glow.x, 1, pow(wind, _Shape.z)) : _Glow.x * (1 - pow(lee, _Shape.w));
        bright *= _Glow.y * v.color.r;
        o.tint = lerp(_CoolTint.rgb, _HotTint.rgb, pow(wind, _Shape.z)) * bright;
        o.posRWS = TransformObjectToWorld(pos);
        o.positionCS = TransformWorldToHClip(o.posRWS);
        o.viewZ = -TransformWorldToView(o.posRWS).z;
        o.normalWS = TransformObjectToWorldNormal(v.normalOS);
        return o;
    }

    float4 Frag(Varyings i) : SV_Target
    {
        float3 V = GetWorldSpaceNormalizeViewDir(i.posRWS);
        float f = abs(dot(normalize(i.normalWS), V));
        float limb = saturate(f / LimbEdge) / max(f, LimbEdge);
        float sceneZ = LinearEyeDepth(LoadCameraDepth(uint2(i.positionCS.xy)), _ZBufferParams);
        float soft = saturate((sceneZ - i.viewZ) / max(_SoftDist, 1e-3));
        float nearFade = saturate((i.viewZ - _NearFade) / max(_NearFade, 1e-3));
        // Эмиссия в нитах → предэкспонированный буфер HDRP, как у эмиссии Lit/Unlit.
        float3 c = _EmissiveColor.rgb * i.tint * (limb * soft * nearFade * GetCurrentExposureMultiplier());
        return float4(c, 0);
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
