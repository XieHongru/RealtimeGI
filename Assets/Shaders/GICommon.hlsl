#ifndef GI_COMMON
#define GI_COMMON

#define PI (3.1415926f)

#define OBJECT_ID_INVALID (-1)
#define MAX_CARD_PER_MESH 12
#define VOXEL_BLOCK_SIZE 4
#define MAX_CASCADE_COUNT 4
#define VOXEL_COUNT_PER_BLOCK (VOXEL_BLOCK_SIZE * VOXEL_BLOCK_SIZE * VOXEL_BLOCK_SIZE)

#define PAGE_ID_INVALID (0x3FFFFFFF)
#define FREE_PAGE_POINTER (0)				// value in this index is pointer to next free page's id
#define RELEASE_PAGE_POINTER (1)			// value in this index is pointer to next location to temporally store released page's id
#define FREE_PAGE_POINTER_READ_ONLY (2)		// value same as FREE_PAGE_POINTER, for avoid data race when release pages
#define RELEASE_PAGE_POINTER_READ_ONLY (3)	// value same as RELEASE_PAGE_POINTER, for avoid data race when release pages

#define VOXEL_FACE_FRONT (0)
#define VOXEL_FACE_BACK (1)
#define VOXEL_FACE_NUM (2)

struct ObjectInfo
{
    int objectId;
    int surfaceCacheId;

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

struct SurfaceCacheInfo
{
    int surfaceCacheId;
    int meshCardCount;
    int meshCardResolution;
};

struct CardInfo
{
    float4x4 localToCardMatrix;
    float4 cardUVTransform;
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

int Index2DTo1D(int2 Index2D, int2 Size2D)
{
    return Index3DTo1D(int3(Index2D, 0), int3(Size2D, 1));
}

int2 Index1DTo2D(int Index1D, int2 Size2D)
{
    return Index1DTo3D(Index1D, int3(Size2D, 1)).xy;
}

float Squaref(float x)
{
    return x * x;
}

void DecodeObjectWorldBound(ObjectInfo objectInfo, out float3 worldBoundsMin, out float3 worldBoundsMax)
{
    float3 worldBoundPadding = float3(0.1, 0.1, 0.1); // prevent zero size bound (like plane)
    worldBoundsMin = objectInfo.worldBoundsMin - worldBoundPadding;
    worldBoundsMax = objectInfo.worldBoundsMax + worldBoundPadding;
}

bool ObjectIntersectAABB(in ObjectInfo objectInfo, float3 aabbCenter, float3 aabbSize)
{
    float3 worldBoundsMin, worldBoundsMax;
    DecodeObjectWorldBound(objectInfo, worldBoundsMin, worldBoundsMax);

    float3 sphereCenter = (worldBoundsMax + worldBoundsMin) * 0.5;
    float sphereRadius = length(worldBoundsMax - worldBoundsMin) * 0.5;
    float4 boundSphere = float4(sphereCenter, sphereRadius);

    float3 aabbMin = aabbCenter - aabbSize * 0.5;
    float3 aabbMax = aabbCenter + aabbSize * 0.5;

    //bool res = SphereIntersectAABB(boundSphere, aabbMin, aabbMax);
    bool res = all(aabbMin < worldBoundsMax) && all(aabbMax > worldBoundsMin);
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

float MaskedBilinearFilter(float4 gatherResult, float2 uv, float atlasResolution, float4 validMask)
{
    float2 lerpFactor = frac(uv * atlasResolution + 0.5 / atlasResolution);

	// find min value from all valid value
    float minValue = 100000;
    for (int i = 0; i < 4; i++)
    {
        if (validMask[i] != 0)
        {
            minValue = min(minValue, gatherResult[i]);
        }
    }

	// 0 is background, but may cause artifact when bilinear filter, we replace zero value using min value
	// we assume 4 depth represent continuous "height field"
    float4 filterValue = float4(
		validMask.x == 0 ? minValue : gatherResult.x,
		validMask.y == 0 ? minValue : gatherResult.y,
		validMask.z == 0 ? minValue : gatherResult.z,
		validMask.w == 0 ? minValue : gatherResult.w
	);

	/*
	w - z
	|   |
	x - y
	*/
    float xLerp0 = lerp(filterValue.x, filterValue.y, lerpFactor.x);
    float xLerp1 = lerp(filterValue.w, filterValue.z, lerpFactor.x);
    float yLerp = lerp(xLerp0, xLerp1, lerpFactor.y);

    return yLerp;
}

float SurfaceCacheSampleDepth(Texture2D depthTextureAtlas, SamplerState linearSampler, float2 uv, float atlasResolution, out float4 outValidMask)
{
    float4 rawDepth = depthTextureAtlas.GatherRed(linearSampler, uv);

	// depth tex represent rim detect result of mesh card, so we record and reuse it later when sample BaseColor, Normal and Emission
    // clip depth less than 0.5, which is the behind half part of object space, may cause inject artifacts when mesh is hollow
    outValidMask = (rawDepth > 0.499);

	// hit background color, nothing in voxel
    if (all(rawDepth == 0))
    {
        return 0;
    }

    float depth = MaskedBilinearFilter(rawDepth, uv, atlasResolution, outValidMask);
    return depth;
}

float3 SurfaceCacheSampleColor(Texture2D surfaceCacheAtlas, SamplerState linearSampler, float2 uv, float atlasResolution, float4 validMask)
{
    float4 gatherRed = surfaceCacheAtlas.GatherRed(linearSampler, uv);
    float4 gatherGreen = surfaceCacheAtlas.GatherGreen(linearSampler, uv);
    float4 gatherBlue = surfaceCacheAtlas.GatherBlue(linearSampler, uv);

    float3 color = float3(0, 0, 0);
    color.r = MaskedBilinearFilter(gatherRed, uv, atlasResolution, validMask);
    color.g = MaskedBilinearFilter(gatherGreen, uv, atlasResolution, validMask);
    color.b = MaskedBilinearFilter(gatherBlue, uv, atlasResolution, validMask);

    return color;
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

// https://tekpool.wordpress.com/category/bit-count/
int BitCount32(uint u)
{
    uint uCount = u - ((u >> 1) & 033333333333) - ((u >> 2) & 011111111111);
    return ((uCount + (uCount >> 3)) & 030707070707) % 63;
}

// return index to sample clipmap
int3 ClipmapAddressMapping(int3 voxelIndex, int3 cascadeResolution, int3 cascadeMoveOffset, int cascadeIndex)
{
	// [0 ~ 128] --> [0 ~ 32]
    int3 blockIndex = voxelIndex / VOXEL_BLOCK_SIZE;
    int3 blockCountInXYZ = cascadeResolution / VOXEL_BLOCK_SIZE;

	// if cascade move, we don't move the data, just move address when access cascade
    int3 roundIndex = (blockIndex + cascadeMoveOffset) % blockCountInXYZ;

	// use 32*32*128 to represent 4 layer clipmap, single cascade is 32x32x32
    int3 accessIndex = roundIndex + int3(0, 0, blockCountInXYZ.z * cascadeIndex);
    return accessIndex;
}

// map voxel index to voxel pool's physic address
int3 PageAddressMapping(int pageId, int3 numPagesInXYZ, int3 voxelIndex)
{
	// note: each "page" is same size as "block", which 4x4x4
    int3 indexInsidePage = voxelIndex % VOXEL_BLOCK_SIZE;

    int3 pageIndex3D = Index1DTo3D(pageId, numPagesInXYZ);
    int3 pageOffset = pageIndex3D * VOXEL_BLOCK_SIZE;

    int3 indexInPool = pageOffset + indexInsidePage;
    return indexInPool;
}

// VoxelPoolRadiance share address with other voxel pool, but double the size for two side voxel
// we pack front-back size nearby in z axis to reduce cache miss when access
int3 TwoSideAddressMapping(int3 indexInPool, int isBackFace)
{
    return indexInPool * int3(1, 1, VOXEL_FACE_NUM) + int3(0, 0, isBackFace);
}

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

#endif

