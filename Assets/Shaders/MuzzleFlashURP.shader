Shader "VRAtis/MuzzleFlashURP"
{
    Properties
    {
        [MainTexture] _MainTex ("Flash 2x2 Atlas (RGBA)", 2D) = "black" {}
        [HDR] _TintColor ("HDR Tint Color", Color) = (1.45, 1.35, 1.15, 1.0)
        _Intensity ("Dynamic Intensity", Range(0.0, 5.0)) = 1.0
        _TileIndex ("Atlas Tile Index (0-3)", Float) = 0.0
        _ViewFade ("Card Angle Edge Fade", Range(0.1, 2.0)) = 1.0
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
            Name "MuzzleFlashAtlasPass"

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float3 viewDirWS : TEXCOORD2;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                half4 _TintColor;
                float _Intensity;
                float _TileIndex;
                float _ViewFade;
            CBUFFER_END

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VertexPositionInputs posInputs = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = posInputs.positionCS;
                output.uv = input.uv;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.viewDirWS = GetWorldSpaceNormalizeViewDir(posInputs.positionWS);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float idx = fmod(max(0.0, floor(_TileIndex + 0.01)), 4.0);
                float tx = fmod(idx, 2.0);
                float ty = floor(idx * 0.5);
                // Guard 1% border inside each 2x2 tile to prevent bilinear bleeding
                float2 safeUV = clamp(input.uv, 0.01, 0.99);
                float2 tileUV = (safeUV * 0.5) + float2(tx, ty) * 0.5;

                half4 tex = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, tileUV);

                float ndotv = abs(dot(normalize(input.normalWS), normalize(input.viewDirWS)));
                float edgeSoft = smoothstep(0.04 * _ViewFade, 0.36 * _ViewFade, ndotv);

                half3 finalRgb = tex.rgb * _TintColor.rgb * (tex.a * _Intensity * edgeSoft);
                return half4(finalRgb, 1.0);
            }
            ENDHLSL
        }
    }
}
