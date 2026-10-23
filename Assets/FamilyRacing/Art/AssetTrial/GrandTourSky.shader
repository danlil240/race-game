Shader "FamilyRacing/Grand Tour Sky"
{
    Properties {
        _Zenith("Zenith",Color)=(.14,.36,.59,1)
        _Horizon("Horizon",Color)=(.72,.82,.83,1)
    }
    SubShader {
        Tags {"Queue"="Background" "RenderType"="Background" "RenderPipeline"="UniversalPipeline" "PreviewType"="Skybox"}
        Cull Off ZWrite Off
        Pass {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            CBUFFER_START(UnityPerMaterial)
            half4 _Zenith,_Horizon;
            CBUFFER_END
            struct A {float4 positionOS:POSITION;};
            struct V {float4 positionCS:SV_POSITION;float3 direction:TEXCOORD0;};
            V vert(A i){V o;o.positionCS=TransformObjectToHClip(i.positionOS.xyz);o.direction=i.positionOS.xyz;return o;}
            float hash(float2 p){return frac(sin(dot(p,float2(127.1,311.7)))*43758.5453);}
            float noise(float2 p){float2 i=floor(p),f=frac(p);f=f*f*(3-2*f);return lerp(lerp(hash(i),hash(i+float2(1,0)),f.x),lerp(hash(i+float2(0,1)),hash(i+1),f.x),f.y);}
            half4 frag(V i):SV_Target {
                float3 d=normalize(i.direction);
                half3 color=lerp(_Horizon.rgb,_Zenith.rgb,pow(saturate(d.y),.45));
                float2 p=d.xz/max(.13,d.y)*1.7+float2(_Time.y*.002,0);
                float n=noise(p)*.55+noise(p*2.03+11)*.27+noise(p*4.1+31)*.13+noise(p*8.2)*.05;
                float clouds=smoothstep(.48,.71,n)*smoothstep(.04,.22,d.y);
                half3 cloudColor=lerp(half3(.74,.8,.81),half3(1,.97,.88),saturate(n*1.2));
                color=lerp(color,cloudColor,clouds*.88);
                float sun=pow(saturate(dot(d,normalize(float3(-.4,.45,-.7)))),180);
                color+=half3(1,.82,.48)*sun*.45;
                return half4(color,1);
            }
            ENDHLSL
        }
    }
}
