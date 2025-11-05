using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using static UnityEditor.Rendering.FilterWindow;
using Color = UnityEngine.Color;

public struct CardCaptureParams
{
    public Matrix4x4[]  viewProjectionMatrices;
    public Vector4[]    viewportInfos;
};

public struct CardInfoGPUData
{
    public Matrix4x4 localToCardMatrix;
    public Vector4 cardUVTransform;
}

public class SurfaceCacheKey : IEquatable<SurfaceCacheKey>
{
    public string hashValue;
    public Mesh mesh;
    public Material[] materials;

    public SurfaceCacheKey() { }

    public SurfaceCacheKey(GameObject gameObject)
    {
        mesh = gameObject.GetComponent<MeshFilter>().sharedMesh;
        materials = gameObject.GetComponent<MeshRenderer>().sharedMaterials;

        StringBuilder sb = new StringBuilder();
        if (mesh != null)
            sb.Append(mesh.GetInstanceID());

        if (materials != null)
        {
            for (int i = 0; i < materials.Length; i++)
            {
                sb.Append("_");
                sb.Append(materials[i] != null ? materials[i].GetInstanceID() : "null");
            }
        }

        hashValue = sb.ToString();
    }

    public bool Equals(SurfaceCacheKey other)
    {
       return hashValue == other.hashValue;
    }
}

public class SurfaceCacheInfo
{
    public int surfaceCacheId;
    public int meshCardCount;
    public int meshCardResolution;

    public Mesh mesh;
    public Material[] materials;
    public List<Matrix4x4> localToCardMatrices;
    public List<Vector4> cardUVTransforms;

    HashSet<int> m_ReferenceHolder;

    public SurfaceCacheInfo(int inSurfaceCacheId)
    {
        surfaceCacheId = inSurfaceCacheId;

        localToCardMatrices = new List<Matrix4x4>();
        cardUVTransforms = new List<Vector4>();
        m_ReferenceHolder = new HashSet<int>();
    }

    public void Empty()
    {
        surfaceCacheId = 0;
        meshCardCount = 0;
        meshCardResolution = 0;

        mesh = null;
        materials = null;
        localToCardMatrices = null;
        cardUVTransforms = null;
    }

    public void AddObjectReference(ObjectInfo objectInfo)
    {
        m_ReferenceHolder.Add(objectInfo.objectId);
    }

    public void RemoveObjectReference(ObjectInfo objectInfo)
    {
        m_ReferenceHolder.Remove(objectInfo.objectId);
    }

    public int GetRefCount() => m_ReferenceHolder.Count;
    public List<int> GetReferenceHolder() => new List<int>(m_ReferenceHolder);
}

public struct SurfaceCacheInfoGPUData
{
    public int surfaceCacheId;
    public int meshCardCount;
    public int meshCardResolution;
}

// -----------------------------------------------------------------------------------------------------------------

public class SurfaceCache
{
    // ----------------------------------
    // 0: base color
    // 1: normal
    // 2: emissive
    // 3: depth
    // ----------------------------------
    RenderTexture[] m_SurfaceCacheAtlas;
    RenderTargetIdentifier[] m_SurfaceCacheRenderTargets;
    RenderTexture m_DepthStencil;
    int m_AtlasResolution;
    const int OBJECT_ID_INVALID = -1;
    const int MAX_CARD_PER_MESH = 12;
    const int MAX_OBJECT_COUNT = 2048;
    const int MAX_SURFACE_CACHE_COUNT = 2048;
    const int USE_QUAD_TREE = 0;

    LinearAllocator<SurfaceCacheKey> m_SurfaceCacheAllocator;
    QuadTreeAllocator m_SurfaceCacheAtlasAllocator;

    List<int> m_SurfaceCacheCaptureCommands;
    SurfaceCacheInfo[] m_SurfaceCacheInfos;

