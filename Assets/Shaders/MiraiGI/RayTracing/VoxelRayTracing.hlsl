#ifndef VOXEL_RAY_TRACING
#define VOXEL_RAY_TRACING

#include "../../GICommon.hlsl"

struct CascadeInfo
{
    int cascadeIndex;
    float3 center;
    float3 size;
    float3 resolution;
    float3 moveOffset;
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
    int3 cascadeMoveOffsetArray[MAX_CASCADE_COUNT];
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
    cascadeInfo.resolution = clipmapInfo.cascadeResolution;
    cascadeInfo.center = clipmapInfo.cascadeCenterArray[cascadeId];
    cascadeInfo.size = clipmapInfo.cascadeSizeArray[cascadeId];
    cascadeInfo.moveOffset = clipmapInfo.cascadeMoveOffsetArray[cascadeId];
    cascadeInfo.voxelSize = cascadeInfo.size / cascadeInfo.resolution;
    return cascadeInfo;
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
    float3 rayDirSign = sign(rayDir);
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

VoxelRayTracingHitPayload VoxelRaytracingSingleCascade(CascadeInfo cascadeInfo, Texture3D<uint2> bitOccupyClipmap, inout VoxelRaytracingRequest RTRequest)
{
    float3 samplePoint = RTRequest.rayStart - cascadeInfo.center;
    int3 voxelIndex = int3(0, 0, 0);
    int mipLevel = 0;
    bool hitMask = false;
    bool needReadBitOccupy = true;
    int3 clipmapAccessIndex = uint3(0, 0, 0);

    for (int i = 0; i < 128; i++, RTRequest.maxStepNum--)
    {
        voxelIndex = CalcVoxelIndexFromPosition(cascadeInfo, samplePoint);

        if (any(voxelIndex < 0) || any(voxelIndex >= cascadeInfo.resolution) || RTRequest.maxStepNum <= 0 || RTRequest.rayDistance > RTRequest.maxDistance)
        {
            break;
        }
        
        clipmapAccessIndex = ClipmapAddressMapping(voxelIndex, cascadeInfo.resolution, cascadeInfo.moveOffset, cascadeInfo.cascadeIndex);
        uint2 bitOccupy = bitOccupyClipmap.Load(int4(clipmapAccessIndex, 0)).xy;

		// 1. check if sample point hit mip 0,1,2 voxel
        bool isHitMip = IsPointInsideVoxel(cascadeInfo, voxelIndex, bitOccupy, mipLevel);
        
		// 2. if hit mip 0 (most accurate level) we assume ray actually hit
        if (isHitMip && mipLevel == 0)
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
        mipLevel = clamp(mipLevel, 0, 2);
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

#endif