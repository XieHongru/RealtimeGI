#ifndef VOXEL_RAY_TRACING
#define VOXEL_RAY_TRACING

#include "../../GICommon.hlsl"

struct CascadeInfo
{
    int cascadeIndex;
    int cascadeCount;
    float3 center;
    float3 size;
    float3 resolution;
    float3 scrolling;
    float3 voxelSize;
};

struct VoxelRaytracingRequest
{
    float3 rayStart;
    float3 rayDir;
    int minCascadeIndex;
    int maxCascadeIndex;
    int maxStepNum;
    float rayDistance;
    float maxDistance;
};

struct ClipmapInfo
{
    int cascadeCount;
    int3 cascadeResolution;
    float3 cascadeCenterArray[MAX_CASCADE_COUNT];
    float3 cascadeSizeArray[MAX_CASCADE_COUNT];
    int3 cascadeScrollingArray[MAX_CASCADE_COUNT];
};

struct VoxelRayTracingHitPayload
{
    float3 position;
    int isHit;
    int3 voxelIndex;
    int cascadeIndex;
    int3 clipmapAccessIndex;
};

CascadeInfo ResolveCascadeInfo(ClipmapInfo clipmapInfo, int cascadeId)
{
    CascadeInfo cascadeInfo;
    cascadeInfo.cascadeIndex = cascadeId;
    cascadeInfo.cascadeCount = clipmapInfo.cascadeCount;
    cascadeInfo.resolution = clipmapInfo.cascadeResolution;
    cascadeInfo.center = clipmapInfo.cascadeCenterArray[cascadeId];
    cascadeInfo.size = clipmapInfo.cascadeSizeArray[cascadeId];
    cascadeInfo.scrolling = clipmapInfo.cascadeScrollingArray[cascadeId];
    cascadeInfo.voxelSize = cascadeInfo.size / cascadeInfo.resolution;
    return cascadeInfo;
}

float3 SignNotZero3(float3 v)
{
    return float3(
		(v.x >= 0.f) ? 1.f : -1.f,
		(v.y >= 0.f) ? 1.f : -1.f,
		(v.z >= 0.f) ? 1.f : -1.f
	);
}

// copy from UE
float2 LineBoxIntersect(float3 RayOrigin, float3 RayEnd, float3 BoxMin, float3 BoxMax)
{
    float3 InvRayDir = 1.0f / (RayEnd - RayOrigin);
	
	//find the ray intersection with each of the 3 planes defined by the minimum extrema.
    float3 FirstPlaneIntersections = (BoxMin - RayOrigin) * InvRayDir;
	//find the ray intersection with each of the 3 planes defined by the maximum extrema.
    float3 SecondPlaneIntersections = (BoxMax - RayOrigin) * InvRayDir;
	//get the closest of these intersections along the ray
    float3 ClosestPlaneIntersections = min(FirstPlaneIntersections, SecondPlaneIntersections);
	//get the furthest of these intersections along the ray
    float3 FurthestPlaneIntersections = max(FirstPlaneIntersections, SecondPlaneIntersections);

    float2 BoxIntersections;
	//find the furthest near intersection
    BoxIntersections.x = max(ClosestPlaneIntersections.x, max(ClosestPlaneIntersections.y, ClosestPlaneIntersections.z));
	//find the closest far intersection
    BoxIntersections.y = min(FurthestPlaneIntersections.x, min(FurthestPlaneIntersections.y, FurthestPlaneIntersections.z));
	//clamp the intersections to be between RayOrigin and RayEnd on the ray
    return saturate(BoxIntersections);
}

