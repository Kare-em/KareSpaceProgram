// Кома и хвосты комет (CometView, GDD §9.3): аддитивное свечение в долях белого экрана мимо экспозиции — как точки
// тел (BodyRenderer.Points, _EmissiveExposureWeight 0): в нитах рассеянный свет хвоста при EV 12+ невидим.
// Яркость — цвет вершины (затухание вдоль хвоста, сохранение потока при расширении до минимума пикселей; HDRP/Unlit
// цвет вершин не берёт — потому свой шейдер), профиль поперёк — текстура (R).
// Дальняя плоскость 1e7: хвост на карте уходит дальше неё — глубина вершин прижимается к ней (как у колец),
// а заслонение телами — обычный ZTest по непрозрачным.
Shader "Kare/Comet Glow HDRP"
{
    Properties
    {
        _MainTex ("Profile (R)", 2D) = "white" {}
    }

    HLSLINCLUDE
    #pragma target 4.5
    #pragma only_renderers d3d11 vulkan metal

    #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"

    TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);

    struct Attributes
    {
        float3 positionOS : POSITION;
        float4 color : COLOR;
        float2 uv : TEXCOORD0;
    };

    struct Varyings
    {
        float4 positionCS : SV_POSITION;
        float4 color : COLOR;
        float2 uv : TEXCOORD0;
    };

    Varyings Vert(Attributes v)
    {
        Varyings o;
        float3 rws = TransformObjectToWorld(v.positionOS);
        o.positionCS = TransformWorldToHClip(rws);
    #if UNITY_REVERSED_Z
        o.positionCS.z = max(o.positionCS.z, o.positionCS.w * 1e-7);
    #else
        o.positionCS.z = min(o.positionCS.z, o.positionCS.w * (1 - 1e-7));
    #endif
        o.color = v.color;
        o.uv = v.uv;
        return o;
    }

    float4 Frag(Varyings i) : SV_Target
    {
        float p = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv).r;
        // Буфер HDRP предэкспонирован: значение пишется как есть — доля белого, экспозиция не трогает.
        return float4(i.color.rgb * p, 0);
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
