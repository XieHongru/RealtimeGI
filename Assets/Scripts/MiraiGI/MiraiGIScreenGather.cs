using System;
using UnityEditor;
using UnityEditor.ShaderGraph.Drawing.Inspector.PropertyDrawers;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class MiraiGIScreenGatherResources
{
    
}

public class MiraiGIScreenGather
{
    int m_FrameNumber;

    Vector2Int m_SceneTextureRTSize;
    Vector2Int m_ScreenGatherRTSize;
    RenderTexture m_NormalDepthTexture;
    RenderTexture m_NormalDepthHistory;

    RenderTexture m_InitialSampleRadiance;
    RenderTexture m_InitialSampleHitInfo;

    // ping pong buffer between two frame
    RenderTexture[] m_TemporalReservoirDataA;
    RenderTexture[] m_TemporalReservoirDataB;
    RenderTexture[] m_TemporalReservoirDataC;
    RenderTexture[] m_TemporalReservoirDataD;

    RenderTexture m_SpatialReservoirDataA;
    RenderTexture m_SpatialReservoirDataB;
    RenderTexture m_SpatialReservoirDataC;
    RenderTexture m_SpatialReservoirDataD;

    ComputeBuffer m_VoxelTraceRayCounter;
    ComputeBuffer m_VoxelTraceRayCompactBuffer;
    ComputeBuffer m_VoxelTraceIndirectArgs;

    RenderTexture m_ScreenGatherOutputTexture;
    RenderTexture m_DiffuseIndirectHistory;
    RenderTexture m_SceneColorHistory;

    RenderTexture m_HiZBuffer;

    Matrix4x4 m_PrevClipMatrix;
    Matrix4x4 m_CurClipMatrix;

    ComputeShader m_HiZBufferGenerateCS;
    ComputeShader m_ScreenGatherCS;

    public void Init()
    {
        m_TemporalReservoirDataA = new RenderTexture[2];
        m_TemporalReservoirDataB = new RenderTexture[2];
        m_TemporalReservoirDataC = new RenderTexture[2];
        m_TemporalReservoirDataD = new RenderTexture[2];

        m_PrevClipMatrix = Matrix4x4.identity;
        m_CurClipMatrix = Matrix4x4.identity;

        m_HiZBufferGenerateCS = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/Shaders/MiraiGI/RayTracing/HiZBufferGenerate.compute");
        m_ScreenGatherCS = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/Shaders/MiraiGI/ScreenGather/ScreenGather.compute");
    }

    public void Release()
    {
        m_NormalDepthTexture?.Release();
        m_NormalDepthHistory?.Release();

        m_InitialSampleRadiance?.Release();
        m_InitialSampleHitInfo?.Release();

        for (int i = 0; i < 2; i++)
        {
            m_TemporalReservoirDataA[i]?.Release();
            m_TemporalReservoirDataB[i]?.Release();
            m_TemporalReservoirDataC[i]?.Release();
            m_TemporalReservoirDataD[i]?.Release();
        }

        m_VoxelTraceRayCounter?.Release();
        m_VoxelTraceRayCompactBuffer?.Release();
        m_VoxelTraceIndirectArgs?.Release();
        m_ScreenGatherOutputTexture?.Release();
        m_DiffuseIndirectHistory?.Release();
        m_SceneColorHistory?.Release();

        m_NormalDepthTexture = null;
        m_NormalDepthHistory = null;

        m_InitialSampleRadiance = null;
        m_InitialSampleHitInfo = null;

        for (int i = 0; i < 2; i++)
        {
            m_TemporalReservoirDataA[i] = null;
            m_TemporalReservoirDataB[i] = null;
            m_TemporalReservoirDataC[i] = null;
            m_TemporalReservoirDataD[i] = null;
        }

        m_VoxelTraceRayCounter = null;
        m_VoxelTraceRayCompactBuffer = null;
        m_VoxelTraceIndirectArgs = null;
        m_ScreenGatherOutputTexture = null;
        m_DiffuseIndirectHistory = null;
        m_VoxelTraceIndirectArgs = null;
    }

