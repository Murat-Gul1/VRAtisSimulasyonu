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
                float3 normalWS : TEXCOORD2;
                float fogFactor : TEXCOORD3;
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
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                float3 posWS = TransformObjectToWorld(input.positionOS.xyz);
                posWS = ApplyWindSway(posWS, input.uv);

                output.positionWS = posWS;
                output.positionCS = TransformWorldToHClip(posWS);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                output.fogFactor = ComputeFogFactor(output.positionCS.z);
                output.vertColor = input.color;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                half4 tex = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv);

                // Smooth far-distance dissolve for grass while preserving pine/shrub visibility
                float camDist = distance(input.positionWS, _WorldSpaceCameraPos);
                float distFade = 1.0 - smoothstep(_FadeStart, max(_FadeStart + 1.0, _FadeEnd), camDist);
                half alpha = tex.a * distFade;
                clip(alpha - _Cutoff);

                half3 albedo = tex.rgb * _BaseColor.rgb * input.vertColor.rgb;
                float3 normalWS = normalize(input.normalWS);
                float3 viewDirWS = GetWorldSpaceNormalizeViewDir(input.positionWS);

                // Receive directional sunlight + real mountain/tree shadows
                float4 shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                Light mainLight = GetMainLight(shadowCoord);

                float ndotl = dot(normalWS, mainLight.direction);
                float wrapDiff = saturate(ndotl * 0.5 + 0.5);
                float shadowTerm = mainLight.shadowAttenuation * mainLight.distanceAttenuation;

                // Subsurface backlit glow when looking toward sunlit grass/pine
                float backLit = (_Translucency > 0.001)
                    ? pow(saturate(dot(-viewDirWS, mainLight.direction)), 3.0) * _Translucency
                    : 0.0;
                half3 directLight = mainLight.color * ((wrapDiff + backLit) * shadowTerm);

                // Trilight ambient sky/equator/ground approximation
                half3 ambient = SampleSH(normalWS) * lerp(0.68h, 1.05h, saturate(input.uv.y));

                half3 litColor = albedo * (directLight + ambient);
                litColor = MixFog(litColor, input.fogFactor);
                return half4(litColor, 1.0);
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
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                float3 posWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(posWS);
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                return output;
            }

            half4 fragShadow(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                half alpha = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).a;
                clip(alpha - _Cutoff);
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
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
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
                clip(alpha - _Cutoff);
                return 0;
            }
            ENDHLSL
        }
    }
}
