#ifndef PROBE_COMMON_HLSL
#define PROBE_COMMON_HLSL

#include "../../GICommon.hlsl"
#include "../RayTracing/VoxelRayTracing.hlsl"
#include "../RayTracing/SHCommon.hlsl"

float2 SignNotZero(float2 v)
{
    return float2(
		(v.x >= 0.f) ? 1.f : -1.f,
		(v.y >= 0.f) ? 1.f : -1.f
	);
}

// https://github.com/NVIDIAGameWorks/RTXGI-DDGI
// give a [0 ~ 1] uv, return ray direction in octahedral map
float3 OctahedralDirection(float2 coords)
{
    coords = coords * 2 - 1;
    float3 direction = float3(coords.x, coords.y, 1.f - abs(coords.x) - abs(coords.y));
    if (direction.z < 0.f)
    {
        direction.xy = (1.f - abs(direction.yx)) * SignNotZero(direction.xy);
    }
    return normalize(direction);
}

// https://github.com/NVIDIAGameWorks/RTXGI-DDGI
// give a ray direction in octahedral map, return [0 ~ 1] uv
float2 OctahedralCoordinates(float3 direction)
{
    float l1norm = abs(direction.x) + abs(direction.y) + abs(direction.z);
    float2 uv = direction.xy * (1.f / l1norm);
    if (direction.z < 0.f)
    {
        uv = (1.f - abs(uv.yx)) * SignNotZero(uv.xy);
    }
    return uv * 0.5 + 0.5;
}

float3 DecodeProbePositionOffset(float3 positionOffsetRaw, float3 voxelSize)
{
    float3 positionOffsetInVoxel = positionOffsetRaw * VOXEL_BLOCK_SIZE; // [0 ~ 3]
    float3 positionOffsetNorm = positionOffsetInVoxel - (VOXEL_BLOCK_SIZE / 2) + 0.5; // [-1.5 ~ 1.5]
    float3 positionOffset = positionOffsetNorm * voxelSize;
    return positionOffset;
}

float3 ProbeEvaluateIrradiance(
	in Texture3D<float4> probeIrradianceCache,
	in Texture3D<float4> probePositionOffsetVolume,
	in CascadeInfo cascadeInfo,
	float3 worldPosition, float3 worldNormal)
{
    // 1. calculate sample point inside which probe
    float3 translatedWorldPosition = worldPosition - cascadeInfo.center;
    int3 voxelIndex = floor(translatedWorldPosition / cascadeInfo.voxelSize) + cascadeInfo.resolution * 0.5;
    int3 probeIndex3D = voxelIndex / VOXEL_BLOCK_SIZE;
    int3 probeCountInXYZ = cascadeInfo.resolution / VOXEL_BLOCK_SIZE;

    // 2. find a good probe to sample
    int3 offsets[7] = { int3(-1, 0, 0), int3(1, 0, 0), int3(0, -1, 0), int3(0, 1, 0), int3(0, 0, -1), int3(0, 0, 1), int3(0, 0, 0) };
    int3 probeVolumeAccessIndex = int3(0, 0, 0);
    for (int i = 0; i < 7; i++)
    {
        int3 neighborProbeIndex = probeIndex3D + offsets[i];
        probeVolumeAccessIndex = (neighborProbeIndex + cascadeInfo.moveOffset) % probeCountInXYZ;

        // 2.1. calculate probe position (consider probe relocation)
        float3 probePositionBase = CalcVoxelCenterPos(neighborProbeIndex, probeCountInXYZ, cascadeInfo.center, cascadeInfo.size);
        float4 probePositionOffsetRaw = probePositionOffsetVolume[probeVolumeAccessIndex];
        float3 probePositionOffset = DecodeProbePositionOffset(probePositionOffsetRaw.xyz, cascadeInfo.size / float3(cascadeInfo.resolution));
        float3 probePosition = probePositionBase + probePositionOffset;

        // 2.2. 
        float3 samplePointToProbe = probePosition - worldPosition;
        bool isProbeBehindSamplePoint = dot(samplePointToProbe, worldNormal) < 0;
        if (!isProbeBehindSamplePoint)
        {
            break;
        }
    }
    
    // 4. sample probe irradiance 
    int3 readIndexBase = probeVolumeAccessIndex * int3(1, 1, 7);

    ThreeBandSHVectorRGB irradianceSH;
    irradianceSH.R.V0 = probeIrradianceCache.Load(int4(readIndexBase + float3(0, 0, 0), 0));
    irradianceSH.R.V1 = probeIrradianceCache.Load(int4(readIndexBase + float3(0, 0, 1), 0));
    irradianceSH.G.V0 = probeIrradianceCache.Load(int4(readIndexBase + float3(0, 0, 2), 0));
    irradianceSH.G.V1 = probeIrradianceCache.Load(int4(readIndexBase + float3(0, 0, 3), 0));
    irradianceSH.B.V0 = probeIrradianceCache.Load(int4(readIndexBase + float3(0, 0, 4), 0));
    irradianceSH.B.V1 = probeIrradianceCache.Load(int4(readIndexBase + float3(0, 0, 5), 0));
    float4 temp = probeIrradianceCache.Load(int4(readIndexBase + float3(0, 0, 6), 0));
    irradianceSH.R.V2 = temp.x;
    irradianceSH.G.V2 = temp.y;
    irradianceSH.B.V2 = temp.z;

    ThreeBandSHVector diffuseTransferSH = CalcDiffuseTransferSH3(worldNormal, 1);
    float3 irradiance = max(float3(0, 0, 0), DotSH3(irradianceSH, diffuseTransferSH)) / PI;

    return irradiance;
}

float2 RadianceProbeAddressMapping(float3 rayDirection, int probeIdInAtlas, int2 radianceProbeCountInAtlasXY, int radianceProbeResolution)
{
    int2 probeIdInAtlas2D = Index1DTo2D(probeIdInAtlas, radianceProbeCountInAtlasXY);
    float2 pixelBaseInAtlas = probeIdInAtlas2D * radianceProbeResolution;

    float2 uvInProbe = OctahedralCoordinates(rayDirection);
    float2 pixelInProbe = uvInProbe * (radianceProbeResolution - 2);

    float2 pixelInAtlas = pixelBaseInAtlas + pixelInProbe + 1;
    float2 uvInAtlas = pixelInAtlas / float2(radianceProbeCountInAtlasXY * radianceProbeResolution);
	
    return uvInAtlas;
}
#endif