    ComputeBuffer m_SurfaceCacheInfoUploadBuffer;
    ComputeBuffer m_SurfaceCacheInfoBuffer;
    ComputeBuffer m_CardInfoUploadBuffer;
    ComputeBuffer m_CardInfoBuffer;

    int m_CardClearQuadsCount;
    ComputeBuffer m_CardClearQuadUVTransformBuffer;

    ComputeShader m_CardInfosSyncCS;

    public ComputeBuffer GetCardInfoBuffer()
    { 
        return m_CardInfoBuffer;
    }

    public ComputeBuffer GetSurfaceCacheInfoBuffer()
    {
        return m_SurfaceCacheInfoBuffer;
    }

    public RenderTexture GetSurfaceCacheTexture(int index)
    {
        if(index < 0 || index > 3)
            return null;
        return m_SurfaceCacheAtlas[index];
    }

    public void Init()
    {
        // 1. shader resources init
        m_CardInfosSyncCS = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/Shaders/MiraiGI/SurfaceCache/SurfaceCacheInfoSync.compute");

        // 2. surface cache atlas init
        m_AtlasResolution = 2048;
        m_SurfaceCacheAtlas = new RenderTexture[4];
        m_SurfaceCacheAtlas[0] = new RenderTexture(m_AtlasResolution, m_AtlasResolution, 0, RenderTextureFormat.ARGB32);
        m_SurfaceCacheAtlas[1] = new RenderTexture(m_AtlasResolution, m_AtlasResolution, 0, RenderTextureFormat.ARGBHalf);
        m_SurfaceCacheAtlas[2] = new RenderTexture(m_AtlasResolution, m_AtlasResolution, 0, RenderTextureFormat.ARGBHalf);
        m_SurfaceCacheAtlas[3] = new RenderTexture(m_AtlasResolution, m_AtlasResolution, 0, RenderTextureFormat.RHalf);

        // 3. surface cache capture rt init
        m_SurfaceCacheRenderTargets = new RenderTargetIdentifier[4];
        m_SurfaceCacheRenderTargets[0] = new RenderTargetIdentifier(m_SurfaceCacheAtlas[0]);
        m_SurfaceCacheRenderTargets[1] = new RenderTargetIdentifier(m_SurfaceCacheAtlas[1]);
        m_SurfaceCacheRenderTargets[2] = new RenderTargetIdentifier(m_SurfaceCacheAtlas[2]);
        m_SurfaceCacheRenderTargets[3] = new RenderTargetIdentifier(m_SurfaceCacheAtlas[3]);

        m_DepthStencil = new RenderTexture(m_AtlasResolution, m_AtlasResolution, 32, RenderTextureFormat.Depth);
        m_DepthStencil.depthStencilFormat = GraphicsFormat.D24_UNorm_S8_UInt;

        m_SurfaceCacheInfos = new SurfaceCacheInfo[MAX_SURFACE_CACHE_COUNT];
        m_SurfaceCacheCaptureCommands = new List<int>();

        // 4. allocator init
        m_SurfaceCacheAllocator = new LinearAllocator<SurfaceCacheKey>();
        m_SurfaceCacheAllocator.Init(MAX_SURFACE_CACHE_COUNT);
        m_SurfaceCacheAtlasAllocator = new QuadTreeAllocator();
        m_SurfaceCacheAtlasAllocator.Init(m_AtlasResolution);

        // 5. GPU compute buffer init
        m_SurfaceCacheInfoUploadBuffer = new ComputeBuffer(MAX_SURFACE_CACHE_COUNT, Marshal.SizeOf<SurfaceCacheInfoGPUData>(), ComputeBufferType.Structured);
        m_SurfaceCacheInfoBuffer = new ComputeBuffer(MAX_SURFACE_CACHE_COUNT, Marshal.SizeOf<SurfaceCacheInfoGPUData>(), ComputeBufferType.Structured);
        m_CardInfoUploadBuffer = new ComputeBuffer(MAX_OBJECT_COUNT * MAX_CARD_PER_MESH, Marshal.SizeOf<CardInfoGPUData>(), ComputeBufferType.Structured);
        m_CardInfoBuffer = new ComputeBuffer(MAX_OBJECT_COUNT * MAX_CARD_PER_MESH, Marshal.SizeOf<CardInfoGPUData>(), ComputeBufferType.Structured);
        m_CardClearQuadUVTransformBuffer = new ComputeBuffer(MAX_OBJECT_COUNT * MAX_CARD_PER_MESH, sizeof(float) * 4, ComputeBufferType.Raw);

    }

