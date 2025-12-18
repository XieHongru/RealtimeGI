#ifndef VOXEL_LIGHTING_PARAMETERS_HLSL
#define VOXEL_LIGHTING_PARAMETERS_HLSL

#include "../../GICommon.hlsl"
#include "../RayTracing/VoxelRayTracing.hlsl"
#include "ProbeCommon.hlsl"

// ---------------------------------------- (^^_) ---------------------------------------- //

int _CascadeIndex;
int _CascadeCount;
int3 _CascadeResolution;
float3 _CascadeCenterArray[MAX_CASCADE_COUNT];
float3 _CascadeSizeArray[MAX_CASCADE_COUNT];
int3 _CascadeScrollingArray[MAX_CASCADE_COUNT];
int3 _VoxelPageCountInXYZ;

SamplerState sampler_linearClamp;

Texture3D<uint2> _VoxelBitOccupyClipmap;
Texture3D<int> _VoxelPageClipmap;
Texture3D<float4> _VoxelPoolBaseColor;
Texture3D<float4> _VoxelPoolNormal;
Texture3D<float4> _VoxelPoolEmissive;
Texture3D<float3> _VoxelPoolRadiance;
Texture3D<float> _DistanceFieldClipmap;

ClipmapInfo ResolveClipmapInfo()
{
    ClipmapInfo clipmapInfo = (ClipmapInfo) 0;
    clipmapInfo.cascadeCount = _CascadeCount;
    clipmapInfo.cascadeResolution = _CascadeResolution;
    clipmapInfo.cascadeCenterArray = _CascadeCenterArray;
    clipmapInfo.cascadeSizeArray = _CascadeSizeArray;
    clipmapInfo.cascadeScrollingArray = _CascadeScrollingArray;

    return clipmapInfo;
}

// -------------------------------------------------------------------------------- //

int _RadianceProbeResolution;
int2 _RadianceProbeCountInAtlasXY;
int _RadianceProbeMinCascadeLevel;
Texture3D<float4> _ProbeOffsetClipmap;
Texture3D<float4> _IrradianceProbeClipmap;
Texture3D<int> _RadianceProbeIdClipmap;
Texture2D<float3> _RadianceProbeAtlas;
Texture2D<float> _RadianceProbeDistanceAtlas;

float3 FetchRadianceFromVoxelScene(VoxelRaytracingRequest RTRequest, VoxelRayTracingHitPayload hit)
{
    int voxelPageId = _VoxelPageClipmap.Load(uint4(hit.clipmapAccessIndex, 0)).r;
    if (voxelPageId == PAGE_ID_INVALID)
    {
        return float3(0, 0, 0);
    }

    int3 indexInPool = PageAddressMapping(voxelPageId, _VoxelPageCountInXYZ, hit.voxelIndex);

    float3 worldNormal = _VoxelPoolNormal.Load(uint4(indexInPool, 0)).rgb * 2 - 1;
    int isBackFace = dot(worldNormal, -RTRequest.rayDir) < 0;

    int3 twoSideIndex = TwoSideAddressMapping(indexInPool, isBackFace);
    float3 hitRadiance = _VoxelPoolRadiance.Load(uint4(twoSideIndex, 0)).rgb;

    return hitRadiance;
}

float3 FetchNormalFromVoxelScene(in VoxelRaytracingRequest RTRequest, in VoxelRayTracingHitPayload hit)
{
    int voxelPageId = _VoxelPageClipmap[hit.clipmapAccessIndex].r;
    if (voxelPageId == PAGE_ID_INVALID)
    {
        return float3(0, 0, 0);
    }

    int3 indexInPool = PageAddressMapping(voxelPageId, _VoxelPageCountInXYZ, hit.voxelIndex);
    float3 worldNormal = _VoxelPoolNormal[indexInPool].rgb * 2 - 1;
    
    return worldNormal;
}

#endif