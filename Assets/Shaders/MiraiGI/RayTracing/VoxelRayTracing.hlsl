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

struct ClipmapInfo
{
    int cascadeCount;
    float4 cascadeCenterArray[MAX_CASCADE_COUNT];
    float4 cascadeSizeArray[MAX_CASCADE_COUNT];
    float4 cascadeResolutionArray[MAX_CASCADE_COUNT];
    float4 cascadeMoveOffsetArray[MAX_CASCADE_COUNT];
};

struct VoxelRayTracingHitPayload
{
    float3 position;
    float isHit;
    int3 voxelIndex;
    int cascadeIndex;
    float3 debugColor;
};

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

VoxelRayTracingHitPayload VoxelRaytracingSingleCascade(CascadeInfo cascadeInfo, Texture3D<uint2> bitOccupyClipmap, float3 rayStart, float3 rayDir)
{
    float3 samplePoint = rayStart - cascadeInfo.center;
    int3 voxelIndex = int3(0, 0, 0);
    bool hitMask = false;
    uint2 bitOccupy = uint2(0, 0);
    bool needReadBitOccupy = true;

    for (int i = 0; i < 128; i++)
    {
        voxelIndex = CalcVoxelIndexFromPosition(cascadeInfo, samplePoint);

        if (any(voxelIndex < 0) || any(voxelIndex >= cascadeInfo.resolution))
        {
            break;
        }
        
		// avoid duplicate texture sample in same location
        if (needReadBitOccupy)
        {
            int3 readIndex = ClipmapAddressMapping(voxelIndex, cascadeInfo.resolution, cascadeInfo.moveOffset, cascadeInfo.cascadeIndex);
            bitOccupy = bitOccupyClipmap.Load(int4(readIndex, 0)).xy;
            needReadBitOccupy = false;
        }

		// 1. check if sample point hit mip 0,1,2 voxel
        float3 mipHitMask = float3(0, 0, 0);
        float3 mipMoveT = float3(0, 0, 0);

        for (int mipLevel = 0; mipLevel <= 2; mipLevel++)
        {
            mipHitMask[mipLevel] = IsPointInsideVoxel(cascadeInfo, voxelIndex, bitOccupy, mipLevel);
            mipMoveT[mipLevel] = MoveToNextCellDDA(cascadeInfo, samplePoint, rayDir, mipLevel);
        }

		// 2. if hit mip 0 (most accurate level) we assume ray actually hit
        if (mipHitMask[0] > 0)
        {
            hitMask = true;
            break;
        }

		// 3. move as far as we can
        float3 mipMoveDistance = mipMoveT * (1 - mipHitMask);
        float moveDistance = max(mipMoveDistance.x, max(mipMoveDistance.y, mipMoveDistance.z));
        float3 newSamplePoint = samplePoint + rayDir * moveDistance;

		// 4. if step into a different block, cached BitOccupy will be flushed
        needReadBitOccupy = IsTwoPointInDifferentBlock(cascadeInfo, samplePoint, newSamplePoint);
        samplePoint = newSamplePoint;
    }
    
    VoxelRayTracingHitPayload payload;
    payload.position = samplePoint + cascadeInfo.center;
    payload.isHit = hitMask;
    payload.voxelIndex = voxelIndex;
    payload.cascadeIndex = cascadeInfo.cascadeIndex;

    float3 hitVoxelCenterPos = (floor(payload.position / cascadeInfo.voxelSize) + 0.5) * cascadeInfo.voxelSize;
    payload.debugColor = payload.isHit ? float3(length(payload.position - hitVoxelCenterPos) / (50.0 * (1 << payload.cascadeIndex)), 0, 0) : float3(0, 0, 0);

    return payload;
}

VoxelRayTracingHitPayload VoxelRaytracing(ClipmapInfo clipmapInfo, Texture3D<uint2> bitOccupyClipmap, float3 cameraPosition, float3 rayDir)
{
    float3 rayStart = cameraPosition;
    for (int cascadeId = 0; cascadeId < clipmapInfo.cascadeCount; cascadeId++)
    {
        CascadeInfo cascadeInfo;
        cascadeInfo.cascadeIndex = cascadeId;
        cascadeInfo.center = clipmapInfo.cascadeCenterArray[cascadeId];
        cascadeInfo.size = clipmapInfo.cascadeSizeArray[cascadeId];
        cascadeInfo.resolution = clipmapInfo.cascadeResolutionArray[cascadeId];
        cascadeInfo.moveOffset = clipmapInfo.cascadeMoveOffsetArray[cascadeId];
        cascadeInfo.voxelSize = cascadeInfo.size / cascadeInfo.resolution;

        VoxelRayTracingHitPayload hit = VoxelRaytracingSingleCascade(cascadeInfo, bitOccupyClipmap, rayStart, rayDir);

        if (hit.isHit)
        {
            return hit;
        }

		// trace from last clip's ray start
        rayStart = hit.position;
    }

    return (VoxelRayTracingHitPayload) 0;
}

#endif