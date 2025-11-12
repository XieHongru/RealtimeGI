Shader "Mirai/VisualizeProbe"
{
    HLSLINCLUDE
    #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
    #include "../../GICommon.hlsl"

    struct VertexInput
    {
        float4 positionOS : POSITION;
        uint instanceID : SV_InstanceID;
    };

    struct FragmentInput
    {
        nointerpolation int3 probeIndex3D : TEXCOORD0;
        float3 rayDirection : TEXCOORD1;
        float3 positionWS : TEXCOORD2;
        float4 positionCS : SV_POSITION;
    };

    int _ProbeResolution;
    int3 _ProbeCountInXYZ;
    int2 _ProbeCountInAtlasXY;
    int _CascadeIndex;
    int _CascadeCount;
    int3 _CascadeResolution;
    float3 _CascadeCenter;
    float3 _CascadeSize;
    int3 _CascadeMoveOffset;

    float4x4 _CameraViewProjection;

    TEXTURE2D(_FarFieldProbeAtlas);
    SAMPLER(sampler_FarFieldProbeAtlas);

    FragmentInput VisualizeFarFieldProbeVS(VertexInput input)
    {
        FragmentInput output;

        output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
        float3 localPosition = input.positionOS.xyz;

        int3 probeIndex3D = Index1DTo3D((int)input.instanceID, _ProbeCountInXYZ);
        float3 probeLocation = CalcVoxelCenterPos(probeIndex3D, _ProbeCountInXYZ, _CascadeCenter, _CascadeSize);
        float3 worldPosition = probeLocation + localPosition * 25.0f / 100.0f * pow(2, _CascadeIndex);

        output.probeIndex3D = probeIndex3D;
        output.rayDirection = normalize(localPosition);
        output.positionCS = mul(_CameraViewProjection, float4(worldPosition, 1));
        output.positionCS.z /= output.positionCS.w;
        output.positionCS.y = -output.positionCS.y;

        return output;
    }

    float4 VisualizeFarFieldProbeFS(FragmentInput input) : SV_Target
    {
        int3 probeIndex3D = input.probeIndex3D;
        float2 uv = FarFieldProbeAddressMapping(
            input.rayDirection, probeIndex3D, _ProbeCountInXYZ,
	        _ProbeCountInAtlasXY, _ProbeResolution
        );

        float3 outColor = SAMPLE_TEXTURE2D(_FarFieldProbeAtlas, sampler_FarFieldProbeAtlas, uv).rgb;

        return float4(outColor, 1);
    }
    ENDHLSL
    
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }
        LOD 100

        Pass
        {
            Name "VisualizeFarFieldProbe"
            
            HLSLPROGRAM
            #pragma enable_d3d11_debug_symbols
            #pragma vertex VisualizeFarFieldProbeVS
            #pragma fragment VisualizeFarFieldProbeFS
            #pragma multi_compile_instancing

            ENDHLSL
        }
    }
}