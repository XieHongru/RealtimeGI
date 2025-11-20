Shader "Mirai/VisualizeProbe"
{
    HLSLINCLUDE
    #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
    #include "ProbeCommon.hlsl"
    #include "../../GICommon.hlsl"

    #define VISUALIZE_IRRADIANCE_PROBE (0)
    #define VISUALIZE_RADIANCE_PROBE (1)

    struct VertexInput
    {
        float4 positionOS : POSITION;
        uint instanceID : SV_InstanceID;
    };

    struct FragmentInput
    {
        nointerpolation int3 probeVolumeAccessIndex : TEXCOORD0;
        nointerpolation int3 probeClipmapAccessIndex : TEXCOORD1;
        nointerpolation int3 probeIndex3D : TEXCOORD2;
        float3 rayDirection : TEXCOORD3;
        float3 positionWS : TEXCOORD4;
        float4 positionCS : SV_POSITION;
    };

    int _ProbeIndex;

    int _VisualizeMode;
    int _RadianceProbeResolution;
    int2 _RadianceProbeCountInAtlasXY;

    int _CascadeIndex;
    int _CascadeCount;
    int3 _CascadeResolution;
    float3 _CascadeCenter;
    float3 _CascadeSize;
    int3 _CascadeMoveOffset;

    float4x4 _CameraViewProjection;

    Texture3D<uint2> _VoxelBitOccupyClipmap;
    Texture3D<float4> _IrradianceProbeClipmap;
    Texture3D<float4> _ProbeOffsetClipmap;
    Texture3D<int> _RadianceProbeIdVolume;
    Texture2D<float3> _RadianceProbeAtlas;

    SamplerState sampler_LinearClamp;

    FragmentInput VisualizeProbeVS(VertexInput input)
    {
        FragmentInput output;

        float3 localPosition = input.positionOS.xyz;

        int3 probeCountInXYZ = _CascadeResolution / VOXEL_BLOCK_SIZE;
        int3 probeIndex3D = Index1DTo3D(_ProbeIndex, probeCountInXYZ);

        int3 probeVolumeAccessIndex = (probeIndex3D + _CascadeMoveOffset) % probeCountInXYZ;
        int3 probeClipmapAccessIndex = probeVolumeAccessIndex + int3(0, 0, probeCountInXYZ.z * _CascadeIndex);

        float3 probePositionBase = CalcVoxelCenterPos(probeIndex3D, probeCountInXYZ, _CascadeCenter, _CascadeSize);
        float4 probePositionOffsetRaw = _ProbeOffsetCascade[probeClipmapAccessIndex];
        float3 probePositionOffset = DecodeProbePositionOffset(probePositionOffsetRaw.xyz, _CascadeSize / float3(_CascadeResolution));
        float3 probePosition = probePositionBase;
        probePosition += probePositionOffset;

        float probeSizeScale = 0.1f;
        float3 worldPosition = probePosition + localPosition * probeSizeScale * pow(2, _CascadeIndex);
        if(probePositionOffsetRaw.w == 0)
        {
            worldPosition *= 0.0;
        }

        output.probeVolumeAccessIndex = probeVolumeAccessIndex;
        output.probeClipmapAccessIndex = probeClipmapAccessIndex;
        output.probeIndex3D = probeIndex3D;
        output.rayDirection = normalize(localPosition);
        output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
        output.positionCS = mul(_CameraViewProjection, float4(worldPosition, 1));
        output.positionCS.z /= output.positionCS.w;
        output.positionCS.y = -output.positionCS.y;

        return output;
    }

    float4 VisualizeProbeFS(FragmentInput input) : SV_Target
    {
        int3 probeCountInXYZ = _CascadeResolution / VOXEL_BLOCK_SIZE;
        float3 rayDirection = normalize(input.rayDirection);
        float3 color = float3(0, 0, 0);

        if (_VisualizeMode == VISUALIZE_IRRADIANCE_PROBE)
        {
            int3 readIndexBase = input.probeClipmapAccessIndex * int3(7, 1, 1);
    
	        ThreeBandSHVectorRGB irradianceSH;
	        irradianceSH.R.V0 = _IrradianceProbeClipmap[readIndexBase + float3(0, 0, 0)];
	        irradianceSH.R.V1 = _IrradianceProbeClipmap[readIndexBase + float3(1, 0, 0)];
	        irradianceSH.G.V0 = _IrradianceProbeClipmap[readIndexBase + float3(2, 0, 0)];
	        irradianceSH.G.V1 = _IrradianceProbeClipmap[readIndexBase + float3(3, 0, 0)];
	        irradianceSH.B.V0 = _IrradianceProbeClipmap[readIndexBase + float3(4, 0, 0)];
	        irradianceSH.B.V1 = _IrradianceProbeClipmap[readIndexBase + float3(5, 0, 0)];

	        float4 temp = _IrradianceProbeClipmap[readIndexBase + float3(6, 0, 0)];
	        irradianceSH.R.V2 = temp.x;
	        irradianceSH.G.V2 = temp.y;
	        irradianceSH.B.V2 = temp.z;

            ThreeBandSHVector diffuseTransferSH = CalcDiffuseTransferSH3(rayDirection, 1);
	        color = max(float3(0,0,0), DotSH3(irradianceSH, diffuseTransferSH)) / PI;
        }

        if (_VisualizeMode == VISUALIZE_RADIANCE_PROBE)
        {
            int probeIdInAtlas = _RadianceProbeIdVolume[input.probeVolumeAccessIndex];
            float2 uvInAtlas = RadianceProbeAddressMapping(rayDirection, probeIdInAtlas, _RadianceProbeCountInAtlasXY, _RadianceProbeResolution);
            color = _RadianceProbeAtlas.SampleLevel(sampler_LinearClamp, uvInAtlas, 0).rgb;
        }

	    return float4(color, 1);
    }

    ENDHLSL
    
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }
        LOD 100

        Pass
        {
            Name "VisualizeProbe"
            
            HLSLPROGRAM
            #pragma enable_d3d11_debug_symbols
            #pragma vertex VisualizeProbeVS
            #pragma fragment VisualizeProbeFS
            #pragma multi_compile_instancing

            ENDHLSL
        }
    }
}