Shader "Mirai/SurfaceCacheCapture"
{
    Properties
    {
        _BaseColor ("Base Color", Color) = (1,1,1,1)
        _BaseMap ("Base Map", 2D) = "white" {}
        _EmissionColor ("Emission Color", Color) = (0,0,0,1)
        _EmissionMap ("Emission Map", 2D) = "white" {}
        _NormalMap ("Normal Map", 2D) = "bump" {}
    }

    HLSLINCLUDE
    #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

    struct VertexInput
    {
        float4 positionOS : POSITION;
        float3 normalOS : NORMAL;
        float2 texcoord : TEXCOORD0;
        //UNITY_VERTEX_INPUT_INSTANCE_ID
        uint instanceID : SV_InstanceID;
    };

    struct FragmentInput
    {
        float4 positionCS : SV_POSITION;
        float3 positionWS : TEXCOORD1;
        float3 positionOS : TEXCOORD2;
        float3 normalOS : TEXCOORD3;
        float cardOrientation : TEXCOORD4;
        float2 uv : TEXCOORD5;
    };

    TEXTURE2D(_BaseMap);
    SAMPLER(sampler_BaseMap);
    TEXTURE2D(_EmissionMap);
    SAMPLER(sampler_EmissionMap);
    TEXTURE2D(_NormalMap);
    SAMPLER(sampler_NormalMap);

    CBUFFER_START(UnityPerMaterial)
        float4 _BaseColor;
        float4 _EmissionColor;
    CBUFFER_END

    CBUFFER_START(CardCaptureParams)
        float3 _CardCenter;
        float3 _CardSize;
        float4x4 _ViewProjectionMatrices[6];
        float _CardOrientations[6];
        float4 _ViewportInfos[6];
        int _UseInstance;
    CBUFFER_END

    float CalcOrientation(float3 inVec3, int orientation)
    {
        if(orientation == 0) return inVec3.x;
        if(orientation == 1) return inVec3.y;
        if(orientation == 2) return inVec3.z;
        return 0;
    }

    FragmentInput SurfaceCacheCaptureVS(VertexInput input)
    {
        FragmentInput output;

        float3 worldPosition = TransformObjectToWorld(input.positionOS.xyz);
        float3 localPosition = input.positionOS.xyz;
        float3 worldNormal = TransformObjectToWorldNormal(input.normalOS);

        output.positionWS = worldPosition;
        output.positionOS = localPosition;
        output.normalOS = input.normalOS;
        output.uv = input.texcoord;

        uint cardIndex = _UseInstance ? input.instanceID : 0;
        output.cardOrientation = _CardOrientations[cardIndex];

        float4 outSVPosition = mul(_ViewProjectionMatrices[cardIndex], float4(localPosition, 1));
        float4 viewportInfo = _ViewportInfos[cardIndex];
        outSVPosition.xy = outSVPosition.xy * viewportInfo.xy + viewportInfo.zw;
	
        output.positionCS = outSVPosition;

        return output;
    }

    struct FragmentOutput
    {
        half4 baseColor : SV_Target0;
        half4 normal : SV_Target1;
        half4 emissive : SV_Target2;
        half4 depth : SV_Target3;
    };

    FragmentOutput SurfaceCacheCaptureFS(FragmentInput input)
    {
        FragmentOutput output;

        half4 baseColor = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv) * _BaseColor;
        half4 emission = SAMPLE_TEXTURE2D(_EmissionMap, sampler_EmissionMap, input.uv) * _EmissionColor;
                
        output.baseColor = baseColor;
        output.emissive = emission;

        float3 encodedNormal = input.normalOS * 0.5 + 0.5;
        output.normal = half4(encodedNormal, 1.0);

        float3 localPosition = input.positionOS;
        float3 encodedPosition = (localPosition - _CardCenter) / _CardSize + 0.5; // map to [0, 1]
        float encodedDepth = CalcOrientation(encodedPosition, input.cardOrientation);
        output.depth = half4(encodedDepth, 0.0, 0.0, 1.0);
                
        return output;
    }
    ENDHLSL
    
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }
        LOD 100

        Pass
        {
            Name "SurfaceCacheCapture"
            
            HLSLPROGRAM
            #pragma enable_d3d11_debug_symbols
            #pragma vertex SurfaceCacheCaptureVS
            #pragma fragment SurfaceCacheCaptureFS
            #pragma multi_compile_instancing

            ENDHLSL
        }
    }
}