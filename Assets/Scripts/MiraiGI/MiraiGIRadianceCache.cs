using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.GlobalIllumination;
using UnityEngine.Rendering;

public class MiraiGIRadianceCache
{
    public uint frameNumberRenderThread;
    int m_CheckerBoardSize = 2;

    ComputeBuffer m_ValidVoxelCounter;
    ComputeBuffer m_ValidVoxelBuffer;
    ComputeBuffer m_VoxelLightingIndirectArgs;

    RenderTexture m_VoxelRadiancePool;

    ComputeShader m_VoxelLightingCS;

    // init date
    int[] m_ValidVoxelCounterInitData;
    int[] m_ValidVoxelBufferInitData;

    public RenderTexture GetVoxelRadiancePool() => m_VoxelRadiancePool;

    public void Init()
    {
        m_VoxelLightingCS = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/Shaders/MiraiGI/VoxelLighting/VoxelLighting.compute");
    }

    public void Release()
    {
        m_ValidVoxelCounter?.Release();
        m_ValidVoxelBuffer?.Release();
        m_VoxelLightingIndirectArgs?.Release();

        m_VoxelRadiancePool?.Release();

        m_ValidVoxelCounter = null;
        m_ValidVoxelBuffer = null;
        m_VoxelLightingIndirectArgs = null;

        m_VoxelRadiancePool = null;
    }

    public void Update(MiraiGIGPUScene scene)
    {
        CommandBuffer cmd = CommandBufferPool.Get("Update radiance cache");

        PrepareRenderResources(scene);

        for (int i = 0; i < MiraiGIClipmap.CASCADE_COUNT; i++)
        {
            PickValidVoxel(cmd, scene, i);
            VoxelLighting(cmd, scene, i);
        }

        Graphics.ExecuteCommandBuffer(cmd);
        CommandBufferPool.Release(cmd);
    }

    public void PrepareRenderResources(MiraiGIGPUScene scene)
    {
        MiraiGIClipmap clipmap = scene.miraiGIClipmap;

        Vector3Int pageCountInXYZ = clipmap.voxelPagePoolSize;
        Vector3Int voxelRadiancePoolSize = pageCountInXYZ * GlobalShared.VOXEL_BLOCK_SIZE;
        // TODO: pool size change
        if (m_VoxelRadiancePool == null)
        {
            m_VoxelRadiancePool = new RenderTexture(voxelRadiancePoolSize.x, voxelRadiancePoolSize.y, 0, RenderTextureFormat.ARGBFloat);
            m_VoxelRadiancePool.dimension = TextureDimension.Tex3D;
            m_VoxelRadiancePool.volumeDepth = voxelRadiancePoolSize.z;
            m_VoxelRadiancePool.enableRandomWrite = true;
            m_VoxelRadiancePool.Create();
        }

        if (m_ValidVoxelCounter == null)
        {
            m_ValidVoxelCounter = new ComputeBuffer(1, sizeof(int));
            m_ValidVoxelCounterInitData = new int[1] { 0 };
        }

        if (m_ValidVoxelBuffer == null)
        {
            Vector3Int blockCountInXYZ = clipmap.voxelResolution / GlobalShared.VOXEL_BLOCK_SIZE;
            int blockCountToLight1D = blockCountInXYZ.x * blockCountInXYZ.y * blockCountInXYZ.z;
            m_ValidVoxelBuffer = new ComputeBuffer(blockCountToLight1D * GlobalShared.VOXEL_COUNT_PER_BLOCK, sizeof(int), ComputeBufferType.Structured);
            m_ValidVoxelBufferInitData = new int[blockCountToLight1D * GlobalShared.VOXEL_COUNT_PER_BLOCK];
            for (int i = 0; i < blockCountToLight1D * GlobalShared.VOXEL_COUNT_PER_BLOCK; i++)
            {
                m_ValidVoxelBufferInitData[i] = 0;
            }
        }

        if (m_VoxelLightingIndirectArgs == null)
        {
            m_VoxelLightingIndirectArgs = new ComputeBuffer(3, sizeof(int), ComputeBufferType.IndirectArguments);
        }

        // flush data per frame
        m_ValidVoxelCounter.SetData(m_ValidVoxelCounterInitData);
        m_ValidVoxelBuffer.SetData(m_ValidVoxelBufferInitData);
    }

    public void PickValidVoxel(CommandBuffer cmd, MiraiGIGPUScene scene, int cascadeId)
    {
        MiraiGIClipmap clipmap = scene.miraiGIClipmap;
        MiraiGICascadeInfo clipmapInfo = clipmap.cascadeInfos[cascadeId];

        Vector3Int blockCountInXYZ = clipmap.voxelResolution / GlobalShared.VOXEL_BLOCK_SIZE;

        int kernel = m_VoxelLightingCS.FindKernel("PickValidVoxel");

        cmd.SetComputeIntParam(m_VoxelLightingCS, Shader.PropertyToID("_CascadeIndex"), cascadeId);
        cmd.SetComputeVectorParam(m_VoxelLightingCS, Shader.PropertyToID("_ClipmapOffset"), new Vector3(0, 0, blockCountInXYZ.z * cascadeId));
        cmd.SetComputeVectorParam(m_VoxelLightingCS, Shader.PropertyToID("_CascadeResolution"), (Vector3) clipmap.voxelResolution);
        cmd.SetComputeVectorParam(m_VoxelLightingCS, Shader.PropertyToID("_CascadeMoveOffset"), (Vector3) clipmapInfo.moveOffset);

        cmd.SetComputeTextureParam(m_VoxelLightingCS, kernel, Shader.PropertyToID("_VoxelBitOccupyClipmap"), clipmap.GetVoxelMap());

        cmd.SetComputeBufferParam(m_VoxelLightingCS, kernel, Shader.PropertyToID("_RWValidVoxelCounter"), m_ValidVoxelCounter);
        cmd.SetComputeBufferParam(m_VoxelLightingCS, kernel, Shader.PropertyToID("_RWValidVoxelBuffer"), m_ValidVoxelBuffer);

        cmd.DispatchCompute(m_VoxelLightingCS, kernel, blockCountInXYZ.x / 2, blockCountInXYZ.y / 2, blockCountInXYZ.z / 2);
    }

