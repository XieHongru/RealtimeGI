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

public class CardCaptureMeshBatch
{
    public int objectId;
    public Mesh mesh;

    public int cardCount;
    public int resolution;
    public List<Matrix4x4> localToCardMatrices;
    public List<Vector4> localToAtlasUVTransforms;
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

    List<CardCaptureMeshBatch> m_CaptureMeshBatches;

    ComputeBuffer m_CardMatrixOffsetUploadBuffer;
    ComputeBuffer m_CardMatrixUploadBuffer;
    ComputeBuffer m_CardMatrixBuffer;

    ComputeShader m_CardInfosSyncCS;

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

        m_CardMatrixOffsetUploadBuffer = new ComputeBuffer(MAX_OBJECT_COUNT, sizeof(int), ComputeBufferType.Default);
        m_CardMatrixUploadBuffer = new ComputeBuffer(MAX_OBJECT_COUNT * MAX_CARD_PER_MESH, sizeof(float) * 16, ComputeBufferType.Raw);
        m_CardMatrixBuffer = new ComputeBuffer(MAX_OBJECT_COUNT * MAX_CARD_PER_MESH, sizeof(float) * 16, ComputeBufferType.Structured);

        m_CardInfosSyncCS = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/Shaders/ObjectCapture/SurfaceCacheInfoSync.compute");
    }

    public void Release()
    {
        foreach (var tex in m_SurfaceCacheAtlas)
        {
            tex?.Release();
        }
        m_DepthStencil.Release();
    }

    public void SyncCardInfosToGPU(CommandBuffer cmd, int objectCount)
    {
        // 1. build data on cpu
        int uploadDataOffset = 0;
        List<int> cardOffsets = new List<int>();
        Matrix4x4[] localToCardMatrices = new Matrix4x4[objectCount * MAX_CARD_PER_MESH];

        foreach (CardCaptureMeshBatch meshBatch in m_CaptureMeshBatches)
        {
            // simple linear allocator
            int cardMatrixOffset = meshBatch.objectId * MAX_CARD_PER_MESH;
            cardOffsets.Add(cardMatrixOffset);

            for (int cardIndex = 0; cardIndex < meshBatch.cardCount; cardIndex++)
            {
                localToCardMatrices[uploadDataOffset + cardIndex] = meshBatch.localToCardMatrices[cardIndex];
            }

            uploadDataOffset += MAX_CARD_PER_MESH;
        }

        // 2. upload per-object card's write offset
        {
            m_CardMatrixOffsetUploadBuffer.SetData(cardOffsets);
        }

        // 3. upload per-object card transform matrix
        {
            m_CardMatrixUploadBuffer.SetData(localToCardMatrices);
        }

        // 4. copy data from transient buffer to RW buffer
        {
            int kernel = m_CardInfosSyncCS.FindKernel("SurfaceInfoUpdate");

            cmd.SetComputeIntParam(m_CardInfosSyncCS, Shader.PropertyToID("_ObjectCount"), objectCount);
            cmd.SetComputeBufferParam(m_CardInfosSyncCS, kernel, Shader.PropertyToID("_RWCardMatrixBuffer"), m_CardMatrixBuffer);
            cmd.SetComputeBufferParam(m_CardInfosSyncCS, kernel, Shader.PropertyToID("_CardMatrixOffsetUploadBuffer"), m_CardMatrixOffsetUploadBuffer);
            cmd.SetComputeBufferParam(m_CardInfosSyncCS, kernel, Shader.PropertyToID("_CardMatrixUploadBuffer"), m_CardMatrixUploadBuffer);

            cmd.DispatchCompute(m_CardInfosSyncCS, kernel, Mathf.CeilToInt((float)objectCount / 8), 1, 1);
        }
    }

    public void CaptureSurfaceCache(List<ObjectInfo> objectsInfo, List<GameObject> objects, List<Mesh> meshes)
    {
        m_CaptureMeshBatches.Clear();
        // capture surface cache per object
        foreach (var objInfo in objectsInfo)
        {
            CardCaptureMeshBatch meshBatch = new CardCaptureMeshBatch();
            meshBatch.localToCardMatrices = new List<Matrix4x4>();
            meshBatch.localToAtlasUVTransforms = new List<Vector4>();

            meshBatch.objectId = objInfo.objectId;
            meshBatch.mesh = meshes[objInfo.meshId];

            Vector3 localBoundsCenter = (objInfo.localBoundsMax + objInfo.localBoundsMin) * 0.5f;
            Vector3 localBoundsSize = (objInfo.localBoundsMax - objInfo.localBoundsMin) * (1.0f + 1e-3f);
            float maxDimension = Mathf.Max(localBoundsSize.x, Mathf.Max(localBoundsSize.y, localBoundsSize.z));

            meshBatch.cardCount = objInfo.cardCount;
            meshBatch.resolution = objInfo.resolution;

            for (int cardIndex = 0; cardIndex < meshBatch.cardCount; cardIndex++)
            {
                Matrix4x4 localToCard = CalcViewProjectionMatrix(localBoundsCenter, maxDimension, cardIndex);
                meshBatch.localToCardMatrices.Add(localToCard);

                Vector4 localToAtlas = CalcViewportInfo(meshBatch.objectId, cardIndex, meshBatch.resolution, meshBatch.cardCount);
                meshBatch.localToAtlasUVTransforms.Add(localToAtlas);
            }

            m_CaptureMeshBatches.Add(meshBatch);
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
                cardCaptureParams.viewportInfos[cardIndex] = meshBatch.localToAtlasUVTransforms[cardIndex];
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

    public Vector4 CalcViewportInfo(int objectId, int cardIndex, int resolution, int cardCount)
    {
        Vector4 uvTransform = CalcCardUVTransform(objectId, cardIndex, resolution, cardCount);

        // @TODO: dynamic sparse quad tree allocation
        // padding 1 texel
        float paddingScale = (resolution - 1.0f) / resolution;

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

    public Vector4 CalcCardUVTransform(int objectId, int cardIndex, int resolution, int cardCount)
    {
        // TODO: dynamic sparse quad tree allocation
        int numCardsInXY = m_AtlasResolution / resolution;

        int indexInAtlas = objectId * cardCount + cardIndex;
        float indexInAtlasX = indexInAtlas % numCardsInXY;
        float indexInAtlasY = indexInAtlas / numCardsInXY;

        float cardSizeInUV = 1.0f / numCardsInXY;
        float scale = cardSizeInUV;

        // map [0, 1] to [-1, 1]
        float offsetX = indexInAtlasX * cardSizeInUV;
        float offsetY = indexInAtlasY * cardSizeInUV;

        // xy: scale, zw: offset
        Vector4 result = new Vector4(scale, scale, offsetX, offsetY);
        return result;
    }

    //Matrix4x4 CalcCardCaptureViewRotationMatrix(int cubeFace)
    //{
    //    Matrix4x4 result = Matrix4x4.identity;
    //    Vector3 xAxis = Vector3.right;
    //    Vector3 yAxis = Vector3.up;
    //    Vector3 zAxis = Vector3.forward;

    //    // vectors we will need for our basis
    //    Vector3 vUp = zAxis;
    //    Vector3 vDir;

    //    switch (cubeFace)
    //    {
    //        case 0:
    //            vDir = XAxis;
    //            break;
    //        case 1:
    //            vDir = -XAxis;
    //            break;
    //        case 2:
    //            vDir = YAxis;
    //            break;
    //        case 3:
    //            vDir = -YAxis;
    //            break;
    //        case 4:
    //            vUp = -YAxis;
    //            vDir = ZAxis;
    //            break;
    //        case 5:
    //            vUp = YAxis;
    //            vDir = -ZAxis;
    //            break;
    //    }

    //    // derive right vector
    //    FVector vRight(vUp ^vDir);
    //    // create matrix from the 3 axes
    //    Result = FBasisVectorMatrix(vRight, vUp, vDir, FVector::ZeroVector);

    //    return Result;
    //}

    //FMatrix CalcCardCaptureViewProjectionMatrix(FVector CardCenter, float Size, ECubeFace Face)
    //{
    //    float Width = Size;
    //    float Height = Size;
    //    float Depth = Size;

    //    float NearPlane = Depth * -0.5;
    //    float FarPlane = Depth * 0.5;
    //    float ZScale = 1.0f / (FarPlane - NearPlane);
    //    float ZOffset = -NearPlane;

    //    FViewMatrices::FMinimalInitializer CaptureViewInitOptions;
    //    CaptureViewInitOptions.ViewRotationMatrix = CalcCardCaptureViewRotationMatrix(Face);
    //    CaptureViewInitOptions.ViewOrigin = CardCenter;
    //    CaptureViewInitOptions.ProjectionMatrix = FReversedZOrthoMatrix(Width * 0.5, Height * 0.5, ZScale, ZOffset);
    //    // CaptureViewInitOptions.ProjectionMatrix = GetCubeProjectionMatrix(90.0 * 0.5f, 128, 0.1f);	// for debug
    //    FViewMatrices CaptureViewMatrices = FViewMatrices(CaptureViewInitOptions);

    //    return CaptureViewMatrices.GetViewProjectionMatrix();
    //}
}