    public void Update(ref RenderingData renderingData, MiraiGIGPUScene scene)
    {
        CommandBuffer cmd = CommandBufferPool.Get("Screen Gather");

        PrepareRenderResources(ref renderingData, cmd, scene);
        NormalDepthDownsample(cmd, scene);
        InitialSampleScreenTrace(ref renderingData, cmd, scene);
        InitialSampleVoxelTrace(cmd, scene);
        ReservoirTemporalReuse(cmd, scene);
        ReservoirSpatialReuse(cmd, scene);
        ReservoirEvaluateIrradiance(cmd, scene);

        cmd.Blit(m_NormalDepthTexture, m_NormalDepthHistory);

        Graphics.ExecuteCommandBuffer(cmd);
        CommandBufferPool.Release(cmd);
    }

    public void VisualizeMiraiGIScreenGather(CommandBuffer cmd, MiraiGIGPUScene scene)
    {
        if (GlobalSettings.Instance.visualizeScreenGather <= 0)
            return;

        cmd.BeginSample("Visualize Screen Gather");

        MiraiGIClipmap clipmap = scene.miraiGIClipmap;

        cmd.Blit(m_ScreenGatherOutputTexture, clipmap.GetVisualizeColorTarget());

        cmd.EndSample("Visualize Screen Gather");
    }

