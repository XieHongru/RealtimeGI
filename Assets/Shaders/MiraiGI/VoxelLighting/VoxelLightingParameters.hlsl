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
int3 _CascadeMoveOffsetArray[MAX_CASCADE_COUNT];
int3 _VoxelPageCountInXYZ;

SamplerState sampler_linearClamp;

Texture3D<uint2> _VoxelBitOccupyClipmap;
Texture3D<int> _VoxelPageClipmap;
Texture3D<float4> _VoxelPoolBaseColor;
Texture3D<float4> _VoxelPoolNormal;
Texture3D<float4> _VoxelPoolEmissive;
Texture3D<float3> _VoxelPoolRadiance;

ClipmapInfo ResolveClipmapInfo()
{
    ClipmapInfo clipmapInfo = (ClipmapInfo) 0;
    clipmapInfo.cascadeCount = _CascadeCount;
    clipmapInfo.cascadeResolution = _CascadeResolution;
    clipmapInfo.cascadeCenterArray = _CascadeCenterArray;
    clipmapInfo.cascadeSizeArray = _CascadeSizeArray;
    clipmapInfo.cascadeMoveOffsetArray = _CascadeMoveOffsetArray;

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

#endif