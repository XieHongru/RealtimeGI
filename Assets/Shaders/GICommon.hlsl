#ifndef GI_COMMON
#define GI_COMMON

#define MAX_CARD_PER_MESH 12

struct ObjectInfo
{
    int objectId;
    int cardCount;
    int resolution;
    int meshId;

    float3 localBoundsMin;
    float3 localBoundsMax;
    float3 worldBoundsMin;
    float3 worldBoundsMax;
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

uint Index3DTo1D(uint3 index3D, uint3 size3D)
{
    int res = 0;
    res += index3D.x;
    res += index3D.y * size3D.x;
    res += index3D.z * (size3D.x * size3D.y);
    return res;
}

uint3 Index1DTo3D(uint index1D, uint3 size3D)
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

// must sync with SurfaceCache.CalcCardUVTransform
float4 CalcCardUVTransform(int objectId, int cardIndex, int cardResolution, int cardCount, int atlasResolution)
{
    int numCardsInXY = atlasResolution / cardResolution;

    int indexInAtlas = objectId * cardCount + cardIndex;
    float indexInAtlasX = indexInAtlas % numCardsInXY;
    float indexInAtlasY = indexInAtlas / numCardsInXY;

    float cardSizeInUV = 1.0 / float(numCardsInXY);
    float scale = cardSizeInUV;

	// map [0, 1] to [-1, 1]
    float offsetX = indexInAtlasX * cardSizeInUV;
    float offsetY = indexInAtlasY * cardSizeInUV;
    
    // xy: scale, zw: offset
    float4 result = float4(scale, scale, offsetX, offsetY);
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

#endif

