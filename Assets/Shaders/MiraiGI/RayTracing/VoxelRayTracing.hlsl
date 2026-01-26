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
    float3 voxelPosition;
    float voxelCellSize;
    int cascadeIndex;
    int3 clipmapAccessIndex;
};

struct RayBoxHitInfo
{
    float tMin;
    float tMax;
    bool hit;
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

RayBoxHitInfo CheckRayBoxIntersection(float3 startUV, float3 dirUV)
{
    RayBoxHitInfo hitInfo = (RayBoxHitInfo) 0;
    const float3 bounds[2] = { float3(0.0f, 0.0f, 0.0f), float3(1.0f, 1.0f, 1.0f) };
    const int3 signs = int3(dirUV.x < 0, dirUV.y < 0, dirUV.z < 0);
    const float3 invDir = 1.0f / dirUV;
    float tmin, tmax, tymin, tymax, tzmin, tzmax;

    tmin = (bounds[signs[0]].x - startUV.x) * invDir.x;
    tmax = (bounds[1 - signs[0]].x - startUV.x) * invDir.x;
    tymin = (bounds[signs[1]].y - startUV.y) * invDir.y;
    tymax = (bounds[1 - signs[1]].y - startUV.y) * invDir.y;

    if ((tmin > tymax) || (tymin > tmax))
        return hitInfo;

    if (tymin > tmin)
        tmin = tymin;
    if (tymax < tmax)
        tmax = tymax;

    tzmin = (bounds[signs[2]].z - startUV.z) * invDir.z;
    tzmax = (bounds[1 - signs[2]].z - startUV.z) * invDir.z;

    if ((tmin > tzmax) || (tzmin > tmax))
        return hitInfo;

    if (tzmin > tmin)
        tmin = tzmin;
    if (tzmax < tmax)
        tmax = tzmax;

    if (tmax <= 0.0f)
        return hitInfo;
    
    hitInfo.tMin = tmin;
    hitInfo.tMax = tmax;
    hitInfo.hit = true;
    return hitInfo;
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
float MoveToNextCellDDA(in CascadeInfo cascadeInfo, float3 samplePoint, float3 rayDir, int mipLevel)
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
    deltaPos /= rayDir + float3(1e-4, 1e-4, 1e-4);
    float moveDistance = min(deltaPos.x, min(deltaPos.y, deltaPos.z));

    return moveDistance;
}

bool IsPointInsideVoxel(in CascadeInfo cascadeInfo, int3 voxelIndex, uint2 bitOccupy, int mipLevel)
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
        uint bit2x2x2 = (bitOccupy[bitComp] >> bitOffsetRound) & 0xFF;
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

VoxelRayTracingHitPayload VoxelRaytracingSingleCascade(CascadeInfo cascadeInfo, in Texture3D<uint2> bitOccupyClipmap, inout VoxelRaytracingRequest RTRequest)
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
        uint2 bitOccupy = bitOccupyClipmap[clipmapAccessIndex].xy;

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
    payload.voxelPosition = CalcVoxelCenterPos(voxelIndex, cascadeInfo.resolution, cascadeInfo.center, cascadeInfo.size);
    payload.voxelCellSize = cascadeInfo.voxelSize.x;
    payload.cascadeIndex = cascadeInfo.cascadeIndex;
    payload.clipmapAccessIndex = clipmapAccessIndex;

    return payload;
}