    public void Release()
    {
        foreach (var tex in m_SurfaceCacheAtlas)
        {
            tex?.Release();
        }
        m_DepthStencil.Release();

        m_SurfaceCacheInfoUploadBuffer.Release();
        m_SurfaceCacheInfoBuffer.Release();
        m_CardInfoUploadBuffer.Release();
        m_CardInfoBuffer.Release();
        m_CardClearQuadUVTransformBuffer.Release();
    }

    public void SyncObjectInfosToGPU(GPUSceneData gpuSceneData)
    {
        List<ObjectInfo> objectsInfo = gpuSceneData.objectsInfo;
        
        for (int i = 0; i < objectsInfo.Count; i++)
        {
            ObjectInfo objectInfo = objectsInfo[i];
            ObjectInfoGPUData objectInfoGPUData = new ObjectInfoGPUData();
            objectInfoGPUData.objectId = objectInfo.objectId;
            objectInfoGPUData.surfaceCacheId = objectInfo.surfaceCacheId;
            objectInfoGPUData.localBoundsMin = objectInfo.localBoundsMin;
            objectInfoGPUData.localBoundsMax = objectInfo.localBoundsMax;
            objectInfoGPUData.worldBoundsMin = objectInfo.worldBoundsMin;
            objectInfoGPUData.worldBoundsMax = objectInfo.worldBoundsMax;
            objectInfoGPUData.localToWorldMatrix = objectInfo.localToWorldMatrix;
            objectInfoGPUData.worldToLocalMatrix = objectInfo.worldToLocalMatrix;

            gpuSceneData.objectInfoGPUData.Add(objectInfoGPUData);
        }

        gpuSceneData.objectInfoBuffer.SetData(gpuSceneData.objectInfoGPUData);
    }

