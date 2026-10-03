// Частицы пака JMO WarFX под HDRP (§9.5, взрывы). Родные шейдеры пака — built-in CG без LightMode: HDRP их
// рисует как SRPDefaultUnlit, но мимо экспозиции и с built-in глубиной. Здесь те же формулы смешивания одним
// шейдером, режим — _Mode (его ставит меню Kare/Convert WarFX to HDRP, WarFxConverter.cs).
// Цвет пишется «экранным», без GetCurrentExposureMultiplier: буфер HDRP уже предэкспонирован, значит 1 — это
// белый на экране и днём, и ночью, как пак и задуман. Умножающие режимы от экспозиции не зависят вовсе.
Shader "Kare/WFX Particle HDRP"
{
    Properties
    {
        _TintColor ("Tint Color", Color) = (0.5, 0.5, 0.5, 0.5)
        _MainTex ("Particle Texture", 2D) = "white" {}
        _ScrollSpeed ("Scroll Speed", Float) = 0
        _InvFade ("Soft Particles Factor (1/м)", Range(0.01, 3.0)) = 1.0
        _Brightness ("Brightness (светящиеся режимы)", Float) = 1
        [HideInInspector] _Mode ("Mode", Float) = 0
        [HideInInspector] _SrcBlend ("Src Blend", Float) = 1
        [HideInInspector] _DstBlend ("Dst Blend", Float) = 1
        // Мягкое касание грунта в метрах экземпляра: BlastEffects ставит масштаб префаба блоком свойств,
        // иначе у взрыва в 100 м шов с землёй резкий (fade пака — 1 м).
        [HideInInspector] _SoftScale ("Soft Scale", Float) = 1
    }

    HLSLINCLUDE
    #pragma target 4.5
    #pragma only_renderers d3d11 vulkan metal

    #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"

    TEXTURE2D(_MainTex);
    SAMPLER(sampler_MainTex);
    float4 _MainTex_ST;
    float4 _TintColor;
    float _ScrollSpeed, _InvFade, _Brightness, _Mode, _SoftScale;

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
        float viewZ : TEXCOORD1;
    };

    Varyings Vert(Attributes v)
    {
        Varyings o;
        float3 posRWS = TransformObjectToWorld(v.positionOS);
        o.positionCS = TransformWorldToHClip(posRWS);
        o.viewZ = -TransformWorldToView(posRWS).z;
        o.color = v.color;
        o.uv = v.uv * _MainTex_ST.xy + _MainTex_ST.zw;
        return o;
    }

    float4 Frag(Varyings i) : SV_Target
    {
        float sceneZ = LinearEyeDepth(LoadCameraDepth(uint2(i.positionCS.xy)), _ZBufferParams);
        float fade = saturate(_InvFade * (sceneZ - i.viewZ) / max(_SoftScale, 1));
        float4 vc = i.color;
        vc.a *= fade;

        float4 t = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv);
        float2 uvScroll = i.uv - float2(0, frac(_Time.y * _ScrollSpeed / 20));
        float4 ts = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uvScroll);
        int mode = (int)_Mode;
        float4 c = 0;
        // Формулы — дословно из шейдеров пака (WFX_S *.shader, Legacy Particles), смешивание — _SrcBlend/_DstBlend.
        if (mode == 0)      // WFX/Additive Alpha8: One One
            c = float4(4 * vc.rgb * _TintColor.rgb * t.a * vc.a, 0);
        else if (mode == 1) // WFX/Additive (Soft) Alpha8: OneMinusDstColor One → One One (в HDR 1−dst < 0)
            c = float4(2 * vc.rgb * _TintColor.rgb * t.a * vc.a, 0);
        else if (mode == 2) // Legacy Particles/Additive: SrcAlpha One → предумножено, One One
        {
            c = 2 * vc * _TintColor * t;
            c = float4(c.rgb * saturate(c.a), 0);
        }
        else if (mode == 3) // Legacy Particles/Additive (Soft): One OneMinusSrcColor
        {
            c = vc * t;
            c.rgb *= c.a;
        }
        else if (mode == 4) // WFX/Alpha Blended, Legacy Particles/Alpha Blended: SrcAlpha OneMinusSrcAlpha
            c = 2 * vc * _TintColor * t;
        else if (mode == 5) // WFX/Scroll/Additive: One One
            c = float4(t.a * vc.a * ts.rgb * vc.rgb, 0);
        else if (mode == 6) // WFX/Scroll/Alpha Blended: SrcAlpha OneMinusSrcAlpha
            c = float4(vc.rgb * ts.rgb, vc.a * t.a);
        else if (mode == 7) // WFX/Multiply Soft Tint (+Scroll): DstColor SrcColor, 0,5 — «без изменений»
        {
            float mask = t.a * vc.a;
            c = float4(ts.rgb * vc.rgb * _TintColor.rgb, ts.a);
            return lerp(0.5, c, mask);
        }
        else if (mode == 8) // WFX/Scroll/Smoke: DstColor SrcAlpha
        {
            float mask = t.a * vc.a;
            c = float4(ts.rgb * vc.rgb * _TintColor.rgb, mask);
            return lerp(0.5, c, mask);
        }
        else                // WFX/Multiply Alpha8: Zero SrcColor (текстуры «Inv»: фон альфы — 1)
            return vc * t.a;

        c.rgb *= _Brightness;
        return c;
    }
    ENDHLSL

    SubShader
    {
        Tags { "RenderPipeline" = "HDRenderPipeline" "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Pass
        {
            Name "ForwardOnly"
            Tags { "LightMode" = "ForwardOnly" }
            Blend [_SrcBlend] [_DstBlend]
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