// https://sugulee.wordpress.com/2021/01/19/screen-space-reflections-implementation-and-optimization-part-2-hi-z-tracing-method/
float MoveToNextCellDDA(CascadeInfo cascadeInfo, float3 samplePoint, float3 rayDir, int mipLevel)
{
    float3 cellSize = cascadeInfo.voxelSize * pow(2, mipLevel);

	// 1. calc move step
    float3 rayDirSign = SignNotZero3(rayDir);
    float3 moveStep = saturate(rayDirSign);
    float3 moveOffset = rayDirSign * cellSize * 1e-4;

	// 2. calc next cell's boundary
    int3 currentCell = floor(samplePoint / cellSize);
    int3 nextCell = currentCell + moveStep;
    float3 nextCellBoundary = nextCell * cellSize + moveOffset;

	// 3. calc min distance to move SamplePoint, make it move at least 1 cell align X, Y or Z 
    float3 deltaPos = nextCellBoundary - samplePoint;
    deltaPos /= rayDir;
    float moveDistance = min(deltaPos.x, min(deltaPos.y, deltaPos.z));

    return moveDistance;
}

bool IsPointInsideVoxel(CascadeInfo cascadeInfo, int3 voxelIndex, uint2 bitOccupy, int mipLevel)
{
    int3 indexInsideBlock = voxelIndex % VOXEL_BLOCK_SIZE;

    if (mipLevel == 2)
    {
        return any(bitOccupy != 0);
    }

    if (mipLevel == 1)
    {
        int bitOffset = Index3DTo1D_2x2x2(indexInsideBlock / 2) * 8;
        int bitComp = bitOffset / 32; // select .x or .y component in bitOccupy
        int bitOffsetRound = bitOffset % 32;
        uint bit2x2x2 = ((bitComp == 0 ? bitOccupy.x : bitOccupy.y) >> bitOffsetRound) & 0xFF;
        return bit2x2x2 != 0;
    }

    if (mipLevel == 0)
    {
        int bitIndex = Index3DTo1D_4x4x4(indexInsideBlock);
        return GetUint64SingleBit(bitOccupy, bitIndex);
    }

    return false;
}

int3 CalcVoxelIndexFromPosition(in CascadeInfo cascadeInfo, float3 position)
{
    int3 result = floor(position / cascadeInfo.voxelSize) + cascadeInfo.resolution * 0.5;
    return result;
}

bool IsTwoPointInDifferentBlock(in CascadeInfo cascadeInfo, float3 pointA, float3 pointB)
{
    int3 voxelIndexA = CalcVoxelIndexFromPosition(cascadeInfo, pointA);
    int3 blockIndexA = voxelIndexA / VOXEL_BLOCK_SIZE;

    int3 voxelIndexB = CalcVoxelIndexFromPosition(cascadeInfo, pointB);
    int3 blockIndexB = voxelIndexB / VOXEL_BLOCK_SIZE;

    return any(blockIndexA != blockIndexB);
}

#define MIN_MIP_LEVEL (0)
#define MAX_MIP_LEVEL (2)

