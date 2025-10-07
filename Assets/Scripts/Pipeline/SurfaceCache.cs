using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Color = UnityEngine.Color;

public struct CardCaptureParams
{
    public Vector3      cardCenter;
    public Vector3      cardSize;
    public Matrix4x4[]  viewProjectionMatrices;
    public float[]  cardOrientations;
    public Vector4[]    viewportInfos;
    public int          useInstance;
};

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
    int m_PerObjectResolution = 128;
    int m_AtlasResolution = 2048;

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
    }

    public void CaptureSurfaceCache(List<ObjectInfo> objectsInfo, List<GameObject> objects, List<Mesh> meshes)
    {
        CommandBuffer cmd = CommandBufferPool.Get("Surface Cache Capture");
        Shader surfaceCacheShader = Shader.Find("Mirai/SurfaceCacheCapture");

        cmd.SetRenderTarget(m_SurfaceCacheRenderTargets, m_DepthStencil);
        cmd.ClearRenderTarget(true, true, Color.black);

        // capture surface cache per object
        foreach (var objInfo in objectsInfo)
        {
            Material captureMaterial = new Material(surfaceCacheShader);
            captureMaterial.enableInstancing = true;

            Mesh mesh = meshes[objInfo.meshId];
            Material[] mat = objects[objInfo.objectId].GetComponent<MeshRenderer>().sharedMaterials;
            int subMeshCount = mesh.subMeshCount;

            Vector3 localBoundsSize = (objInfo.worldBoundsMax - objInfo.worldBoundsMin);
            float maxDimension = Mathf.Max(localBoundsSize.x, Mathf.Max(localBoundsSize.y, localBoundsSize.z));

            CardCaptureParams cardCaptureParams = new CardCaptureParams();
            cardCaptureParams.useInstance = 1;
            cardCaptureParams.cardCenter = (objInfo.localBoundsMin + objInfo.localBoundsMax) / 2;
            cardCaptureParams.cardSize = new Vector3(maxDimension, maxDimension, maxDimension);
            cardCaptureParams.viewProjectionMatrices = new Matrix4x4[6];
            cardCaptureParams.cardOrientations = new float[6];
            cardCaptureParams.viewportInfos = new Vector4[6];

            for (int cubeFace = 0; cubeFace < 6; cubeFace++)
            {
                cardCaptureParams.viewProjectionMatrices[cubeFace] = CalcViewProjectionMatrix(cardCaptureParams.cardCenter, maxDimension, cubeFace).transpose;
                cardCaptureParams.cardOrientations[cubeFace] = cubeFace / 2;
                cardCaptureParams.viewportInfos[cubeFace] = CalcViewportInfo(objInfo.objectId, cubeFace);
            }

            Matrix4x4[] identityMats = new Matrix4x4[6];
            for (int i = 0; i < subMeshCount; i++)
            {
                captureMaterial.SetColor("_BaseColor", mat[i].GetColor("_BaseColor"));
                captureMaterial.SetTexture("_BaseMap", mat[i].GetTexture("_BaseMap"));
                captureMaterial.SetColor("_EmissionColor", mat[i].GetColor("_EmissionColor"));
                captureMaterial.SetTexture("_EmissionMap", mat[i].GetTexture("_EmissionMap"));
                captureMaterial.SetTexture("_NormalMap", mat[i].GetTexture("_DetailNormalMap"));

                captureMaterial.SetVector("_CardCenter", cardCaptureParams.cardCenter);
                captureMaterial.SetVector("_CardSize", cardCaptureParams.cardSize);
                captureMaterial.SetMatrixArray("_ViewProjectionMatrices", cardCaptureParams.viewProjectionMatrices);
                captureMaterial.SetFloatArray("_CardOrientations", cardCaptureParams.cardOrientations);
                captureMaterial.SetVectorArray("_ViewportInfos", cardCaptureParams.viewportInfos);
                captureMaterial.SetInt("_UseInstance", cardCaptureParams.useInstance);
                
                cmd.DrawMeshInstanced(mesh, i, captureMaterial, 0, identityMats, 6);
            }
        }

        Graphics.ExecuteCommandBuffer(cmd);
        cmd.Release();
    }

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
        if (SystemInfo.usesReversedZBuffer)
        {
            viewMatrix[2, 0] = -viewMatrix[2, 0];
            viewMatrix[2, 1] = -viewMatrix[2, 1];
            viewMatrix[2, 2] = -viewMatrix[2, 2];
            viewMatrix[2, 3] = -viewMatrix[2, 3];
        }
        Matrix4x4 projectionMatrix = Matrix4x4.Ortho(-halfSize, halfSize, -halfSize, halfSize, -halfSize, halfSize);

        return projectionMatrix * viewMatrix;
    }

    public Vector4 CalcViewportInfo(int objectId, int cardIndex)
    {
        Vector4 uvTransform = CalcCardUVTransform(objectId, cardIndex);

        // @TODO: dynamic sparse quad tree allocation
        // padding 1 texel
        float paddingScale = (m_PerObjectResolution - 1.0f) / m_PerObjectResolution;

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

    public Vector4 CalcCardUVTransform(int objectId, int cardIndex)
    {
        // TODO: dynamic sparse quad tree allocation
        int numCardsInXY = m_AtlasResolution / m_PerObjectResolution;

        int indexInAtlas = objectId * 6 + cardIndex;
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

    public void Release()
    {
        foreach (var tex in m_SurfaceCacheAtlas)
        {
            tex?.Release();
        }
    }
}