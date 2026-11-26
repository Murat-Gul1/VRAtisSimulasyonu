Shader "VRAtis/AlpineFoliageURP"
{
    Properties
    {
        [MainTexture] _BaseMap ("Foliage Texture (RGBA)", 2D) = "white" {}
        [MainColor] _BaseColor ("Tint Color", Color) = (1.0, 1.0, 1.0, 1.0)
        _Cutoff ("Alpha Cutoff", Range(0.05, 0.9)) = 0.35
        _WindStrength ("Wind Sway Strength", Range(0.0, 0.35)) = 0.08
        _WindSpeed ("Wind Speed", Range(0.1, 6.0)) = 1.65
        _FadeStart ("Distance Fade Start (m)", Float) = 120.0
        _FadeEnd ("Distance Fade End (m)", Float) = 175.0
        _Translucency ("Backlit Sun Glow", Range(0.0, 0.8)) = 0.25
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "AlphaTest"
            "RenderType" = "TransparentCutout"
        }

        Cull Off

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                half4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                half4 directAndFade : TEXCOORD2;
                half3 ambientLight : TEXCOORD3;
                float fogFactor : TEXCOORD4;
                half4 vertColor : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _BaseColor;
                float _Cutoff;
                float _WindStrength;
                float _WindSpeed;
                float _FadeStart;
                float _FadeEnd;
                float _Translucency;
            CBUFFER_END

            float3 ApplyWindSway(float3 worldPos, float2 uv)
            {
                if (_WindStrength <= 0.0001)
                    return worldPos;

                float weight = saturate(uv.y) * saturate(uv.y);
                float phase = dot(worldPos.xz, float2(0.42, 0.31)) + _Time.y * _WindSpeed;
                float gust = sin(phase) * 0.65 + sin(phase * 2.13 + 1.4) * 0.35;
                worldPos.x += gust * _WindStrength * weight;
                worldPos.z += cos(phase * 0.85) * (_WindStrength * 0.55) * weight;
                return worldPos;
            }

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                float3 posWS = TransformObjectToWorld(input.positionOS.xyz);
                float camDist = distance(posWS, _WorldSpaceCameraPos);
                float safeFadeEnd = max(_FadeStart + 1.0, _FadeEnd);

                // Collapse completely faded-out distant grass tufts in vertex stage -> 0 rasterized fragments on mobile TBDR
                if (camDist >= safeFadeEnd)
                {
                    output.positionCS = float4(0.0, 0.0, 0.0, 0.0);
                    return output;
                }

                if (camDist < 42.0)
                    posWS = ApplyWindSway(posWS, input.uv);

                float distFade = 1.0 - smoothstep(_FadeStart, safeFadeEnd, camDist);
                float3 normalWS = normalize(TransformObjectToWorldNormal(input.normalOS));
                Light mainLight = GetMainLight();

                float ndotl = dot(normalWS, mainLight.direction);
                half wrapDiff = (half)saturate(ndotl * 0.5 + 0.5);

                half backLit = 0.0h;
                if (_Translucency > 0.001 && camDist < 65.0)
                {
                    float3 viewDirWS = GetWorldSpaceNormalizeViewDir(posWS);
                    float bl = saturate(dot(-viewDirWS, mainLight.direction));
                    backLit = (half)(bl * bl * bl * _Translucency);
                }

                output.positionWS = posWS;
                output.positionCS = TransformWorldToHClip(posWS);
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                output.directAndFade = half4(mainLight.color * (wrapDiff + backLit), (half)distFade);
                output.ambientLight = SampleSH(normalWS) * lerp(0.68h, 1.05h, (half)saturate(input.uv.y));
                output.fogFactor = ComputeFogFactor(output.positionCS.z);
                output.vertColor = input.color;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                half4 tex = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv);
                half alpha = tex.a * input.directAndFade.a;
                clip(alpha - (half)_Cutoff);

                half shadowTerm = 1.0h;
                #if defined(_MAIN_LIGHT_SHADOWS) || defined(_MAIN_LIGHT_SHADOWS_CASCADE) || defined(_MAIN_LIGHT_SHADOWS_SCREEN)
                float3 dCam = input.positionWS - _WorldSpaceCameraPos;
                if (dot(dCam, dCam) < 1764.0)
                {
                    Light shadowedLight = GetMainLight(TransformWorldToShadowCoord(input.positionWS));
                    shadowTerm = (half)(shadowedLight.shadowAttenuation * shadowedLight.distanceAttenuation);
                }
                #endif

                half3 albedo = tex.rgb * _BaseColor.rgb * input.vertColor.rgb;
                half3 litColor = albedo * (input.directAndFade.rgb * shadowTerm + input.ambientLight);
                litColor = MixFog(litColor, input.fogFactor);
                return half4(litColor, 1.0h);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Off

            HLSLPROGRAM
            #pragma vertex vertShadow
            #pragma fragment fragShadow
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _BaseColor;
                float _Cutoff;
                float _WindStrength;
                float _WindSpeed;
                float _FadeStart;
                float _FadeEnd;
                float _Translucency;
            CBUFFER_END

            Varyings vertShadow(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                float3 posWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(posWS);
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                return output;
            }

            half4 fragShadow(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                half alpha = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).a;
                clip(alpha - (half)_Cutoff);
                return 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask R
            Cull Off

            HLSLPROGRAM
            #pragma vertex vertDepth
            #pragma fragment fragDepth
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _BaseColor;
                float _Cutoff;
                float _WindStrength;
                float _WindSpeed;
                float _FadeStart;
                float _FadeEnd;
                float _Translucency;
            CBUFFER_END

            Varyings vertDepth(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                float3 posWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(posWS);
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                return output;
            }

            half4 fragDepth(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                half alpha = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).a;
                clip(alpha - (half)_Cutoff);
                return 0;
            }
            ENDHLSL
        }
    }
}
