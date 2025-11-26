using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public class MiraiGIScreenGatherResources
{
    public RenderTexture screenGatherOutputTexture;
    public RenderTexture diffuseIndirectHistory;

    public void Init()
    {
        screenGatherOutputTexture = new RenderTexture(Camera.main.pixelWidth, Camera.main.pixelHeight, 0, RenderTextureFormat.RGB111110Float);
        screenGatherOutputTexture.enableRandomWrite = true;
        screenGatherOutputTexture.Create();
    }

    public void Release()
    {
        screenGatherOutputTexture?.Release();
        diffuseIndirectHistory?.Release();

        screenGatherOutputTexture = null;
        diffuseIndirectHistory = null;
    }
}

public class MiraiGIScreenGather
{
    ComputeShader m_ScreenGatherCS;

    public void Init()
    {
        m_ScreenGatherCS = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/Shaders/MiraiGI/ScreenGather/ScreenGather.compute");
    }

    public void DiffuseIndirectScreenGather(MiraiGIGPUScene scene)
    {
        CommandBuffer cmd = CommandBufferPool.Get("Diffuse Indirect Screen Gather");

        MiraiGIClipmap clipmap = scene.miraiGIClipmap;
        MiraiGIRadianceCache radianceCache = scene.miraiGIRadianceCache;
        MiraiGIScreenGatherResources screenGatherResources = radianceCache.screenGatherResources;

        int kernel = m_ScreenGatherCS.FindKernel("ScreenGather");

        clipmap.SetupVoxelRaytracingParameters(cmd, m_ScreenGatherCS, kernel, scene);
        radianceCache.SetupProbeVolumeParameters(cmd, m_ScreenGatherCS, kernel, scene);

        cmd.SetComputeVectorParam(m_ScreenGatherCS, Shader.PropertyToID("_ScreenResolution"), new Vector4(Camera.main.pixelWidth, Camera.main.pixelHeight));
        cmd.SetComputeVectorParam(m_ScreenGatherCS, Shader.PropertyToID("_CameraPosition"), Camera.main.transform.position);
        cmd.SetComputeMatrixParam(m_ScreenGatherCS, Shader.PropertyToID("_ProjMat"), Camera.main.projectionMatrix);
        cmd.SetComputeMatrixParam(m_ScreenGatherCS, Shader.PropertyToID("_ViewProjMat"), Camera.main.projectionMatrix * Camera.main.worldToCameraMatrix);
        cmd.SetComputeMatrixParam(m_ScreenGatherCS, Shader.PropertyToID("_InvViewProjMat"), (Camera.main.projectionMatrix * Camera.main.worldToCameraMatrix).inverse);
        cmd.SetComputeIntParam(m_ScreenGatherCS, Shader.PropertyToID("_FrameCountMod8"), (int)radianceCache.frameNumberRenderThread % 8);

        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_GBuffer0"), Shader.GetGlobalTexture("_GBuffer0"));
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_GBuffer1"), Shader.GetGlobalTexture("_GBuffer2"));
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_SceneDepthTexture"), Shader.GetGlobalTexture("_CameraDepthTexture"));
        cmd.SetComputeTextureParam(m_ScreenGatherCS, kernel, Shader.PropertyToID("_RWScreenGatherOutput"), screenGatherResources.screenGatherOutputTexture);

        cmd.DispatchCompute(m_ScreenGatherCS, kernel, Mathf.CeilToInt((float)Camera.main.pixelWidth / 8), Mathf.CeilToInt((float)Camera.main.pixelHeight / 8), 1);

        Graphics.ExecuteCommandBuffer(cmd);
        CommandBufferPool.Release(cmd);
    }

    public void VisualizeMiraiGIScreenGather(CommandBuffer cmd, MiraiGIGPUScene scene, RenderTargetIdentifier sceneColorTexture)
    {
        MiraiGIRadianceCache radianceCache = scene.miraiGIRadianceCache;
        MiraiGIScreenGatherResources screenGatherResources = radianceCache.screenGatherResources;

        cmd.Blit(screenGatherResources.screenGatherOutputTexture, sceneColorTexture);
    }
}