    public void SyncCardInfosToGPU(CommandBuffer cmd, int surfaceCacheCount)
    {
        // 1. fill data: surface cache info
        List<SurfaceCacheInfoGPUData> surfaceCacheInfoUploadData = new List<SurfaceCacheInfoGPUData>();
        foreach (var surfaceCacheId in m_SurfaceCacheCaptureCommands)
        {
            SurfaceCacheInfo surfaceCacheInfo = m_SurfaceCacheInfos[surfaceCacheId];
            SurfaceCacheInfoGPUData surfaceCacheInfoGPUData = new SurfaceCacheInfoGPUData();
            surfaceCacheInfoGPUData.surfaceCacheId = surfaceCacheInfo.surfaceCacheId;
            surfaceCacheInfoGPUData.meshCardCount = surfaceCacheInfo.meshCardCount;
            surfaceCacheInfoGPUData.meshCardResolution = surfaceCacheInfo.meshCardResolution;

            surfaceCacheInfoUploadData.Add(surfaceCacheInfoGPUData);
        }

        // 2. upload surface cache info
        {
            m_SurfaceCacheInfoUploadBuffer.SetData(surfaceCacheInfoUploadData);
        }

        // 3. fill data: mesh card data for each surface cache
        int uploadDataOffset = 0;
        CardInfoGPUData[] cardInfoUploadData = new CardInfoGPUData[surfaceCacheCount * MAX_CARD_PER_MESH];

        foreach (var surfaceCacheId in m_SurfaceCacheCaptureCommands)
        {
            SurfaceCacheInfo surfaceCacheInfo = m_SurfaceCacheInfos[surfaceCacheId];
            for (int cardIndex = 0; cardIndex < surfaceCacheInfo.meshCardCount; cardIndex++)
            {
                cardInfoUploadData[uploadDataOffset + cardIndex].localToCardMatrix = surfaceCacheInfo.localToCardMatrices[cardIndex];
                cardInfoUploadData[uploadDataOffset + cardIndex].cardUVTransform = surfaceCacheInfo.cardUVTransforms[cardIndex];
            }

            uploadDataOffset += MAX_CARD_PER_MESH;
        }

        // 4. upload mesh card info
        {
            m_CardInfoUploadBuffer.SetData(cardInfoUploadData);
        }

        // 5. copy data from transient buffer to RW buffer
        {
            int kernel = m_CardInfosSyncCS.FindKernel("SurfaceInfoUpdate");

            cmd.SetComputeIntParam(m_CardInfosSyncCS, Shader.PropertyToID("_SurfaceCacheCount"), surfaceCacheCount);
            cmd.SetComputeBufferParam(m_CardInfosSyncCS, kernel, Shader.PropertyToID("_SurfaceCacheInfoUploadBuffer"), m_SurfaceCacheInfoUploadBuffer);
            cmd.SetComputeBufferParam(m_CardInfosSyncCS, kernel, Shader.PropertyToID("_RWSurfaceCacheInfoBuffer"), m_SurfaceCacheInfoBuffer);
            cmd.SetComputeBufferParam(m_CardInfosSyncCS, kernel, Shader.PropertyToID("_CardInfoUploadBuffer"), m_CardInfoUploadBuffer);
            cmd.SetComputeBufferParam(m_CardInfosSyncCS, kernel, Shader.PropertyToID("_RWCardInfoBuffer"), m_CardInfoBuffer);

            cmd.DispatchCompute(m_CardInfosSyncCS, kernel, Mathf.CeilToInt((float)surfaceCacheCount / 8), 1, 1);
        }

        // 6. TODO: fill data for removed object's cards cleaning, and upload card clear list
    }

    public void CaptureSurfaceCache(GPUSceneData gpuSceneData)
    {
        List<ObjectInfo> objectsInfo = gpuSceneData.objectsInfo;
        // TODO: clear surface cache capture command list?

        // 1. allocate surface cache per object
        foreach (var objInfo in objectsInfo)
        {
            ReferenceSurfaceCache(objInfo);
        }

        // 2. TODO: loop and see if surface cache need change size (update per frame)

        // 3. allocate surface cache atlas space
        foreach (int surfaceCacheId in m_SurfaceCacheCaptureCommands)
        {
            GatherSurfaceCacheInfo(surfaceCacheId, objectsInfo);
            AllocateSurfaceCache(surfaceCacheId);
        }

        CommandBuffer cmd = CommandBufferPool.Get("Surface Cache Capture");

        SyncObjectInfosToGPU(gpuSceneData);
        SyncCardInfosToGPU(cmd, m_SurfaceCacheCaptureCommands.Count);

        Shader surfaceCacheShader = Shader.Find("Mirai/SurfaceCacheCapture");

        cmd.SetRenderTarget(m_SurfaceCacheRenderTargets, m_DepthStencil);
        cmd.ClearRenderTarget(true, true, Color.black);

        foreach (var surfaceCacheId in m_SurfaceCacheCaptureCommands)
        {
            SurfaceCacheInfo surfaceCacheInfo = m_SurfaceCacheInfos[surfaceCacheId];

            Material captureMaterial = new Material(surfaceCacheShader);
            captureMaterial.enableInstancing = true;

            Mesh mesh = surfaceCacheInfo.mesh;
            Material[] mat = surfaceCacheInfo.materials;
            int subMeshCount = mesh.subMeshCount;

            CardCaptureParams cardCaptureParams = new CardCaptureParams();
            cardCaptureParams.viewProjectionMatrices = new Matrix4x4[6];
            cardCaptureParams.viewportInfos = new Vector4[6];

            for (int cardIndex = 0; cardIndex < surfaceCacheInfo.meshCardCount; cardIndex++)
            {
                cardCaptureParams.viewProjectionMatrices[cardIndex] = surfaceCacheInfo.localToCardMatrices[cardIndex];
                cardCaptureParams.viewportInfos[cardIndex] = CalcViewportInfo(surfaceCacheInfo, cardIndex);
            }

            Matrix4x4[] identityMats = new Matrix4x4[6];
            for (int i = 0; i < subMeshCount; i++)
            {
                if (mat[i].shader.name == "Universal Render Pipeline/Nature/SpeedTree8")
                    continue;
                captureMaterial.SetColor("_BaseColor", mat[i].GetColor("_BaseColor"));
                captureMaterial.SetTexture("_BaseMap", mat[i].GetTexture("_BaseMap"));
                captureMaterial.SetColor("_EmissionColor", mat[i].GetColor("_EmissionColor"));
                captureMaterial.SetTexture("_EmissionMap", mat[i].GetTexture("_EmissionMap"));
                captureMaterial.SetTexture("_NormalMap", mat[i].GetTexture("_DetailNormalMap"));

                captureMaterial.SetMatrixArray("_ViewProjectionMatrices", cardCaptureParams.viewProjectionMatrices);
                captureMaterial.SetVectorArray("_ViewportInfos", cardCaptureParams.viewportInfos);
                
                cmd.DrawMeshInstanced(mesh, i, captureMaterial, 0, identityMats, 6);
            }
        }

        Graphics.ExecuteCommandBuffer(cmd);
        cmd.Release();
    }

