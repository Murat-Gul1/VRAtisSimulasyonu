Shader "VRAtis/MuzzleFlashURP"
{
    Properties
    {
        [HDR] _CoreColor ("Core Color (White-Yellow)", Color) = (2.5, 2.0, 1.2, 1.0)
        [HDR] _OuterColor ("Outer Flame Color (Orange)", Color) = (1.8, 0.55, 0.08, 1.0)
        _Intensity ("Dynamic Intensity", Range(0.0, 4.0)) = 1.0
        _IsSideFin ("Side Fin Mode (0=FrontStar, 1=SideCone)", Float) = 1.0
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Transparent+15"
            "RenderType" = "Transparent"
            "IgnoreProjector" = "True"
        }

        Blend One One
        ZWrite Off
        Cull Off

        Pass
        {
            Name "MuzzleFlashPass"

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
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

            CBUFFER_START(UnityPerMaterial)
                half4 _CoreColor;
                half4 _OuterColor;
                float _Intensity;
                float _IsSideFin;
            CBUFFER_END

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = input.uv;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float shapeMask = 0.0;
                float coreMask = 0.0;

                if (_IsSideFin > 0.5)
                {
                    // Side cone flame extending from u=0 (muzzle) to u=1 (tip)
                    float x = saturate(input.uv.x);
                    float y = (input.uv.y - 0.5) * 2.0;

                    // Tapered teardrop profile + slight prong flare near base
                    float width = lerp(0.18, 0.55, pow(x, 0.35)) * (1.0 - x);
                    float radial = saturate(1.0 - abs(y) / max(0.05, width));
                    float axial = smoothstep(0.0, 0.08, x) * pow(1.0 - x, 1.35);

                    shapeMask = pow(radial, 1.6) * axial;
                    coreMask = pow(radial, 3.0) * pow(1.0 - x, 2.8) * smoothstep(0.0, 0.04, x);
                }
                else
                {
                    // Front 4-prong star pattern (flash-hider signature)
                    float2 p = (input.uv - 0.5) * 2.0;
                    float r = length(p);
                    float angle = atan2(p.y, p.x);

                    // 4 sharp prongs + soft core
                    float prongs = pow(abs(cos(angle * 2.0)), 6.0);
                    float starRadius = lerp(0.28, 0.92, prongs);
                    shapeMask = saturate(1.0 - r / starRadius);
                    shapeMask = pow(shapeMask, 1.7);
                    coreMask = saturate(1.0 - r / 0.32);
                    coreMask = pow(coreMask, 2.4);
                }

                half3 flameCol = lerp(_OuterColor.rgb, _CoreColor.rgb, saturate(coreMask * 1.3));
                half3 finalRgb = flameCol * (shapeMask * _Intensity);
                return half4(finalRgb, 1.0);
            }
            ENDHLSL
        }
    }
}
