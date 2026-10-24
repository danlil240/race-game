Shader "FamilyRacing/Trial Coastal Sky"
{
    Properties
    {
        _Zenith("Zenith", Color) = (.12,.43,.72,1)
        _Horizon("Horizon", Color) = (.71,.85,.87,1)
    }
    SubShader
    {
        Tags { "Queue"="Background" "RenderType"="Background" "RenderPipeline"="UniversalPipeline" "PreviewType"="Skybox" }
        Cull Off ZWrite Off
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            CBUFFER_START(UnityPerMaterial)
                half4 _Zenith;
                half4 _Horizon;
            CBUFFER_END
            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings { float4 positionCS : SV_POSITION; float3 direction : TEXCOORD0; };
            Varyings vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.direction = input.positionOS.xyz;
                return output;
            }
            half4 frag(Varyings input) : SV_Target
            {
                float3 direction = normalize(input.direction);
                half3 color = lerp(_Horizon.rgb, _Zenith.rgb, pow(saturate(direction.y), .55));
                float2 cloudUV = direction.xz / max(.2, direction.y) + float2(_Time.y * .002, 0);
                float clouds = sin(cloudUV.x * 2 + sin(cloudUV.y * 1.7))
                    * sin(cloudUV.y * 2.5 + cos(cloudUV.x * 1.3));
                float mask = smoothstep(.25, .65, clouds) * smoothstep(.06, .2, direction.y);
                color = lerp(color, half3(.97,.97,.91), mask * .75);
                return half4(color, 1);
            }
            ENDHLSL
        }
    }
}