VoxelRayTracingHitPayload VoxelRaytracing(ClipmapInfo clipmapInfo, in Texture3D<uint2> bitOccupyClipmap, inout VoxelRaytracingRequest RTRequest)
{
    for (int cascadeId = RTRequest.minCascadeIndex; cascadeId <= RTRequest.maxCascadeIndex; cascadeId++)
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

		// 2. load distance
        float distance = DecodeDistance(distanceFieldClipmap.SampleLevel(linearSampler, sampleUV, 0).r, cascadeInfo.voxelSize);

		// 3. check if we hit
        if (distance < tolerance)
        {
            hitMask = true;
            break;
        }

        // note: sqrt 3 is for conservative step scale (propagate distance may > real distance)
        float stepDistance = distance / sqrt(3.0f);
        samplePoint += RTRequest.rayDir * stepDistance;
        RTRequest.rayDistance += stepDistance;
    }

	// 4. find a voxel we actually hit by searching voxel neighbors
	// note: hit point may outside voxel, cause hit tolerance distance usually larger than voxel center's distance
    int3 voxelIndex = CalcVoxelIndexFromPosition(cascadeInfo, samplePoint);
    float minRayDistance = 1000;
    int3 offset = int3(0, 0, 0);
    int3 neighborOffsets[7] = { int3(0, 0, 0), int3(-1, 0, 0), int3(1, 0, 0), int3(0, -1, 0), int3(0, 1, 0), int3(0, 0, -1), int3(0, 0, 1)};
    for (int i = 0; i < 7; i++)
    {
        int3 neighborVoxelIndex = clamp(voxelIndex + neighborOffsets[i], int3(0, 0, 0), cascadeInfo.resolution - 1);
        int3 neighborSampleIndex = VoxelClipmapAddressMapping(neighborVoxelIndex, cascadeInfo.resolution, cascadeInfo.scrolling, cascadeInfo.cascadeIndex);
        bool hasVoxel = DecodeDistance(distanceFieldClipmap[neighborSampleIndex].r, cascadeInfo.voxelSize) < tolerance;
        
        float3 voxelPos = CalcVoxelCenterPos(neighborVoxelIndex, cascadeInfo.resolution, cascadeInfo.center, cascadeInfo.size);
        float voxelToCamera = length(voxelPos - RTRequest.rayStart);

        // fint closest voxel as hit voxel
        if (hasVoxel && voxelToCamera < minRayDistance)
        {
            minRayDistance = voxelToCamera;
            offset = neighborOffsets[i];
        }
    }
    voxelIndex = clamp(voxelIndex + offset, int3(0, 0, 0), cascadeInfo.resolution - 1);

	// 5. pack hit result
    int3 blockIndex = voxelIndex / VOXEL_BLOCK_SIZE;
    int3 clipmapAccessIndex = BlockClipmapAddressMapping(blockIndex, cascadeInfo.resolution, cascadeInfo.scrolling, cascadeInfo.cascadeIndex);

    VoxelRayTracingHitPayload payload = (VoxelRayTracingHitPayload) 0;
    payload.position = samplePoint + cascadeInfo.center;
    payload.isHit = hitMask;
    payload.voxelIndex = voxelIndex;
    payload.voxelPosition = CalcVoxelCenterPos(voxelIndex, cascadeInfo.resolution, cascadeInfo.center, cascadeInfo.size);
    payload.voxelCellSize = cascadeInfo.voxelSize.x;
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

// ROMA
// --------------------------------------------------------------------------------------- //

inline uint lowBitDistance(uint bits)
{
    return 31 - log2(bits & (-bits));
}
inline uint foremostBitDistance(uint bits)
{
    return 31 - int(log2(bits));
}

