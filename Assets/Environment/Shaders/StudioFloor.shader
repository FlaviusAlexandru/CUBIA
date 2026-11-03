// CUBIA studio floor: a light, effectively infinite floor that doubles as a depth cue.
//  - 1 m and 10 m grid lines, anti-aliased and faded out as they get too dense to read;
//  - distance rings around the table (5, 10, 25, 50, 100 m) so a plot scaled up 100x still reads at scale;
//  - a soft footprint under the plot's bounds, at any height or size (set by PlotFloorFootprint);
//  - receives main-light shadows;
//  - blends into the sky's horizon colour with distance, so there is no visible floor edge.
Shader "CUBIA/StudioFloor"
{
    Properties
    {
        _BaseColor      ("Base", Color) = (0.86, 0.87, 0.885, 1)
        _HorizonColor   ("Horizon (match the sky)", Color) = (0.93, 0.935, 0.945, 1)
        _MinorColor     ("Minor lines (1 m)", Color) = (0.74, 0.75, 0.77, 1)
        _MajorColor     ("Major lines (10 m)", Color) = (0.60, 0.62, 0.66, 1)
        _RingColor      ("Distance rings", Color) = (0.42, 0.52, 0.66, 1)
        _FootprintColor ("Plot footprint", Color) = (0.30, 0.40, 0.55, 1)
        _MinorSpacing   ("Minor spacing (m)", Float) = 1
        _MajorSpacing   ("Major spacing (m)", Float) = 10
        _LineWidthPx    ("Line width (px)", Float) = 1.2
        _RingWidthPx    ("Ring width (px)", Float) = 1.8
        _Rings          ("Ring radii (m)", Vector) = (5, 10, 25, 50)
        _Ring4          ("Fifth ring radius (m)", Float) = 100
        _FadeStart      ("Horizon fade start (m)", Float) = 25
        _FadeEnd        ("Horizon fade end (m)", Float) = 220
        _ShadowStrength ("Shadow strength", Range(0, 1)) = 0.35
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        Pass
        {
            Name "StudioFloor"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor, _HorizonColor, _MinorColor, _MajorColor, _RingColor, _FootprintColor;
                float _MinorSpacing, _MajorSpacing, _LineWidthPx, _RingWidthPx;
                float4 _Rings;
                float _Ring4, _FadeStart, _FadeEnd, _ShadowStrength;
            CBUFFER_END

            // Set every frame by PlotFloorFootprint (all zero = no plot / no footprint).
            float4 _CubiaStudioCenter;   // xz = table position the rings are centred on
            float4 _CubiaPlotBoundsMin;  // world AABB of the plot; w = 1 when valid
            float4 _CubiaPlotBoundsMax;

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionWS = TransformObjectToWorld(v.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                return o;
            }

            // 1 on a grid line, 0 elsewhere; also fades lines out where they would alias.
            float GridLines(float2 p, float spacing, float widthPx)
            {
                float2 c = p / spacing;
                float2 w = max(fwidth(c), 1e-5);
                float2 d = abs(frac(c - 0.5) - 0.5) / w;
                float line01 = 1.0 - saturate(min(d.x, d.y) - (widthPx - 1.0) * 0.5);
                float density = max(w.x, w.y);           // cells per pixel
                return line01 * saturate(1.0 - (density - 0.08) / 0.25);
            }

            float Ring(float r, float radius, float widthPx)
            {
                float d = abs(r - radius) / max(fwidth(r), 1e-5);
                return 1.0 - saturate(d - (widthPx - 1.0) * 0.5);
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float3 p = i.positionWS;
                half3 col = _BaseColor.rgb;

                // Plot footprint: a soft rectangle under the plot's bounds, darker the closer the plot is to the floor.
                if (_CubiaPlotBoundsMin.w > 0.5)
                {
                    float2 bmin = _CubiaPlotBoundsMin.xz, bmax = _CubiaPlotBoundsMax.xz;
                    float2 halfSize = max((bmax - bmin) * 0.5, 1e-3);
                    float2 q = abs(p.xz - (bmin + bmax) * 0.5) - halfSize;
                    float outside = length(max(q, 0.0)) + min(max(q.x, q.y), 0.0);  // signed distance (m)
                    float soft = max(min(halfSize.x, halfSize.y) * 0.35, 0.02);
                    float fill = 1.0 - smoothstep(-soft, soft, outside);
                    float height = max(_CubiaPlotBoundsMin.y - p.y, 0.0);
                    float strength = lerp(0.22, 0.10, saturate(height / (height + 2.0 * max(halfSize.x, halfSize.y))));
                    col = lerp(col, _FootprintColor.rgb, fill * strength);
                    float edgePx = abs(outside) / max(fwidth(outside), 1e-5);
                    col = lerp(col, _FootprintColor.rgb, (1.0 - saturate(edgePx - 0.5)) * 0.35);
                }

                col = lerp(col, _MinorColor.rgb, GridLines(p.xz, _MinorSpacing, _LineWidthPx) * 0.8);
                col = lerp(col, _MajorColor.rgb, GridLines(p.xz, _MajorSpacing, _LineWidthPx + 0.6));

                float r = length(p.xz - _CubiaStudioCenter.xz);
                float rings = max(max(Ring(r, _Rings.x, _RingWidthPx), Ring(r, _Rings.y, _RingWidthPx)),
                                  max(max(Ring(r, _Rings.z, _RingWidthPx), Ring(r, _Rings.w, _RingWidthPx)),
                                      Ring(r, _Ring4, _RingWidthPx)));
                col = lerp(col, _RingColor.rgb, rings * 0.75);

                float4 shadowCoord = TransformWorldToShadowCoord(p);
                half shadow = MainLightRealtimeShadow(shadowCoord);
                col *= lerp(1.0 - _ShadowStrength, 1.0, shadow);

                float dist = distance(p, _WorldSpaceCameraPos);
                col = lerp(col, _HorizonColor.rgb, smoothstep(_FadeStart, _FadeEnd, dist));
                return half4(col, 1);
            }
            ENDHLSL
        }

        UsePass "Universal Render Pipeline/Unlit/DepthOnly"
    }
    FallBack Off
}
