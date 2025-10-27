#ifndef GI_COMMON
#define GI_COMMON

#define MAX_CARD_PER_MESH 12
#define VOXEL_BLOCK_SIZE 4
#define MAX_CASCADE_COUNT 4

struct ObjectInfo
{
    int objectId;
    int cardCount;
    int resolution;
    int meshId;

    float4 localBoundsMin;
    float4 localBoundsMax;
    float4 worldBoundsMin;
    float4 worldBoundsMax;
    float4x4 localToWorldMatrix;
    float4x4 worldToLocalMatrix;
};

struct MeshInfo
{
    int meshId;
    int vertexOffset;
    int vertexCount;
};

StructuredBuffer<ObjectInfo>    _ObjectsInfo;
StructuredBuffer<MeshInfo>      _MeshInfo;
StructuredBuffer<float3>        _VertexBuffer;
StructuredBuffer<int>           _IndexBuffer;

// Morton
uint Index3DTo1D_2x2x2(uint3 Index3D)
{
    return (Index3D.z << 2) + (Index3D.y << 1) + (Index3D.x << 0);
}

uint3 Index1DTo3D_2x2x2(uint Index1D)
{
    return uint3(
		(Index1D >> 0) & 0x01,
		(Index1D >> 1) & 0x01,
		(Index1D >> 2) & 0x01
	);
}

uint Index3DTo1D_4x4x4(uint3 Index3D)
{
    int3 BlockId = Index3D / 2;
    int3 InsideBolckId = Index3D % 2;

    return Index3DTo1D_2x2x2(BlockId) * 8 + Index3DTo1D_2x2x2(InsideBolckId);
}

uint3 Index1DTo3D_4x4x4(uint Index1D)
{
    int BlockId_1D = Index1D / 8;
    int InsideBolckId_1D = Index1D % 8;

    int3 BlockId = Index1DTo3D_2x2x2(BlockId_1D);
    int3 InsideBlockId = Index1DTo3D_2x2x2(InsideBolckId_1D);

    return BlockId * 2 + InsideBlockId;
}

// Linear
uint Index3DTo1D(uint3 index3D, uint3 size3D)
{
    int res = 0;
    res += index3D.x;
    res += index3D.y * size3D.x;
    res += index3D.z * (size3D.x * size3D.y);
    return res;
}

int3 Index1DTo3D(int index1D, int3 size3D)
{
    int3 res;

    res.z = index1D / (size3D.x * size3D.y);
    index1D -= res.z * (size3D.x * size3D.y);

    res.y = index1D / size3D.x;
    index1D -= res.y * size3D.x;

    res.x = index1D;

    return res;
}

float Squaref(float x)
{
    return x * x;
}

// copy from UE
bool SphereIntersectAABB(float4 sphere, float3 aabbMin, float3 aabbMax)
{
    float3 sphereCenter = sphere.xyz;
    float radiusSquared = sphere.w * sphere.w;

    float distSquared = 0.f;
    if (sphereCenter.x < aabbMin.x)
    {
        distSquared += Squaref(sphereCenter.x - aabbMin.x);
    }
    else if (sphereCenter.x > aabbMax.x)
    {
        distSquared += Squaref(sphereCenter.x - aabbMax.x);
    }
    if (sphereCenter.y < aabbMin.y)
    {
        distSquared += Squaref(sphereCenter.y - aabbMin.y);
    }
    else if (sphereCenter.y > aabbMax.y)
    {
        distSquared += Squaref(sphereCenter.y - aabbMax.y);
    }
    if (sphereCenter.z < aabbMin.z)
    {
        distSquared += Squaref(sphereCenter.z - aabbMin.z);
    }
    else if (sphereCenter.z > aabbMax.z)
    {
        distSquared += Squaref(sphereCenter.z - aabbMax.z);
    }
    return distSquared <= radiusSquared;
}