    void ReferenceSurfaceCache(ObjectInfo objectInfo)
    {
        SurfaceCacheKey key = new SurfaceCacheKey(objectInfo.gameObject);
        int surfaceCacheId = OBJECT_ID_INVALID;
        
        // if not exist, allocate it
        if (!m_SurfaceCacheAllocator.Find(key, out surfaceCacheId))
        {
            surfaceCacheId = m_SurfaceCacheAllocator.AllocateElement(key);
            m_SurfaceCacheInfos[surfaceCacheId] = new SurfaceCacheInfo(surfaceCacheId);

            m_SurfaceCacheCaptureCommands.Add(surfaceCacheId);
        }

        m_SurfaceCacheInfos[surfaceCacheId].AddObjectReference(objectInfo);

        // if surface cache change, release old item
        if (objectInfo.surfaceCacheKey != key && objectInfo.surfaceCacheId != OBJECT_ID_INVALID)
        {
            DeReferenceSurfaceCache(objectInfo);
        }

        objectInfo.surfaceCacheId = surfaceCacheId;
        objectInfo.surfaceCacheKey = key;
    }

    void DeReferenceSurfaceCache(ObjectInfo objectInfo)
    {
        SurfaceCacheInfo surfaceCacheInfo = m_SurfaceCacheInfos[objectInfo.surfaceCacheId];
        surfaceCacheInfo.RemoveObjectReference(objectInfo);

        // if nobody use surface cache, we release it
        if (surfaceCacheInfo.GetRefCount() == 0)
        {
            m_SurfaceCacheAllocator.ReleaseElement(objectInfo.surfaceCacheKey);
            ReleaseSurfaceCache(objectInfo.surfaceCacheId);
            surfaceCacheInfo.Empty();
        }

        // sync info to object
        objectInfo.surfaceCacheKey = new SurfaceCacheKey();    // reset to null
        objectInfo.surfaceCacheId = OBJECT_ID_INVALID;
    }

