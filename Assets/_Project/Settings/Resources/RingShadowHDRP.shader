// Тень колец на диске планеты (GDD §9.4). Тела теней не отбрасывают (каскады — 2 км, BodyRenderer.Start), поэтому
// тень считается аналитически: сфера чуть больше планеты, луч камеры бьётся в истинную сферу, из точки поверхности —
// луч на Солнце до плоскости колец, там τ из того же профиля и пропускание e^(−τ/|L.y|). Результат множится на уже
// нарисованную планету (Blend DstColor Zero) — свет освещённой стороны гаснет полосами B, A, C, щель Кассини светлая.
// Очередь раньше колец (RingQueue − 50): кольца ложатся поверх уже затенённого диска.
Shader "Kare/Ring Shadow HDRP"
{
    Properties
    {
        _TauTex ("Tau (R, нормирована)", 2D) = "black" {}
        _TauScale ("Tau Scale", Float) = 1
        _RIn ("R In", Float) = 1
        _ROut ("R Out", Float) = 2
        _PlanetR ("Planet Radius", Float) = 1
        _SunDirOS ("Sun Dir (local)", Vector) = (0, 1, 0, 0)
    }

    HLSLINCLUDE
    #pragma target 4.5
    #pragma only_renderers d3d11 vulkan metal

    #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"

    TEXTURE2D(_TauTex); SAMPLER(sampler_TauTex);
    float _TauScale, _RIn, _ROut, _PlanetR;
    float4 _SunDirOS;

    // Мип профиля для тени: тень лежит на диске наискось и мельче самих колец — полный 9-километровый профиль
    // давал бы на ней рябь в пиксель. 3 → ≈75 км у Сатурна.
    #define ShadowMip 3

    struct Attributes { float3 positionOS : POSITION; };
    struct Varyings { float4 positionCS : SV_POSITION; float3 posOS : TEXCOORD0; };

    Varyings Vert(Attributes v)
    {
        Varyings o;
        o.positionCS = TransformWorldToHClip(TransformObjectToWorld(v.positionOS));
        o.posOS = v.positionOS;
        return o;
    }

    float4 Frag(Varyings i) : SV_Target
    {
        // Над пустым небом (оболочка шире сферы на доли процента) — ничего не трогать.
        float raw = LoadCameraDepth(uint2(i.positionCS.xy));
    #if UNITY_REVERSED_Z
        if (raw <= 0) return 1;
    #else
        if (raw >= 1) return 1;
    #endif
        float3 cam = mul(UNITY_MATRIX_I_M, float4(0, 0, 0, 1)).xyz;
        float3 d = normalize(i.posOS - cam);
        // Луч камеры и истинная сфера радиуса _PlanetR.
        float b = dot(cam, d), c = dot(cam, cam) - _PlanetR * _PlanetR;
        float h = b * b - c;
        if (h < 0) return 1;
        float t = -b - sqrt(h);
        if (t < 0) return 1;
        float3 P = cam + d * t;
        float3 L = normalize(_SunDirOS.xyz);
        // Ночная сторона и равноденствие (Солнце в плоскости колец) — тени нет.
        if (dot(P, L) <= 0 || abs(L.y) < 1e-4) return 1;
        float s = -P.y / L.y;
        if (s <= 0) return 1;
        float r = length((P + L * s).xz);
        if (r < _RIn || r > _ROut) return 1;
        float tau = SAMPLE_TEXTURE2D_LOD(_TauTex, sampler_TauTex, float2((r - _RIn) / (_ROut - _RIn), 0.5), ShadowMip).r * _TauScale;
        float T = exp(-tau / abs(L.y));
        return float4(T, T, T, 1);
    }
    ENDHLSL

    SubShader
    {
        Tags { "RenderPipeline" = "HDRenderPipeline" "Queue" = "Transparent-50" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Pass
        {
            Name "ForwardOnly"
            Tags { "LightMode" = "ForwardOnly" }
            Blend DstColor Zero
            ZWrite Off
            ZTest LEqual
            Cull Back
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            ENDHLSL
        }
    }
}
