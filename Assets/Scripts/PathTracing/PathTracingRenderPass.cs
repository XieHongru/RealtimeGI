using System.Collections;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using static Unity.VisualScripting.Member;

public class PathTracingRenderPass : ScriptableRenderPass
{
    private RayTracingAccelerationStructure rayTracingAccelerationStructure = null;
    public RayTracingShader rayTracingShader = null;

    private RenderTexture rayTracingOutput = null;

    //光线相关
    public Vector3 sunDirection = new Vector3(1.0f, 0.0f, 0.0f);
    public Vector3 sunColor = new Vector3(1.0f,1.0f, 1.0f);

    //时域降噪相关
    private Matrix4x4 prevCameraMatrix;
    private int prevBounceCountOpaque;
    private int prevBounceCountTransparent;

    public int bounceCountOpaque = 4;
    public int bounceCountTransparent = 4;

    public int convergenceStep = 0;
    public PathTracingRenderPass()
    {
        // 初始化
        prevCameraMatrix = Matrix4x4.identity;
        prevBounceCountOpaque = bounceCountOpaque;
        prevBounceCountTransparent = bounceCountTransparent;
    }

    public void setup()
    {
        CreateRayTracingAccelerationStructure();
        bounceCountOpaque = PathTracingSettings.Instance.bounceCountOpaque;
        bounceCountTransparent = PathTracingSettings.Instance.bounceCountTransparent;
        rayTracingShader = PathTracingSettings.Instance.rayTracingShader;
        sunDirection = PathTracingSettings.Instance.sunDirection;
        sunColor = PathTracingSettings.Instance.sunColor;
        if (rayTracingOutput)
            rayTracingOutput.Release();

        RenderTextureDescriptor rtDesc = new RenderTextureDescriptor()
        {
            dimension = TextureDimension.Tex2D,
            width = Camera.main.pixelWidth,
            height = Camera.main.pixelHeight,
            depthBufferBits = 0,
            volumeDepth = 1,
            msaaSamples = 1,
            vrUsage = VRTextureUsage.OneEye,
            graphicsFormat = GraphicsFormat.R32G32B32A32_SFloat,
            enableRandomWrite = true,
        };

        rayTracingOutput = new RenderTexture(rtDesc);
        rayTracingOutput.Create();
    }
    public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
    {
        if (!Application.isPlaying)
        {
            return;
        }
        //Debug.Log(rayTracingAccelerationStructure.GetInstanceCount());
        Debug.Log(rayTracingOutput.IsCreated());
        RenderTargetIdentifier cameraTarget = renderingData.cameraData.renderer.cameraColorTargetHandle;
        CommandBuffer cmd = CommandBufferPool.Get("Blit Visualize Result");
        //不支持光追的处理
        if (!SystemInfo.supportsRayTracing || !rayTracingShader)
        {
            Debug.LogWarning("RayTracing not supported.");
            cmd.Blit(cameraTarget, cameraTarget); // 简单拷贝

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
            ReleaseResources();
            return;
        }
        //cmd.Blit(, cameraTarget);
        
        if (rayTracingAccelerationStructure == null)
            return;
        var camera = renderingData.cameraData.camera;

        // 1. 相机矩阵变化 -> reset
        if (prevCameraMatrix != camera.cameraToWorldMatrix)
            convergenceStep = 0;

        // 2. BounceCount 变化 -> reset
        if (prevBounceCountOpaque != bounceCountOpaque)
            convergenceStep = 0;

        if (prevBounceCountTransparent != bounceCountTransparent)
            convergenceStep = 0;
        // Not really needed per frame if the scene is static.
        rayTracingAccelerationStructure.Build();

        //rayTracingShader.SetShaderPass("PathTracing");

        Shader.SetGlobalInt(Shader.PropertyToID("g_BounceCountOpaque"), (int)bounceCountOpaque);
        Shader.SetGlobalInt(Shader.PropertyToID("g_BounceCountTransparent"), (int)bounceCountTransparent);
        // Input
        rayTracingShader.SetAccelerationStructure(Shader.PropertyToID("g_AccelStruct"), rayTracingAccelerationStructure);
        rayTracingShader.SetFloat(Shader.PropertyToID("g_Zoom"), Mathf.Tan(Mathf.Deg2Rad * Camera.main.fieldOfView * 0.5f));
        rayTracingShader.SetFloat(Shader.PropertyToID("g_AspectRatio"), camera.pixelWidth / (float)camera.pixelHeight);
        rayTracingShader.SetInt(Shader.PropertyToID("g_ConvergenceStep"), convergenceStep);
        rayTracingShader.SetInt(Shader.PropertyToID("g_FrameIndex"), Time.frameCount);
        rayTracingShader.SetVector(Shader.PropertyToID("_SunDirection"), sunDirection.normalized);
        rayTracingShader.SetVector(Shader.PropertyToID("_SunColor"),sunColor.normalized);

        rayTracingShader.SetTexture(Shader.PropertyToID("g_Radiance"), rayTracingOutput);
        int threadGroupSize = 8;
        int threadGroupX = Mathf.CeilToInt(camera.pixelWidth / (float)threadGroupSize);
        int threadGroupY = Mathf.CeilToInt(camera.pixelHeight / (float)threadGroupSize);

        rayTracingShader.Dispatch("MainRayGenShader", threadGroupX, threadGroupY, 1);

        cmd.Blit(rayTracingOutput, cameraTarget);
        context.ExecuteCommandBuffer(cmd);
        CommandBufferPool.Release(cmd);
        context.Submit();

        convergenceStep++;

        prevCameraMatrix = Camera.main.cameraToWorldMatrix;
        prevBounceCountOpaque = bounceCountOpaque;
        prevBounceCountTransparent = bounceCountTransparent;
    }

    private void CreateRayTracingAccelerationStructure()
    {
        if (rayTracingAccelerationStructure == null)
        {
            RayTracingAccelerationStructure.RASSettings settings = new RayTracingAccelerationStructure.RASSettings();
            settings.rayTracingModeMask = RayTracingAccelerationStructure.RayTracingModeMask.Everything;
            settings.managementMode = RayTracingAccelerationStructure.ManagementMode.Automatic;
            settings.layerMask = 255;

            rayTracingAccelerationStructure = new RayTracingAccelerationStructure(settings);
            AddSceneObjectsToAccelerationStructure();
        }
    }
    private void ReleaseResources()
    {
        if (rayTracingAccelerationStructure != null)
        {
            rayTracingAccelerationStructure.Release();
            rayTracingAccelerationStructure = null;
        }
    }

    void AddSceneObjectsToAccelerationStructure()
    {
        if(rayTracingAccelerationStructure!=null)
        {
            foreach (var meshRenderer in Object.FindObjectsOfType<MeshRenderer>())
            {
                rayTracingAccelerationStructure.AddInstance(meshRenderer);
            }
        }
    }
}
