// Входные данные шейдера частей снаряжения: всё как у URP Lit, а цвет поверхности под маской качества
// (_QualityMask, её пишет Tools/Blender/quality_mask.py) заменяется цветом качества той же яркости:
// складки и тени полосы остаются, меняется только цвет.
#ifndef GAME_TACTICK_GEAR_INPUT_INCLUDED
#define GAME_TACTICK_GEAR_INPUT_INCLUDED

// Функция Lit под другим именем: проходы URP зовут InitializeStandardLitSurfaceData, и это будет своя ниже
#define InitializeStandardLitSurfaceData InitializeStandardLitSurfaceData_Lit
#include "Packages/com.unity.render-pipelines.universal/Shaders/LitInput.hlsl"
#undef InitializeStandardLitSurfaceData
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"

// Вне UnityPerMaterial: блок констант Lit задан в LitInput.hlsl, дополнить его нельзя. Шейдер поэтому
// не идёт через SRP Batcher, но цвет качества и так задаётся отдельно для каждой части (MaterialPropertyBlock)
TEXTURE2D(_QualityMask);    SAMPLER(sampler_QualityMask);
half4 _QualityColor;
half _QualityLuma;

inline void InitializeStandardLitSurfaceData(float2 uv, out SurfaceData outSurfaceData)
{
    InitializeStandardLitSurfaceData_Lit(uv, outSurfaceData);
    half mask = SAMPLE_TEXTURE2D(_QualityMask, sampler_QualityMask, uv).r;
    // Средняя яркость полосы (_QualityLuma) даёт ровно цвет качества, светлее и темнее — так же светлее и темнее
    half3 tinted = _QualityColor.rgb * (Luminance(outSurfaceData.albedo) / _QualityLuma);
    outSurfaceData.albedo = lerp(outSurfaceData.albedo, saturate(tinted), mask);
}

#endif