VoxelRayTracingHitPayload BaseOMRaytracingSingleCascade(in CascadeInfo cascadeInfo, in Texture2DArray<uint> baseOM,
                                                        in float4x4 baseOMViewProjMat, in float4x4 baseOMInvViewProjMat,
                                                        in SamplerState linearSampler, inout VoxelRaytracingRequest RTRequest)
{
    VoxelRayTracingHitPayload payload = (VoxelRayTracingHitPayload) 0;
    
    float3 posVFloat = positionWSToUV(RTRequest.rayStart, baseOMViewProjMat);
    float3 dirV = normalize(directionWSToUV(RTRequest.rayDir, baseOMViewProjMat));
    float3 posV = posVFloat;
    RayBoxHitInfo info = CheckRayBoxIntersection(posV, dirV);
    bool startFromInside = false;
    const float epsilon = 1e-6;
    // Fail to enter volume
    if (!info.hit)
        return payload;
    // Check if we're starting from outside of the volume
    // if start from outside, step forward the ray into volume
    if (any(posV <= 0) || any(posV >= 1))
    {
        startFromInside = false;
        const float offset = (info.tMax - info.tMin) / sqrt(2.0f * BASE_OM_SIZE * BASE_OM_SIZE);
        posV += (info.tMin + epsilon) * dirV;
        info.tMax -= (info.tMin + epsilon) + offset;
    }
    else
    {
        const float offset = info.tMax / sqrt(2.0f * BASE_OM_SIZE * BASE_OM_SIZE);
        info.tMax -= epsilon;
    }
    
    // Intersect that OM
    const uint3 gridSize = uint3(BASE_OM_SIZE, BASE_OM_SIZE, BASE_OM_SIZE);
    
    const float2 rayPosGridXY = posV.xy * float2(gridSize.xy);
    const float tRatio = 1.0f / length(dirV.xy * float2(gridSize.x, gridSize.y));
    int2 texelIndex = int2(floor(rayPosGridXY));
    float2 rayUnitStepSize = float2(sqrt(1.0f + dirV.y * dirV.y / (dirV.x * dirV.x)),
                                    sqrt(1.0f + dirV.x * dirV.x / (dirV.y * dirV.y)));

    float2 rayLength1D;
    int2 rayStep;
    if (dirV.x < 0.f)
    {
        rayStep.x = -1;
        rayLength1D.x = (rayPosGridXY.x - float(texelIndex.x)) * rayUnitStepSize.x;
    }
    else
    {
        rayStep.x = 1;
        rayLength1D.x = (float(texelIndex.x) + 1.0f - rayPosGridXY.x) * rayUnitStepSize.x;
    }
    if (dirV.y < 0)
    {
        rayStep.y = -1;
        rayLength1D.y = (rayPosGridXY.y - float(texelIndex.y)) * rayUnitStepSize.y;
    }
    else
    {
        rayStep.y = 1;
        rayLength1D.y = (float(texelIndex.y) + 1.0f - rayPosGridXY.y) * rayUnitStepSize.y;
    }
    
    int2 prevTexelIndex = texelIndex;
    int numIteration = 0;
    float prevT = 0.0f;
    float curT = 0.0f;
    while (curT < info.tMax && numIteration < RTRequest.maxStepNum)
    {
        if (rayLength1D.x < rayLength1D.y)
        {
            texelIndex.x += rayStep.x;
            curT = rayLength1D.x * tRatio;
            rayLength1D.x += rayUnitStepSize.x;
        }
        else
        {
            texelIndex.y += rayStep.y;
            curT = rayLength1D.y * tRatio;
            rayLength1D.y += rayUnitStepSize.y;
        }
        const float3 prevPosR = posV + prevT * dirV;
        const float3 curPosR = posV + curT * dirV;
        const float prevZR = prevPosR.z;
        const float curZR = curPosR.z;
        const float minZ = min(prevZR, curZR);
        const float maxZ = max(prevZR, curZR);
        const uint startZIndex = uint(floor(prevZR * gridSize.z));
        const uint endZIndex = uint(floor(curZR * gridSize.z));
        
        uint occluded = 0;
        // Check hit
        for (int i = 0; i < TOTAL_UINT_IN_BASE_OM; i++)
        {
            int index = (startZIndex >= endZIndex) ? (TOTAL_UINT_IN_BASE_OM - 1) - i : i;
            uint omBit = baseOM[int3(uint2(prevTexelIndex), index + cascadeInfo.cascadeIndex * BASE_OM_SIZE / 32)];
            // Find the intersection of intervals
            int amin = index * 32;
            int amax = (index + 1) * 32 - 1;
            int bMin = min(startZIndex, endZIndex);
            int bMax = max(startZIndex, endZIndex);
            int insecMin = max(amin, bMin);
            int insecMax = min(amax, bMax);
            if (insecMin > insecMax)
                continue;
            occluded = omBit << (insecMin - amin) >> (insecMin - amin + amax - insecMax) << (amax - insecMax);
            if (occluded != 0)
            {
                uint3 hitIndex = uint3(prevTexelIndex.xy, index * 32 + ((startZIndex >= endZIndex) ? lowBitDistance(occluded) : foremostBitDistance(occluded)));
                float3 hitUV = (hitIndex + 0.5f) / float(BASE_OM_SIZE);
                payload.isHit = true;
                payload.position = positionUVToWS(hitUV, baseOMInvViewProjMat);
                int3 hitVoxel = CalcVoxelIndexFromPosition(cascadeInfo, payload.position - cascadeInfo.center);
                int3 blockIndex = hitVoxel / VOXEL_BLOCK_SIZE;
                int3 clipmapAccessIndex = BlockClipmapAddressMapping(blockIndex, cascadeInfo.resolution, cascadeInfo.scrolling, cascadeInfo.cascadeIndex);
                payload.voxelIndex = hitVoxel;
                payload.voxelPosition = CalcVoxelCenterPos(hitVoxel, cascadeInfo.resolution, cascadeInfo.center, cascadeInfo.size);
                payload.voxelCellSize = cascadeInfo.voxelSize.x;
                payload.cascadeIndex = cascadeInfo.cascadeIndex;
                payload.clipmapAccessIndex = clipmapAccessIndex;
                return payload;
            }
        }
        
        prevT = curT;
        prevTexelIndex = texelIndex;
        numIteration++;
    }

    payload.position = positionUVToWS(posV + (curT + epsilon) * dirV, baseOMInvViewProjMat);
    return payload;
}