    void PrepareRenderResources(ref RenderingData renderingData, CommandBuffer cmd, MiraiGIGPUScene scene)
    {
        m_FrameNumber = (int)scene.miraiGIRadianceCache.frameNumberRenderThread;

        int downsampleFactor = Mathf.Clamp(GlobalSettings.Instance.screenGatherDownsampleFactor, 1, 4);
        m_SceneTextureRTSize = new Vector2Int(renderingData.cameraData.cameraTargetDescriptor.width, renderingData.cameraData.cameraTargetDescriptor.height);
        m_ScreenGatherRTSize = m_SceneTextureRTSize / downsampleFactor;

        if (m_NormalDepthTexture == null)
        {
            m_NormalDepthTexture = new RenderTexture(m_ScreenGatherRTSize.x, m_ScreenGatherRTSize.y, 0, RenderTextureFormat.ARGBFloat);
            m_NormalDepthTexture.enableRandomWrite = true;
            m_NormalDepthTexture.Create();
        }

        if (m_NormalDepthHistory == null)
        {
            m_NormalDepthHistory = new RenderTexture(m_ScreenGatherRTSize.x, m_ScreenGatherRTSize.y, 0, RenderTextureFormat.ARGBFloat);
            m_NormalDepthHistory.enableRandomWrite = true;
            m_NormalDepthHistory.Create();
        }

        if (m_InitialSampleRadiance == null)
        {
            m_InitialSampleRadiance = new RenderTexture(m_ScreenGatherRTSize.x, m_ScreenGatherRTSize.y, 0, RenderTextureFormat.ARGBFloat);
            m_InitialSampleRadiance.enableRandomWrite = true;
            m_InitialSampleRadiance.Create();
        }

        if (m_InitialSampleHitInfo == null)
        {
            m_InitialSampleHitInfo = new RenderTexture(m_ScreenGatherRTSize.x, m_ScreenGatherRTSize.y, 0, RenderTextureFormat.ARGBFloat);
            m_InitialSampleHitInfo.enableRandomWrite = true;
            m_InitialSampleHitInfo.Create();
        }

        // reservoir
        if (m_TemporalReservoirDataA[0] == null)
        {
            for (int i = 0; i < 2; i++)
            {
                m_TemporalReservoirDataA[i] = new RenderTexture(m_ScreenGatherRTSize.x, m_ScreenGatherRTSize.y, 0, RenderTextureFormat.ARGBFloat);
                m_TemporalReservoirDataA[i].enableRandomWrite = true;
                m_TemporalReservoirDataA[i].Create();
                m_TemporalReservoirDataB[i] = new RenderTexture(m_ScreenGatherRTSize.x, m_ScreenGatherRTSize.y, 0, RenderTextureFormat.ARGBFloat);
                m_TemporalReservoirDataB[i].enableRandomWrite = true;
                m_TemporalReservoirDataB[i].Create();
                m_TemporalReservoirDataC[i] = new RenderTexture(m_ScreenGatherRTSize.x, m_ScreenGatherRTSize.y, 0, RenderTextureFormat.ARGB32);
                m_TemporalReservoirDataC[i].enableRandomWrite = true;
                m_TemporalReservoirDataC[i].Create();
                m_TemporalReservoirDataD[i] = new RenderTexture(m_ScreenGatherRTSize.x, m_ScreenGatherRTSize.y, 0, RenderTextureFormat.ARGB32);
                m_TemporalReservoirDataD[i].enableRandomWrite = true;
                m_TemporalReservoirDataD[i].Create();
            }
        }

        if (m_SpatialReservoirDataA == null)
        {
            m_SpatialReservoirDataA = new RenderTexture(m_ScreenGatherRTSize.x, m_ScreenGatherRTSize.y, 0, RenderTextureFormat.ARGBFloat);
            m_SpatialReservoirDataA.enableRandomWrite = true;
            m_SpatialReservoirDataA.Create();
            m_SpatialReservoirDataB = new RenderTexture(m_ScreenGatherRTSize.x, m_ScreenGatherRTSize.y, 0, RenderTextureFormat.ARGBFloat);
            m_SpatialReservoirDataB.enableRandomWrite = true;
            m_SpatialReservoirDataB.Create();
            m_SpatialReservoirDataC = new RenderTexture(m_ScreenGatherRTSize.x, m_ScreenGatherRTSize.y, 0, RenderTextureFormat.ARGBFloat);
            m_SpatialReservoirDataC.enableRandomWrite = true;
            m_SpatialReservoirDataC.Create();
            m_SpatialReservoirDataD = new RenderTexture(m_ScreenGatherRTSize.x, m_ScreenGatherRTSize.y, 0, RenderTextureFormat.ARGBFloat);
            m_SpatialReservoirDataD.enableRandomWrite = true;
            m_SpatialReservoirDataD.Create();
        }

        if (m_ScreenGatherOutputTexture == null)
        {
            m_ScreenGatherOutputTexture = new RenderTexture(m_ScreenGatherRTSize.x, m_ScreenGatherRTSize.y, 0, RenderTextureFormat.ARGBFloat);
            m_ScreenGatherOutputTexture.enableRandomWrite = true;
            m_ScreenGatherOutputTexture.Create();
        }

        if (m_SceneColorHistory == null)
        {
            m_SceneColorHistory = new RenderTexture(m_ScreenGatherRTSize.x, m_ScreenGatherRTSize.y, 0, RenderTextureFormat.ARGBFloat);
            m_SceneColorHistory.enableRandomWrite = true;
            m_SceneColorHistory.Create();
        }

        if (m_HiZBuffer == null)
        {
            int width = renderingData.cameraData.cameraTargetDescriptor.width;
            int height = renderingData.cameraData.cameraTargetDescriptor.height;

            m_HiZBuffer = new RenderTexture(width, height, 0, RenderTextureFormat.RFloat);
            m_HiZBuffer.useMipMap = true;
            m_HiZBuffer.autoGenerateMips = false;
            m_HiZBuffer.enableRandomWrite = true;
            m_HiZBuffer.filterMode = FilterMode.Point;
            m_HiZBuffer.Create();
        }
        {
            cmd.BeginSample("HiZ Generate");

            int width = renderingData.cameraData.cameraTargetDescriptor.width;
            int height = renderingData.cameraData.cameraTargetDescriptor.height;

            int kernel = m_HiZBufferGenerateCS.FindKernel("HiZBufferGenerate");

            cmd.SetComputeTextureParam(m_HiZBufferGenerateCS, kernel, Shader.PropertyToID("_DepthTexture"), Shader.GetGlobalTexture("_CameraDepthTexture"));

            for (int mip = 0; (width > 8 || height > 8) && mip <= GlobalShared.MAX_HZB_LEVEL; mip++)
            {
                cmd.SetComputeIntParam(m_HiZBufferGenerateCS, Shader.PropertyToID("_MipLevel"), mip);
                cmd.SetComputeTextureParam(m_HiZBufferGenerateCS, kernel, Shader.PropertyToID("_RWHiZBuffer"), m_HiZBuffer, mip);
                cmd.DispatchCompute(m_HiZBufferGenerateCS, kernel, Mathf.CeilToInt((float)width / 8), Mathf.CeilToInt((float)height / 8), 1);
                width = Mathf.Max(1, width >> 1);
                height = Mathf.Max(1, height >> 1);
            }

            cmd.EndSample("HiZ Generate");
        }

        if (m_VoxelTraceRayCounter == null)
        {
            m_VoxelTraceRayCounter = new ComputeBuffer(1, sizeof(int));
        }
        m_VoxelTraceRayCounter.SetData(new int[1] { 0 });

        if (m_VoxelTraceRayCompactBuffer == null)
        {
            m_VoxelTraceRayCompactBuffer = new ComputeBuffer(m_ScreenGatherRTSize.x * m_ScreenGatherRTSize.y, sizeof(int) * 2);
        }

        if (m_VoxelTraceIndirectArgs == null)
        {
            m_VoxelTraceIndirectArgs = new ComputeBuffer(3, sizeof(int), ComputeBufferType.IndirectArguments);
        }

        m_PrevClipMatrix = m_CurClipMatrix;
        m_CurClipMatrix = Camera.main.projectionMatrix * Camera.main.worldToCameraMatrix;
    }

