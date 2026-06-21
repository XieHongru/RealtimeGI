#ifndef ROMA_HLSL
#define ROMA_HLSL

#include "VoxelRayTracing.hlsl"

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
                                                        in SamplerState linearSampler, inout VoxelRaytracingRequest RTRequest, bool anyHit)
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
                if (anyHit)
                {
                    payload.isHit = true;
                    return payload;
                }
                else
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
                    payload.stepCount = numIteration;
                    return payload;
                }
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
                                                        in SamplerState linearSampler, inout VoxelRaytracingRequest RTRequest, bool anyHit)
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
                if (anyHit)
                {
                    payload.isHit = true;
                    return payload;
                }
                else
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
                    payload.stepCount = numIteration;
                    return payload;
                }
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
                                                    in SamplerState linearSampler, inout VoxelRaytracingRequest RTRequest, bool anyHit)
{
    float3 rayDirection = RTRequest.rayDir;
    
    for (int cascadeId = RTRequest.minCascadeIndex; cascadeId <= RTRequest.maxCascadeIndex; cascadeId++)
    {
        CascadeInfo cascadeInfo = ResolveCascadeInfo(clipmapInfo, cascadeId);

        VoxelRayTracingHitPayload hit = BaseOMRaytracingSingleCascade(cascadeInfo, occupancyMap, baseOMViewProjMatArray[cascadeId], baseOMInvViewProjMatArray[cascadeId],
                                                                            linearSampler, RTRequest, anyHit);
        
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
                                            in SamplerState linearSampler, inout VoxelRaytracingRequest RTRequest, bool anyHit)
{
    float3 rayDirection = RTRequest.rayDir;
    float2 u = mapConcentricHemisphereBackToSquare((rayDirection.z < 0 ? -rayDirection : rayDirection));
    int2 squareIndex = clamp(int2(floor(u * float2(ROMA_AXIS_COUNT_X, ROMA_AXIS_COUNT_Y))), 0, 3);
    int ROMAIndex = squareIndex.y * ROMA_AXIS_COUNT_Y + squareIndex.x;
    
    for (int cascadeId = RTRequest.minCascadeIndex; cascadeId <= RTRequest.maxCascadeIndex; cascadeId++)
    {
        CascadeInfo cascadeInfo = ResolveCascadeInfo(clipmapInfo, cascadeId);

        VoxelRayTracingHitPayload hit = ROMARaytracingSingleCascade(cascadeInfo, ROMA, directionParamsArray[cascadeId * ROMA_COUNT + ROMAIndex],
                                                                    ROMAIndex, linearSampler, RTRequest, anyHit);
        
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