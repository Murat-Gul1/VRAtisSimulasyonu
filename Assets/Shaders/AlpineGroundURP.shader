Shader "VRAtis/AlpineGroundURP"
{
    Properties
    {
        [MainTexture] _BaseMap ("Albedo Texture", 2D) = "white" {}
        [MainColor] _BaseColor ("Meadow Base Tint", Color) = (0.68, 0.84, 0.56, 1.0)
        _BumpMap ("Normal Map", 2D) = "bump" {}
        _BumpScale ("Normal Scale", Range(0.0, 2.0)) = 0.85
        _DarkPatchColor ("Lush Deep Meadow Tint", Color) = (0.46, 0.66, 0.40, 1.0)
        _LightPatchColor ("Sunlit Pasture Tint", Color) = (0.80, 0.92, 0.58, 1.0)
        _MacroScale ("Macro Patch Frequency", Float) = 0.024
        _DetailScale ("Mid Patch Frequency", Float) = 0.11
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Geometry"
            "RenderType" = "Opaque"
        }

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
                float4 tangentOS : TANGENT;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                float3 normalWS : TEXCOORD2;
                float3 tangentWS : TEXCOORD3;
                float3 bitangentWS : TEXCOORD4;
                float fogFactor : TEXCOORD5;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);
            TEXTURE2D(_BumpMap);
            SAMPLER(sampler_BumpMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _BaseColor;
                half4 _DarkPatchColor;
                half4 _LightPatchColor;
                float _BumpScale;
                float _MacroScale;
                float _DetailScale;
            CBUFFER_END

            float Hash21(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

            float ValueNoise2D(float2 p)
            {
                float2 i = floor(p);
                float2 f = frac(p);
                float2 u = f * f * (3.0 - 2.0 * f);
                float a = Hash21(i);
                float b = Hash21(i + float2(1.0, 0.0));
                float c = Hash21(i + float2(0.0, 1.0));
                float d = Hash21(i + float2(1.0, 1.0));
                return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
            }

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VertexPositionInputs posInputs = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normInputs = GetVertexNormalInputs(input.normalOS, input.tangentOS);

                output.positionCS = posInputs.positionCS;
                output.positionWS = posInputs.positionWS;
                output.normalWS = normInputs.normalWS;
                output.tangentWS = normInputs.tangentWS;
                output.bitangentWS = normInputs.bitangentWS;
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                output.fogFactor = ComputeFogFactor(posInputs.positionCS.z);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                half4 baseTex = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv);
                half3 normalTS = UnpackNormalScale(SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, input.uv), _BumpScale);

                float3x3 tbn = float3x3(normalize(input.tangentWS), normalize(input.bitangentWS), normalize(input.normalWS));
                float3 normalWS = normalize(mul(normalTS, tbn));

                // Dual-frequency organic alpine pasture color variation eliminates distant tiling & blends with static grass
                float macro = ValueNoise2D(input.positionWS.xz * _MacroScale);
                float detail = ValueNoise2D(input.positionWS.xz * _DetailScale + 17.3);
                float micro = ValueNoise2D(input.positionWS.xz * 0.55 + 41.7);
                float blend = saturate(macro * 0.55 + detail * 0.30 + micro * 0.15);

                half3 patchTint = lerp(_DarkPatchColor.rgb, _LightPatchColor.rgb, blend);
                half3 albedo = baseTex.rgb * _BaseColor.rgb * patchTint * 1.15h;

                // Fade micro normal ripple gently in far distance to keep horizon clean
                float camDist = distance(input.positionWS, _WorldSpaceCameraPos);
                float normalBlend = saturate(camDist / 75.0);
                normalWS = normalize(lerp(normalWS, normalize(input.normalWS), normalBlend * 0.75));

                float4 shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                Light mainLight = GetMainLight(shadowCoord);
                float ndotl = saturate(dot(normalWS, mainLight.direction));
                float shadowTerm = mainLight.shadowAttenuation * mainLight.distanceAttenuation;

                half3 direct = mainLight.color * (ndotl * shadowTerm);
                half3 ambient = SampleSH(normalWS);

                half3 col = albedo * (direct + ambient);
                col = MixFog(col, input.fogFactor);
                return half4(col, 1.0);
            }
            ENDHLSL
        }
    }
}
