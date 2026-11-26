Shader "VRAtis/WorldMapEdgeMistURP"
{
    Properties
    {
        _MistColor ("Mist Cloud Color", Color) = (0.76, 0.83, 0.89, 1)
        _MistHighlight ("Sunlit Mist Highlight", Color) = (0.91, 0.94, 0.97, 1)
        _NoiseTex ("Mist Noise Texture", 2D) = "gray" {}
        _NorthStartZ ("North Mist Start Z", Float) = 490
        _NorthFullZ ("North Mist Full Z", Float) = 695
        _EastStartX ("East Mist Start X", Float) = 500
        _EastFullX ("East Mist Full X", Float) = 705
        _MaxOpacity ("Max Mist Opacity", Range(0, 1)) = 0.94
        _DriftSpeed ("Cloud Drift Speed", Float) = 0.008
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Transparent"
            "Queue" = "Transparent+20"
        }

        Pass
        {
            Name "EdgeMist"
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 positionWS  : TEXCOORD0;
            };

            TEXTURE2D(_NoiseTex);
            SAMPLER(sampler_NoiseTex);

            CBUFFER_START(UnityPerMaterial)
                half4 _MistColor;
                half4 _MistHighlight;
                float _NorthStartZ;
                float _NorthFullZ;
                float _EastStartX;
                float _EastFullX;
                half  _MaxOpacity;
                float _DriftSpeed;
            CBUFFER_END

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                VertexPositionInputs posInputs = GetVertexPositionInputs(IN.positionOS.xyz);
                OUT.positionHCS = posInputs.positionCS;
                OUT.positionWS = posInputs.positionWS;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float2 wxz = IN.positionWS.xz;
                float t = _Time.y;

                // Dual slow-drifting cloud layers for organic mountain mist
                float2 uv1 = wxz * 0.0018 + float2(t * _DriftSpeed, -t * _DriftSpeed * 0.45);
                float2 uv2 = wxz * 0.0042 + float2(-t * _DriftSpeed * 0.65, t * _DriftSpeed * 0.35);
                half n1 = SAMPLE_TEXTURE2D(_NoiseTex, sampler_NoiseTex, uv1).r;
                half n2 = SAMPLE_TEXTURE2D(_NoiseTex, sampler_NoiseTex, uv2).r;
                half cloud = saturate(n1 * 0.65h + n2 * 0.35h);

                // Soft organic edge perturbation so mist wisps curl naturally down mountain slopes
                float edgeWarp = (cloud - 0.5) * 105.0;

                half northMask = saturate((wxz.y + edgeWarp - _NorthStartZ) / max(1.0, _NorthFullZ - _NorthStartZ));
                half eastMask  = saturate((wxz.x + edgeWarp - _EastStartX) / max(1.0, _EastFullX - _EastStartX));

                // Smooth S-curve falloff
                northMask = northMask * northMask * (3.0h - 2.0h * northMask);
                eastMask  = eastMask * eastMask * (3.0h - 2.0h * eastMask);

                half edgeMask = max(northMask, eastMask);
                if (edgeMask <= 0.002h)
                    discard;

                // Wispy at foothill start, dense obscuring alpine cloud bank toward outer map border
                half densityMod = lerp(0.45h + cloud * 0.55h, 1.0h, saturate(edgeMask * 1.45h));
                half alpha = saturate(edgeMask * densityMod * _MaxOpacity);

                half3 col = lerp(_MistColor.rgb, _MistHighlight.rgb, cloud * 0.78h);
                return half4(col, alpha);
            }
            ENDHLSL
        }
    }
}