    public void VoxelLighting(CommandBuffer cmd, MiraiGIGPUScene scene, int cascadeId)
    {
        MiraiGIClipmap clipmap = scene.miraiGIClipmap;
        MiraiGICascadeInfo clipmapInfo = clipmap.cascadeInfos[cascadeId];

        GameObject lightObject = GameObject.Find("Directional Light");
        Light directionalLight = lightObject.GetComponent<Light>();
        Vector3 mainLightDirection = directionalLight.transform.forward;
        Color mainLightColor = directionalLight.color;

        // 1. build indirect args cause valid voxel num is unpredictable
        {
            int kernel = m_VoxelLightingCS.FindKernel("BuildVoxelLightingIndirectArgs");

            cmd.SetComputeIntParam(m_VoxelLightingCS, Shader.PropertyToID("_ThreadsCountForVoxelLighting"), 8);
            cmd.SetComputeBufferParam(m_VoxelLightingCS, kernel, Shader.PropertyToID("_ValidVoxelCounter"), m_ValidVoxelCounter);
            cmd.SetComputeBufferParam(m_VoxelLightingCS, kernel, Shader.PropertyToID("_RWIndirectArgs"), m_VoxelLightingIndirectArgs);

            cmd.DispatchCompute(m_VoxelLightingCS, kernel, 1, 1, 1);
        }

        // 2. calculate lighting for each voxel
        {
            int kernel = m_VoxelLightingCS.FindKernel("VoxelLighting");

            cmd.SetComputeIntParam(m_VoxelLightingCS, Shader.PropertyToID("_CascadeIndex"), cascadeId);
            cmd.SetComputeVectorParam(m_VoxelLightingCS, Shader.PropertyToID("_CascadeResolution"), (Vector3) clipmap.voxelResolution);
            cmd.SetComputeVectorParam(m_VoxelLightingCS, Shader.PropertyToID("_CascadeMoveOffset"), (Vector3) clipmapInfo.moveOffset);
            cmd.SetComputeVectorParam(m_VoxelLightingCS, Shader.PropertyToID("_VoxelPagePoolSize"), (Vector3) clipmap.voxelPagePoolSize);
            cmd.SetComputeVectorParam(m_VoxelLightingCS, Shader.PropertyToID("_MainLightDirection"), mainLightDirection);
            cmd.SetComputeVectorParam(m_VoxelLightingCS, Shader.PropertyToID("_MainLightColor"), mainLightColor);
            cmd.SetComputeIntParam(m_VoxelLightingCS, Shader.PropertyToID("_SurfaceCacheAtlasResolution"), scene.surfaceCache.atlasResolution);

            cmd.SetComputeBufferParam(m_VoxelLightingCS, kernel, Shader.PropertyToID("_ObjectsInfo"), scene.GPUSceneData.objectInfoBuffer);
            cmd.SetComputeBufferParam(m_VoxelLightingCS, kernel, Shader.PropertyToID("_ValidVoxelCounter"), m_ValidVoxelCounter);
            cmd.SetComputeBufferParam(m_VoxelLightingCS, kernel, Shader.PropertyToID("_ValidVoxelBuffer"), m_ValidVoxelBuffer);

            cmd.SetComputeTextureParam(m_VoxelLightingCS, kernel, Shader.PropertyToID("_VoxelPagePool"), clipmap.GetVoxelPagePool());
            cmd.SetComputeTextureParam(m_VoxelLightingCS, kernel, Shader.PropertyToID("_VoxelPageClipmap"), clipmap.GetVoxelPageClipmap());
            cmd.SetComputeTextureParam(m_VoxelLightingCS, kernel, Shader.PropertyToID("_SurfaceCacheBaseColor"), scene.surfaceCache.GetSurfaceCacheTexture((int)CardCaptureRTSlot.BaseColor));
            cmd.SetComputeTextureParam(m_VoxelLightingCS, kernel, Shader.PropertyToID("_SurfaceCacheNormal"), scene.surfaceCache.GetSurfaceCacheTexture((int)CardCaptureRTSlot.Normal));
            cmd.SetComputeTextureParam(m_VoxelLightingCS, kernel, Shader.PropertyToID("_SurfaceCacheEmissive"), scene.surfaceCache.GetSurfaceCacheTexture((int)CardCaptureRTSlot.Emissive));
            cmd.SetComputeTextureParam(m_VoxelLightingCS, kernel, Shader.PropertyToID("_RWVoxelRadiancePool"), m_VoxelRadiancePool);

            cmd.DispatchCompute(m_VoxelLightingCS, kernel, m_VoxelLightingIndirectArgs, 0);
        }
    }
}