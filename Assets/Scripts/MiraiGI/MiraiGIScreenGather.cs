using System;
using System.Linq;
using UnityEditor;
using UnityEditor.ShaderGraph.Drawing.Inspector.PropertyDrawers;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using static Unity.Burst.Intrinsics.X86.Avx;

public enum TraceMode
{
    TM_Diffuse = 0,
    TM_Specular,
    TM_Num
};

public enum ReservoirSource
{
    RS_Temporal = 0,
    RS_Spatial,
    RS_Num
}

public class MiraiGIScreenGather
{
    int m_FrameNumber;
    Vector2Int m_SceneTextureRTSize;
    Vector2Int m_ScreenGatherRTSize;

    RenderTexture m_NormalDepthTexture;
    RenderTexture m_MiniDepthTexture;
    RenderTexture m_NormalDepthHistory;
    RenderTexture m_DirectLightingTexture;

    RenderTexture m_InitialSampleRadiance;
    RenderTexture m_InitialSampleHitInfo;
    RenderTexture m_InitialSampleRayInfo;

    // ping pong buffer between two frame
    RenderTexture[] m_TemporalReservoirDataA;
    RenderTexture[] m_TemporalReservoirDataB;
    RenderTexture[] m_TemporalReservoirDataC;
    RenderTexture[] m_TemporalReservoirDataD;

    RenderTexture[] m_SpatialReservoirDataA;
    RenderTexture[] m_SpatialReservoirDataB;
    RenderTexture[] m_SpatialReservoirDataC;
    RenderTexture[] m_SpatialReservoirDataD;

    ComputeBuffer m_VoxelTraceRayCounter;
    ComputeBuffer m_VoxelTraceRayCompactBuffer;
    ComputeBuffer m_VoxelTraceIndirectArgs;

    // for diffuse
    RenderTexture m_TemporalReservoirIrradiance;
    RenderTexture m_SpatialReservoirIrradiance;
    RenderTexture m_DiffuseResolveOutputTexture;    // a pointer to TemporalReservoirIrradiance or SpatialReservoirIrradiance
    RenderTexture m_DiffuseTemporalFilterOutput;
    RenderTexture m_DiffuseSpatialFilterOutput;
    RenderTexture m_DiffuseIndirectHistory;
    RenderTexture m_DiffuseCompositeTexture;
    RenderTexture m_DiffuseAccumulatedFrameTexture;

    RenderTexture m_IndirectShadowTexture;
    RenderTexture m_IndirectShadowTemporalFilterOutput;
    RenderTexture m_IndirectShadowSpatialFilterOutput;
    RenderTexture m_IndirectShadowHistory;

    // for specular
    RenderTexture m_SpecularResolveOutputTexture;
    RenderTexture m_SpecularTemporalFilterOutput;
    RenderTexture m_SpecularSpatialFilterOutput;
    RenderTexture m_SpecularIndirectHistory;
    RenderTexture m_SpecularCompositeTexture;
    RenderTexture m_SpecularAccumulatedFrameTexture;

    RenderTexture m_HiZBuffer;
    RenderTexture m_TestOutput;

    Matrix4x4 m_PrevClipMatrix;
    Matrix4x4 m_CurClipMatrix;

    ComputeShader m_HiZBufferGenerateCS;
    ComputeShader m_ScreenGatherCS;

    public RenderTexture GetDiffuseCompositeTexture() => m_DiffuseCompositeTexture;
    public RenderTexture GetSpecularCompositeTexture() => m_SpecularCompositeTexture;

    public void Init()
    {
        m_TemporalReservoirDataA = new RenderTexture[2];
        m_TemporalReservoirDataB = new RenderTexture[2];
        m_TemporalReservoirDataC = new RenderTexture[2];
        m_TemporalReservoirDataD = new RenderTexture[2];

        m_SpatialReservoirDataA = new RenderTexture[2];
        m_SpatialReservoirDataB = new RenderTexture[2];
        m_SpatialReservoirDataC = new RenderTexture[2];
        m_SpatialReservoirDataD = new RenderTexture[2];

        m_PrevClipMatrix = Matrix4x4.identity;
        m_CurClipMatrix = Matrix4x4.identity;

        m_HiZBufferGenerateCS = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/Shaders/MiraiGI/RayTracing/HiZBufferGenerate.compute");
        m_ScreenGatherCS = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/Shaders/MiraiGI/ScreenGather/ScreenGather.compute");
    }

    public void Release()
    {
        m_NormalDepthTexture?.Release();
        m_MiniDepthTexture?.Release();
        m_NormalDepthHistory?.Release();
        m_DirectLightingTexture?.Release();

        m_InitialSampleRadiance?.Release();
        m_InitialSampleHitInfo?.Release();
        m_InitialSampleRayInfo?.Release();

        for (int i = 0; i < 2; i++)
        {
            m_TemporalReservoirDataA[i]?.Release();
            m_TemporalReservoirDataB[i]?.Release();
            m_TemporalReservoirDataC[i]?.Release();
            m_TemporalReservoirDataD[i]?.Release();
            m_SpatialReservoirDataA[i]?.Release();
            m_SpatialReservoirDataB[i]?.Release();
            m_SpatialReservoirDataC[i]?.Release();
            m_SpatialReservoirDataD[i]?.Release();
        }

        m_VoxelTraceRayCounter?.Release();
        m_VoxelTraceRayCompactBuffer?.Release();
        m_VoxelTraceIndirectArgs?.Release();

        // for diffuse
        m_TemporalReservoirIrradiance?.Release();
        m_SpatialReservoirIrradiance?.Release();
        m_DiffuseTemporalFilterOutput?.Release();
        m_DiffuseSpatialFilterOutput?.Release();
        m_DiffuseIndirectHistory?.Release();
        m_DiffuseCompositeTexture?.Release();
        m_DiffuseAccumulatedFrameTexture?.Release();

        m_IndirectShadowTexture?.Release();
        m_IndirectShadowTemporalFilterOutput?.Release();
        m_IndirectShadowSpatialFilterOutput?.Release();
        m_IndirectShadowHistory?.Release();

        // for specular
        m_SpecularResolveOutputTexture?.Release();
        m_SpecularTemporalFilterOutput?.Release();
        m_SpecularSpatialFilterOutput?.Release();
        m_SpecularIndirectHistory?.Release();
        m_SpecularCompositeTexture?.Release();
        m_SpecularAccumulatedFrameTexture?.Release();

        m_HiZBuffer?.Release();

        m_NormalDepthTexture = null;
        m_MiniDepthTexture = null;
        m_NormalDepthHistory = null;
        m_DirectLightingTexture = null;

        m_InitialSampleRadiance = null;
        m_InitialSampleHitInfo = null;
        m_InitialSampleRayInfo = null;

        for (int i = 0; i < 2; i++)
        {
            m_TemporalReservoirDataA[i] = null;
            m_TemporalReservoirDataB[i] = null;
            m_TemporalReservoirDataC[i] = null;
            m_TemporalReservoirDataD[i] = null;
            m_SpatialReservoirDataA[i] = null;
            m_SpatialReservoirDataB[i] = null;
            m_SpatialReservoirDataC[i] = null;
            m_SpatialReservoirDataD[i] = null;
        }

        m_VoxelTraceRayCounter = null;
        m_VoxelTraceRayCompactBuffer = null;
        m_VoxelTraceIndirectArgs = null;

        // for diffuse
        m_TemporalReservoirIrradiance = null;
        m_SpatialReservoirIrradiance = null;
        m_DiffuseResolveOutputTexture = null;
        m_DiffuseTemporalFilterOutput = null;
        m_DiffuseSpatialFilterOutput = null;
        m_DiffuseIndirectHistory = null;
        m_DiffuseCompositeTexture = null;
        m_DiffuseAccumulatedFrameTexture = null;

        m_IndirectShadowTexture = null;
        m_IndirectShadowTemporalFilterOutput = null;
        m_IndirectShadowSpatialFilterOutput = null;
        m_IndirectShadowHistory = null;

        // for specular
        m_SpecularResolveOutputTexture = null;
        m_SpecularTemporalFilterOutput = null;
        m_SpecularSpatialFilterOutput = null;
        m_SpecularIndirectHistory = null;
        m_SpecularCompositeTexture = null;
        m_SpecularAccumulatedFrameTexture = null;

        m_HiZBuffer = null;

        m_TestOutput?.Release();
        m_TestOutput = null;
    }

