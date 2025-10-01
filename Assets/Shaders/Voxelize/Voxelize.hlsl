#ifndef VOXELIZE_INCLUDE
#define VOXELIZE_INCLUDE

#define UNINITIALIZED_BRICK 0xffffffffu

cbuffer params
{
    int _TrianglesCount;
    
    float _VoxelSize;
    float3 _CascadeMin;
    float3 _CascadeMax;
    float3 _CascadeSize;
};

struct VoxelizeCounter
{
    int maxTriangleSize;
    int maxReference;
    
    int triangleSizeCount;
    int referenceCount;
};

struct Item
{
    int3 boundMin;
    int3 boundMax;
};

struct Triangle
{
    int id;
    float3 v0, v1, v2;
};

struct TriangleBound
{
    float3 boundMin;
    float3 boundMax;
    int3 uboundMin;
    int3 uboundMax;
};

struct TriangleRef
{
    uint voxelId;
    uint triangleId;
    uint localRefId;
};

bool GetTriangleBounds(Triangle tri, out TriangleBound bound)
{
    float inflationSize = _VoxelSize / 7.0f;
    bound.boundMin = float3(min(tri.v0.x, min(tri.v1.x, tri.v2.x)),
                            min(tri.v0.y, min(tri.v1.y, tri.v2.y)),
                            min(tri.v0.z, min(tri.v1.z, tri.v2.z)));
    bound.boundMax = float3(max(tri.v0.x, max(tri.v1.x, tri.v2.x)),
                            max(tri.v0.y, max(tri.v1.y, tri.v2.y)),
                            max(tri.v0.z, max(tri.v1.z, tri.v2.z)));

    float3 boundMin;

    boundMin.x = bound.boundMin.x > 0 ? bound.boundMin.x : bound.boundMin.x - 1;
    boundMin.y = bound.boundMin.y > 0 ? bound.boundMin.y : bound.boundMin.y - 1;
    boundMin.z = bound.boundMin.z > 0 ? bound.boundMin.z : bound.boundMin.z - 1;

    bound.uboundMin  = min(int3(63,63,63), max(int3(0,0,0), int3((boundMin - float3(inflationSize, inflationSize, inflationSize)) / _VoxelSize)));

    float3 boundMax;

    boundMax.x = bound.boundMax.x > 0 ? bound.boundMax.x : bound.boundMax.x - 1;
    boundMax.y = bound.boundMax.y > 0 ? bound.boundMax.y : bound.boundMax.y - 1;
    boundMax.z = bound.boundMax.z > 0 ? bound.boundMax.z : bound.boundMax.z - 1;

    bound.uboundMax = min(int3(63, 63, 63), max(int3(0, 0, 0), int3((boundMax + float3(inflationSize, inflationSize, inflationSize)) / _VoxelSize))) + int3(1,1,1);
    
    return all(bound.boundMin <= _CascadeSize + float3(inflationSize, inflationSize, inflationSize)) && 
            all(bound.boundMax >= float3(-inflationSize, -inflationSize, -inflationSize));
}

uint3 VoxelOffset1DTo3D(uint offset, uint3 dim)
{
    return uint3(offset % dim.x, (offset / dim.x) % dim.y, offset / (dim.x * dim.y));
}

uint VoxelOffset3DTo1D(uint3 voxelCoord, uint degree)
{
    return voxelCoord.x | (voxelCoord.y << degree) | (voxelCoord.z << (2 * degree));
}

#endif