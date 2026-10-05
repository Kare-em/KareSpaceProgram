// Возврат цвета геометрии, которую HDRP по ошибке «затуманил» атмосферой (GDD §9.2, вид на планету из космоса).
// Ошибка HDRP 17.6 (AtmosphericScattering.hlsl, EvaluatePbrAtmosphere): камера над атмосферой, фрагмент ближе точки
// входа луча в атмосферу (tFrag < tEntry) — код всё равно считает «луч кончается внутри» и вычитает свечение от
// точки ВНЕ атмосферы. Разность таблиц у лимба не ноль: на борту рисуется синяя линия лимба и голубая дымка.
// Пара: SpaceFogFix.cs сохраняет цвет до тумана (AfterOpaqueAndSky) и зовёт этот проход после (BeforePreRefraction).
Shader "Hidden/Kare/SpaceFogRestore"
{
    HLSLINCLUDE
    #pragma vertex Vert
    #pragma target 4.5
    #pragma only_renderers d3d11 vulkan metal

    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/RenderPass/CustomPass/CustomPassCommon.hlsl"

    TEXTURE2D_X(_KareSavedColor);
    // Дальше этого (м от камеры) возможна настоящая атмосфера — там туман HDRP верный, не трогаем.
    float _KareRestoreDist;

    float4 FullScreenPass(Varyings varyings) : SV_Target
    {
        UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(varyings);
        float depth = LoadCameraDepth(varyings.positionCS.xy);
        if (depth == UNITY_RAW_FAR_CLIP_VALUE) discard; // небо HDRP рисует сам и верно
        PositionInputs posInput = GetPositionInput(varyings.positionCS.xy, _ScreenSize.zw, depth, UNITY_MATRIX_I_VP, UNITY_MATRIX_V);
        float dist = length(posInput.positionWS - GetCurrentViewPosition());
        if (dist >= _KareRestoreDist) discard;
        return LOAD_TEXTURE2D_X(_KareSavedColor, varyings.positionCS.xy);
    }
    ENDHLSL

    SubShader
    {
        Tags { "RenderPipeline" = "HDRenderPipeline" }
        Pass
        {
            Name "Restore"
            ZWrite Off
            ZTest Always
            Blend Off
            Cull Off

            HLSLPROGRAM
            #pragma fragment FullScreenPass
            ENDHLSL
        }
    }
    Fallback Off
}
