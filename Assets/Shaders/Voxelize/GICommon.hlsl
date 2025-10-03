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

#endif