    // TODO: support directions apart from axis-dir
    public Matrix4x4 CalcViewProjectionMatrix(Vector3 center, float size, int cubeFace)
    {
        float halfSize = size * 0.5f;

        Vector3 viewDir = Vector3.forward;
        Vector3 up = Vector3.up;

        switch (cubeFace)
        {
            case 0: viewDir = Vector3.right; up = Vector3.up; break;
            case 1: viewDir = Vector3.left; up = Vector3.up; break;
            case 2: viewDir = Vector3.up; up = Vector3.back; break;
            case 3: viewDir = Vector3.down; up = Vector3.forward; break;
            case 4: viewDir = Vector3.forward; up = Vector3.up; break;
            case 5: viewDir = Vector3.back; up = Vector3.up; break;
        }

        Matrix4x4 viewMatrix = Matrix4x4.LookAt(center, center + viewDir, up).inverse;
        Matrix4x4 projectionMatrix = Matrix4x4.Ortho(-halfSize, halfSize, -halfSize, halfSize, -halfSize, halfSize);
        if (SystemInfo.usesReversedZBuffer)
        {
            projectionMatrix = Matrix4x4.Ortho(-halfSize, halfSize, -halfSize, halfSize, halfSize, -halfSize);
        }

        return projectionMatrix * viewMatrix;
    }

    public Vector4 CalcViewportInfo(SurfaceCacheInfo surfaceCacheInfo, int cardIndex)
    {
        Vector4 cardSizeAndOffset = surfaceCacheInfo.cardUVTransforms[cardIndex];
        Vector4 uvTransform = cardSizeAndOffset / (float)m_AtlasResolution;

        // @TODO: dynamic sparse quad tree allocation
        // padding 1 texel
        float paddingScale = (surfaceCacheInfo.meshCardResolution - 1.0f) / (float)surfaceCacheInfo.meshCardResolution;

        // viewport center is (0, 0) but uv center is (0.5, 0.5)
        float offsetX = 0.5f * uvTransform.x;
        float offsetY = 0.5f * uvTransform.y;

        Vector4 result = new Vector4(
            uvTransform.x * paddingScale,
            uvTransform.y * paddingScale,
            (uvTransform.z + offsetX) * 2.0f - 1.0f, // using this offset in clip space [-1, 1]
            (uvTransform.w + offsetY) * 2.0f - 1.0f
        );

        return result;
    }

    public Vector4 AllocateCardUVTransform(SurfaceCacheInfo surfaceCacheInfo, int cardIndex)
    {
        return Vector4.zero;
        //if (USE_QUAD_TREE == 1)
        //{
        //    QuadTreeNode node = m_SurfaceCacheAtlasAllocator.AllocateElement(surfaceCacheInfo.meshCardResolution);

        //    Vector4 result = new Vector4(node.size, node.size, node.min.x, node.min.y);
        //    return result;
        //}
        //else
        //{
        //    // TODO: dynamic sparse quad tree allocation
        //    int numCardsInXY = m_AtlasResolution / surfaceCacheInfo.meshCardResolution;

        //    int indexInAtlas = meshBatch.objectId * meshBatch.cardCount + cardIndex;
        //    float indexInAtlasX = indexInAtlas % numCardsInXY;
        //    float indexInAtlasY = indexInAtlas / numCardsInXY;

        //    float sizeX = meshBatch.resolution;
        //    float sizeY = meshBatch.resolution;

        //    // map [0, 1] to [-1, 1]
        //    float offsetX = indexInAtlasX * meshBatch.resolution;
        //    float offsetY = indexInAtlasY * meshBatch.resolution;

        //    // xy: scale, zw: offset
        //    Vector4 result = new Vector4(sizeX, sizeY, offsetX, offsetY);
        //    return result;
        //}
    }

