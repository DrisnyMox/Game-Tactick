// Шейдер воды воксельной арены: второй сабмеш мешей чанков и узлов.
// Цвет воды — из палитры биома (_VoxelPaletteTex, r цвета вершины — индекс), как у суши.
// Полупрозрачная: сквозь неё видно дно. Рябь — нормаль, которую качают волны по мировым x и z,
// сами вершины не двигаются. По ряби — блик солнца и отражение неба, у краёв обзора отражения больше.
// Теней вода не отбрасывает (нет прохода ShadowCaster), в глубину не пишет.
Shader "NoobGenerals/VoxelWater"
{
    Properties
    {
        _Alpha ("Непрозрачность", Range(0, 1)) = 0.72
        _ColorBrightness ("Яркость цвета палитры", Range(0.5, 2)) = 1
        _WaveScale ("Частота ряби", Range(0.05, 4)) = 0.9
        _WaveSpeed ("Скорость ряби", Range(0, 4)) = 0.8
        _WaveStrength ("Сила ряби", Range(0, 1)) = 0.18
        _Specular ("Сила блика", Range(0, 4)) = 1.2
        _Shininess ("Резкость блика", Range(8, 512)) = 160
        _Reflection ("Сила отражения неба", Range(0, 1)) = 0.6
        _FresnelPower ("Отражение у краёв обзора", Range(1, 8)) = 4
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half _Alpha;
                half _ColorBrightness;
                float _WaveScale;
                float _WaveSpeed;
                half _WaveStrength;
                half _Specular;
                half _Shininess;
                half _Reflection;
                half _FresnelPower;
            CBUFFER_END

            TEXTURE2D(_VoxelPaletteTex);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float4 color : COLOR;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float4 color : TEXCOORD2;
                float fogFactor : TEXCOORD3;
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                VertexPositionInputs positions = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = positions.positionCS;
                output.positionWS = positions.positionWS;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.color = input.color;
                output.fogFactor = ComputeFogFactor(positions.positionCS.z);
                return output;
            }

            // Наклон ряби по x и z: сумма трёх синусоид в разных направлениях
            float2 WaveSlope(float2 p, float t)
            {
                float2 d1 = float2(0.8, 0.6), d2 = float2(-0.5, 0.86), d3 = float2(0.2, -0.98);
                float f1 = 1.0, f2 = 1.7, f3 = 2.9;
                float2 slope = d1 * f1 * cos(dot(d1, p) * f1 + t);
                slope += d2 * f2 * 0.6 * cos(dot(d2, p) * f2 + t * 1.3);
                slope += d3 * f3 * 0.35 * cos(dot(d3, p) * f3 + t * 1.9);
                return slope;
            }

            half4 frag(Varyings input) : SV_Target
            {
                float3 normalWS = normalize(input.normalWS);
                // Рябь только на гранях, смотрящих вверх: бока воды (у обрывов и краёв карты) ровные
                float up = saturate(normalWS.y);
                float2 slope = WaveSlope(input.positionWS.xz * _WaveScale, _Time.y * _WaveSpeed) * _WaveStrength * up;
                normalWS = normalize(normalWS + float3(-slope.x, 0, -slope.y));

                int paletteIndex = (int)round(input.color.r * 255.0);
                half3 albedo = LOAD_TEXTURE2D(_VoxelPaletteTex, int2(paletteIndex, 0)).rgb * _ColorBrightness;

                float3 viewDirWS = normalize(GetWorldSpaceViewDir(input.positionWS));
                Light mainLight = GetMainLight(TransformWorldToShadowCoord(input.positionWS));
                half shadow = mainLight.shadowAttenuation;

                half diffuseTerm = saturate(dot(normalWS, mainLight.direction));
                half3 diffuse = mainLight.color * diffuseTerm * shadow;
                half3 ambient = SampleSH(normalWS);
                half3 color = albedo * (diffuse + ambient);

                // Отражение неба: у краёв обзора сильнее
                half fresnel = pow(1.0 - saturate(dot(normalWS, viewDirWS)), _FresnelPower);
                half3 reflectDir = reflect(-viewDirWS, normalWS);
                half3 sky = GlossyEnvironmentReflection(reflectDir, 0.1, 1.0);
                color = lerp(color, sky, saturate(_Reflection * fresnel + 0.1 * _Reflection));

                // Блик солнца по ряби
                half3 halfDir = normalize(mainLight.direction + viewDirWS);
                half specular = pow(saturate(dot(normalWS, halfDir)), _Shininess) * _Specular * shadow;
                color += mainLight.color * specular;

                // У краёв обзора вода плотнее: дно сквозь неё видно хуже
                half alpha = saturate(lerp(_Alpha, 1.0, fresnel) + specular);
                color = MixFog(color, input.fogFactor);
                return half4(color, alpha);
            }
            ENDHLSL
        }
    }
}
