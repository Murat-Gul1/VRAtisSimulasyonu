Shader "VRAtis/AlpineRainURP"
{
    Properties
    {
        [MainTexture] _BaseMap ("Rain Streak Texture", 2D) = "white" {}
        [MainColor] _BaseColor ("Rain Tint Color", Color) = (0.86, 0.92, 0.98, 0.72)
        _BoxSize ("Wrap Volume Size (XYZ)", Vector) = (14.0, 14.0, 14.0, 0.0)
        _BaseFallSpeed ("Base Fall Speed (m/s)", Float) = 19.0
        _MinLen ("Min Streak Length (m)", Float) = 0.55
        _MaxLen ("Max Streak Length (m)", Float) = 1.05
        _MinWidth ("Min Streak Width (m)", Float) = 0.015
        _MaxWidth ("Max Streak Width (m)", Float) = 0.032
        _NearFadeStart ("Eye Fade Start (m)", Float) = 0.55
        _NearFadeEnd ("Eye Fade End (m)", Float) = 1.65
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Transparent+10"
            "RenderType" = "Transparent"
            "IgnoreProjector" = "True"
        }

        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off

        Pass
        {
            Name "AlpineRainPass"

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION; // Seed center (x0, y0, z0) shared by all 4 corners of one drop
                float2 uv : TEXCOORD0;        // u = along drop length [0..1], v = across width [0..1]
                half4 color : COLOR;          // r = speed rand, g = length rand, b = width rand, a = alpha rand
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                half alpha : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _BaseColor;
                float4 _BoxSize;
                float _BaseFallSpeed;
                float _MinLen;
                float _MaxLen;
                float _MinWidth;
                float _MaxWidth;
                float _NearFadeStart;
                float _NearFadeEnd;
            CBUFFER_END

            // Per-instance / dynamic driver uniforms updated by RainWeatherController
            float _RainIntensity;
            float4 _RainWindXZ;
            float4 _RainAnchorPos;
            float4 _RainCamForward;

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                if (_RainIntensity <= 0.002)
                {
                    output.positionCS = float4(2.0, 2.0, 2.0, 1.0);
                    output.uv = input.uv;
                    output.alpha = 0.0h;
                    return output;
                }

                float3 box = max(_BoxSize.xyz, float3(1.0, 1.0, 1.0));
                float speedScale = lerp(0.80, 1.24, input.color.r);
                float3 vel = float3(_RainWindXZ.x, -_BaseFallSpeed * speedScale, _RainWindXZ.y);

                // Wrap shared drop seed cleanly inside the moving camera-front box (no per-corner tearing)
                float3 rel = frac((input.positionOS.xyz + vel * _Time.y - _RainAnchorPos.xyz) / box + 0.5) - 0.5;
                float3 centerWS = _RainAnchorPos.xyz + rel * box;

                float3 toDrop = centerWS - _WorldSpaceCameraPos;
                float camDist = length(toDrop);

                // Early-cull drops behind the viewer or outside the ~62 deg VR view cone (zero fill-rate cost)
                float fwdLenSq = dot(_RainCamForward.xyz, _RainCamForward.xyz);
                if (fwdLenSq > 0.5)
                {
                    float camFwdDot = dot(toDrop, _RainCamForward.xyz);
                    if (camFwdDot < 0.15 || (camDist > 1.1 && camFwdDot < camDist * 0.45))
                    {
                        output.positionCS = float4(2.0, 2.0, 2.0, 1.0);
                        output.uv = input.uv;
                        output.alpha = 0.0h;
                        return output;
                    }
                }

                // Fade softly near volume borders and suppress drops clipping beneath the terrain plane
                float borderDist = min(min(0.5 - abs(rel.x), 0.5 - abs(rel.y)), 0.5 - abs(rel.z));
                float edgeMask = saturate(borderDist * 8.0);
                float groundMask = smoothstep(0.05, 0.55, centerWS.y);

                // Cylindrical velocity-aligned billboard around each independent falling drop
                float3 axisLen = normalize(vel);
                float3 toCamDir = camDist > 0.001 ? (-toDrop / camDist) : float3(0.0, 0.0, 1.0);
                float3 axisSide = cross(axisLen, toCamDir);
                float sideLenSq = dot(axisSide, axisSide);
                axisSide = sideLenSq > 1e-5 ? (axisSide * rsqrt(sideLenSq)) : float3(1.0, 0.0, 0.0);

                float dropLen = lerp(_MinLen, _MaxLen, input.color.g);
                float dropWidth = lerp(_MinWidth, _MaxWidth, input.color.b);

                float3 posWS = centerWS
                    + axisLen * ((input.uv.x - 0.5) * dropLen)
                    + axisSide * ((input.uv.y - 0.5) * dropWidth);

                float eyeFade = smoothstep(_NearFadeStart, max(_NearFadeStart + 0.1, _NearFadeEnd), camDist);
                float dropAlpha = _BaseColor.a * _RainIntensity * lerp(0.55, 1.0, input.color.a) * edgeMask * groundMask * eyeFade;

                output.positionCS = TransformWorldToHClip(posWS);
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                output.alpha = (half)dropAlpha;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                if (input.alpha <= 0.004h)
                    discard;

                half4 tex = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv);
                half a = tex.a * input.alpha;
                if (a <= 0.004h)
                    discard;

                return half4(tex.rgb * _BaseColor.rgb, a);
            }
            ENDHLSL
        }
    }
}