VoxelRayTracingHitPayload ROMARaytracingSingleCascade(in CascadeInfo cascadeInfo, in Texture2DArray<uint> ROMA, 
                                                        in DirectionParams directionParams, in int OMIndex,
                                                        in SamplerState linearSampler, inout VoxelRaytracingRequest RTRequest)
{
    VoxelRayTracingHitPayload payload = (VoxelRayTracingHitPayload) 0;
    
    float3 posVFloat = positionWSToUV(RTRequest.rayStart, directionParams.viewProjMat);
    float3 dirV = normalize(directionWSToUV(RTRequest.rayDir, directionParams.viewProjMat));
    float3 posV = posVFloat;
    RayBoxHitInfo info = CheckRayBoxIntersection(posV, dirV);
    bool startFromInside = false;
    const float epsilon = 1e-6;
    // Fail to enter volume
    if (!info.hit)
        return payload;
    // Check if we're starting from outside of the volume
    // if start from outside, step forward the ray into volume
    if (any(posV <= 0) || any(posV >= 1))
    {
        startFromInside = false;
        const float offset = (info.tMax - info.tMin) / sqrt(2.0f * BASE_OM_SIZE * BASE_OM_SIZE);
        posV += (info.tMin + epsilon) * dirV;
        info.tMax -= (info.tMin + epsilon) + offset;
    }
    else
    {
        const float offset = info.tMax / sqrt(2.0f * BASE_OM_SIZE * BASE_OM_SIZE);
        info.tMax -= epsilon;
    }
    
    // Intersect that OM
    const uint3 gridSize = uint3(BASE_OM_SIZE, BASE_OM_SIZE, BASE_OM_SIZE);
    
    const float2 rayPosGridXY = posV.xy * float2(gridSize.xy);
    const float tRatio = 1.0f / length(dirV.xy * float2(gridSize.x, gridSize.y));
    int2 texelIndex = int2(floor(rayPosGridXY));
    float2 rayUnitStepSize = float2(sqrt(1.0f + dirV.y * dirV.y / (dirV.x * dirV.x)),
                                    sqrt(1.0f + dirV.x * dirV.x / (dirV.y * dirV.y)));

    float2 rayLength1D;
    int2 rayStep;
    if (dirV.x < 0.f)
    {
        rayStep.x = -1;
        rayLength1D.x = (rayPosGridXY.x - float(texelIndex.x)) * rayUnitStepSize.x;
    }
    else
    {
        rayStep.x = 1;
        rayLength1D.x = (float(texelIndex.x) + 1.0f - rayPosGridXY.x) * rayUnitStepSize.x;
    }
    if (dirV.y < 0)
    {
        rayStep.y = -1;
        rayLength1D.y = (rayPosGridXY.y - float(texelIndex.y)) * rayUnitStepSize.y;
    }
    else
    {
        rayStep.y = 1;
        rayLength1D.y = (float(texelIndex.y) + 1.0f - rayPosGridXY.y) * rayUnitStepSize.y;
    }
    
    int2 prevTexelIndex = texelIndex;
    int numIteration = 0;
    float prevT = 0.0f;
    float curT = 0.0f;
    while (curT < info.tMax && numIteration < RTRequest.maxStepNum)
    {
        if (rayLength1D.x < rayLength1D.y)
        {
            texelIndex.x += rayStep.x;
            curT = rayLength1D.x * tRatio;
            rayLength1D.x += rayUnitStepSize.x;
        }
        else
        {
            texelIndex.y += rayStep.y;
            curT = rayLength1D.y * tRatio;
            rayLength1D.y += rayUnitStepSize.y;
        }
        const float3 prevPosR = posV + prevT * dirV;
        const float3 curPosR = posV + curT * dirV;
        const float prevZR = prevPosR.z;
        const float curZR = curPosR.z;
        const float minZ = min(prevZR, curZR);
        const float maxZ = max(prevZR, curZR);
        const uint startZIndex = uint(floor(prevZR * gridSize.z));
        const uint endZIndex = uint(floor(curZR * gridSize.z));
        
        uint occluded = 0;
        // Check hit
        for (int i = 0; i < TOTAL_UINT_IN_BASE_OM; i++)
        {
            int index = (startZIndex >= endZIndex) ? (TOTAL_UINT_IN_BASE_OM - 1) - i : i;
            uint omBit = ROMA[int3(uint2(prevTexelIndex), index + OMIndex * BASE_OM_SIZE / 32 + cascadeInfo.cascadeIndex * BASE_OM_SIZE / 32 * ROMA_COUNT)];
            // Find the intersection of intervals
            int amin = index * 32;
            int amax = (index + 1) * 32 - 1;
            int bMin = min(startZIndex, endZIndex);
            int bMax = max(startZIndex, endZIndex);
            int insecMin = max(amin, bMin);
            int insecMax = min(amax, bMax);
            if (insecMin > insecMax)
                continue;
            occluded = omBit << (insecMin - amin) >> (insecMin - amin + amax - insecMax) << (amax - insecMax);
            if (occluded != 0)
            {
                uint3 hitIndex = uint3(prevTexelIndex.xy, index * 32 + ((startZIndex >= endZIndex) ? lowBitDistance(occluded) : foremostBitDistance(occluded)));
                float3 hitUV = (hitIndex + 0.5f) / float(BASE_OM_SIZE);
                payload.isHit = true;
                payload.position = positionUVToWS(hitUV, directionParams.invViewProjMat);
                int3 hitVoxel = CalcVoxelIndexFromPosition(cascadeInfo, payload.position - cascadeInfo.center);
                int3 blockIndex = hitVoxel / VOXEL_BLOCK_SIZE;
                int3 clipmapAccessIndex = BlockClipmapAddressMapping(blockIndex, cascadeInfo.resolution, cascadeInfo.scrolling, cascadeInfo.cascadeIndex);
                payload.voxelIndex = hitVoxel;
                payload.voxelPosition = CalcVoxelCenterPos(hitVoxel, cascadeInfo.resolution, cascadeInfo.center, cascadeInfo.size);
                payload.voxelCellSize = cascadeInfo.voxelSize.x;
                payload.cascadeIndex = cascadeInfo.cascadeIndex;
                payload.clipmapAccessIndex = clipmapAccessIndex;
                return payload;
            }
        }
        
        prevT = curT;
        prevTexelIndex = texelIndex;
        numIteration++;
    }

    payload.position = positionUVToWS(posV + (curT + epsilon) * dirV, directionParams.invViewProjMat);
    return payload;
}

