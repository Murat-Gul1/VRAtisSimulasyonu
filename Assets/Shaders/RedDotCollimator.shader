Shader "VRAtis/RedDotCollimator"
{
    Properties
    {
        [HDR] _DotColor ("Dot Color (HDR)", Color) = (3.6, 0.16, 0.08, 1.0)
        _ReticlePower ("Reticle Power (On/Off)", Range(0.0, 1.0)) = 0.0
        _DotRadius ("Dot Size (MOA Scale)", Range(0.0005, 0.05)) = 0.0028
        _DotSoftness ("Dot Edge Softness", Range(0.0003, 0.02)) = 0.0011
        _ParallaxScale ("Collimation Scale", Range(0.5, 4.0)) = 1.6
        _RingEnable ("Show Tactical Outer Ring (0/1)", Range(0, 1)) = 0.0
        _RingRadius ("Outer Ring Radius", Range(0.05, 0.35)) = 0.14
        _RingThickness ("Outer Ring Thickness", Range(0.002, 0.03)) = 0.008
        _LensTint ("Glass Tint", Color) = (0.04, 0.14, 0.18, 0.14)
        _ForwardAxis ("Lens Local Forward Axis (X,Y,Z)", Vector) = (0, 0, 1, 0)
        _UpAxis ("Lens Local Up Axis (X,Y,Z)", Vector) = (0, 1, 0, 0)
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Transparent+20"
            "RenderType" = "Transparent"
            "IgnoreProjector" = "True"
        }

        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off

        Pass
        {
            Name "RedDotCollimatorPass"

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
                float3 positionOS : TEXCOORD1;
                float3 camPosOS : TEXCOORD2;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            CBUFFER_START(UnityPerMaterial)
                half4 _DotColor;
                float _ReticlePower;
                float _DotRadius;
                float _DotSoftness;
                float _ParallaxScale;
                float _RingEnable;
                float _RingRadius;
                float _RingThickness;
                half4 _LensTint;
                float4 _ForwardAxis;
                float4 _UpAxis;
            CBUFFER_END

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VertexPositionInputs posInputs = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = posInputs.positionCS;
                output.uv = input.uv;
                output.positionOS = input.positionOS.xyz;

                // Camera position in object space (works per-eye in Stereo VR)
                output.camPosOS = TransformWorldToObject(GetCameraPositionWS());
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                // Mask to circular glass interior
                float radialMask = 1.0 - smoothstep(0.46, 0.495, length(input.uv - 0.5));
                if (radialMask <= 0.001)
                    discard;

                // Basis vectors for the sight tube in object space
                float3 fwd = normalize(_ForwardAxis.xyz);
                float3 upVec = normalize(_UpAxis.xyz);
                float3 rightVec = normalize(cross(upVec, fwd));
                upVec = normalize(cross(fwd, rightVec));

                // Ray from eye through this glass fragment (in object space)
                float3 rayOS = normalize(input.positionOS - input.camPosOS);
                float denom = dot(rayOS, fwd);

                // Only project reticle when looking forward through the tube
                float facingMask = step(0.15, denom);
                float safeDenom = max(0.15, denom);

                // Infinite-focus angular projection onto reticle UV space
                float2 angularOffset = float2(dot(rayOS, rightVec), dot(rayOS, upVec)) / safeDenom;
                float distFromAim = length(angularOffset * _ParallaxScale);

                // Crisp glowing ~3 MOA center dot with screen-space AA so needle-point dot stays sharp without shimmer
                float aa = max(_DotSoftness, fwidth(distFromAim) * 0.85);
                float dotIntensity = 1.0 - smoothstep(_DotRadius, _DotRadius + aa, distFromAim);

                // Optional EOTech-style outer reticle ring
                float ringDist = abs(distFromAim - _RingRadius);
                float ringIntensity = (1.0 - smoothstep(_RingThickness, _RingThickness + _DotSoftness, ringDist)) * _RingEnable;

                float reticleAlpha = saturate(max(dotIntensity, ringIntensity)) * facingMask * radialMask * saturate(_ReticlePower);

                half3 color = lerp(_LensTint.rgb, _DotColor.rgb, reticleAlpha);
                half alpha = saturate(_LensTint.a * radialMask + reticleAlpha);

                return half4(color, alpha);
            }
            ENDHLSL
        }
    }
}
