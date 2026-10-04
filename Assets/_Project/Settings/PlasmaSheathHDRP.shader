// Ударный слой входа в атмосферу (GDD §4.6): тонкая светящаяся оболочка вокруг лба и вдоль борта. Меш —
// поверхность вращения (VesselView.SheathMesh), яркость и цвет по профилю — в цветах вершин, общая яркость —
// _EmissiveColor в нитах (то же имя, что у эмиссии HDRP, — VesselView.SetEmissive ставит его блоком свойств).
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
    }

    HLSLINCLUDE
    #pragma target 4.5
    #pragma only_renderers d3d11 vulkan metal

    #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"

    float4 _EmissiveColor;
    float _SoftDist, _NearFade;

    // Пик свечения у силуэта, |N·V| = LimbEdge: 1/LimbEdge ≈ 3,3 — толщина слоя к радиусу ≈ 0,1 (пара: ShockStandoff).
    #define LimbEdge 0.3

    struct Attributes
    {
        float3 positionOS : POSITION;
        float3 normalOS : NORMAL;
        float4 color : COLOR;
    };

    struct Varyings
    {
        float4 positionCS : SV_POSITION;
        float4 color : COLOR;
        float3 normalWS : TEXCOORD0;
        float3 posRWS : TEXCOORD1;
        float viewZ : TEXCOORD2;
    };

    Varyings Vert(Attributes v)
    {
        Varyings o;
        o.posRWS = TransformObjectToWorld(v.positionOS);
        o.positionCS = TransformWorldToHClip(o.posRWS);
        o.viewZ = -TransformWorldToView(o.posRWS).z;
        o.normalWS = TransformObjectToWorldNormal(v.normalOS);
        o.color = v.color;
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
        float3 c = _EmissiveColor.rgb * i.color.rgb * (limb * soft * nearFade * GetCurrentExposureMultiplier());
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