VoxelRayTracingHitPayload OccupancyMapRaytracing(in ClipmapInfo clipmapInfo, in Texture2DArray<uint> occupancyMap, 
                                                    in float4x4 baseOMViewProjMatArray[MAX_CASCADE_COUNT], in float4x4 baseOMInvViewProjMatArray[MAX_CASCADE_COUNT],
                                                    in SamplerState linearSampler, inout VoxelRaytracingRequest RTRequest)
{
    float3 rayDirection = RTRequest.rayDir;
    
    for (int cascadeId = RTRequest.minCascadeIndex; cascadeId <= RTRequest.maxCascadeIndex; cascadeId++)
    {
        CascadeInfo cascadeInfo = ResolveCascadeInfo(clipmapInfo, cascadeId);

        VoxelRayTracingHitPayload hit = BaseOMRaytracingSingleCascade(cascadeInfo, occupancyMap, baseOMViewProjMatArray[cascadeId], baseOMInvViewProjMatArray[cascadeId],
                                                                            linearSampler, RTRequest);
        
        if (hit.isHit)
        {
            return hit;
        }
        
		// trace from last clip's ray start
        RTRequest.rayStart = hit.position;
    }

    return (VoxelRayTracingHitPayload) 0;
}

VoxelRayTracingHitPayload ROMARaytracing(in ClipmapInfo clipmapInfo, in Texture2DArray<uint> ROMA, in StructuredBuffer<DirectionParams> directionParamsArray,
                                            in int ROMAIndex, in SamplerState linearSampler, inout VoxelRaytracingRequest RTRequest)
{
    float3 rayDirection = RTRequest.rayDir;
    
    for (int cascadeId = RTRequest.minCascadeIndex; cascadeId <= RTRequest.maxCascadeIndex; cascadeId++)
    {
        CascadeInfo cascadeInfo = ResolveCascadeInfo(clipmapInfo, cascadeId);

        VoxelRayTracingHitPayload hit = ROMARaytracingSingleCascade(cascadeInfo, ROMA, directionParamsArray[cascadeId * ROMA_COUNT + ROMAIndex], 
                                                                    ROMAIndex, linearSampler, RTRequest);
        
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
    const float padding = 4.0f;
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