VoxelRayTracingHitPayload VoxelRaytracingSingleCascade(CascadeInfo cascadeInfo, Texture3D<uint2> bitOccupyClipmap, inout VoxelRaytracingRequest RTRequest)
{
    float3 samplePoint = RTRequest.rayStart - cascadeInfo.center;
    int3 voxelIndex = int3(0, 0, 0);
    int mipLevel = MIN_MIP_LEVEL;
    bool hitMask = false;
    bool needReadBitOccupy = true;
    int3 clipmapAccessIndex = int3(0, 0, 0);

    for (int i = 0; i < 128; i++, RTRequest.maxStepNum--)
    {
        voxelIndex = CalcVoxelIndexFromPosition(cascadeInfo, samplePoint);

        if (any(voxelIndex < 0) || any(voxelIndex >= cascadeInfo.resolution) || RTRequest.maxStepNum <= 0 || RTRequest.rayDistance > RTRequest.maxDistance)
        {
            break;
        }
        
        int3 blockIndex = voxelIndex / VOXEL_BLOCK_SIZE;
        clipmapAccessIndex = BlockClipmapAddressMapping(blockIndex, cascadeInfo.resolution, cascadeInfo.scrolling, cascadeInfo.cascadeIndex);
        uint2 bitOccupy = bitOccupyClipmap.Load(int4(clipmapAccessIndex, 0)).xy;

		// 1. check if sample point hit mip 0,1,2 voxel
        bool isHitMip = IsPointInsideVoxel(cascadeInfo, voxelIndex, bitOccupy, mipLevel);
        
		// 2. if hit mip 0 (most accurate level) we assume ray actually hit
        if (isHitMip && mipLevel == MIN_MIP_LEVEL)
        {
            hitMask = true;
            break;
        }

		// 3. if not hit in cur mip, we can skip entire cell by DDA march 1 step
        float moveDistance = isHitMip ? 0 : MoveToNextCellDDA(cascadeInfo, samplePoint, RTRequest.rayDir, mipLevel);
        samplePoint += RTRequest.rayDir * moveDistance;
        RTRequest.rayDistance += moveDistance;

		// 4. if hit in cur mip, we stay in place, just go down to more accurate mip level
        mipLevel += isHitMip ? -1 : 1;
        mipLevel = clamp(mipLevel, MIN_MIP_LEVEL, MAX_MIP_LEVEL);
    }
    
    VoxelRayTracingHitPayload payload;
    payload.position = samplePoint + cascadeInfo.center;
    payload.isHit = hitMask;
    payload.voxelIndex = voxelIndex;
    payload.cascadeIndex = cascadeInfo.cascadeIndex;
    payload.clipmapAccessIndex = clipmapAccessIndex;

    return payload;
}

VoxelRayTracingHitPayload VoxelRaytracing(ClipmapInfo clipmapInfo, Texture3D<uint2> bitOccupyClipmap, inout VoxelRaytracingRequest RTRequest)
{
    for (int cascadeId = RTRequest.minCascadeIndex; cascadeId < RTRequest.maxCascadeIndex; cascadeId++)
    {
        CascadeInfo cascadeInfo = ResolveCascadeInfo(clipmapInfo, cascadeId);

        VoxelRayTracingHitPayload hit = VoxelRaytracingSingleCascade(cascadeInfo, bitOccupyClipmap, RTRequest);

        if (hit.isHit)
        {
            return hit;
        }

		// trace from last clip's ray start
        RTRequest.rayStart = hit.position;
    }

    return (VoxelRayTracingHitPayload) 0;
}

