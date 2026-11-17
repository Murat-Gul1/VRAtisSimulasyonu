Shader "VRAtis/AlpineCloudsSkybox"
{
    Properties
    {
        [NoScaleOffset] _CloudTex ("Alpine Clouds Tile (RGBA)", 2D) = "gray" {}
        _CumulusScale ("Cumulus Tile Scale", Range(0.05, 1.5)) = 0.22
        _CirrusScale ("Cirrus Tile Scale", Range(0.05, 1.5)) = 0.14
        _CloudThreshold ("Cloud Coverage Threshold", Range(0.15, 0.85)) = 0.44
        _CloudSoftness ("Cloud Edge Softness", Range(0.04, 0.45)) = 0.24
        _CumulusOpacity ("Cumulus Density Opacity", Range(0.2, 1.0)) = 0.94
        _CirrusOpacity ("Cirrus Opacity", Range(0.0, 0.8)) = 0.36
        _StarIntensity ("Night Star Brightness", Range(0.0, 2.0)) = 1.0

        [Header(Fallback Colors Without DynamicTimeOfDay)]
        _FallbackZenith ("Zenith Sky Color", Color) = (0.14, 0.36, 0.72, 1.0)
        _FallbackHorizon ("Horizon Sky Color", Color) = (0.64, 0.77, 0.90, 1.0)
        _FallbackGround ("Ground Haze Color", Color) = (0.28, 0.32, 0.35, 1.0)
        _FallbackCloudLit ("Cloud Lit Color", Color) = (1.0, 0.98, 0.95, 1.0)
        _FallbackCloudShadow ("Cloud Shadow Color", Color) = (0.50, 0.59, 0.72, 1.0)
        _FallbackSunDir ("Fallback Sun Direction", Vector) = (-0.25, 0.38, -0.89, 0.0)
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Background"
            "RenderType" = "Background"
            "PreviewType" = "Skybox"
            "RenderPipeline" = "UniversalPipeline"
        }

        Cull Off
        ZWrite Off

        Pass
        {
            Name "AlpineCloudsSkyboxPass"

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 viewDir : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            TEXTURE2D(_CloudTex);
            SAMPLER(sampler_CloudTex);

            CBUFFER_START(UnityPerMaterial)
                float _CumulusScale;
                float _CirrusScale;
                float _CloudThreshold;
                float _CloudSoftness;
                float _CumulusOpacity;
                float _CirrusOpacity;
                float _StarIntensity;
                half4 _FallbackZenith;
                half4 _FallbackHorizon;
                half4 _FallbackGround;
                half4 _FallbackCloudLit;
                half4 _FallbackCloudShadow;
                float4 _FallbackSunDir;
            CBUFFER_END

            // Driven live by DynamicTimeOfDay without dirtying .mat assets on disk
            float _VRAtisSkyActive;
            float _VRAtisSkyDaylight;
            float _VRAtisSkyGoldenMix;
            float4 _VRAtisSkySunDir;
            half4 _VRAtisSkySunColor;
            half4 _VRAtisSkyZenithColor;
            half4 _VRAtisSkyHorizonColor;
            half4 _VRAtisSkyGroundColor;
            half4 _VRAtisSkyCloudLitColor;
            half4 _VRAtisSkyCloudShadowColor;
            float4 _VRAtisSkyWindOffset;
            float _VRAtisSkyRain;

            float StarHash(float3 p)
            {
                p = frac(p * float3(443.897, 441.423, 437.195));
                p += dot(p, p.yzx + 19.19);
                return frac((p.x + p.y) * p.z);
            }

            // Triangular screen-space dither to eliminate 8-bit color banding in dark night clouds
            float SkyDither(float2 screenPos)
            {
                float ign = frac(52.9829189 * frac(dot(screenPos, float2(0.06711056, 0.00583715))));
                return (ign - 0.5) / 255.0;
            }

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VertexPositionInputs posInputs = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = posInputs.positionCS;
                output.viewDir = input.positionOS.xyz;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float3 dir = normalize(input.viewDir);

                bool live = _VRAtisSkyActive > 0.5;
                float daylight = live ? _VRAtisSkyDaylight : 1.0;
                float goldenMix = live ? _VRAtisSkyGoldenMix : 0.0;
                float3 sunDir = normalize(live ? _VRAtisSkySunDir.xyz : _FallbackSunDir.xyz);
                half3 sunCol = live ? _VRAtisSkySunColor.rgb : half3(1.0, 0.96, 0.88);
                half3 zenithCol = live ? _VRAtisSkyZenithColor.rgb : _FallbackZenith.rgb;
                half3 horizonCol = live ? _VRAtisSkyHorizonColor.rgb : _FallbackHorizon.rgb;
                half3 groundCol = live ? _VRAtisSkyGroundColor.rgb : _FallbackGround.rgb;
                half3 cloudLitCol = live ? _VRAtisSkyCloudLitColor.rgb : _FallbackCloudLit.rgb;
                half3 cloudShadowCol = live ? _VRAtisSkyCloudShadowColor.rgb : _FallbackCloudShadow.rgb;
                float2 windOffset = live ? _VRAtisSkyWindOffset.xy : float2(0.0, 0.0);
                float rainMix = live ? saturate(_VRAtisSkyRain) : 0.0;

                // 1. Atmospheric Sky Gradient
                float up = saturate(dir.y);
                float horizonCurve = pow(1.0 - up, 3.0);
                half3 skyCol = lerp(zenithCol, horizonCol, horizonCurve);

                // Below horizon fade toward distant mountain haze/ground
                if (dir.y < 0.0)
                {
                    float downFactor = saturate(-dir.y * 5.0);
                    skyCol = lerp(horizonCol, groundCol, downFactor);
                }

                // 2. Sun Disc & Atmospheric Scatter Halo (veiled by storm clouds during rain)
                float sunCos = max(dot(dir, sunDir), 0.0);
                float sunVisible = smoothstep(-0.08, 0.10, sunDir.y) * smoothstep(0.05, 0.35, daylight) * (1.0 - rainMix * 0.92);
                half3 sunHalo = sunCol * (pow(sunCos, 6.0) * (0.16 + goldenMix * 0.42) + pow(sunCos, 96.0) * 0.95) * sunVisible;
                half3 sunDisc = sunCol * (smoothstep(0.99935, 0.99980, sunCos) * 4.5) * sunVisible;

                // Subtle Moon glow at night
                float3 moonDir = normalize(float3(-sunDir.x, max(0.32, abs(sunDir.y) + 0.25), -sunDir.z));
                float moonCos = max(dot(dir, moonDir), 0.0);
                float nightMask = smoothstep(0.28, 0.02, daylight) * (1.0 - rainMix * 0.85);
                half3 moonGlow = half3(0.55, 0.68, 0.95) * (pow(moonCos, 128.0) * 0.45 + smoothstep(0.9996, 0.99985, moonCos) * 1.4) * nightMask;

                skyCol += sunHalo + moonGlow;

                // 3. Crisp pinpoint Alpine Night Stars in clear patches (hidden by rain overcast)
                half3 starCol = half3(0, 0, 0);
                float starVisibility = (1.0 - rainMix * 0.96);
                if (nightMask > 0.01 && starVisibility > 0.02 && dir.y > 0.04)
                {
                    float3 starCell = floor(dir * 340.0);
                    float h = StarHash(starCell);
                    if (h > 0.9958)
                    {
                        float3 cellCenter = normalize(starCell + 0.5);
                        float dist = length(dir - cellCenter) * 340.0;
                        float pinpoint = smoothstep(0.22, 0.03, dist);
                        float twinkle = (h - 0.9958) / 0.0042;
                        starCol = half3(0.84, 0.90, 1.0) * (pinpoint * twinkle * _StarIntensity * nightMask * starVisibility * smoothstep(0.04, 0.25, dir.y));
                    }
                }

                // 4. Perspective-Projected 2-Layer Clouds above Horizon
                half3 finalColor = skyCol + starCol + sunDisc;
                if (dir.y > 0.004)
                {
                    float horizonFade = smoothstep(0.004, 0.145, dir.y);
                    float curvedY = max(0.032, dir.y + 0.145 * (1.0 - dir.y * dir.y));
                    float2 projUV = dir.xz / curvedY;

                    // Layer A: High-altitude wispy cirrus streaks
                    float2 cirrusUV = projUV * _CirrusScale + windOffset * 0.42;
                    half cirrusSample = SAMPLE_TEXTURE2D(_CloudTex, sampler_CloudTex, cirrusUV).b;
                    half cirrusDetail = SAMPLE_TEXTURE2D(_CloudTex, sampler_CloudTex, cirrusUV * 1.7 + float2(0.17, 0.31)).a;
                    float cirrusMask = saturate((cirrusSample * 1.15 - (1.0 - cirrusDetail) * 0.32)) * _CirrusOpacity * horizonFade;
                    float cirrusAlpha = cirrusMask * lerp(0.25, 1.0, daylight);

                    half3 cirrusColor = lerp(cloudShadowCol, cloudLitCol, 0.78);
                    half3 bgBase = lerp(skyCol, cirrusColor, cirrusAlpha * 0.65);
                    finalColor = lerp(finalColor, cirrusColor, cirrusAlpha);

                    // Layer B: Soft billowing 3D-lit Alpine Cumulus Clouds (thickens to overcast during storm rain)
                    float2 cumUV = projUV * _CumulusScale + windOffset;
                    half4 texMain = SAMPLE_TEXTURE2D(_CloudTex, sampler_CloudTex, cumUV);
                    half4 texDetail = SAMPLE_TEXTURE2D(_CloudTex, sampler_CloudTex, cumUV * 1.65 - windOffset * 0.28 + float2(0.37, 0.19));

                    float macro = texMain.r;
                    float erode = texDetail.g * 0.55 + texDetail.a * 0.45;
                    float density = macro * 0.84 + texMain.g * 0.24 - (1.0 - erode) * 0.22;
                    float effThreshold = lerp(_CloudThreshold, 0.20, rainMix);
                    float effSoftness = lerp(_CloudSoftness, 0.30, rainMix);
                    float effOpacity = lerp(_CumulusOpacity, 0.98, rainMix);
                    float cloudMask = smoothstep(effThreshold - effSoftness, effThreshold + effSoftness, density);

                    // Softer night cloud silhouette so low-luminance clouds avoid harsh blotches
                    float nightSoftAlpha = lerp(0.68, 1.0, smoothstep(0.05, 0.35, daylight));
                    float opticalAlpha = saturate(cloudMask * effOpacity * nightSoftAlpha * horizonFade);

                    // Directional single-step self-shadow toward Sun XZ for crisp 3D volume
                    float2 sunXZ = sunDir.xz;
                    float xzLen = length(sunXZ);
                    float2 lightDir2D = xzLen > 0.001 ? (sunXZ / xzLen) : float2(0.7, 0.7);
                    half lightSample = SAMPLE_TEXTURE2D(_CloudTex, sampler_CloudTex, cumUV + lightDir2D * 0.024).r;

                    float thicknessShadow = smoothstep(-0.14, 0.26, lightSample - macro);
                    float litFactor = saturate(0.76 - thicknessShadow * 0.52 + (1.0 - cloudMask) * 0.24);

                    // Silver lining / Mie forward scatter near sun direction
                    float rimThin = cloudMask * (1.0 - cloudMask * 0.55);
                    float forwardGlow = pow(sunCos, 5.0) * rimThin * sunVisible * (0.45 + goldenMix * 0.65);

                    half3 cumCol = lerp(cloudShadowCol, cloudLitCol, litFactor) + sunCol * forwardGlow;

                    // Blend horizon haze softly into distant cloud bottoms
                    cumCol = lerp(horizonCol, cumCol, smoothstep(0.01, 0.22, dir.y));

                    // Re-compose stars/sun occluded smoothly by clouds while preserving high cirrus
                    half3 occludedSky = lerp(finalColor, bgBase, saturate(opticalAlpha * 1.15));
                    finalColor = lerp(occludedSky, cumCol, opticalAlpha);
                }

                finalColor += SkyDither(input.positionCS.xy);
                return half4(max(half3(0, 0, 0), finalColor), 1.0);
            }
            ENDHLSL
        }
    }
}