    void GatherSurfaceCacheInfo(int surfaceCacheId, List<ObjectInfo> objectsInfo)
    {
        SurfaceCacheInfo surfaceCacheInfo = m_SurfaceCacheInfos[surfaceCacheId];
        ObjectInfo objectInfo = objectsInfo[surfaceCacheInfo.GetReferenceHolder()[0]];

        surfaceCacheInfo.mesh = objectInfo.gameObject.GetComponent<MeshFilter>().sharedMesh;
        surfaceCacheInfo.materials = objectInfo.gameObject.GetComponent<MeshRenderer>().sharedMaterials;

        // setup mesh card placement and capture matrix
        // @TODO: precomputed card placement
        surfaceCacheInfo.meshCardCount = 6;

        Vector3 localBoundsCenter = (objectInfo.localBoundsMax + objectInfo.localBoundsMin) * 0.5f;
        Vector3 localBoundsSize = (objectInfo.localBoundsMax - objectInfo.localBoundsMin) * (1.0f + 1e-3f);
        float maxDimension = Mathf.Max(localBoundsSize.x, Mathf.Max(localBoundsSize.y, localBoundsSize.z));

        for (int cardIndex = 0; cardIndex < surfaceCacheInfo.meshCardCount; cardIndex++)
        {
            Matrix4x4 localToCard = CalcViewProjectionMatrix(localBoundsCenter, maxDimension, cardIndex);
            surfaceCacheInfo.localToCardMatrices.Add(localToCard);
        }
    }

    void AllocateSurfaceCache(int surfaceCacheId)
    {
        SurfaceCacheInfo surfaceCacheInfo = m_SurfaceCacheInfos[surfaceCacheId];

        // if first time allocate, we give a default size
        if (surfaceCacheInfo.meshCardResolution == 0)
        {
            surfaceCacheInfo.meshCardResolution = 32;
        }

        //if (USE_QUAD_TREE == 1)
        //{
        //    Vector3 localSizeXYZ = objectInfo.localBoundsMax - objectInfo.localBoundsMin;
        //    Vector3 worldScale = objects[objectInfo.objectId].GetComponent<MeshFilter>().transform.lossyScale;

        //    float worldSize = 0;
        //    worldSize = Mathf.Max(worldSize, worldScale.x * localSizeXYZ.x);
        //    worldSize = Mathf.Max(worldSize, worldScale.y * localSizeXYZ.y);
        //    worldSize = Mathf.Max(worldSize, worldScale.z * localSizeXYZ.z);

        //    float cardSizef = worldSize / 0.25f;    // 4 texel per meter
        //    int cardSize = Mathf.NextPowerOfTwo((int)cardSizef);
        //    meshBatch.resolution = Mathf.Clamp(cardSize, m_SurfaceCacheAtlasAllocator.GetMinNodeSize(), m_SurfaceCacheAtlasAllocator.GetMaxNodeSize());
        //}
        //else
        //{
        //    meshBatch.resolution = 32;
        //}

        for (int cardIndex = 0; cardIndex < surfaceCacheInfo.meshCardCount; cardIndex++)
        {
            QuadTreeNode node = m_SurfaceCacheAtlasAllocator.AllocateElement(surfaceCacheInfo.meshCardResolution);
            Vector4 cardSizeAndOffset = new Vector4(node.size, node.size, node.min.x, node.min.y);
            surfaceCacheInfo.cardUVTransforms.Add(cardSizeAndOffset);
        }
    }

    void ReleaseSurfaceCache(int surfaceCacheId)
    {
        SurfaceCacheInfo surfaceCacheInfo = m_SurfaceCacheInfos[surfaceCacheId];
        if (surfaceCacheInfo.meshCardResolution == 0)
            return;

        for (int cardIndex = 0; cardIndex < surfaceCacheInfo.meshCardCount; cardIndex++)
        {
            Vector4 cardSizeAndOffset = surfaceCacheInfo.cardUVTransforms[cardIndex];

            QuadTreeNode freeNode = new QuadTreeNode();
            freeNode.size = (int)cardSizeAndOffset.x;    // x == y always
            freeNode.min = new Vector2Int((int)cardSizeAndOffset.z, (int)cardSizeAndOffset.w);
            freeNode.max = freeNode.min + new Vector2Int((int)cardSizeAndOffset.x, (int)cardSizeAndOffset.y);
            freeNode.center = (freeNode.max + freeNode.min) / 2;

            m_SurfaceCacheAtlasAllocator.ReleaseElement(freeNode);
        }
    }
}