VoxelRayTracingHitPayload DistanceFieldRaytracingSingleCascade(in CascadeInfo cascadeInfo, in Texture3D<float> distanceFieldClipmap, 
                                                                in SamplerState linearSampler, inout VoxelRaytracingRequest RTRequest)
{
    float3 cascadeMin = (cascadeInfo.size * -0.5) + cascadeInfo.voxelSize; // padding 1 texel
    float3 cascadeMax = (cascadeInfo.size * 0.5) - cascadeInfo.voxelSize;
    float3 samplePoint = RTRequest.rayStart - cascadeInfo.center;
    bool hitMask = false;
    float tolerance = cascadeInfo.voxelSize * 0.5001; // distance is 0 in voxel center, so half the radius

    for (int i = 0; i < 128; i++, RTRequest.maxStepNum--)
    {
        if (any(samplePoint <= cascadeMin) || any(samplePoint >= cascadeMax) || RTRequest.maxStepNum <= 0 || RTRequest.rayDistance > RTRequest.maxDistance)
        {
            break;
        }

		// 1. map translated position to 3d texture uv
        float3 samplePosition01 = (samplePoint / cascadeInfo.size) + 0.5;
        float3 sampleUV = frac(samplePosition01 + cascadeInfo.scrolling / float3(cascadeInfo.resolution)); // frac is for x mod 1
        sampleUV.z /= float(cascadeInfo.cascadeCount);
        sampleUV.z += cascadeInfo.cascadeIndex / float(cascadeInfo.cascadeCount);

		// 2. load distance, sqrt is for conservative step scale (propagate distance may > real distance)
        float distance = DecodeDistance(distanceFieldClipmap.SampleLevel(linearSampler, sampleUV, 0).r, cascadeInfo.voxelSize);
        distance /= sqrt(3.0f);

		// 3. check if we hit
        if (distance < tolerance)
        {
            hitMask = true;
            break;
        }

        samplePoint += RTRequest.rayDir * distance;
    }

	// 4. find a voxel we actually hit by searching voxel neighbors
	// note: hit point may outside voxel, cause hit tolerance distance usually larger than voxel center's distance
    int3 voxelIndex = CalcVoxelIndexFromPosition(cascadeInfo, samplePoint);
    int3 neighborOffsets[6] = { int3(-1, 0, 0), int3(1, 0, 0), int3(0, -1, 0), int3(0, 1, 0), int3(0, 0, -1), int3(0, 0, 1) };
    for (int i = 0; i < 6; i++)
    {
        int3 neighborVoxelIndex = clamp(voxelIndex + neighborOffsets[i], int3(0, 0, 0), cascadeInfo.resolution - 1);
        int3 voxelSampleIndex = VoxelClipmapAddressMapping(neighborVoxelIndex, cascadeInfo.resolution, cascadeInfo.scrolling, cascadeInfo.cascadeIndex);
        float distance = DecodeDistance(distanceFieldClipmap[voxelSampleIndex].r, cascadeInfo.voxelSize);
        if (distance < tolerance)
        {
            voxelIndex += neighborOffsets[i];
            break;
        }
    }

	// 5. pack hit result
    int3 blockIndex = voxelIndex / VOXEL_BLOCK_SIZE;
    int3 clipmapAccessIndex = BlockClipmapAddressMapping(blockIndex, cascadeInfo.resolution, cascadeInfo.scrolling, cascadeInfo.cascadeIndex);

    VoxelRayTracingHitPayload payload = (VoxelRayTracingHitPayload) 0;
    payload.position = samplePoint + cascadeInfo.center;
    payload.isHit = hitMask;
    payload.voxelIndex = voxelIndex;
    payload.cascadeIndex = cascadeInfo.cascadeIndex;
    payload.clipmapAccessIndex = clipmapAccessIndex;

    return payload;
}

VoxelRayTracingHitPayload DistanceFieldRaytracing(in ClipmapInfo clipmapInfo, in Texture3D<float> distanceFieldClipmap,
                                                    in SamplerState linearSampler, inout VoxelRaytracingRequest RTRequest)
{
    for (int cascadeId = RTRequest.minCascadeIndex; cascadeId <= RTRequest.maxCascadeIndex; cascadeId++)
    {
        CascadeInfo cascadeInfo = ResolveCascadeInfo(clipmapInfo, cascadeId);

        VoxelRayTracingHitPayload hit = DistanceFieldRaytracingSingleCascade(cascadeInfo, distanceFieldClipmap, linearSampler, RTRequest);

        if (hit.isHit)
        {
            return hit;
        }

		// trace from last clip's ray start
        RTRequest.rayStart = hit.position;
    }

    return (VoxelRayTracingHitPayload) 0;
}

float3 GetVoxelCellSize(in ClipmapInfo clipmapInfo, float3 worldPosition)
{
    const float padding = 3.0f;
    int i = 0;
    float3 voxelCellSize = float3(0, 0, 0);
    for (int i = 0; i < clipmapInfo.cascadeCount; i++)
    {
        voxelCellSize = clipmapInfo.cascadeSizeArray[i] / float3(clipmapInfo.cascadeResolution);
        float3 cascadeMin = clipmapInfo.cascadeCenterArray[i] - clipmapInfo.cascadeSizeArray[i] * 0.5 + voxelCellSize * padding;
        float3 cascadeMax = clipmapInfo.cascadeCenterArray[i] + clipmapInfo.cascadeSizeArray[i] * 0.5 - voxelCellSize * padding;
        if (all(cascadeMin < worldPosition) && all(worldPosition < cascadeMax))
        {
            break;
        }
    }

    return voxelCellSize;
}

#endif