bool ObjectIntersectAABB(in ObjectInfo objectInfo, float3 aabbCenter, float3 aabbSize)
{
    float3 worldBoundsMin = objectInfo.worldBoundsMin;
    float3 worldBoundsMax = objectInfo.worldBoundsMax;

    float3 sphereCenter = (worldBoundsMax + worldBoundsMin) * 0.5;
    float sphereRadius = length(worldBoundsMax - worldBoundsMin) * 0.5;
    float4 boundSphere = float4(sphereCenter, sphereRadius);

    float3 aabbMin = aabbCenter - aabbSize * 0.5;
    float3 aabbMax = aabbCenter + aabbSize * 0.5;

    bool res = SphereIntersectAABB(boundSphere, aabbMin, aabbMax);
    return res;
}

float3 CalcVoxelCenterPos(float3 index, float3 voxelResolution, float3 boundsCenter, float3 boundsSize)
{
    float3 cellSize = boundsSize / voxelResolution;
    float3 indexNorm = index - voxelResolution * 0.5; // [-2, -1, 0, 1] if VolumeResolution is 4, Index is in [0, 1, 2, 3]
    float3 result = boundsCenter
				  + cellSize * 0.5
				  + cellSize * indexNorm;

    return result;
}

float WeightedBilinearFilter(Texture2D depthTextureAtlas, SamplerState linearSampler, float2 uv, float atlasResolution)
{
    float2 lerpFactor = frac(uv * atlasResolution + 0.5 / atlasResolution);
    float4 rawDepth = depthTextureAtlas.GatherRed(linearSampler, uv);

	// hit background color, nothing in voxel
    if (all(rawDepth == 0))
    {
        return 0;
    }

    float minDepth = 1.0;
    for (int i = 0; i < 4; i++)
    {
        if (rawDepth[i] != 0)
        {
            minDepth = min(minDepth, rawDepth[i]);
        }
    }

	// 0 is background, but may cause artifact when bilinear filter, we replace zero value using min value
	// we assume 4 depth represent continuous "height field"
    float4 filterDepth = float4(
		rawDepth.x == 0 ? minDepth : rawDepth.x,
		rawDepth.y == 0 ? minDepth : rawDepth.y,
		rawDepth.z == 0 ? minDepth : rawDepth.z,
		rawDepth.w == 0 ? minDepth : rawDepth.w
	);

	/*
	w - z
	|   |
	x - y
	*/
    float xLerp0 = lerp(filterDepth.x, filterDepth.y, lerpFactor.x);
    float xLerp1 = lerp(filterDepth.w, filterDepth.z, lerpFactor.x);
    float yLerp = lerp(xLerp0, xLerp1, lerpFactor.y);

    return yLerp;
}

void SetUint32SingleBit(inout uint u32, uint bitId, bool b)
{
    if (b)
    {
        u32 |= 1 << bitId;
    }
    else
    {
        u32 &= ~(1 << bitId);
    }
}

bool GetUint32SingleBit(in uint u32, uint bitId)
{
    return (u32 & (1 << bitId)) > 0;
}

void SetUint64SingleBit(inout uint2 u64, uint bitId, bool b)
{
    int compId = bitId / 32; // if 0~31 bit we set u64.x, if 32~64 bit we set u64.y
    int bitId32 = bitId % 32;

    SetUint32SingleBit(u64[compId], bitId32, b);
}

bool GetUint64SingleBit(in uint2 u64, uint bitId)
{
    int compId = bitId / 32;
    int bitId32 = bitId % 32;

    bool result = GetUint32SingleBit(u64[compId], bitId32);
    return result;
}

int3 ClipmapAddressMapping(int3 voxelIndex, int3 cascadeResolution, int3 cascadeMoveOffset, int cascadeIndex)
{
	// [0 ~ 128] --> [0 ~ 32]
    int3 blockIndex = voxelIndex / VOXEL_BLOCK_SIZE;
    int3 blockResolution = cascadeResolution / VOXEL_BLOCK_SIZE;

	// if cascade move, we don't move the data, just move address when access cascade
    int3 roundIndex = (blockIndex + cascadeMoveOffset) % blockResolution;

	// use 32*32*128 to represent 4 layer clipmap, single cascade is 32x32x32
    int3 accessIndex = roundIndex + int3(0, 0, blockResolution.z * cascadeIndex);
    return accessIndex;
}

#endif

