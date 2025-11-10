using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.GlobalIllumination;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class MiraiGIRadianceCache
{
    public uint frameNumberRenderThread;
    int m_CheckerBoardSize = 2;

    ComputeBuffer m_ValidVoxelCounter;
    ComputeBuffer m_ValidVoxelBuffer;
    ComputeBuffer m_VoxelLightingIndirectArgs;

    RenderTexture m_VoxelPoolRadiance;

    ComputeShader m_VoxelLightingCS;
    ComputeShader m_VoxelPoolInitCS;

    // init date
    int[] m_ValidVoxelCounterInitData;
    int[] m_ValidVoxelBufferInitData;

    public RenderTexture GetVoxelPoolRadiance() => m_VoxelPoolRadiance;

    public void Init()
    {
        frameNumberRenderThread = 0;

        m_VoxelLightingCS = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/Shaders/MiraiGI/VoxelLighting/VoxelLighting.compute");
        m_VoxelPoolInitCS = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/Shaders/MiraiGI/VoxelClipmap/VoxelPoolInit.compute");
    }

    public void Release()
    {
        m_ValidVoxelCounter?.Release();
        m_ValidVoxelBuffer?.Release();
        m_VoxelLightingIndirectArgs?.Release();

        m_VoxelPoolRadiance?.Release();

        m_ValidVoxelCounter = null;
        m_ValidVoxelBuffer = null;
        m_VoxelLightingIndirectArgs = null;

        m_VoxelPoolRadiance = null;
    }

    public void Update(ref RenderingData renderingData, MiraiGIGPUScene scene)
    {
        CommandBuffer cmd = CommandBufferPool.Get("Update radiance cache");

        PrepareRenderResources(scene);

        for (int i = 0; i < MiraiGIClipmap.CASCADE_COUNT; i++)
        {
            PickValidVoxel(cmd, scene, i);

            if (GlobalSettings.Instance.freezeLightingForDebug > 0)
            {
                continue;
            }

            VoxelLighting(cmd, ref renderingData, scene, i);
        }

        Graphics.ExecuteCommandBuffer(cmd);
        CommandBufferPool.Release(cmd);

        // forbid overflow
        frameNumberRenderThread = (frameNumberRenderThread + 1) % 8;
    }

    public void PrepareRenderResources(MiraiGIGPUScene scene)
    {
        MiraiGIClipmap clipmap = scene.miraiGIClipmap;

        Vector3Int pageCountInXYZ = clipmap.voxelPageCountInXYZ;
        Vector3Int voxelRadiancePoolSize = pageCountInXYZ * GlobalShared.VOXEL_BLOCK_SIZE;
        voxelRadiancePoolSize.z *= 2;// for two side voxel
        // TODO: pool size change
        if (m_VoxelPoolRadiance == null)
        {
            m_VoxelPoolRadiance = new RenderTexture(voxelRadiancePoolSize.x, voxelRadiancePoolSize.y, 0, RenderTextureFormat.RGB111110Float);
            m_VoxelPoolRadiance.dimension = TextureDimension.Tex3D;
            m_VoxelPoolRadiance.volumeDepth = voxelRadiancePoolSize.z;
            m_VoxelPoolRadiance.enableRandomWrite = true;
            m_VoxelPoolRadiance.Create();

            CommandBuffer cmd = CommandBufferPool.Get("Init Voxel Pool Radiance");
            cmd.SetComputeTextureParam(m_VoxelPoolInitCS, 1, Shader.PropertyToID("_RWVoxelPoolRadiance"), m_VoxelPoolRadiance);
            cmd.DispatchCompute(m_VoxelPoolInitCS, 1, voxelRadiancePoolSize.x / 4, voxelRadiancePoolSize.y / 4, voxelRadiancePoolSize.z / 8);
            Graphics.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
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

        m_CheckerBoardSize = GlobalSettings.Instance.voxelLightingCheckerBoardSize;
        m_CheckerBoardSize = Mathf.NextPowerOfTwo(m_CheckerBoardSize);
        m_CheckerBoardSize = Mathf.Clamp(m_CheckerBoardSize, 1, 4);

        Vector3Int blockCountInXYZ = clipmap.voxelResolution / GlobalShared.VOXEL_BLOCK_SIZE;
        Vector3Int blockCountToLightInXYZ = blockCountInXYZ / m_CheckerBoardSize;
        int maxFrameNum = m_CheckerBoardSize * m_CheckerBoardSize * m_CheckerBoardSize;
        Vector3Int checkerBoardOffset = GlobalShared.Index1DTo3DLinear((int)frameNumberRenderThread % maxFrameNum, 
                                                                        new Vector3Int(m_CheckerBoardSize, m_CheckerBoardSize, m_CheckerBoardSize));

        int kernel = m_VoxelLightingCS.FindKernel("PickValidVoxel");

        cmd.SetComputeIntParam(m_VoxelLightingCS, Shader.PropertyToID("_CascadeIndex"), cascadeId);
        cmd.SetComputeVectorParam(m_VoxelLightingCS, Shader.PropertyToID("_CascadeResolution"), (Vector3) clipmap.voxelResolution);
        cmd.SetComputeVectorParam(m_VoxelLightingCS, Shader.PropertyToID("_CascadeMoveOffset"), (Vector3) clipmapInfo.moveOffset);
        cmd.SetComputeVectorParam(m_VoxelLightingCS, Shader.PropertyToID("_CheckerBoardInfo"), new Vector4(checkerBoardOffset.x, checkerBoardOffset.y, checkerBoardOffset.z, m_CheckerBoardSize));

        cmd.SetComputeTextureParam(m_VoxelLightingCS, kernel, Shader.PropertyToID("_VoxelBitOccupyClipmap"), clipmap.GetVoxelMap());

        cmd.SetComputeBufferParam(m_VoxelLightingCS, kernel, Shader.PropertyToID("_RWValidVoxelCounter"), m_ValidVoxelCounter);
        cmd.SetComputeBufferParam(m_VoxelLightingCS, kernel, Shader.PropertyToID("_RWValidVoxelBuffer"), m_ValidVoxelBuffer);

        cmd.DispatchCompute(m_VoxelLightingCS, kernel, blockCountToLightInXYZ.x / 4, blockCountToLightInXYZ.y / 4, blockCountToLightInXYZ.z / 4);
    }

    public void VoxelLighting(CommandBuffer cmd, ref RenderingData renderingData, MiraiGIGPUScene scene, int cascadeId)
    {
        MiraiGIClipmap clipmap = scene.miraiGIClipmap;
        MiraiGICascadeInfo clipmapInfo = clipmap.cascadeInfos[cascadeId];
        MiraiGICascadeInfo[] cascadeInfos = clipmap.cascadeInfos;

        // get main light
        GameObject mainLightObject = GameObject.Find("Directional Light");
        Light mainLight = mainLightObject.GetComponent<Light>();
        Vector3 mainLightDirection = mainLight.transform.forward * -1;
        Color mainLightColor = mainLight.color;

        // get main light shadow
        Matrix4x4[] worldToShadowMatrices = new Matrix4x4[4];
        Vector4[] shadowBounds = new Vector4[4];
        RenderTexture shadowDepthTexture = null;
        if (mainLight)
        {
            GetMainLightShadowInfos(mainLight, ref renderingData, out worldToShadowMatrices, out shadowBounds, out shadowDepthTexture);
        }

        Vector4[] cascadeCenterArray = new Vector4[MiraiGIClipmap.MAX_CASCADE_COUNT];
        Vector4[] cascadeSizeArray = new Vector4[MiraiGIClipmap.MAX_CASCADE_COUNT];
        Vector4[] cascadeMoveOffsetArray = new Vector4[MiraiGIClipmap.MAX_CASCADE_COUNT];

        for (int cascadeIndex = 0; cascadeIndex < cascadeInfos.Length; cascadeIndex++)
        {
            MiraiGICascadeInfo cascadeInfo = cascadeInfos[cascadeIndex];
            cascadeCenterArray[cascadeIndex] = cascadeInfo.cascadeCenter;
            cascadeSizeArray[cascadeIndex] = cascadeInfo.cascadeSize;
            cascadeMoveOffsetArray[cascadeIndex] = (Vector3)cascadeInfo.moveOffset;
        }

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

            cmd.SetComputeVectorParam(m_VoxelLightingCS, Shader.PropertyToID("_VoxelPageCountInXYZ"), (Vector3)clipmap.voxelPageCountInXYZ);
            cmd.SetComputeIntParam(m_VoxelLightingCS, Shader.PropertyToID("_CascadeCount"), MiraiGIClipmap.CASCADE_COUNT);
            cmd.SetComputeVectorArrayParam(m_VoxelLightingCS, Shader.PropertyToID("_CascadeCenterArray"), cascadeCenterArray);
            cmd.SetComputeVectorArrayParam(m_VoxelLightingCS, Shader.PropertyToID("_CascadeSizeArray"), cascadeSizeArray);
            cmd.SetComputeVectorArrayParam(m_VoxelLightingCS, Shader.PropertyToID("_CascadeMoveOffsetArray"), cascadeMoveOffsetArray);

            cmd.SetComputeMatrixParam(m_VoxelLightingCS, Shader.PropertyToID("_CameraViewMatrix"), Camera.main.worldToCameraMatrix);
            cmd.SetComputeVectorParam(m_VoxelLightingCS, Shader.PropertyToID("_MainLightDirection"), mainLightDirection);
            cmd.SetComputeVectorParam(m_VoxelLightingCS, Shader.PropertyToID("_MainLightColor"), mainLightColor);
            cmd.SetComputeFloatParam(m_VoxelLightingCS, Shader.PropertyToID("_ShadowRayMaxDistance"), GlobalSettings.Instance.shadowRayMaxDistance);
            cmd.SetComputeIntParam(m_VoxelLightingCS, Shader.PropertyToID("_ShadowRayBoostClipmapOffset"), GlobalSettings.Instance.shadowRayBoostClipmapOffset);
            cmd.SetComputeIntParam(m_VoxelLightingCS, Shader.PropertyToID("_CSMNumCascades"), 4);
            cmd.SetComputeVectorArrayParam(m_VoxelLightingCS, Shader.PropertyToID("_CSMShadowBounds"), shadowBounds);
            cmd.SetComputeMatrixArrayParam(m_VoxelLightingCS, Shader.PropertyToID("_CSMWorldToShadowMatrices"), worldToShadowMatrices);

            cmd.SetComputeBufferParam(m_VoxelLightingCS, kernel, Shader.PropertyToID("_ValidVoxelCounter"), m_ValidVoxelCounter);
            cmd.SetComputeBufferParam(m_VoxelLightingCS, kernel, Shader.PropertyToID("_ValidVoxelBuffer"), m_ValidVoxelBuffer);

            cmd.SetComputeTextureParam(m_VoxelLightingCS, kernel, Shader.PropertyToID("_ShadowDepthTexture"), shadowDepthTexture);
            cmd.SetComputeTextureParam(m_VoxelLightingCS, kernel, Shader.PropertyToID("_VoxelPoolBaseColor"), clipmap.GetVoxelPoolBaseColor());
            cmd.SetComputeTextureParam(m_VoxelLightingCS, kernel, Shader.PropertyToID("_VoxelPoolNormal"), clipmap.GetVoxelPoolNormal());
            cmd.SetComputeTextureParam(m_VoxelLightingCS, kernel, Shader.PropertyToID("_VoxelPoolEmissive"), clipmap.GetVoxelPoolEmissive());
            cmd.SetComputeTextureParam(m_VoxelLightingCS, kernel, Shader.PropertyToID("_VoxelPageClipmap"), clipmap.GetVoxelPageClipmap());
            cmd.SetComputeTextureParam(m_VoxelLightingCS, kernel, Shader.PropertyToID("_VoxelBitOccupyClipmap"), clipmap.GetVoxelMap());
            cmd.SetComputeTextureParam(m_VoxelLightingCS, kernel, Shader.PropertyToID("_RWVoxelPoolRadiance"), m_VoxelPoolRadiance);

            cmd.DispatchCompute(m_VoxelLightingCS, kernel, m_VoxelLightingIndirectArgs, 0);
        }
    }

    void GetMainLightShadowInfos(Light light, ref RenderingData renderingData,
        out Matrix4x4[] outWorldToShadowMatrices, out Vector4[] outShadowBounds, out RenderTexture outShadowDepthTexture)
    {
        outWorldToShadowMatrices = new Matrix4x4[4];
        outShadowBounds = new Vector4[4];

        Light mainLight = GameObject.Find("Directional Light").GetComponent<Light>();
        int shadowLightIndex = renderingData.lightData.mainLightIndex;
        int renderTargetWidth = renderingData.shadowData.mainLightShadowmapWidth;
        int renderTargetHeight = renderingData.shadowData.mainLightShadowmapHeight;
        int shadowResolution = ShadowUtils.GetMaxTileResolutionInAtlas(renderingData.shadowData.mainLightShadowmapWidth,
                renderingData.shadowData.mainLightShadowmapHeight, 4);

        ShadowSliceData[] cascadeSlices = new ShadowSliceData[4];
        Vector4[] cascadeSplitDistances = new Vector4[4];

        for (int cascadeIndex = 0; cascadeIndex < 4; cascadeIndex++)
        {
            bool success = ShadowUtils.ExtractDirectionalLightMatrix(ref renderingData.cullResults, ref renderingData.shadowData,
                    shadowLightIndex, cascadeIndex, renderTargetWidth, renderTargetHeight, shadowResolution, mainLight.shadowNearPlane,
                    out cascadeSplitDistances[cascadeIndex], out cascadeSlices[cascadeIndex]);

            if (success)
            {
                // TODO: check reverse is right or not in unity?

                // world to shadow matrices
                outWorldToShadowMatrices[3 - cascadeIndex] = cascadeSlices[cascadeIndex].shadowTransform;
                // shadow bounds
                outShadowBounds[3 - cascadeIndex] = outShadowBounds[cascadeIndex];
            }
        }

        outShadowDepthTexture = (RenderTexture) Shader.GetGlobalTexture("_MainLightShadowmapTexture");
    }
}