    public void Update(ref RenderingData renderingData, MiraiGIGPUScene scene)
    {
        CommandBuffer cmd = CommandBufferPool.Get("Screen Gather");

        PrepareRenderResources(ref renderingData, cmd, scene);
        NormalDepthDownsample(cmd, scene);

        cmd.Blit(Shader.GetGlobalTexture("_CameraOpaqueTexture"), m_DirectLightingTexture);

        {
            cmd.BeginSample("Diffuse Indirect");

            InitialSampleScreenTrace(ref renderingData, cmd, scene, TraceMode.TM_Diffuse);
            InitialSampleVoxelTrace(cmd, scene, TraceMode.TM_Diffuse);
            ReservoirTemporalReuse(cmd, scene);
            ReservoirEvaluateIrradiance(cmd, scene, ReservoirSource.RS_Temporal);
            m_DiffuseResolveOutputTexture = m_TemporalReservoirIrradiance;
            if (GlobalSettings.Instance.useReservoirSpatialReuse > 0)
            {
                // reuse from temporal reservoir to spatial reservoir
                ReservoirSpatialReuse(cmd, scene, ReservoirSource.RS_Temporal);

                // secondary reuse from ping pong reservoir buffer
                if (GlobalSettings.Instance.spatialSecondaryReuse > 0)
                {
                    ReservoirSpatialReuse(cmd, scene, ReservoirSource.RS_Spatial);
                }

                // resolve
                ReservoirEvaluateIrradiance(cmd, scene, ReservoirSource.RS_Spatial);
                m_DiffuseResolveOutputTexture = m_SpatialReservoirIrradiance;
            }
            RenderFilterGuidanceSSAO(cmd, scene);
            DiffuseTemporalFilter(cmd, scene);
            DiffuseSpatialFilter(cmd, scene);
            cmd.EndSample("Diffuse Indirect");
        }

        {
            cmd.BeginSample("Specular Indirect");

            InitialSampleScreenTrace(ref renderingData, cmd, scene, TraceMode.TM_Specular);
            InitialSampleVoxelTrace(cmd, scene, TraceMode.TM_Specular);
            SpecularResolve(cmd, scene);
            SpecularTemporalFilter(cmd, scene);
            SpecularSpatialFilter(cmd, scene);

            cmd.EndSample("Specular Indirect");
        }

        {
            cmd.BeginSample("NormalDepthTexture Copy");

            cmd.Blit(m_NormalDepthTexture, m_NormalDepthHistory);

            cmd.EndSample("NormalDepthTexture Copy");
        }

        {
            cmd.Blit(Shader.GetGlobalTexture("_CameraOpaqueTexture"), m_DiffuseCompositeTexture);
            cmd.Blit(Shader.GetGlobalTexture("_CameraOpaqueTexture"), m_SpecularCompositeTexture);
        }

        Graphics.ExecuteCommandBuffer(cmd);
        CommandBufferPool.Release(cmd);
    }