    void NormalDepthDownsample(CommandBuffer cmd, MiraiGIGPUScene scene)
    {
        cmd.BeginSample("Normal Depth Downsample");

        int kernel = m_ScreenGatherCS.FindKernel("NormalDepthDownsample");

        cmd.SetComputeVectorParam(m_ScreenGatherCS, Shader.PropertyToID("_ScreenGatherRTSize"), (Vector2)m_ScreenGatherRTSize);
        cmd.SetComputeVectorParam(m_ScreenGatherCS, Shader.PropertyToID("_SceneTextureRTSize"), (Vector2)m_SceneTextureRTSize);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_GBufferNormal"), Shader.GetGlobalTexture("_GBuffer2"));
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_SceneDepthTexture"), Shader.GetGlobalTexture("_CameraDepthTexture"));
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_RWNormalDepthTexture"), m_NormalDepthTexture);

        cmd.DispatchCompute(m_ScreenGatherCS, kernel, Mathf.CeilToInt((float)m_ScreenGatherRTSize.x / 8), Mathf.CeilToInt((float)m_ScreenGatherRTSize.y / 8), 1);

        cmd.EndSample("Normal Depth Downsample");
    }

    void InitialSampleScreenTrace(ref RenderingData renderingData, CommandBuffer cmd, MiraiGIGPUScene scene)
    {
        cmd.BeginSample("Screen Trace");

        MiraiGIClipmap clipmap = scene.miraiGIClipmap;
        MiraiGIRadianceCache radianceCache = scene.miraiGIRadianceCache;

        int kernel = m_ScreenGatherCS.FindKernel("InitialSampleScreenTrace");

        cmd.Blit(renderingData.cameraData.renderer.cameraColorTargetHandle, m_SceneColorHistory);

        Matrix4x4 viewRotation = Camera.main.worldToCameraMatrix;
        viewRotation.m03 = viewRotation.m13 = viewRotation.m23 = 0;
        viewRotation.m30 = viewRotation.m31 = viewRotation.m32 = 0;
        viewRotation.m33 = 1;

        Matrix4x4 translatedWorldToClip = Camera.main.projectionMatrix * viewRotation;

        cmd.SetComputeVectorParam(m_ScreenGatherCS, Shader.PropertyToID("_CameraPosition"), Camera.main.transform.position);
        cmd.SetComputeMatrixParam(m_ScreenGatherCS, Shader.PropertyToID("_ProjMat"), Camera.main.projectionMatrix);
        cmd.SetComputeMatrixParam(m_ScreenGatherCS, Shader.PropertyToID("_TranslatedWorldToClip"), translatedWorldToClip);

        // ReconstructWorldPositionFromDepth params
        float near = Camera.main.nearClipPlane;
        float far = Camera.main.farClipPlane;
        Vector4 zBufferParam = new Vector4(far / near - 1.0f, 1.0f, (far / near - 1.0f) / far, 1.0f / far);
        cmd.SetComputeVectorParam(m_ScreenGatherCS, Shader.PropertyToID("_ZBufferParam"), zBufferParam);
        cmd.SetComputeMatrixParam(m_ScreenGatherCS, Shader.PropertyToID("_InvProjMat"), Camera.main.projectionMatrix.inverse);
        cmd.SetComputeMatrixParam(m_ScreenGatherCS, Shader.PropertyToID("_InvViewMat"), Camera.main.cameraToWorldMatrix);

        cmd.SetComputeVectorParam(m_ScreenGatherCS, Shader.PropertyToID("_ScreenPositionScaleBias"), new Vector4(0.5f, -0.5f, 0.5f, 0.5f)); // is it right?
        cmd.SetComputeMatrixParam(m_ScreenGatherCS, Shader.PropertyToID("_ClipToPrevClip"), m_PrevClipMatrix * m_CurClipMatrix.inverse);

        Vector2 HZBUVFactor = new Vector2(0.5f, 0.5f);
        cmd.SetComputeVectorParam(m_ScreenGatherCS, Shader.PropertyToID("_HZBUvFactorAndInvFactor"), new Vector4(HZBUVFactor.x, HZBUVFactor.y, 1.0f / HZBUVFactor.x, 1.0f / HZBUVFactor.y));
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_HZB"), m_HiZBuffer);

        // 
        cmd.SetComputeIntParam(m_ScreenGatherCS, Shader.PropertyToID("_FrameNumber"), m_FrameNumber);
        cmd.SetComputeVectorParam(m_ScreenGatherCS, Shader.PropertyToID("_ScreenGatherRTSize"), (Vector2)m_ScreenGatherRTSize);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_NormalDepthTexture"), m_NormalDepthTexture);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_NormalDepthHistory"), m_NormalDepthHistory);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_SceneColorHistory"), m_SceneColorHistory);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_RWInitialSampleRadiance"), m_InitialSampleRadiance);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_RWInitialSampleHitInfo"), m_InitialSampleHitInfo);
        cmd.SetComputeBufferParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_RWVoxelTraceRayCounter"), m_VoxelTraceRayCounter);
        cmd.SetComputeBufferParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_RWVoxelTraceRayCompactBuffer"), m_VoxelTraceRayCompactBuffer);

        cmd.DispatchCompute(m_ScreenGatherCS, kernel, Mathf.CeilToInt((float)m_ScreenGatherRTSize.x / 8), Mathf.CeilToInt((float)m_ScreenGatherRTSize.y / 8), 1);

        cmd.EndSample("Screen Trace");
    }

    void InitialSampleVoxelTrace(CommandBuffer cmd, MiraiGIGPUScene scene)
    {
        cmd.BeginSample("Voxel Trace");

        MiraiGIClipmap clipmap = scene.miraiGIClipmap;
        MiraiGIRadianceCache radianceCache = scene.miraiGIRadianceCache;

        // 1. build indirect args
        {
            int kernel = m_ScreenGatherCS.FindKernel("BuildVoxelTraceIndirectArgs");

            cmd.SetComputeIntParam(m_ScreenGatherCS, Shader.PropertyToID("_NumThreadsForVoxelTrace"), 64);
            cmd.SetComputeBufferParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_VoxelTraceRayCounter"), m_VoxelTraceRayCounter);
            cmd.SetComputeBufferParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_RWIndirectArgs"), m_VoxelTraceIndirectArgs);

            cmd.DispatchCompute(m_ScreenGatherCS, kernel, 1, 1, 1);
        }

        // 2. voxel trace
        {
            int kernel = m_ScreenGatherCS.FindKernel("InitialSampleVoxelTrace");

            clipmap.SetupVoxelRaytracingParameters(cmd, m_ScreenGatherCS, kernel, scene);
            radianceCache.SetupProbeVolumeParameters(cmd, m_ScreenGatherCS, kernel, scene);

            cmd.SetComputeVectorParam(m_ScreenGatherCS, Shader.PropertyToID("_CameraPosition"), Camera.main.transform.position);

            // ReconstructWorldPositionFromDepth params
            float near = Camera.main.nearClipPlane;
            float far = Camera.main.farClipPlane;
            Vector4 zBufferParam = new Vector4(far / near - 1.0f, 1.0f, (far / near - 1.0f) / far, 1.0f / far);
            cmd.SetComputeVectorParam(m_ScreenGatherCS, Shader.PropertyToID("_ZBufferParam"), zBufferParam);
            cmd.SetComputeMatrixParam(m_ScreenGatherCS, Shader.PropertyToID("_InvProjMat"), Camera.main.projectionMatrix.inverse);
            cmd.SetComputeMatrixParam(m_ScreenGatherCS, Shader.PropertyToID("_InvViewMat"), Camera.main.cameraToWorldMatrix);

            //
            cmd.SetComputeIntParam(m_ScreenGatherCS, Shader.PropertyToID("_FrameNumber"), m_FrameNumber);
            cmd.SetComputeVectorParam(m_ScreenGatherCS, Shader.PropertyToID("_ScreenGatherRTSize"), (Vector2)m_ScreenGatherRTSize);
            cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_NormalDepthTexture"), m_NormalDepthTexture);
            cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_RWInitialSampleRadiance"), m_InitialSampleRadiance);
            cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_RWInitialSampleHitInfo"), m_InitialSampleHitInfo);
            cmd.SetComputeBufferParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_VoxelTraceRayCounter"), m_VoxelTraceRayCounter);
            cmd.SetComputeBufferParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_VoxelTraceRayCompactBuffer"), m_VoxelTraceRayCompactBuffer);

            cmd.DispatchCompute(m_ScreenGatherCS, kernel, m_VoxelTraceIndirectArgs, 0);
        }

        cmd.EndSample("Screen Trace");
    }

    void ReservoirTemporalReuse(CommandBuffer cmd, MiraiGIGPUScene scene)
    {
        cmd.BeginSample("Reservoir Temporal Reuse");

        int curFrame = (m_FrameNumber + 0) % 2;
        int prevFrame = (m_FrameNumber + 1) % 2;

        int kernel = m_ScreenGatherCS.FindKernel("ReservoirTemporalReuse");

        cmd.SetComputeVectorParam(m_ScreenGatherCS, Shader.PropertyToID("_CameraPosition"), Camera.main.transform.position);
        cmd.SetComputeMatrixParam(m_ScreenGatherCS, Shader.PropertyToID("_ClipToPrevClip"), m_PrevClipMatrix * m_CurClipMatrix.inverse);
        cmd.SetComputeVectorParam(m_ScreenGatherCS, Shader.PropertyToID("_ScreenPositionScaleBias"), new Vector4(0.5f, -0.5f, 0.5f, 0.5f));

        // ReconstructWorldPositionFromDepth params
        float near = Camera.main.nearClipPlane;
        float far = Camera.main.farClipPlane;
        Vector4 zBufferParam = new Vector4(far / near - 1.0f, 1.0f, (far / near - 1.0f) / far, 1.0f / far);
        cmd.SetComputeVectorParam(m_ScreenGatherCS, Shader.PropertyToID("_ZBufferParam"), zBufferParam);
        cmd.SetComputeMatrixParam(m_ScreenGatherCS, Shader.PropertyToID("_InvProjMat"), Camera.main.projectionMatrix.inverse);
        cmd.SetComputeMatrixParam(m_ScreenGatherCS, Shader.PropertyToID("_InvViewMat"), Camera.main.cameraToWorldMatrix);

        cmd.SetComputeIntParam(m_ScreenGatherCS, Shader.PropertyToID("_FrameNumber"), m_FrameNumber);
        cmd.SetComputeVectorParam(m_ScreenGatherCS, Shader.PropertyToID("_ScreenGatherRTSize"), (Vector2)m_ScreenGatherRTSize);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_InitialSampleRadiance"), m_InitialSampleRadiance);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_InitialSampleHitInfo"), m_InitialSampleHitInfo);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_NormalDepthTexture"), m_NormalDepthTexture);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_NormalDepthHistory"), m_NormalDepthHistory);
        // reservoir read
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_ReservoirDataA"), m_TemporalReservoirDataA[prevFrame]);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_ReservoirDataB"), m_TemporalReservoirDataB[prevFrame]);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_ReservoirDataC"), m_TemporalReservoirDataC[prevFrame]);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_ReservoirDataD"), m_TemporalReservoirDataD[prevFrame]);
        // reservoir write
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_RWReservoirDataA"), m_TemporalReservoirDataA[curFrame]);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_RWReservoirDataB"), m_TemporalReservoirDataB[curFrame]);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_RWReservoirDataC"), m_TemporalReservoirDataC[curFrame]);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_RWReservoirDataD"), m_TemporalReservoirDataD[curFrame]);

        cmd.DispatchCompute(m_ScreenGatherCS, kernel, Mathf.CeilToInt((float)m_ScreenGatherRTSize.x / 8), Mathf.CeilToInt((float)m_ScreenGatherRTSize.y / 8), 1);

        cmd.EndSample("Reservoir Temporal Reuse");
    }

    void ReservoirSpatialReuse(CommandBuffer cmd, MiraiGIGPUScene scene)
    {
        int curFrame = (m_FrameNumber + 0) % 2;
        int prevFrame = (m_FrameNumber + 1) % 2;
        bool useSpatialReuse = GlobalSettings.Instance.useReservoirSpatialReuse > 0;
        if (!useSpatialReuse)
        {
            return;
        }

        cmd.BeginSample("Reservoir Temporal Reuse");

        int kernel = m_ScreenGatherCS.FindKernel("ReservoirSpatialReuse");

        cmd.SetComputeVectorParam(m_ScreenGatherCS, Shader.PropertyToID("_CameraPosition"), Camera.main.transform.position);
        cmd.SetComputeMatrixParam(m_ScreenGatherCS, Shader.PropertyToID("_ClipToPrevClip"), m_PrevClipMatrix * m_CurClipMatrix.inverse);
        cmd.SetComputeVectorParam(m_ScreenGatherCS, Shader.PropertyToID("_ScreenPositionScaleBias"), new Vector4(0.5f, -0.5f, 0.5f, 0.5f));

        // ReconstructWorldPositionFromDepth params
        float near = Camera.main.nearClipPlane;
        float far = Camera.main.farClipPlane;
        Vector4 zBufferParam = new Vector4(far / near - 1.0f, 1.0f, (far / near - 1.0f) / far, 1.0f / far);
        cmd.SetComputeVectorParam(m_ScreenGatherCS, Shader.PropertyToID("_ZBufferParam"), zBufferParam);
        cmd.SetComputeMatrixParam(m_ScreenGatherCS, Shader.PropertyToID("_InvProjMat"), Camera.main.projectionMatrix.inverse);
        cmd.SetComputeMatrixParam(m_ScreenGatherCS, Shader.PropertyToID("_InvViewMat"), Camera.main.cameraToWorldMatrix);

        cmd.SetComputeIntParam(m_ScreenGatherCS, Shader.PropertyToID("_FrameNumber"), m_FrameNumber);
        cmd.SetComputeVectorParam(m_ScreenGatherCS, Shader.PropertyToID("_ScreenGatherRTSize"), (Vector2)m_ScreenGatherRTSize);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_NormalDepthTexture"), m_NormalDepthTexture);

        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_ReservoirDataA"), m_TemporalReservoirDataA[curFrame]);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_ReservoirDataB"), m_TemporalReservoirDataB[curFrame]);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_ReservoirDataC"), m_TemporalReservoirDataC[curFrame]);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_ReservoirDataD"), m_TemporalReservoirDataD[curFrame]);

        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_RWReservoirDataA"), m_SpatialReservoirDataA);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_RWReservoirDataB"), m_SpatialReservoirDataB);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_RWReservoirDataC"), m_SpatialReservoirDataC);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_RWReservoirDataD"), m_SpatialReservoirDataD);

        cmd.DispatchCompute(m_ScreenGatherCS, kernel, Mathf.CeilToInt((float)m_ScreenGatherRTSize.x / 8), Mathf.CeilToInt((float)m_ScreenGatherRTSize.y / 8), 1);

        cmd.EndSample("Reservoir Temporal Reuse");
    }

    void ReservoirEvaluateIrradiance(CommandBuffer cmd, MiraiGIGPUScene scene)
    {
        cmd.BeginSample("Reservoir Evaluate Irradiance");

        int curFrame = (m_FrameNumber + 0) % 2;
        int prevFrame = (m_FrameNumber + 1) % 2;
        bool useSpatialReuse = GlobalSettings.Instance.useReservoirSpatialReuse > 0;

        int kernel = m_ScreenGatherCS.FindKernel("ReservoirEvaluateIrradiance");

        cmd.SetComputeVectorParam(m_ScreenGatherCS, Shader.PropertyToID("_CameraPosition"), Camera.main.transform.position);

        // ReconstructWorldPositionFromDepth params
        float near = Camera.main.nearClipPlane;
        float far = Camera.main.farClipPlane;
        Vector4 zBufferParam = new Vector4(far / near - 1.0f, 1.0f, (far / near - 1.0f) / far, 1.0f / far);
        cmd.SetComputeVectorParam(m_ScreenGatherCS, Shader.PropertyToID("_ZBufferParam"), zBufferParam);
        cmd.SetComputeMatrixParam(m_ScreenGatherCS, Shader.PropertyToID("_InvProjMat"), Camera.main.projectionMatrix.inverse);
        cmd.SetComputeMatrixParam(m_ScreenGatherCS, Shader.PropertyToID("_InvViewMat"), Camera.main.cameraToWorldMatrix);

        cmd.SetComputeIntParam(m_ScreenGatherCS, Shader.PropertyToID("_FrameNumber"), m_FrameNumber);
        cmd.SetComputeVectorParam(m_ScreenGatherCS, Shader.PropertyToID("_ScreenGatherRTSize"), (Vector2)m_ScreenGatherRTSize);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_NormalDepthTexture"), m_NormalDepthTexture);
        if (useSpatialReuse)
        {
            cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_ReservoirDataA"), m_SpatialReservoirDataA);
            cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_ReservoirDataB"), m_SpatialReservoirDataB);
            cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_ReservoirDataC"), m_SpatialReservoirDataC);
            cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_ReservoirDataD"), m_SpatialReservoirDataD);
        }
        else
        {
            cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_ReservoirDataA"), m_TemporalReservoirDataA[curFrame]);
            cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_ReservoirDataB"), m_TemporalReservoirDataB[curFrame]);
            cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_ReservoirDataC"), m_TemporalReservoirDataC[curFrame]);
            cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_ReservoirDataD"), m_TemporalReservoirDataD[curFrame]);
        }
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_RWScreenGatherOutputTexture"), m_ScreenGatherOutputTexture);

        cmd.DispatchCompute(m_ScreenGatherCS, kernel, Mathf.CeilToInt((float)m_ScreenGatherRTSize.x / 8), Mathf.CeilToInt((float)m_ScreenGatherRTSize.y / 8), 1);

        cmd.EndSample("Reservoir Evaluate Irradiance");
    }
}