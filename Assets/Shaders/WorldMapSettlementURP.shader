Shader "VRAtis/WorldMapSettlementURP"
{
    Properties
    {
        _SunBoost ("Sunlit Direct Strength", Range(0.5, 1.5)) = 1.05
        _ShadowTint ("Shadow Ambient Tint", Color) = (0.24, 0.28, 0.34, 1)
        _AmbientStrength ("Ambient Fill Strength", Range(0.1, 0.8)) = 0.42
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Opaque"
            "Queue" = "Geometry"
        }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            Cull Back
            ZWrite On

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                half4  color      : COLOR;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 positionWS  : TEXCOORD0;
                half3  normalWS    : TEXCOORD1;
                half4  color       : COLOR;
                float  fogFactor   : TEXCOORD2;
            };

            CBUFFER_START(UnityPerMaterial)
                half  _SunBoost;
                half4 _ShadowTint;
                half  _AmbientStrength;
            CBUFFER_END

            half Hash31(float3 p)
            {
                p = frac(p * 0.1031);
                p += dot(p, p.yzx + 33.33);
                return frac((p.x + p.y) * p.z);
            }

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                VertexPositionInputs posInputs = GetVertexPositionInputs(IN.positionOS.xyz);
                VertexNormalInputs normInputs = GetVertexNormalInputs(IN.normalOS);

                OUT.positionHCS = posInputs.positionCS;
                OUT.positionWS = posInputs.positionWS;
                OUT.normalWS = normInputs.normalWS;
                OUT.color = IN.color;
                OUT.fogFactor = ComputeFogFactor(posInputs.positionCS.z);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                half3 N = normalize(IN.normalWS);
                float4 shadowCoord = TransformWorldToShadowCoord(IN.positionWS);
                Light mainLight = GetMainLight(shadowCoord);

                // Subtle stone/roof masonry surface grain so buildings look tactile & matte from strategy view
                half grain = lerp(0.92h, 1.07h, Hash31(floor(IN.positionWS * 2.2)));

                half ndl = saturate(dot(N, mainLight.direction));
                half shadow = lerp(0.42h, 1.0h, mainLight.shadowAttenuation);

                half hemi = saturate(N.y * 0.5h + 0.5h);
                half3 ambient = lerp(_ShadowTint.rgb, half3(0.68h, 0.74h, 0.80h), hemi) * _AmbientStrength;
                half3 direct = mainLight.color * (ndl * shadow * _SunBoost);

                half ao = IN.color.a;
                half3 lit = (IN.color.rgb * grain) * (ambient + direct) * ao;
                lit = MixFog(lit, IN.fogFactor);
                return half4(lit, 1.0h);
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
            Cull Back

            HLSLPROGRAM
            #pragma vertex ShadowPassVertex
            #pragma fragment ShadowPassFragment

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            Varyings ShadowPassVertex(Attributes input)
            {
                Varyings output;
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, _LightDirection));
                return output;
            }

            half4 ShadowPassFragment(Varyings input) : SV_TARGET
            {
                return 0;
            }
            ENDHLSL
        }
    }
}