    public void DiffuseComposite(ref RenderingData renderingData)
    {
        CommandBuffer cmd = CommandBufferPool.Get("Diffuse Composite");

        cmd.BeginSample("Diffuse Composite");

        int kernel = m_ScreenGatherCS.FindKernel("DiffuseComposite");

        cmd.SetComputeVectorParam(m_ScreenGatherCS, Shader.PropertyToID("_SceneTextureRTSize"), (Vector2)m_SceneTextureRTSize);
        cmd.SetComputeVectorParam(m_ScreenGatherCS, Shader.PropertyToID("_ScreenGatherRTSize"), (Vector2)m_ScreenGatherRTSize);
        cmd.SetComputeIntParam(m_ScreenGatherCS, Shader.PropertyToID("_FrameNumber"), m_FrameNumber);
        cmd.SetComputeFloatParam(m_ScreenGatherCS, Shader.PropertyToID("_AOIntensity"), GlobalSettings.Instance.filterGuidanceSSAOIntensity);
        cmd.SetComputeFloatParam(m_ScreenGatherCS, Shader.PropertyToID("_IndirectShadowSharpness"), GlobalSettings.Instance.indirectShadowSharpness);
        cmd.SetComputeFloatParam(m_ScreenGatherCS, Shader.PropertyToID("_IndirectShadowIntensity"), GlobalSettings.Instance.indirectShadowIntensity);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_NormalDepthTexture"), m_NormalDepthTexture);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_MiniDepthTexture"), m_MiniDepthTexture);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_GBufferBaseColor"), Shader.GetGlobalTexture("_GBuffer0"));
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_GBufferNormal"), Shader.GetGlobalTexture("_GBuffer2"));
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_SceneColor"), Shader.GetGlobalTexture("_CameraOpaqueTexture"));
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_SceneDepthTexture"), m_DirectLightingTexture);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_DiffuseIndirectTexture"), m_DiffuseSpatialFilterOutput);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_IndirectShadowTexture"), m_IndirectShadowSpatialFilterOutput);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_RWDiffuseCompositeTexture"), m_DiffuseCompositeTexture);

        cmd.DispatchCompute(m_ScreenGatherCS, kernel, Mathf.CeilToInt((float)m_SceneTextureRTSize.x / 8), Mathf.CeilToInt((float)m_SceneTextureRTSize.y / 8), 1);

        cmd.EndSample("Diffuse Composite");

        Graphics.ExecuteCommandBuffer(cmd);
        CommandBufferPool.Release(cmd);
    }

    public void SpecularComposite(ref RenderingData renderingData)
    {
        CommandBuffer cmd = CommandBufferPool.Get("Specular Composite");

        cmd.BeginSample("Specular Composite");

        int kernel = m_ScreenGatherCS.FindKernel("SpecularComposite");

        cmd.SetComputeVectorParam(m_ScreenGatherCS, Shader.PropertyToID("_SceneTextureRTSize"), (Vector2)m_SceneTextureRTSize);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_NormalDepthTexture"), m_NormalDepthTexture);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_GBufferSpecular"), Shader.GetGlobalTexture("_GBuffer1"));
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_GBufferNormal"), Shader.GetGlobalTexture("_GBuffer2"));
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_SceneColor"), m_DiffuseCompositeTexture);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_SceneDepthTexture"), Shader.GetGlobalTexture("_CameraDepthTexture"));
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_SpecularIndirectTexture"), m_SpecularSpatialFilterOutput);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_RWSpecularCompositeTexture"), m_SpecularCompositeTexture);

        // @TODO: brdfLUT 
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("PreIntegratedGF"), new RenderTexture(m_SceneTextureRTSize.x, m_SceneTextureRTSize.y, 0));

        cmd.DispatchCompute(m_ScreenGatherCS, kernel, Mathf.CeilToInt((float)m_SceneTextureRTSize.x / 8), Mathf.CeilToInt((float)m_SceneTextureRTSize.y / 8), 1);

        cmd.EndSample("Specular Composite");

        Graphics.ExecuteCommandBuffer(cmd);
        CommandBufferPool.Release(cmd);
    }

    public void VisualizeMiraiGIScreenGather(CommandBuffer cmd, MiraiGIGPUScene scene)
    {
        int visualizeMode = GlobalSettings.Instance.visualizeScreenGather;
        if (GlobalSettings.Instance.visualizeScreenGather <= 0)
            return;

        cmd.BeginSample("Visualize Screen Gather");

        MiraiGIClipmap clipmap = scene.miraiGIClipmap;

        if (visualizeMode == 1)
        {
            cmd.Blit(m_DiffuseResolveOutputTexture, clipmap.GetVisualizeColorTarget());
        }
        else if (visualizeMode == 2)
        {
            cmd.Blit(m_DiffuseTemporalFilterOutput, clipmap.GetVisualizeColorTarget());
        }
        else if (visualizeMode == 3)
        {
            cmd.Blit(m_DiffuseSpatialFilterOutput, clipmap.GetVisualizeColorTarget());
        }
        else if (visualizeMode == 4)
        {
            cmd.Blit(m_SpecularResolveOutputTexture, clipmap.GetVisualizeColorTarget());
        }
        else if (visualizeMode == 5)
        {
            cmd.Blit(m_SpecularTemporalFilterOutput, clipmap.GetVisualizeColorTarget());
        }
        else if (visualizeMode == 6)
        {
            cmd.Blit(m_SpecularSpatialFilterOutput, clipmap.GetVisualizeColorTarget());
        }
        else if (visualizeMode == 7)
        {
            cmd.Blit(m_IndirectShadowSpatialFilterOutput, clipmap.GetVisualizeColorTarget());
        }
        else if (visualizeMode == 8)
        {
            cmd.Blit(m_TestOutput, clipmap.GetVisualizeColorTarget());
        }

        cmd.EndSample("Visualize Screen Gather");
    }

    void PrepareRenderResources(ref RenderingData renderingData, CommandBuffer cmd, MiraiGIGPUScene scene)
    {
        m_FrameNumber = scene.frameNumber;

        int downsampleFactor = Mathf.Clamp(GlobalSettings.Instance.screenGatherDownsampleFactor, 1, 4);
        m_SceneTextureRTSize = new Vector2Int(renderingData.cameraData.cameraTargetDescriptor.width, renderingData.cameraData.cameraTargetDescriptor.height);
        m_ScreenGatherRTSize = m_SceneTextureRTSize / downsampleFactor;

        if (m_NormalDepthTexture == null)
        {
            m_NormalDepthTexture = new RenderTexture(m_ScreenGatherRTSize.x, m_ScreenGatherRTSize.y, 0, RenderTextureFormat.ARGBHalf);
            m_NormalDepthTexture.enableRandomWrite = true;
            m_NormalDepthTexture.Create();
        }

        if (m_MiniDepthTexture == null)
        {
            m_MiniDepthTexture = new RenderTexture(m_ScreenGatherRTSize.x, m_ScreenGatherRTSize.y, 0, RenderTextureFormat.R16);
            m_MiniDepthTexture.enableRandomWrite = true;
            m_MiniDepthTexture.Create();
        }

        if (m_NormalDepthHistory == null)
        {
            m_NormalDepthHistory = new RenderTexture(m_ScreenGatherRTSize.x, m_ScreenGatherRTSize.y, 0, RenderTextureFormat.ARGBHalf);
            m_NormalDepthHistory.enableRandomWrite = true;
            m_NormalDepthHistory.Create();
        }

        if (m_DirectLightingTexture == null)
        {
            m_DirectLightingTexture = new RenderTexture(m_ScreenGatherRTSize.x, m_ScreenGatherRTSize.y, 0, RenderTextureFormat.ARGBFloat);
            m_DirectLightingTexture.enableRandomWrite = true;
            m_DirectLightingTexture.Create();
        }

        if (m_InitialSampleRadiance == null)
        {
            m_InitialSampleRadiance = new RenderTexture(m_ScreenGatherRTSize.x, m_ScreenGatherRTSize.y, 0, RenderTextureFormat.ARGBHalf);
            m_InitialSampleRadiance.enableRandomWrite = true;
            m_InitialSampleRadiance.name = "InitialSampleRadiance";
            m_InitialSampleRadiance.Create();
        }

        if (m_InitialSampleHitInfo == null)
        {
            m_InitialSampleHitInfo = new RenderTexture(m_ScreenGatherRTSize.x, m_ScreenGatherRTSize.y, 0, RenderTextureFormat.ARGBHalf);
            m_InitialSampleHitInfo.enableRandomWrite = true;
            m_InitialSampleHitInfo.name = "InitialSampleHitInfo";
            m_InitialSampleHitInfo.Create();
        }

        if (m_InitialSampleRayInfo == null)
        {
            m_InitialSampleRayInfo = new RenderTexture(m_ScreenGatherRTSize.x, m_ScreenGatherRTSize.y, 0, RenderTextureFormat.ARGBHalf);
            m_InitialSampleRayInfo.enableRandomWrite = true;
            m_InitialSampleRayInfo.name = "InitialSampleRayInfo";
            m_InitialSampleRayInfo.Create();
        }

        // reservoir
        if (m_TemporalReservoirDataA[0] == null)
        {
            for (int i = 0; i < 2; i++)
            {
                m_TemporalReservoirDataA[i] = new RenderTexture(m_ScreenGatherRTSize.x, m_ScreenGatherRTSize.y, 0, RenderTextureFormat.ARGBHalf);
                m_TemporalReservoirDataA[i].enableRandomWrite = true;
                m_TemporalReservoirDataA[i].Create();
                m_TemporalReservoirDataB[i] = new RenderTexture(m_ScreenGatherRTSize.x, m_ScreenGatherRTSize.y, 0, RenderTextureFormat.ARGBHalf);
                m_TemporalReservoirDataB[i].enableRandomWrite = true;
                m_TemporalReservoirDataB[i].Create();
                m_TemporalReservoirDataC[i] = new RenderTexture(m_ScreenGatherRTSize.x, m_ScreenGatherRTSize.y, 0, RenderTextureFormat.ARGBFloat);
                m_TemporalReservoirDataC[i].enableRandomWrite = true;
                m_TemporalReservoirDataC[i].Create();
                m_TemporalReservoirDataD[i] = new RenderTexture(m_ScreenGatherRTSize.x, m_ScreenGatherRTSize.y, 0, RenderTextureFormat.ARGBFloat);
                m_TemporalReservoirDataD[i].enableRandomWrite = true;
                m_TemporalReservoirDataD[i].Create();

                m_SpatialReservoirDataA[i] = new RenderTexture(m_ScreenGatherRTSize.x, m_ScreenGatherRTSize.y, 0, RenderTextureFormat.ARGBHalf);
                m_SpatialReservoirDataA[i].enableRandomWrite = true;
                m_SpatialReservoirDataA[i].Create();
                m_SpatialReservoirDataB[i] = new RenderTexture(m_ScreenGatherRTSize.x, m_ScreenGatherRTSize.y, 0, RenderTextureFormat.ARGBHalf);
                m_SpatialReservoirDataB[i].enableRandomWrite = true;
                m_SpatialReservoirDataB[i].Create();
                m_SpatialReservoirDataC[i] = new RenderTexture(m_ScreenGatherRTSize.x, m_ScreenGatherRTSize.y, 0, RenderTextureFormat.ARGBFloat);
                m_SpatialReservoirDataC[i].enableRandomWrite = true;
                m_SpatialReservoirDataC[i].Create();
                m_SpatialReservoirDataD[i] = new RenderTexture(m_ScreenGatherRTSize.x, m_ScreenGatherRTSize.y, 0, RenderTextureFormat.ARGBFloat);
                m_SpatialReservoirDataD[i].enableRandomWrite = true;
                m_SpatialReservoirDataD[i].Create();
            }
        }

        // diffuse
        {
            if (m_TemporalReservoirIrradiance == null)
            {
                m_TemporalReservoirIrradiance = new RenderTexture(m_ScreenGatherRTSize.x, m_ScreenGatherRTSize.y, 0, RenderTextureFormat.ARGBHalf);
                m_TemporalReservoirIrradiance.enableRandomWrite = true;
                m_TemporalReservoirIrradiance.Create();
            }

            if (m_SpatialReservoirIrradiance == null)
            {
                m_SpatialReservoirIrradiance = new RenderTexture(m_ScreenGatherRTSize.x, m_ScreenGatherRTSize.y, 0, RenderTextureFormat.ARGBHalf);
                m_SpatialReservoirIrradiance.enableRandomWrite = true;
                m_SpatialReservoirIrradiance.Create();
            }

            if (m_DiffuseTemporalFilterOutput == null)
            {
                m_DiffuseTemporalFilterOutput = new RenderTexture(m_ScreenGatherRTSize.x, m_ScreenGatherRTSize.y, 0, RenderTextureFormat.ARGBHalf);
                m_DiffuseTemporalFilterOutput.enableRandomWrite = true;
                m_DiffuseTemporalFilterOutput.Create();
            }

            if (m_DiffuseSpatialFilterOutput == null)
            {
                m_DiffuseSpatialFilterOutput = new RenderTexture(m_ScreenGatherRTSize.x, m_ScreenGatherRTSize.y, 0, RenderTextureFormat.ARGBHalf);
                m_DiffuseSpatialFilterOutput.enableRandomWrite = true;
                m_DiffuseSpatialFilterOutput.Create();
            }

            if (m_DiffuseIndirectHistory == null)
            {
                m_DiffuseIndirectHistory = new RenderTexture(m_ScreenGatherRTSize.x, m_ScreenGatherRTSize.y, 0, RenderTextureFormat.ARGBHalf);
                m_DiffuseIndirectHistory.enableRandomWrite = true;
                m_DiffuseIndirectHistory.Create();
            }

            if (m_DiffuseAccumulatedFrameTexture == null)
            {
                m_DiffuseAccumulatedFrameTexture = new RenderTexture(m_ScreenGatherRTSize.x, m_ScreenGatherRTSize.y, 0, RenderTextureFormat.R8);
                m_DiffuseAccumulatedFrameTexture.enableRandomWrite = true;
                m_DiffuseAccumulatedFrameTexture.Create();
            }

            if (m_DiffuseCompositeTexture == null)
            {
                m_DiffuseCompositeTexture = new RenderTexture(m_SceneTextureRTSize.x, m_SceneTextureRTSize.y, 0, RenderTextureFormat.ARGBFloat);
                m_DiffuseCompositeTexture.enableRandomWrite = true;
                m_DiffuseCompositeTexture.Create();
            }

            if (m_IndirectShadowTexture == null)
            {
                m_IndirectShadowTexture = new RenderTexture(m_ScreenGatherRTSize.x, m_ScreenGatherRTSize.y, 0, RenderTextureFormat.R8);
                m_IndirectShadowTexture.enableRandomWrite = true;
                m_IndirectShadowTexture.Create();
            }

            if (m_IndirectShadowTemporalFilterOutput == null)
            {
                m_IndirectShadowTemporalFilterOutput = new RenderTexture(m_ScreenGatherRTSize.x, m_ScreenGatherRTSize.y, 0, RenderTextureFormat.R8);
                m_IndirectShadowTemporalFilterOutput.enableRandomWrite = true;
                m_IndirectShadowTemporalFilterOutput.Create();
            }

            if (m_IndirectShadowSpatialFilterOutput == null)
            {
                m_IndirectShadowSpatialFilterOutput = new RenderTexture(m_ScreenGatherRTSize.x, m_ScreenGatherRTSize.y, 0, RenderTextureFormat.R8);
                m_IndirectShadowSpatialFilterOutput.enableRandomWrite = true;
                m_IndirectShadowSpatialFilterOutput.Create();
            }

            if (m_IndirectShadowHistory == null)
            {
                m_IndirectShadowHistory = new RenderTexture(m_ScreenGatherRTSize.x, m_ScreenGatherRTSize.y, 0, RenderTextureFormat.R8);
                m_IndirectShadowHistory.enableRandomWrite = true;
                m_IndirectShadowHistory.Create();
            }
        }

        // specular
        {
            if (m_SpecularResolveOutputTexture == null)
            {
                m_SpecularResolveOutputTexture = new RenderTexture(m_SceneTextureRTSize.x, m_SceneTextureRTSize.y, 0, RenderTextureFormat.ARGBHalf);
                m_SpecularResolveOutputTexture.enableRandomWrite = true;
                m_SpecularResolveOutputTexture.Create();
            }

            if (m_SpecularTemporalFilterOutput == null)
            {
                m_SpecularTemporalFilterOutput = new RenderTexture(m_SceneTextureRTSize.x, m_SceneTextureRTSize.y, 0, RenderTextureFormat.ARGBHalf);
                m_SpecularTemporalFilterOutput.enableRandomWrite = true;
                m_SpecularTemporalFilterOutput.Create();
            }

            if (m_SpecularSpatialFilterOutput == null)
            {
                m_SpecularSpatialFilterOutput = new RenderTexture(m_SceneTextureRTSize.x, m_SceneTextureRTSize.y, 0, RenderTextureFormat.ARGBHalf);
                m_SpecularSpatialFilterOutput.enableRandomWrite = true;
                m_SpecularSpatialFilterOutput.Create();
            }

            if (m_SpecularIndirectHistory == null)
            {
                m_SpecularIndirectHistory = new RenderTexture(m_SceneTextureRTSize.x, m_SceneTextureRTSize.y, 0, RenderTextureFormat.ARGBHalf);
                m_SpecularIndirectHistory.enableRandomWrite = true;
                m_SpecularIndirectHistory.Create();
            }

            if (m_SpecularAccumulatedFrameTexture == null)
            {
                m_SpecularAccumulatedFrameTexture = new RenderTexture(m_SceneTextureRTSize.x, m_SceneTextureRTSize.y, 0, RenderTextureFormat.R8);
                m_SpecularAccumulatedFrameTexture.enableRandomWrite = true;
                m_SpecularAccumulatedFrameTexture.Create();
            }

            if (m_SpecularCompositeTexture == null)
            {
                m_SpecularCompositeTexture = new RenderTexture(m_SceneTextureRTSize.x, m_SceneTextureRTSize.y, 0, RenderTextureFormat.ARGBFloat);
                m_SpecularCompositeTexture.enableRandomWrite = true;
                m_SpecularCompositeTexture.Create();
            }
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

        if (m_TestOutput == null)
        {
            m_TestOutput = new RenderTexture(m_SceneTextureRTSize.x, m_SceneTextureRTSize.y, 0, RenderTextureFormat.ARGBFloat);
            m_TestOutput.enableRandomWrite = true;
            m_TestOutput.Create();
        }

        if (m_VoxelTraceRayCounter == null)
        {
            m_VoxelTraceRayCounter = new ComputeBuffer(1, sizeof(int));
        }

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
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_RWMiniDepthTexture"), m_MiniDepthTexture);

        cmd.DispatchCompute(m_ScreenGatherCS, kernel, Mathf.CeilToInt((float)m_ScreenGatherRTSize.x / 8), Mathf.CeilToInt((float)m_ScreenGatherRTSize.y / 8), 1);

        cmd.EndSample("Normal Depth Downsample");
    }

    void InitialSampleScreenTrace(ref RenderingData renderingData, CommandBuffer cmd, MiraiGIGPUScene scene, TraceMode traceMode)
    {
        m_VoxelTraceRayCounter.SetData(new int[1] { 0 });

        cmd.BeginSample("Screen Trace");

        MiraiGIClipmap clipmap = scene.miraiGIClipmap;
        MiraiGIRadianceCache radianceCache = scene.miraiGIRadianceCache;

        int kernel = m_ScreenGatherCS.FindKernel("InitialSampleScreenTrace");

        if (traceMode == TraceMode.TM_Specular)
        {
            cmd.EnableShaderKeyword("TRACE_SPECULAR_RAY");
        }
        else
        {
            cmd.DisableShaderKeyword("TRACE_SPECULAR_RAY");
        }

        Matrix4x4 worldToClip = Camera.main.projectionMatrix * Camera.main.worldToCameraMatrix;

        cmd.SetComputeVectorParam(m_ScreenGatherCS, Shader.PropertyToID("_CameraPosition"), Camera.main.transform.position);
        cmd.SetComputeMatrixParam(m_ScreenGatherCS, Shader.PropertyToID("_ProjMat"), Camera.main.projectionMatrix);
        cmd.SetComputeMatrixParam(m_ScreenGatherCS, Shader.PropertyToID("_WorldToClip"), worldToClip);

        // ReconstructWorldPositionFromDepth params
        float near = Camera.main.nearClipPlane;
        float far = Camera.main.farClipPlane;
        Vector4 zBufferParam = new Vector4(far / near - 1.0f, 1.0f, (far / near - 1.0f) / far, 1.0f / far);
        cmd.SetComputeVectorParam(m_ScreenGatherCS, Shader.PropertyToID("_ZBufferParam"), zBufferParam);
        cmd.SetComputeMatrixParam(m_ScreenGatherCS, Shader.PropertyToID("_InvProjMat"), Camera.main.projectionMatrix.inverse);
        cmd.SetComputeMatrixParam(m_ScreenGatherCS, Shader.PropertyToID("_InvViewMat"), Camera.main.cameraToWorldMatrix);

        cmd.SetComputeVectorParam(m_ScreenGatherCS, Shader.PropertyToID("_ScreenPositionScaleBias"), new Vector4(0.5f, -0.5f, 0.5f, 0.5f)); // is it right?
        cmd.SetComputeMatrixParam(m_ScreenGatherCS, Shader.PropertyToID("_ClipToPrevClip"), m_PrevClipMatrix * m_CurClipMatrix.inverse);

        Vector2 HZBUVFactor = new Vector2(1.0f, 1.0f);
        cmd.SetComputeVectorParam(m_ScreenGatherCS, Shader.PropertyToID("_HZBUvFactorAndInvFactor"), new Vector4(HZBUVFactor.x, HZBUVFactor.y, 1.0f / HZBUVFactor.x, 1.0f / HZBUVFactor.y));
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_HZB"), m_HiZBuffer);

        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_MotionVectors"), Shader.GetGlobalTexture("_MotionVectorTexture"));
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_GBufferNormal"), Shader.GetGlobalTexture("_GBuffer2"));

        // 
        cmd.SetComputeIntParam(m_ScreenGatherCS, Shader.PropertyToID("_FrameNumber"), m_FrameNumber);
        cmd.SetComputeVectorParam(m_ScreenGatherCS, Shader.PropertyToID("_ScreenGatherRTSize"), (Vector2)m_ScreenGatherRTSize);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_NormalDepthTexture"), m_NormalDepthTexture);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_NormalDepthHistory"), m_NormalDepthHistory);
        //cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_SceneColorHistory"), m_DirectLightingTexture); 
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_SceneColorHistory"), m_DiffuseCompositeTexture);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_RWInitialSampleRadiance"), m_InitialSampleRadiance);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_RWInitialSampleHitInfo"), m_InitialSampleHitInfo);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_RWInitialSampleRayInfo"), m_InitialSampleRayInfo);
        cmd.SetComputeBufferParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_RWVoxelTraceRayCounter"), m_VoxelTraceRayCounter);
        cmd.SetComputeBufferParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_RWVoxelTraceRayCompactBuffer"), m_VoxelTraceRayCompactBuffer);

        cmd.DispatchCompute(m_ScreenGatherCS, kernel, Mathf.CeilToInt((float)m_ScreenGatherRTSize.x / 8), Mathf.CeilToInt((float)m_ScreenGatherRTSize.y / 8), 1);

        cmd.EndSample("Screen Trace");
    }

    void InitialSampleVoxelTrace(CommandBuffer cmd, MiraiGIGPUScene scene, TraceMode traceMode)
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

            if (traceMode == TraceMode.TM_Specular)
            {
                cmd.EnableShaderKeyword("TRACE_SPECULAR_RAY");
            }
            else
            {
                cmd.DisableShaderKeyword("TRACE_SPECULAR_RAY");
            }

            int useROMA = GlobalSettings.Instance.useROMA;
            if (useROMA == 1)
            {
                cmd.EnableShaderKeyword("USE_BOM");
                cmd.DisableShaderKeyword("USE_ROMA");
            }
            else if (useROMA == 2)
            {
                cmd.DisableShaderKeyword("USE_BOM");
                cmd.EnableShaderKeyword("USE_ROMA");
            }
            else
            {
                cmd.DisableShaderKeyword("USE_BOM");
                cmd.DisableShaderKeyword("USE_ROMA");
            }

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
            cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_RWInitialSampleRayInfo"), m_InitialSampleRayInfo);
            cmd.SetComputeBufferParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_VoxelTraceRayCounter"), m_VoxelTraceRayCounter);
            cmd.SetComputeBufferParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_VoxelTraceRayCompactBuffer"), m_VoxelTraceRayCompactBuffer);

            cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_EnvMap"), scene.envMap);

            cmd.DispatchCompute(m_ScreenGatherCS, kernel, m_VoxelTraceIndirectArgs, 0);
        }

        cmd.EndSample("Voxel Trace");
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

        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_MotionVectors"), Shader.GetGlobalTexture("_MotionVectorTexture"));

        cmd.SetComputeIntParam(m_ScreenGatherCS, Shader.PropertyToID("_FrameNumber"), m_FrameNumber);
        cmd.SetComputeVectorParam(m_ScreenGatherCS, Shader.PropertyToID("_ScreenGatherRTSize"), (Vector2)m_ScreenGatherRTSize);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_InitialSampleRadiance"), m_InitialSampleRadiance);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_InitialSampleHitInfo"), m_InitialSampleHitInfo);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_InitialSampleRayInfo"), m_InitialSampleRayInfo);
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

    void ReservoirSpatialReuse(CommandBuffer cmd, MiraiGIGPUScene scene, ReservoirSource reservoirSource)
    {
        int curFrame = (m_FrameNumber + 0) % 2;
        int prevFrame = (m_FrameNumber + 1) % 2;

        cmd.BeginSample("Reservoir Spatial Reuse");

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
        cmd.SetComputeFloatParam(m_ScreenGatherCS, Shader.PropertyToID("_SpatialReuseSearchRange"), GlobalSettings.Instance.spatialReuseSearchRange);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_NormalDepthTexture"), m_NormalDepthTexture);

        if (reservoirSource == ReservoirSource.RS_Temporal)
        {
            cmd.SetComputeIntParam(m_ScreenGatherCS, Shader.PropertyToID("_SpatialReuseSampleCount"), GlobalSettings.Instance.spatialReuseSampleCount);

            cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_ReservoirDataA"), m_TemporalReservoirDataA[curFrame]);
            cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_ReservoirDataB"), m_TemporalReservoirDataB[curFrame]);
            cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_ReservoirDataC"), m_TemporalReservoirDataC[curFrame]);
            cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_ReservoirDataD"), m_TemporalReservoirDataD[curFrame]);

            cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_RWReservoirDataA"), m_SpatialReservoirDataA[0]);
            cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_RWReservoirDataB"), m_SpatialReservoirDataB[0]);
            cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_RWReservoirDataC"), m_SpatialReservoirDataC[0]);
            cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_RWReservoirDataD"), m_SpatialReservoirDataD[0]);
        }
        else
        {
            cmd.SetComputeIntParam(m_ScreenGatherCS, Shader.PropertyToID("_SpatialReuseSampleCount"), GlobalSettings.Instance.spatialSecondaryReuseSampleCount);

            cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_ReservoirDataA"), m_SpatialReservoirDataA[0]);
            cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_ReservoirDataB"), m_SpatialReservoirDataB[0]);
            cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_ReservoirDataC"), m_SpatialReservoirDataC[0]);
            cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_ReservoirDataD"), m_SpatialReservoirDataD[0]);

            cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_RWReservoirDataA"), m_SpatialReservoirDataA[1]);
            cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_RWReservoirDataB"), m_SpatialReservoirDataB[1]);
            cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_RWReservoirDataC"), m_SpatialReservoirDataC[1]);
            cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_RWReservoirDataD"), m_SpatialReservoirDataD[1]);
        }
        

        cmd.DispatchCompute(m_ScreenGatherCS, kernel, Mathf.CeilToInt((float)m_ScreenGatherRTSize.x / 8), Mathf.CeilToInt((float)m_ScreenGatherRTSize.y / 8), 1);

        cmd.EndSample("Reservoir Spatial Reuse");
    }

    void ReservoirEvaluateIrradiance(CommandBuffer cmd, MiraiGIGPUScene scene, ReservoirSource reservoirSource)
    {
        cmd.BeginSample("Reservoir Evaluate Irradiance");

        bool indirectShadowEnable = (reservoirSource == ReservoirSource.RS_Spatial) && (GlobalSettings.Instance.indirectShadowEnable > 0);
        if (indirectShadowEnable)
        {
            m_ScreenGatherCS.EnableKeyword("INDIRECT_SHADOW_ENABLE");
        }
        else
        {
            m_ScreenGatherCS.DisableKeyword("INDIRECT_SHADOW_ENABLE");
        }

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
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_MiniDepthTexture"), m_MiniDepthTexture);
        if (reservoirSource == ReservoirSource.RS_Spatial)
        {
            cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_IrradianceFallbackTexture"), m_TemporalReservoirIrradiance);

            int spatialReservoirSource = GlobalSettings.Instance.spatialSecondaryReuse > 0 ? 1 : 0;
            cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_ReservoirDataA"), m_SpatialReservoirDataA[spatialReservoirSource]);
            cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_ReservoirDataB"), m_SpatialReservoirDataB[spatialReservoirSource]);
            cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_ReservoirDataC"), m_SpatialReservoirDataC[spatialReservoirSource]);
            cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_ReservoirDataD"), m_SpatialReservoirDataD[spatialReservoirSource]);

            cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_RWReservoirIrradiance"), m_SpatialReservoirIrradiance);
        }
        else
        {
            int temporalReservoirCurFrame = (m_FrameNumber + 0) % 2;
            cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_ReservoirDataA"), m_TemporalReservoirDataA[temporalReservoirCurFrame]);
            cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_ReservoirDataB"), m_TemporalReservoirDataB[temporalReservoirCurFrame]);
            cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_ReservoirDataC"), m_TemporalReservoirDataC[temporalReservoirCurFrame]);
            cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_ReservoirDataD"), m_TemporalReservoirDataD[temporalReservoirCurFrame]);

            cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_RWReservoirIrradiance"), m_TemporalReservoirIrradiance);
        }
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_RWIndirectShadowTexture"), m_IndirectShadowTexture);

        cmd.DispatchCompute(m_ScreenGatherCS, kernel, Mathf.CeilToInt((float)m_ScreenGatherRTSize.x / 8), Mathf.CeilToInt((float)m_ScreenGatherRTSize.y / 8), 1);

        cmd.EndSample("Reservoir Evaluate Irradiance");
    }

    void RenderFilterGuidanceSSAO(CommandBuffer cmd, MiraiGIGPUScene scene)
    {
        cmd.BeginSample("Render Filter Guidance SSAO");

        int kernel = m_ScreenGatherCS.FindKernel("FilterGuidanceSSAO");

        RenderTexture diffuseResolveInput = RenderTexture.GetTemporary(m_DiffuseResolveOutputTexture.descriptor);
        cmd.Blit(m_DiffuseResolveOutputTexture, diffuseResolveInput);

        cmd.SetComputeVectorParam(m_ScreenGatherCS, Shader.PropertyToID("_CameraPosition"), Camera.main.transform.position);

        // ReconstructWorldPositionFromDepth params
        float near = Camera.main.nearClipPlane;
        float far = Camera.main.farClipPlane;
        Vector4 zBufferParam = new Vector4(far / near - 1.0f, 1.0f, (far / near - 1.0f) / far, 1.0f / far);
        cmd.SetComputeVectorParam(m_ScreenGatherCS, Shader.PropertyToID("_ZBufferParam"), zBufferParam);
        cmd.SetComputeMatrixParam(m_ScreenGatherCS, Shader.PropertyToID("_InvProjMat"), Camera.main.projectionMatrix.inverse);
        cmd.SetComputeMatrixParam(m_ScreenGatherCS, Shader.PropertyToID("_InvViewMat"), Camera.main.cameraToWorldMatrix);

        Matrix4x4 translatedWorldToView = Camera.main.worldToCameraMatrix;
        translatedWorldToView.m03 = translatedWorldToView.m13 = translatedWorldToView.m23 = 0;
        translatedWorldToView.m30 = translatedWorldToView.m31 = translatedWorldToView.m32 = 0;
        translatedWorldToView.m33 = 1;
        cmd.SetComputeMatrixParam(m_ScreenGatherCS, Shader.PropertyToID("_ViewMat"), Camera.main.worldToCameraMatrix);
        cmd.SetComputeMatrixParam(m_ScreenGatherCS, Shader.PropertyToID("_TranslatedWorldToView"), translatedWorldToView);

        cmd.SetComputeIntParam(m_ScreenGatherCS, Shader.PropertyToID("_FrameNumber"), m_FrameNumber);
        cmd.SetComputeVectorParam(m_ScreenGatherCS, Shader.PropertyToID("_ScreenGatherRTSize"), (Vector2)m_ScreenGatherRTSize);
        cmd.SetComputeFloatParam(m_ScreenGatherCS, Shader.PropertyToID("_AOWorldRange"), GlobalSettings.Instance.filterGuidanceSSAORange);
        cmd.SetComputeFloatParam(m_ScreenGatherCS, Shader.PropertyToID("_AOSharpness"), GlobalSettings.Instance.filterGuidanceSSAOSharpness);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_NormalDepthTexture"), m_NormalDepthTexture);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_MiniDepthTexture"), m_MiniDepthTexture);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_DiffuseResolveInput"), diffuseResolveInput);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_RWDiffuseResolveOutputTexture"), m_DiffuseResolveOutputTexture);

        cmd.DispatchCompute(m_ScreenGatherCS, kernel, Mathf.CeilToInt((float)m_ScreenGatherRTSize.x / 8), Mathf.CeilToInt((float)m_ScreenGatherRTSize.y / 8), 1);

        RenderTexture.ReleaseTemporary(diffuseResolveInput);

        cmd.EndSample("Render Filter Guidance SSAO");
    }

    void DiffuseTemporalFilter(CommandBuffer cmd, MiraiGIGPUScene scene)
    {
        cmd.BeginSample("Diffuse Temporal Filter");

        int kernel = m_ScreenGatherCS.FindKernel("DiffuseTemporalFilter");

        RenderTexture diffuseAccumulatedFrameTextureInput = RenderTexture.GetTemporary(m_DiffuseResolveOutputTexture.descriptor);
        cmd.Blit(m_DiffuseAccumulatedFrameTexture, diffuseAccumulatedFrameTextureInput);

        cmd.SetComputeVectorParam(m_ScreenGatherCS, Shader.PropertyToID("_CameraPosition"), Camera.main.transform.position);

        // ReconstructWorldPositionFromDepth params
        float near = Camera.main.nearClipPlane;
        float far = Camera.main.farClipPlane;
        Vector4 zBufferParam = new Vector4(far / near - 1.0f, 1.0f, (far / near - 1.0f) / far, 1.0f / far);
        cmd.SetComputeVectorParam(m_ScreenGatherCS, Shader.PropertyToID("_ZBufferParam"), zBufferParam);
        cmd.SetComputeMatrixParam(m_ScreenGatherCS, Shader.PropertyToID("_InvProjMat"), Camera.main.projectionMatrix.inverse);
        cmd.SetComputeMatrixParam(m_ScreenGatherCS, Shader.PropertyToID("_InvViewMat"), Camera.main.cameraToWorldMatrix);

        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_MotionVectors"), Shader.GetGlobalTexture("_MotionVectorTexture"));

        cmd.SetComputeIntParam(m_ScreenGatherCS, Shader.PropertyToID("_FrameNumber"), m_FrameNumber);
        cmd.SetComputeVectorParam(m_ScreenGatherCS, Shader.PropertyToID("_ScreenGatherRTSize"), (Vector2)m_ScreenGatherRTSize);
        cmd.SetComputeFloatParam(m_ScreenGatherCS, Shader.PropertyToID("_MaxAccumulatedFrame"), GlobalSettings.Instance.diffuseMaxAccumulatedFrame);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_NormalDepthTexture"), m_NormalDepthTexture);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_NormalDepthHistory"), m_NormalDepthHistory);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_DiffuseIndirectTexture"), m_DiffuseResolveOutputTexture);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_DiffuseIndirectHistory"), m_DiffuseIndirectHistory);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_AccumulatedFrameTexture"), diffuseAccumulatedFrameTextureInput);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_IndirectShadowTexture"), m_IndirectShadowTexture);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_IndirectShadowHistory"), m_IndirectShadowHistory);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_RWDiffuseTemporalFilterOutput"), m_DiffuseTemporalFilterOutput);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_RWIndirectShadowTemporalFilterOutput"), m_IndirectShadowTemporalFilterOutput);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_RWAccumulatedFrameTexture"), m_DiffuseAccumulatedFrameTexture);

        cmd.DispatchCompute(m_ScreenGatherCS, kernel, Mathf.CeilToInt((float)m_ScreenGatherRTSize.x / 8), Mathf.CeilToInt((float)m_ScreenGatherRTSize.y / 8), 1);

        cmd.Blit(m_DiffuseTemporalFilterOutput, m_DiffuseIndirectHistory);
        cmd.Blit(m_IndirectShadowTemporalFilterOutput, m_IndirectShadowHistory);

        RenderTexture.ReleaseTemporary(diffuseAccumulatedFrameTextureInput);

        cmd.EndSample("Diffuse Temporal Filter");
    }

    void DiffuseSpatialFilter(CommandBuffer cmd, MiraiGIGPUScene scene)
    {
        cmd.BeginSample("Diffuse Spatial Filter");

        RenderTexture diffuseSwapTexture = RenderTexture.GetTemporary(m_DiffuseSpatialFilterOutput.descriptor);
        RenderTexture indirectShadowSwapTexture = RenderTexture.GetTemporary(m_IndirectShadowSpatialFilterOutput.descriptor);

        int iterrationCount = Mathf.Clamp(GlobalSettings.Instance.diffuseSpatialFilterIterationCount, 1, 10);
        SpatialFilter(cmd, m_DiffuseTemporalFilterOutput, m_DiffuseSpatialFilterOutput, 1,
                            m_IndirectShadowTemporalFilterOutput, m_IndirectShadowSpatialFilterOutput, true);
        for (int i = 1; i < iterrationCount; i++)
        {
            bool filterIndirectShadow = i < GlobalSettings.Instance.indirectShadowSpatialFilterIterationCount;

            SpatialFilter(cmd, m_DiffuseSpatialFilterOutput, diffuseSwapTexture, (1 << i),
                                m_IndirectShadowSpatialFilterOutput, indirectShadowSwapTexture, filterIndirectShadow);

            RenderTexture temp = m_DiffuseSpatialFilterOutput;
            m_DiffuseSpatialFilterOutput = diffuseSwapTexture;
            diffuseSwapTexture = temp;

            if (filterIndirectShadow)
            {
                RenderTexture tempShadow = m_IndirectShadowSpatialFilterOutput;
                m_IndirectShadowSpatialFilterOutput = indirectShadowSwapTexture;
                indirectShadowSwapTexture = tempShadow;
            }
        }

        RenderTexture.ReleaseTemporary(diffuseSwapTexture);
        RenderTexture.ReleaseTemporary(indirectShadowSwapTexture);

        cmd.EndSample("Diffuse Spatial Filter");
    }

    void SpatialFilter(CommandBuffer cmd, RenderTexture inputTex, RenderTexture outputTex, int filterRadius,
                        RenderTexture inputShadowTex, RenderTexture outputShadowTex, bool filterIndirectShadow)
    {
        if (filterIndirectShadow)
        {
            m_ScreenGatherCS.EnableKeyword("INDIRECT_SHADOW_ENABLE");
        }
        else
        {
            m_ScreenGatherCS.DisableKeyword("INDIRECT_SHADOW_ENABLE");
        }

        int kernel = m_ScreenGatherCS.FindKernel("DiffuseSpatialFilter");

        cmd.SetComputeVectorParam(m_ScreenGatherCS, Shader.PropertyToID("_CameraPosition"), Camera.main.transform.position);

        // ReconstructWorldPositionFromDepth params
        float near = Camera.main.nearClipPlane;
        float far = Camera.main.farClipPlane;
        Vector4 zBufferParam = new Vector4(far / near - 1.0f, 1.0f, (far / near - 1.0f) / far, 1.0f / far);
        cmd.SetComputeVectorParam(m_ScreenGatherCS, Shader.PropertyToID("_ZBufferParam"), zBufferParam);
        cmd.SetComputeMatrixParam(m_ScreenGatherCS, Shader.PropertyToID("_InvProjMat"), Camera.main.projectionMatrix.inverse);
        cmd.SetComputeMatrixParam(m_ScreenGatherCS, Shader.PropertyToID("_InvViewMat"), Camera.main.cameraToWorldMatrix);

        cmd.SetComputeIntParam(m_ScreenGatherCS, Shader.PropertyToID("_FrameNumber"), m_FrameNumber);
        cmd.SetComputeVectorParam(m_ScreenGatherCS, Shader.PropertyToID("_RTSize"), new Vector2(inputTex.descriptor.width, inputTex.descriptor.height));
        cmd.SetComputeFloatParam(m_ScreenGatherCS, Shader.PropertyToID("_FilterRadius"), filterRadius);
        cmd.SetComputeFloatParam(m_ScreenGatherCS, Shader.PropertyToID("_SSAOGuidanceWeight"), GlobalSettings.Instance.filterGuidanceSSAOWeight);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_NormalDepthTexture"), m_NormalDepthTexture);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_NormalDepthHistory"), m_NormalDepthHistory);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_DiffuseIndirectTexture"), inputTex);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_RWSpatialFilterOutput"), outputTex);

        if (filterIndirectShadow)
        {
            cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_IndirectShadowTexture"), inputShadowTex);
            cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_RWShadowOutput"), outputShadowTex);
        }

        cmd.DispatchCompute(m_ScreenGatherCS, kernel, Mathf.CeilToInt((float)m_ScreenGatherRTSize.x / 8), Mathf.CeilToInt((float)m_ScreenGatherRTSize.y / 8), 1);
    }

    void SpecularResolve(CommandBuffer cmd, MiraiGIGPUScene scene)
    {
        cmd.BeginSample("Specular Resolve");

        int kernel = m_ScreenGatherCS.FindKernel("SpecularResolve");

        cmd.SetComputeVectorParam(m_ScreenGatherCS, Shader.PropertyToID("_CameraPosition"), Camera.main.transform.position);

        // ReconstructWorldPositionFromDepth params
        float near = Camera.main.nearClipPlane;
        float far = Camera.main.farClipPlane;
        Vector4 zBufferParam = new Vector4(far / near - 1.0f, 1.0f, (far / near - 1.0f) / far, 1.0f / far);
        cmd.SetComputeVectorParam(m_ScreenGatherCS, Shader.PropertyToID("_ZBufferParam"), zBufferParam);
        cmd.SetComputeMatrixParam(m_ScreenGatherCS, Shader.PropertyToID("_InvProjMat"), Camera.main.projectionMatrix.inverse);
        cmd.SetComputeMatrixParam(m_ScreenGatherCS, Shader.PropertyToID("_InvViewMat"), Camera.main.cameraToWorldMatrix);

        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_GBufferSpecular"), Shader.GetGlobalTexture("_GBuffer1"));
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_GBufferNormal"), Shader.GetGlobalTexture("_GBuffer2"));
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_SceneDepthTexture"), Shader.GetGlobalTexture("_CameraDepthTexture"));

        cmd.SetComputeIntParam(m_ScreenGatherCS, Shader.PropertyToID("_FrameNumber"), m_FrameNumber);
        cmd.SetComputeFloatParam(m_ScreenGatherCS, Shader.PropertyToID("_SpecularResolveSearchRange"), GlobalSettings.Instance.specularResolveSearchRange);
        cmd.SetComputeVectorParam(m_ScreenGatherCS, Shader.PropertyToID("_ScreenGatherRTSize"), (Vector2)m_ScreenGatherRTSize);
        cmd.SetComputeVectorParam(m_ScreenGatherCS, Shader.PropertyToID("_SceneTextureRTSize"), (Vector2)m_SceneTextureRTSize);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_InitialSampleRadiance"), m_InitialSampleRadiance);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_InitialSampleHitInfo"), m_InitialSampleHitInfo);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_InitialSampleRayInfo"), m_InitialSampleRayInfo);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_NormalDepthTexture"), m_NormalDepthTexture);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_RWSpecularResolveOutputTexture"), m_SpecularResolveOutputTexture);

        // @TODO: brdfLUT

        cmd.DispatchCompute(m_ScreenGatherCS, kernel, m_SceneTextureRTSize.x / 8, m_SceneTextureRTSize.y / 8, 1);

        cmd.EndSample("Specular Resolve");
    }

    void SpecularTemporalFilter(CommandBuffer cmd, MiraiGIGPUScene scene)
    {
        cmd.BeginSample("Specular Temporal Filter");

        int kernel = m_ScreenGatherCS.FindKernel("SpecularTemporalFilter");

        RenderTexture specularAccumulatedFrameTextureIntput = RenderTexture.GetTemporary(m_SpecularAccumulatedFrameTexture.descriptor);
        cmd.Blit(m_SpecularAccumulatedFrameTexture, specularAccumulatedFrameTextureIntput);

        cmd.SetComputeVectorParam(m_ScreenGatherCS, Shader.PropertyToID("_CameraPosition"), Camera.main.transform.position);

        // ReconstructWorldPositionFromDepth params
        float near = Camera.main.nearClipPlane;
        float far = Camera.main.farClipPlane;
        Vector4 zBufferParam = new Vector4(far / near - 1.0f, 1.0f, (far / near - 1.0f) / far, 1.0f / far);
        cmd.SetComputeVectorParam(m_ScreenGatherCS, Shader.PropertyToID("_ZBufferParam"), zBufferParam);
        cmd.SetComputeMatrixParam(m_ScreenGatherCS, Shader.PropertyToID("_InvProjMat"), Camera.main.projectionMatrix.inverse);
        cmd.SetComputeMatrixParam(m_ScreenGatherCS, Shader.PropertyToID("_InvViewMat"), Camera.main.cameraToWorldMatrix);

        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_GBufferNormal"), Shader.GetGlobalTexture("_GBuffer2"));
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_SceneDepthTexture"), Shader.GetGlobalTexture("_CameraDepthTexture"));
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_MotionVectors"), Shader.GetGlobalTexture("_MotionVectorTexture"));

        cmd.SetComputeIntParam(m_ScreenGatherCS, Shader.PropertyToID("_FrameNumber"), m_FrameNumber);
        cmd.SetComputeFloatParam(m_ScreenGatherCS, Shader.PropertyToID("_MaxAccumulatedFrame"), GlobalSettings.Instance.specularMaxAccumulatedFrame);
        cmd.SetComputeVectorParam(m_ScreenGatherCS, Shader.PropertyToID("_SceneTextureRTSize"), (Vector2)m_SceneTextureRTSize);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_SpecularIndirectTexture"), m_SpecularResolveOutputTexture);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_SpecularIndirectHistory"), m_SpecularIndirectHistory);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_AccumulatedFrameTexture"), specularAccumulatedFrameTextureIntput);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_NormalDepthTexture"), m_NormalDepthTexture);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_NormalDepthHistory"), m_NormalDepthHistory);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_RWSpecularTemporalFilterOutput"), m_SpecularTemporalFilterOutput);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_RWAccumulatedFrameTexture"), m_SpecularAccumulatedFrameTexture);

        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_RWTestOutput"), m_TestOutput);

        // @TODO: brdfLUT
        //cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_PreIntegratedGF"), );

        cmd.DispatchCompute(m_ScreenGatherCS, kernel, m_SceneTextureRTSize.x / 8, m_SceneTextureRTSize.y / 8, 1);

        cmd.Blit(m_SpecularTemporalFilterOutput, m_SpecularIndirectHistory);

        RenderTexture.ReleaseTemporary(specularAccumulatedFrameTextureIntput); 

        cmd.EndSample("Specular Temporal Filter");
    }

    void SpecularSpatialFilter(CommandBuffer cmd, MiraiGIGPUScene scene)
    {
        cmd.BeginSample("Specular Spatial Filter");

        int kernel = m_ScreenGatherCS.FindKernel("SpecularSpatialFilter");

        cmd.SetComputeVectorParam(m_ScreenGatherCS, Shader.PropertyToID("_CameraPosition"), Camera.main.transform.position);

        // ReconstructWorldPositionFromDepth params
        float near = Camera.main.nearClipPlane;
        float far = Camera.main.farClipPlane;
        Vector4 zBufferParam = new Vector4(far / near - 1.0f, 1.0f, (far / near - 1.0f) / far, 1.0f / far);
        cmd.SetComputeVectorParam(m_ScreenGatherCS, Shader.PropertyToID("_ZBufferParam"), zBufferParam);
        cmd.SetComputeMatrixParam(m_ScreenGatherCS, Shader.PropertyToID("_InvProjMat"), Camera.main.projectionMatrix.inverse);
        cmd.SetComputeMatrixParam(m_ScreenGatherCS, Shader.PropertyToID("_InvViewMat"), Camera.main.cameraToWorldMatrix);

        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_GBufferNormal"), Shader.GetGlobalTexture("_GBuffer2"));
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_SceneDepthTexture"), Shader.GetGlobalTexture("_CameraDepthTexture"));

        cmd.SetComputeIntParam(m_ScreenGatherCS, Shader.PropertyToID("_FrameNumber"), m_FrameNumber);
        cmd.SetComputeVectorParam(m_ScreenGatherCS, Shader.PropertyToID("_RTSize"), (Vector2)m_SceneTextureRTSize);
        cmd.SetComputeFloatParam(m_ScreenGatherCS, Shader.PropertyToID("_FilterRadius"), GlobalSettings.Instance.specularFilterSearchRange);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_SpecularIndirectTexture"), m_SpecularTemporalFilterOutput);
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_RWSpecularSpatialFilterOutput"), m_SpecularSpatialFilterOutput);

        cmd.DispatchCompute(m_ScreenGatherCS, kernel, m_SceneTextureRTSize.x / 8, m_SceneTextureRTSize.y / 8, 1);

        cmd.EndSample("Specular Spatial Filter");
    }
}