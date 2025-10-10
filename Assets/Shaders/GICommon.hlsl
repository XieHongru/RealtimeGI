#ifndef GI_COMMON
#define GI_COMMON

struct ObjectInfo
{
    int objectId;
    int meshId;

    float3 localBoundsMin;
    float3 localBoundsMax;
    float3 worldBoundsMin;
    float3 worldBoundsMax;
    float4x4 localToWorldMatrix;
    float4x4 worldToWorldMatrix;
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


#endif

