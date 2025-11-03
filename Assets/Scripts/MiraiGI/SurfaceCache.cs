using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
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
    public Matrix4x4 cardToLocalMatrix;
    public Vector4 cardUVTransform;
}

public class CardCaptureMeshBatch
{
    public int objectId;
    public Mesh mesh;

    public int cardCount;
    public int resolution;
    public List<Matrix4x4> localToCardMatrices;
    public List<Vector4> cardUVTransforms;
}

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
    const int MAX_CARD_PER_MESH = 12;
    const int MAX_OBJECT_COUNT = 2048;
    const int USE_QUAD_TREE = 0;

    QuadTreeAllocator m_SurfaceCacheAtlasAllocator;

    List<CardCaptureMeshBatch> m_CaptureMeshBatches;

    ComputeBuffer m_CardInfoWriteOffsetUploadBuffer;
    ComputeBuffer m_CardInfoUploadBuffer;
    ComputeBuffer m_CardInfoBuffer;

    int m_CardClearQuadsCount;
    ComputeBuffer m_CardClearQuadUVTransformBuffer;

    ComputeShader m_CardInfosSyncCS;

    public ComputeBuffer GetCardInfoBuffer()
    { 
        return m_CardInfoBuffer;
    }

    public RenderTexture GetSurfaceCacheTexture(int index)
    {
        if(index < 0 || index > 3)
            return null;
        return m_SurfaceCacheAtlas[index];
    }

    public void Init()
    {
        m_AtlasResolution = 2048;
        m_SurfaceCacheAtlas = new RenderTexture[4];
        m_SurfaceCacheAtlas[0] = new RenderTexture(m_AtlasResolution, m_AtlasResolution, 0, RenderTextureFormat.ARGB32);
        m_SurfaceCacheAtlas[1] = new RenderTexture(m_AtlasResolution, m_AtlasResolution, 0, RenderTextureFormat.ARGBHalf);
        m_SurfaceCacheAtlas[2] = new RenderTexture(m_AtlasResolution, m_AtlasResolution, 0, RenderTextureFormat.ARGBHalf);
        m_SurfaceCacheAtlas[3] = new RenderTexture(m_AtlasResolution, m_AtlasResolution, 0, RenderTextureFormat.RHalf);

        m_SurfaceCacheRenderTargets = new RenderTargetIdentifier[4];
        m_SurfaceCacheRenderTargets[0] = new RenderTargetIdentifier(m_SurfaceCacheAtlas[0]);
        m_SurfaceCacheRenderTargets[1] = new RenderTargetIdentifier(m_SurfaceCacheAtlas[1]);
        m_SurfaceCacheRenderTargets[2] = new RenderTargetIdentifier(m_SurfaceCacheAtlas[2]);
        m_SurfaceCacheRenderTargets[3] = new RenderTargetIdentifier(m_SurfaceCacheAtlas[3]);

        m_DepthStencil = new RenderTexture(m_AtlasResolution, m_AtlasResolution, 32, RenderTextureFormat.Depth);
        m_DepthStencil.depthStencilFormat = GraphicsFormat.D24_UNorm_S8_UInt;

        m_CaptureMeshBatches = new List<CardCaptureMeshBatch>();
        m_SurfaceCacheAtlasAllocator = new QuadTreeAllocator();
        m_SurfaceCacheAtlasAllocator.TryInit(m_AtlasResolution);

        m_CardInfoWriteOffsetUploadBuffer = new ComputeBuffer(MAX_OBJECT_COUNT, sizeof(int), ComputeBufferType.Default);
        m_CardInfoUploadBuffer = new ComputeBuffer(MAX_OBJECT_COUNT * MAX_CARD_PER_MESH, Marshal.SizeOf<CardInfoGPUData>(), ComputeBufferType.Structured);
        m_CardInfoBuffer = new ComputeBuffer(MAX_OBJECT_COUNT * MAX_CARD_PER_MESH, Marshal.SizeOf<CardInfoGPUData>(), ComputeBufferType.Structured);
        m_CardClearQuadUVTransformBuffer = new ComputeBuffer(MAX_OBJECT_COUNT * MAX_CARD_PER_MESH, sizeof(float) * 4, ComputeBufferType.Raw);

        m_CardInfosSyncCS = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/Shaders/MiraiGI/SurfaceCache/SurfaceCacheInfoSync.compute");
    }

    public void Release()
    {
        foreach (var tex in m_SurfaceCacheAtlas)
        {
            tex?.Release();
        }
        m_DepthStencil.Release();

        m_CardInfoWriteOffsetUploadBuffer.Release();
        m_CardInfoUploadBuffer.Release();
        m_CardInfoBuffer.Release();
        m_CardClearQuadUVTransformBuffer.Release();
    }

    public void SyncCardInfosToGPU(CommandBuffer cmd, int objectCount)
    {
        // 1. build data on cpu
        int uploadDataOffset = 0;
        List<int> cardOffsets = new List<int>();
        CardInfoGPUData[] cardInfoUploadData = new CardInfoGPUData[objectCount * MAX_CARD_PER_MESH];

        foreach (CardCaptureMeshBatch meshBatch in m_CaptureMeshBatches)
        {
            // simple linear allocator
            int cardMatrixOffset = meshBatch.objectId * MAX_CARD_PER_MESH;
            cardOffsets.Add(cardMatrixOffset);

            for (int cardIndex = 0; cardIndex < meshBatch.cardCount; cardIndex++)
            {
                cardInfoUploadData[uploadDataOffset + cardIndex].localToCardMatrix = meshBatch.localToCardMatrices[cardIndex];
                cardInfoUploadData[uploadDataOffset + cardIndex].cardToLocalMatrix = meshBatch.localToCardMatrices[cardIndex].inverse;
                cardInfoUploadData[uploadDataOffset + cardIndex].cardUVTransform = meshBatch.cardUVTransforms[cardIndex];
            }

            uploadDataOffset += MAX_CARD_PER_MESH;
        }

        // 2. upload per-object card's write offset
        {
            m_CardInfoWriteOffsetUploadBuffer.SetData(cardOffsets);
        }

        // 3. upload per-object card data
        {
            m_CardInfoUploadBuffer.SetData(cardInfoUploadData);
        }

        // 4. copy data from transient buffer to RW buffer
        {
            int kernel = m_CardInfosSyncCS.FindKernel("SurfaceInfoUpdate");

            cmd.SetComputeIntParam(m_CardInfosSyncCS, Shader.PropertyToID("_ObjectCount"), objectCount);
            cmd.SetComputeBufferParam(m_CardInfosSyncCS, kernel, Shader.PropertyToID("_CardInfoWriteOffsetUploadBuffer"), m_CardInfoWriteOffsetUploadBuffer);
            cmd.SetComputeBufferParam(m_CardInfosSyncCS, kernel, Shader.PropertyToID("_CardInfoUploadBuffer"), m_CardInfoUploadBuffer);
            cmd.SetComputeBufferParam(m_CardInfosSyncCS, kernel, Shader.PropertyToID("_RWCardInfoBuffer"), m_CardInfoBuffer);

            cmd.DispatchCompute(m_CardInfosSyncCS, kernel, Mathf.CeilToInt((float)objectCount / 8), 1, 1);
        }

        // 5. TODO: fill data for removed object's cards cleaning, and upload card clear list
    }

    public void CaptureSurfaceCache(List<ObjectInfo> objectsInfo, List<GameObject> objects, List<Mesh> meshes)
    {
        m_CaptureMeshBatches.Clear();

        m_SurfaceCacheAtlasAllocator = new QuadTreeAllocator();
        m_SurfaceCacheAtlasAllocator.TryInit(m_AtlasResolution);
        // capture surface cache per object
        foreach (var objInfo in objectsInfo)
        {
            AllocateMeshCard(objInfo, objects, meshes);
        }

        CommandBuffer cmd = CommandBufferPool.Get("Surface Cache Capture");

        SyncCardInfosToGPU(cmd, objects.Count);

        Shader surfaceCacheShader = Shader.Find("Mirai/SurfaceCacheCapture");

        cmd.SetRenderTarget(m_SurfaceCacheRenderTargets, m_DepthStencil);
        cmd.ClearRenderTarget(true, true, Color.black);

        
        //foreach (var objInfo in objectsInfo)
        foreach (var meshBatch in m_CaptureMeshBatches)
        {
            Material captureMaterial = new Material(surfaceCacheShader);
            captureMaterial.enableInstancing = true;

            Mesh mesh = meshBatch.mesh;
            Material[] mat = objects[meshBatch.objectId].GetComponent<MeshRenderer>().sharedMaterials;
            int subMeshCount = mesh.subMeshCount;

            CardCaptureParams cardCaptureParams = new CardCaptureParams();
            cardCaptureParams.viewProjectionMatrices = new Matrix4x4[6];
            cardCaptureParams.viewportInfos = new Vector4[6];

            for (int cardIndex = 0; cardIndex < 6; cardIndex++)
            {
                cardCaptureParams.viewProjectionMatrices[cardIndex] = meshBatch.localToCardMatrices[cardIndex];
                cardCaptureParams.viewportInfos[cardIndex] = CalcViewportInfo(meshBatch, cardIndex);
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

    public Vector4 CalcViewportInfo(CardCaptureMeshBatch meshBatch, int cardIndex)
    {
        Vector4 cardSizeAndOffset = meshBatch.cardUVTransforms[cardIndex];
        Vector4 uvTransform = cardSizeAndOffset / (float)m_AtlasResolution;

        // @TODO: dynamic sparse quad tree allocation
        // padding 1 texel
        float paddingScale = (meshBatch.resolution - 1.0f) / (float)meshBatch.resolution;

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

    public Vector4 AllocateCardUVTransform(CardCaptureMeshBatch meshBatch, int cardIndex)
    {
        if (USE_QUAD_TREE == 1)
        {
            QuadTreeNode node = m_SurfaceCacheAtlasAllocator.AllocateElement(meshBatch.resolution);

            Vector4 result = new Vector4(node.size, node.size, node.min.x, node.min.y);
            return result;
        }
        else
        {
            // TODO: dynamic sparse quad tree allocation
            int numCardsInXY = m_AtlasResolution / meshBatch.resolution;

            int indexInAtlas = meshBatch.objectId * meshBatch.cardCount + cardIndex;
            float indexInAtlasX = indexInAtlas % numCardsInXY;
            float indexInAtlasY = indexInAtlas / numCardsInXY;

            float sizeX = meshBatch.resolution;
            float sizeY = meshBatch.resolution;

            // map [0, 1] to [-1, 1]
            float offsetX = indexInAtlasX * meshBatch.resolution;
            float offsetY = indexInAtlasY * meshBatch.resolution;

            // xy: scale, zw: offset
            Vector4 result = new Vector4(sizeX, sizeY, offsetX, offsetY);
            return result;
        }
    }

    void AllocateMeshCard(ObjectInfo objectInfo, List<GameObject> objects, List<Mesh> meshes)
    {
        CardCaptureMeshBatch meshBatch = new CardCaptureMeshBatch();
        meshBatch.localToCardMatrices = new List<Matrix4x4>();
        meshBatch.cardUVTransforms = new List<Vector4>();

        meshBatch.objectId = objectInfo.objectId;
        meshBatch.mesh = meshes[objectInfo.meshId];

        meshBatch.cardCount = 6;

        // 1. calculate card capture mvp matrix
        Vector3 localBoundsCenter = (objectInfo.localBoundsMax + objectInfo.localBoundsMin) * 0.5f;
        Vector3 localBoundsSize = (objectInfo.localBoundsMax - objectInfo.localBoundsMin) * (1.0f + 1e-3f);
        float maxDimension = Mathf.Max(localBoundsSize.x, Mathf.Max(localBoundsSize.y, localBoundsSize.z));

        for (int cardIndex = 0; cardIndex < meshBatch.cardCount; cardIndex++)
        {
            Matrix4x4 localToCard = CalcViewProjectionMatrix(localBoundsCenter, maxDimension, cardIndex);
            meshBatch.localToCardMatrices.Add(localToCard);
        }

        // 2. calculate card resolution based on object's size
        if (USE_QUAD_TREE == 1)
        {
            Vector3 localSizeXYZ = objectInfo.localBoundsMax - objectInfo.localBoundsMin;
            Vector3 worldScale = objects[objectInfo.objectId].GetComponent<MeshFilter>().transform.lossyScale;

            float worldSize = 0;
            worldSize = Mathf.Max(worldSize, worldScale.x * localSizeXYZ.x);
            worldSize = Mathf.Max(worldSize, worldScale.y * localSizeXYZ.y);
            worldSize = Mathf.Max(worldSize, worldScale.z * localSizeXYZ.z);

            float cardSizef = worldSize / 0.25f;    // 4 texel per meter
            int cardSize = Mathf.NextPowerOfTwo((int)cardSizef);
            meshBatch.resolution = Mathf.Clamp(cardSize, m_SurfaceCacheAtlasAllocator.GetMinNodeSize(), m_SurfaceCacheAtlasAllocator.GetMaxNodeSize());
        }
        else
        {
            meshBatch.resolution = 32;
        }

        // 3. allocate space in mesh card atlas
        for (int cardIndex = 0; cardIndex < meshBatch.cardCount; cardIndex++)
        {
            Vector4 cardSizeAndOffset = AllocateCardUVTransform(meshBatch, cardIndex);
            meshBatch.cardUVTransforms.Add(cardSizeAndOffset);
        }

        m_CaptureMeshBatches.Add(meshBatch);
    }

    void ReleaseMeshCard(CardCaptureMeshBatch meshBatch)
    {
        for (int cardIndex = 0; cardIndex < meshBatch.cardCount; cardIndex++)
        {
            Vector4 cardSizeAndOffset = meshBatch.cardUVTransforms[cardIndex];

            QuadTreeNode freeNode = new QuadTreeNode();
            freeNode.size = (int)cardSizeAndOffset.x;    // x == y always
            freeNode.min = new Vector2Int((int)cardSizeAndOffset.z, (int)cardSizeAndOffset.w);
            freeNode.max = freeNode.min + new Vector2Int((int)cardSizeAndOffset.x, (int)cardSizeAndOffset.y);
            freeNode.center = (freeNode.max + freeNode.min) / 2;

            m_SurfaceCacheAtlasAllocator.ReleaseElement(freeNode);
        }
    }
}