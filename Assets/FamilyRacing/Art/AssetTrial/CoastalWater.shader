Shader "FamilyRacing/Trial Coastal Water"
{
    Properties
    {
        _Shallow("Shallows", Color) = (.05,.57,.62,1)
        _Deep("Deep water", Color) = (.025,.25,.38,1)
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" "Queue"="Geometry" }
        Pass
        {
            Tags { "LightMode"="UniversalForward" }
            ZWrite On
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            CBUFFER_START(UnityPerMaterial)
                half4 _Shallow;
                half4 _Deep;
            CBUFFER_END
            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings { float4 positionCS : SV_POSITION; float3 world : TEXCOORD0; half fog : TEXCOORD1; };
            Varyings vert(Attributes input)
            {
                Varyings output;
                output.world = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(output.world);
                output.fog = ComputeFogFactor(output.positionCS.z);
                return output;
            }
            half4 frag(Varyings input) : SV_Target
            {
                float2 p = input.world.xz;
                float wave = sin(p.x * .19 + p.y * .12 + _Time.y * .65)
                    + sin(p.y * .29 - p.x * .09 - _Time.y * .45);
                float sparkle = pow(saturate(sin(p.x * .9 + p.y * .63 + wave * .8 + _Time.y)), 20);
                float distanceFade = saturate(1 - distance(input.world, GetCameraPositionWS()) / 260);
                half3 color = lerp(_Deep.rgb, _Shallow.rgb, .48 + wave * .12);
                color += half3(.55,.74,.7) * sparkle * .12 * distanceFade;
                return half4(MixFog(color, input.fog), 1);
            }
            ENDHLSL
        }
    }
}
