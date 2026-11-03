// CUBIA studio sky: a plain vertical gradient. The horizon colour matches the StudioFloor's
// horizon colour, so the floor fades into the sky with no visible edge.
Shader "CUBIA/GradientSky"
{
    Properties
    {
        _ZenithColor  ("Zenith", Color) = (0.82, 0.86, 0.91, 1)
        _HorizonColor ("Horizon", Color) = (0.93, 0.935, 0.945, 1)
        _GroundColor  ("Below horizon", Color) = (0.90, 0.905, 0.915, 1)
        _Exponent     ("Gradient falloff", Range(0.1, 4)) = 0.55
    }

    SubShader
    {
        Tags { "Queue" = "Background" "RenderType" = "Background" "PreviewType" = "Skybox" "RenderPipeline" = "UniversalPipeline" }
        Cull Off ZWrite Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _ZenithColor, _HorizonColor, _GroundColor;
                float _Exponent;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; float3 dir : TEXCOORD0; UNITY_VERTEX_OUTPUT_STEREO };

            Varyings vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.dir = v.positionOS.xyz;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float y = normalize(i.dir).y;
                half3 up = lerp(_HorizonColor.rgb, _ZenithColor.rgb, pow(saturate(y), _Exponent));
                half3 down = lerp(_HorizonColor.rgb, _GroundColor.rgb, saturate(-y * 4.0));
                return half4(y >= 0 ? up : down, 1);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
