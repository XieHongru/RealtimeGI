Shader "Mirai/VisualizeProbe"
{
    HLSLINCLUDE
    #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
    #include "../RayTracing/SHCommon.hlsl"
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
    
    // -------------------------------------------------------------------------------- //
    Texture3D<uint2> _VoxelBitOccupyClipmap;
    Texture3D<float4> _NearFieldProbeVolume;

    FragmentInput VisualizeNearFieldProbeVS(VertexInput input)
    {
        FragmentInput output;

        output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
        float3 localPosition = input.positionOS.xyz;
        int3 probeCountInXYZ = _CascadeResolution / VOXEL_BLOCK_SIZE;
        int3 probeIndex3D = Index1DTo3D(input.instanceID, probeCountInXYZ);

        float3 probeLocation = CalcVoxelCenterPos(probeIndex3D, probeCountInXYZ, _CascadeCenter, _CascadeSize);
        float3 worldPosition = probeLocation + localPosition * 15.0f / 100.f * pow(2, _CascadeIndex);

        int3 clipmapAccessIndex = ClipmapAddressMapping(probeIndex3D * VOXEL_BLOCK_SIZE, _CascadeResolution, _CascadeMoveOffset, _CascadeIndex);
        int2 bitOccupy = _VoxelBitOccupyClipmap.Load(int4(clipmapAccessIndex, 0)).xy;
        bool isProbeValid = any(bitOccupy != 0);
        if(!isProbeValid)
        {
            worldPosition *= 0.0;
        }

        output.probeIndex3D = probeIndex3D;
        output.rayDirection = normalize(localPosition);
        output.positionCS = mul(_CameraViewProjection, float4(worldPosition, 1));
        output.positionCS.z /= output.positionCS.w;
        output.positionCS.y = -output.positionCS.y;

        return output;
    }

    float4 VisualizeNearFieldProbeFS(FragmentInput input) : SV_Target
    {
        int3 probeCountInXYZ = _CascadeResolution / VOXEL_BLOCK_SIZE;
        int3 probeCascadeAccessIndex = (input.probeIndex3D + _CascadeMoveOffset) % probeCountInXYZ;
        int3 readIndexBase = probeCascadeAccessIndex * int3(1, 1, 7);
    
	    ThreeBandSHVectorRGB irradianceSH;
	    irradianceSH.R.V0 = _NearFieldProbeVolume.Load(int4(readIndexBase + float3(0, 0, 0), 0));
	    irradianceSH.R.V1 = _NearFieldProbeVolume.Load(int4(readIndexBase + float3(0, 0, 1), 0));
	    irradianceSH.G.V0 = _NearFieldProbeVolume.Load(int4(readIndexBase + float3(0, 0, 2), 0));
	    irradianceSH.G.V1 = _NearFieldProbeVolume.Load(int4(readIndexBase + float3(0, 0, 3), 0));
	    irradianceSH.B.V0 = _NearFieldProbeVolume.Load(int4(readIndexBase + float3(0, 0, 4), 0));
	    irradianceSH.B.V1 = _NearFieldProbeVolume.Load(int4(readIndexBase + float3(0, 0, 5), 0));

	    float4 temp = _NearFieldProbeVolume.Load(int4(readIndexBase + float3(0, 0, 6), 0));
	    irradianceSH.R.V2 = temp.x;
	    irradianceSH.G.V2 = temp.y;
	    irradianceSH.B.V2 = temp.z;

        ThreeBandSHVector diffuseTransferSH = CalcDiffuseTransferSH3(input.rayDirection, 1);
	    float3 color = max(float3(0,0,0), DotSH3(irradianceSH, diffuseTransferSH)) / PI;

        // ThreeBandSHVector Sh3Basis = SHBasisFunction3(input.rayDirection);
        // float3 color = DotSH3(radianceSH3, Sh3Basis);

	    return float4(color, 1);
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

        Pass
        {
            Name "VisualizeNearFieldProbe"
            
            HLSLPROGRAM
            #pragma enable_d3d11_debug_symbols
            #pragma vertex VisualizeNearFieldProbeVS
            #pragma fragment VisualizeNearFieldProbeFS
            #pragma multi_compile_instancing

            ENDHLSL
        }
    }
}