#ifndef PROBE_COMMON_HLSL
#define PROBE_COMMON_HLSL

#include "../../GICommon.hlsl"
#include "../RayTracing/VoxelRayTracing.hlsl"
#include "../RayTracing/SHCommon.hlsl"

#define DISTANCE_SCALE (1000.0f)

float2 SignNotZero2(float2 v)
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
        direction.xy = (1.f - abs(direction.yx)) * SignNotZero2(direction.xy);
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
        uv = (1.f - abs(uv.yx)) * SignNotZero2(uv.xy);
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
    int3 offsets[7] = { int3(0, 0, 0), int3(-1, 0, 0), int3(1, 0, 0), int3(0, -1, 0), int3(0, 1, 0), int3(0, 0, -1), int3(0, 0, 1) };
    int3 probeVolumeAccessIndex = int3(0, 0, 0);
    for (int i = 0; i < 7; i++)
    {
        int3 neighborProbeIndex = probeIndex3D + offsets[i];
        probeVolumeAccessIndex = (neighborProbeIndex + cascadeInfo.moveOffset) % probeCountInXYZ;

        // 2.1. calculate probe position (consider probe relocation)
        float3 probePositionBase = CalcVoxelCenterPos(neighborProbeIndex, probeCountInXYZ, cascadeInfo.center, cascadeInfo.size);
        float4 probePositionOffsetRaw = probePositionOffsetVolume[probeVolumeAccessIndex];
        float3 probePositionOffset = DecodeProbePositionOffset(probePositionOffsetRaw.xyz, cascadeInfo.voxelSize);
        float3 probePosition = probePositionBase + probePositionOffset;

        // 2.2. see if probe is suitable, only accept front-face probe
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

int2 RedirectBorderPixel(int2 pixelIndexInProbe, int radianceProbeResolution)
{
    int2 result = pixelIndexInProbe;
    int pixelIndexMax = radianceProbeResolution - 1;

    // row
    if (pixelIndexInProbe.y == 0 || pixelIndexInProbe.y == pixelIndexMax)
    {
        result.x = pixelIndexMax - pixelIndexInProbe.x;
        result.y += (pixelIndexInProbe.y == 0) ? 1 : -1; // top or bottom row
    }
    // col
    if (pixelIndexInProbe.x == 0 || pixelIndexInProbe.x == pixelIndexMax)
    {
        result.x += (pixelIndexInProbe.x == 0) ? 1 : -1; // left or right col
        result.y = pixelIndexMax - pixelIndexInProbe.y;
    }

    // left top
    if (pixelIndexInProbe.x == 0 && pixelIndexInProbe.y == 0)
    {
        result = int2(pixelIndexMax - 1, pixelIndexMax - 1);
    }
    // right top
    if (pixelIndexInProbe.x == pixelIndexMax && pixelIndexInProbe.y == 0)
    {
        result = int2(1, pixelIndexMax - 1);
    }
    // left bottom
    if (pixelIndexInProbe.x == 0 && pixelIndexInProbe.y == pixelIndexMax)
    {
        result = int2(pixelIndexMax - 1, 1);
    }
    // right bottom
    if (pixelIndexInProbe.x == pixelIndexMax && pixelIndexInProbe.y == pixelIndexMax)
    {
        result = int2(1, 1);
    }

    return result;
}

float3 SphericalFibonacciSample(float i, float n)
{
    float theta = 2 * PI * i / ((1 + sqrt(5)) * 0.5);
    float phi = acos(1 - 2 * (i) / n);
    return float3(
		sin(phi) * cos(theta),
		sin(phi) * sin(theta),
		cos(phi)
	);
}

float GetRadianceProbeSize(in ClipmapInfo clipmapInfo)
{
    CascadeInfo cascadeInfo = ResolveCascadeInfo(clipmapInfo, clipmapInfo.cascadeCount - 1);
    int3 probeCountInXYZ = cascadeInfo.resolution / VOXEL_BLOCK_SIZE;
    int3 probeSize = cascadeInfo.size / float3(probeCountInXYZ);
    return max(max(probeSize.x, probeSize.y), probeSize.z);
}

float EncodeHitDistance(float rawDistance)
{
    return rawDistance / DISTANCE_SCALE;
}

float DecodeHitDistance(float encodedDistance)
{
    return encodedDistance * DISTANCE_SCALE;
}

int3 GetTrilinearSampleOffset(float3 pixelIndex)
{
    return int3(
        pixelIndex.x > 0.5 ? 0 : -1,
        pixelIndex.y > 0.5 ? 0 : -1,
        pixelIndex.z > 0.5 ? 0 : -1
    );
}

float3 TrilinearInterpolationFloat3(in float3 value[8], float3 rate)
{
    float3 a = lerp(value[0], value[4], rate.x); // 000, 100
    float3 b = lerp(value[2], value[6], rate.x); // 010, 110
    float3 c = lerp(value[1], value[5], rate.x); // 001, 101
    float3 d = lerp(value[3], value[7], rate.x); // 011, 111
    float3 e = lerp(a, b, rate.y);
    float3 f = lerp(c, d, rate.y);
    float3 g = lerp(e, f, rate.z);
    return g;
}

float3 ProbeEvaluateRadiance(
    in Texture2D<float3> radianceProbeAtlas,
    in Texture2D<float> radianceProbeDistanceAtlas,
    in Texture3D<int> radianceProbeIdVolume,
    in Texture3D<float4> probePositionOffsetVolume,
    in SamplerState linearSampler,
    in ClipmapInfo clipmapInfo,
    int2 radianceProbeCountInAtlasXY, int radianceProbeResolution,
    float3 worldPosition, float3 direction)
{
    // radiance probe only in highest clipmap
    CascadeInfo cascadeInfo = ResolveCascadeInfo(clipmapInfo, clipmapInfo.cascadeCount - 1);
    
    // 1. calculate sample point inside which radiance probe
    int3 probeCountInXYZ = cascadeInfo.resolution / VOXEL_BLOCK_SIZE;
    float radianceProbeSize = GetRadianceProbeSize(clipmapInfo);
    float3 translatedWorldPosition = worldPosition - cascadeInfo.center;
    int3 probeIndex3D = floor(translatedWorldPosition / radianceProbeSize) + probeCountInXYZ * 0.5;
    float3 trilinearWeight = frac(translatedWorldPosition / radianceProbeSize);
    int3 trilinearOffset = GetTrilinearSampleOffset(trilinearWeight);
    
    // 2. find a good radiance probe to sample
    int3 offsets[8] = { int3(0, 0, 0), int3(0, 0, 1), int3(0, 1, 0), int3(0, 1, 1), int3(1, 0, 0), int3(1, 0, 1), int3(1, 1, 0), int3(1, 1, 1), };
    int3 probeVolumeAccessIndex = int3(0, 0, 0);
    float3 probePosition = float3(0, 0, 0);
    for (int i = 0; i < 8; i++)
    {
        int3 neighborProbeIndex = probeIndex3D + offsets[i] + trilinearOffset;
        probeVolumeAccessIndex = (neighborProbeIndex + cascadeInfo.moveOffset) % probeCountInXYZ;
        
        if (any(neighborProbeIndex >= probeCountInXYZ) || any(probeCountInXYZ < 0))
        {
            continue;
        }

        // 2.1. calculate probe position (consider probe relocation)
        float3 probePositionBase = CalcVoxelCenterPos(neighborProbeIndex, probeCountInXYZ, cascadeInfo.center, cascadeInfo.size);
        float4 probePositionOffsetRaw = probePositionOffsetVolume[probeVolumeAccessIndex];
        float3 probePositionOffset = DecodeProbePositionOffset(probePositionOffsetRaw.xyz, cascadeInfo.voxelSize);
        probePosition = probePositionBase + probePositionOffset;

        // 2.2. see if probe is suitable, only accept front-face probe
        float3 samplePointToProbe = probePosition - worldPosition;
        bool isProbeBehindSamplePoint = dot(samplePointToProbe, direction) < 0;
        if (!isProbeBehindSamplePoint)
        {
            break;
        }
    }

    // 3. calculate parallex
    float4 radianceProbeSphere = float4(probePosition, radianceProbeSize);
    float2 sphereIntersections = RayIntersectSphere(worldPosition, direction, radianceProbeSphere);
    float3 intersectionPosition = worldPosition + direction * sphereIntersections.y;
    float3 parallexDirection = normalize(intersectionPosition - probePosition);

    // 4. sample radiance
    int probeIdInAtlas = radianceProbeIdVolume[probeVolumeAccessIndex];
    float2 uvInAtlas = RadianceProbeAddressMapping(direction, probeIdInAtlas, radianceProbeCountInAtlasXY, radianceProbeResolution);
    float3 radiance = radianceProbeAtlas.SampleLevel(linearSampler, uvInAtlas, 0).rgb;

    return radiance;
}

#endif