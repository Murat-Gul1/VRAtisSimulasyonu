Shader "VRAtis/WorldMapWaterURP"
{
    Properties
    {
        _WaveNormalMap ("Wave Normal Map", 2D) = "bump" {}
        _DepthMap ("Baked Water Depth Map", 2D) = "white" {}
        _ShallowColor ("Shallow Turquoise", Color) = (0.18, 0.56, 0.62, 1)
        _MidColor ("Coastal Blue", Color) = (0.09, 0.36, 0.52, 1)
        _DeepColor ("Deep Ocean Navy", Color) = (0.04, 0.17, 0.31, 1)
        _SkyReflectColor ("Sky Reflection Tint", Color) = (0.64, 0.79, 0.90, 1)
        _FoamColor ("Shoreline Foam Color", Color) = (0.90, 0.96, 0.98, 1)
        _WaveScale ("Wave Tiling", Float) = 0.022
        _WaveSpeed ("Wave Speed", Float) = 0.032
        _WaveStrength ("Wave Normal Strength", Range(0.05, 1.5)) = 0.55
        _FoamDepth ("Foam Depth Threshold", Range(0.02, 0.45)) = 0.14
        _SpecularPower ("Sun Specular Power", Range(8, 256)) = 96
        _SpecularIntensity ("Sun Specular Intensity", Range(0, 2)) = 0.72
        _MapOriginXZ ("Terrain Origin XZ", Vector) = (-800, -800, 0, 0)
        _MapSize ("Terrain Size Meters", Float) = 1600
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Opaque"
            "Queue" = "Geometry+10"
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
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 positionWS  : TEXCOORD0;
                float  fogFactor   : TEXCOORD1;
            };

            TEXTURE2D(_WaveNormalMap);
            SAMPLER(sampler_WaveNormalMap);
            TEXTURE2D(_DepthMap);
            SAMPLER(sampler_DepthMap);

            CBUFFER_START(UnityPerMaterial)
                half4 _ShallowColor;
                half4 _MidColor;
                half4 _DeepColor;
                half4 _SkyReflectColor;
                half4 _FoamColor;
                float _WaveScale;
                float _WaveSpeed;
                half  _WaveStrength;
                half  _FoamDepth;
                half  _SpecularPower;
                half  _SpecularIntensity;
                float4 _MapOriginXZ;
                float _MapSize;
            CBUFFER_END

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                VertexPositionInputs posInputs = GetVertexPositionInputs(IN.positionOS.xyz);
                OUT.positionHCS = posInputs.positionCS;
                OUT.positionWS = posInputs.positionWS;
                OUT.fogFactor = ComputeFogFactor(posInputs.positionCS.z);
                return OUT;
            }

            half3 SampleAnimatedWaterNormal(float2 worldXZ, float t)
            {
                float2 uv1 = worldXZ * _WaveScale + float2(t * _WaveSpeed, t * _WaveSpeed * 0.62);
                float2 uv2 = worldXZ * (_WaveScale * 1.47) + float2(-t * _WaveSpeed * 0.78, t * _WaveSpeed * 0.91);

                half3 n1 = UnpackNormalScale(SAMPLE_TEXTURE2D(_WaveNormalMap, sampler_WaveNormalMap, uv1), _WaveStrength);
                half3 n2 = UnpackNormalScale(SAMPLE_TEXTURE2D(_WaveNormalMap, sampler_WaveNormalMap, uv2), _WaveStrength * 0.8);

                // Tangent-space on horizontal XZ plane: tangent=(1,0,0), bitangent=(0,0,1), normal=(0,1,0)
                half3 blended = normalize(half3(n1.x + n2.x, 1.0h, n1.y + n2.y));
                return blended;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float t = _Time.y;
                float2 worldXZ = IN.positionWS.xz;

                // Map world XZ to Terrain normalized [0,1] UV; outside terrain smoothly treated as deep ocean (depth = 1)
                float2 mapUV = (worldXZ - _MapOriginXZ.xy) / max(_MapSize, 1.0);
                float outside = step(mapUV.x, 0.001) + step(0.999, mapUV.x) + step(mapUV.y, 0.001) + step(0.999, mapUV.y);
                float2 clampedUV = clamp(mapUV, 0.002, 0.998);
                half bakedDepth = SAMPLE_TEXTURE2D(_DepthMap, sampler_DepthMap, clampedUV).r;
                half depth = outside > 0.5 ? 1.0h : bakedDepth;

                half3 normalWS = SampleAnimatedWaterNormal(worldXZ, t);
                half3 viewDirWS = normalize(GetWorldSpaceViewDir(IN.positionWS));

                // Shallow turquoise -> mid coastal blue -> deep ocean navy
                half midT = saturate(depth * 2.6h);
                half deepT = saturate((depth - 0.28h) * 1.65h);
                half3 waterAlbedo = lerp(_ShallowColor.rgb, _MidColor.rgb, midT);
                waterAlbedo = lerp(waterAlbedo, _DeepColor.rgb, deepT);

                // Directional lighting + Fresnel sky sheen + sun specular sparkle
                Light mainLight = GetMainLight();
                half3 lightDir = normalize(mainLight.direction);
                half ndl = saturate(dot(normalWS, lightDir));
                half3 diffuse = waterAlbedo * (0.58h + 0.42h * ndl) * mainLight.color;

                half fresnel = pow(1.0h - saturate(dot(normalWS, viewDirWS)), 3.2h);
                half3 color = lerp(diffuse, _SkyReflectColor.rgb, fresnel * 0.48h);

                half3 halfDir = normalize(lightDir + viewDirWS);
                half spec = pow(saturate(dot(normalWS, halfDir)), _SpecularPower) * _SpecularIntensity;
                color += mainLight.color * spec;

                // Soft animated white surf foam along beaches and smooth riverbanks
                half shoreZone = 1.0h - smoothstep(0.0h, _FoamDepth, depth);
                half wavePulse = sin(depth * 36.0h - t * 2.4h + normalWS.x * 4.0h) * 0.5h + 0.5h;
                half edgeFoam = 1.0h - smoothstep(0.0h, _FoamDepth * 0.34h, depth);
                half foamMask = saturate(edgeFoam * 0.82h + shoreZone * wavePulse * 0.42h);
                color = lerp(color, _FoamColor.rgb, foamMask * 0.85h);

                color = MixFog(color, IN.fogFactor);
                return half4(color, 1.0h);
            }
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Lit"
}
