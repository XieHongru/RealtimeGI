using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class MiraiGIScreenGatherResources
{
    
}

public class MiraiGIScreenGather
{
    Vector2Int m_ScreenGatherRTSize;

    RenderTexture m_HitRadianceTexture;

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
        m_PrevClipMatrix = Matrix4x4.identity;
        m_CurClipMatrix = Matrix4x4.identity;

        m_HiZBufferGenerateCS = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/Shaders/MiraiGI/RayTracing/HiZBufferGenerate.compute");
        m_ScreenGatherCS = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/Shaders/MiraiGI/ScreenGather/ScreenGather.compute");
    }

    public void Release()
    {
        m_HitRadianceTexture?.Release();
        m_VoxelTraceRayCounter?.Release();
        m_VoxelTraceRayCompactBuffer?.Release();
        m_VoxelTraceIndirectArgs?.Release();
        m_ScreenGatherOutputTexture?.Release();
        m_DiffuseIndirectHistory?.Release();
        m_SceneColorHistory?.Release();

        m_HitRadianceTexture = null;
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
        InitialSampleScreenTrace(ref renderingData, cmd, scene);
        InitialSampleVoxelTrace(cmd, scene);

        Graphics.ExecuteCommandBuffer(cmd);
        CommandBufferPool.Release(cmd);
    }

    public void VisualizeMiraiGIScreenGather(CommandBuffer cmd, MiraiGIGPUScene scene)
    {
        if (GlobalSettings.Instance.visualizeScreenGather <= 0)
            return;

        cmd.BeginSample("Visualize Screen Gather");

        MiraiGIClipmap clipmap = scene.miraiGIClipmap;

        cmd.Blit(m_HitRadianceTexture, clipmap.GetVisualizeColorTarget());

        cmd.EndSample("Visualize Screen Gather");
    }

    void PrepareRenderResources(ref RenderingData renderingData, CommandBuffer cmd, MiraiGIGPUScene scene)
    {
        m_ScreenGatherRTSize = new Vector2Int(renderingData.cameraData.cameraTargetDescriptor.width / 2, renderingData.cameraData.cameraTargetDescriptor.height / 2);

        if (m_HitRadianceTexture == null)
        {
            m_HitRadianceTexture = new RenderTexture(m_ScreenGatherRTSize.x, m_ScreenGatherRTSize.y, 0, RenderTextureFormat.ARGBFloat);
            m_HitRadianceTexture.enableRandomWrite = true;
            m_HitRadianceTexture.Create();
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

        cmd.SetComputeVectorParam(m_ScreenGatherCS, Shader.PropertyToID("_ScreenResolution"), new Vector4(Camera.main.pixelWidth, Camera.main.pixelHeight));
        cmd.SetComputeVectorParam(m_ScreenGatherCS, Shader.PropertyToID("_CameraPosition"), Camera.main.transform.position);
        cmd.SetComputeMatrixParam(m_ScreenGatherCS, Shader.PropertyToID("_ProjMat"), Camera.main.projectionMatrix);
        cmd.SetComputeMatrixParam(m_ScreenGatherCS, Shader.PropertyToID("_TranslatedWorldToClip"), translatedWorldToClip);
        cmd.SetComputeMatrixParam(m_ScreenGatherCS, Shader.PropertyToID("_ViewProjMat"), Camera.main.projectionMatrix * Camera.main.worldToCameraMatrix);
        cmd.SetComputeMatrixParam(m_ScreenGatherCS, Shader.PropertyToID("_InvViewProjMat"), (Camera.main.projectionMatrix * Camera.main.worldToCameraMatrix).inverse);
        cmd.SetComputeIntParam(m_ScreenGatherCS, Shader.PropertyToID("_FrameCountMod8"), (int)radianceCache.frameNumberRenderThread % 8);

        float near = Camera.main.nearClipPlane;
        float far = Camera.main.farClipPlane;
        Vector4 zBufferParam = new Vector4(far / near - 1.0f, 1.0f, (far / near - 1.0f) / far, 1.0f / far);
        cmd.SetComputeVectorParam(m_ScreenGatherCS, Shader.PropertyToID("_ZBufferParam"), zBufferParam);
        cmd.SetComputeMatrixParam(m_ScreenGatherCS, Shader.PropertyToID("_InvProjMat"), Camera.main.projectionMatrix.inverse);
        cmd.SetComputeMatrixParam(m_ScreenGatherCS, Shader.PropertyToID("_InvViewMat"), Camera.main.cameraToWorldMatrix);

        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_GBufferNormal"), Shader.GetGlobalTexture("_GBuffer2"));
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_SceneDepthTexture"), Shader.GetGlobalTexture("_CameraDepthTexture"));

        cmd.SetComputeVectorParam(m_ScreenGatherCS, Shader.PropertyToID("_ScreenPositionScaleBias"), new Vector4(0.5f, -0.5f, 0.5f, 0.5f)); // is it right?
        cmd.SetComputeVectorParam(m_ScreenGatherCS, Shader.PropertyToID("_PrevScreenPositionScaleBias"), new Vector4(0.5f, -0.5f, 0.5f, 0.5f));
        cmd.SetComputeMatrixParam(m_ScreenGatherCS, Shader.PropertyToID("_ClipToPrevClip"), m_PrevClipMatrix * m_CurClipMatrix.inverse);
        cmd.SetComputeVectorParam(m_ScreenGatherCS, Shader.PropertyToID("_ScreenGatherRTSize"), (Vector2)m_ScreenGatherRTSize);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_SceneColorHistory"), m_SceneColorHistory);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_RWHitRadianceTexture"), m_HitRadianceTexture);

        Vector2 HZBUVFactor = new Vector2(0.5f, 0.5f);

        cmd.SetComputeVectorParam(m_ScreenGatherCS, Shader.PropertyToID("_HZBUvFactorAndInvFactor"), new Vector4(HZBUVFactor.x, HZBUVFactor.y, 1.0f / HZBUVFactor.x, 1.0f / HZBUVFactor.y));
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_HZB"), m_HiZBuffer);
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

            cmd.SetComputeVectorParam(m_ScreenGatherCS, Shader.PropertyToID("_ScreenResolution"), new Vector4(Camera.main.pixelWidth, Camera.main.pixelHeight));
            cmd.SetComputeVectorParam(m_ScreenGatherCS, Shader.PropertyToID("_CameraPosition"), Camera.main.transform.position);
            cmd.SetComputeMatrixParam(m_ScreenGatherCS, Shader.PropertyToID("_ProjMat"), Camera.main.projectionMatrix);
            cmd.SetComputeMatrixParam(m_ScreenGatherCS, Shader.PropertyToID("_ViewProjMat"), Camera.main.projectionMatrix * Camera.main.worldToCameraMatrix);
            cmd.SetComputeMatrixParam(m_ScreenGatherCS, Shader.PropertyToID("_InvViewProjMat"), (Camera.main.projectionMatrix * Camera.main.worldToCameraMatrix).inverse);
            cmd.SetComputeIntParam(m_ScreenGatherCS, Shader.PropertyToID("_FrameCountMod8"), (int)radianceCache.frameNumberRenderThread % 8);

            float near = Camera.main.nearClipPlane;
            float far = Camera.main.farClipPlane;
            Vector4 zBufferParam = new Vector4(far / near - 1.0f, 1.0f, (far / near - 1.0f) / far, 1.0f / far);
            cmd.SetComputeVectorParam(m_ScreenGatherCS, Shader.PropertyToID("_ZBufferParam"), zBufferParam);
            cmd.SetComputeMatrixParam(m_ScreenGatherCS, Shader.PropertyToID("_InvProjMat"), Camera.main.projectionMatrix.inverse);
            cmd.SetComputeMatrixParam(m_ScreenGatherCS, Shader.PropertyToID("_InvViewMat"), Camera.main.cameraToWorldMatrix);

            cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_GBufferNormal"), Shader.GetGlobalTexture("_GBuffer2"));
            cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_SceneDepthTexture"), Shader.GetGlobalTexture("_CameraDepthTexture"));

            cmd.SetComputeVectorParam(m_ScreenGatherCS, Shader.PropertyToID("_ScreenGatherRTSize"), (Vector2)m_ScreenGatherRTSize);
            cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_RWHitRadianceTexture"), m_HitRadianceTexture);
            cmd.SetComputeBufferParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_VoxelTraceRayCounter"), m_VoxelTraceRayCounter);
            cmd.SetComputeBufferParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_VoxelTraceRayCompactBuffer"), m_VoxelTraceRayCompactBuffer);

            cmd.DispatchCompute(m_ScreenGatherCS, kernel, m_VoxelTraceIndirectArgs, 0);
        }

        cmd.EndSample("Screen Trace");
    }
}