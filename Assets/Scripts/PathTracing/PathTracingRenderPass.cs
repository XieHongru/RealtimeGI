using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using static UnityEngine.GraphicsBuffer;

public class PathTracingRenderPass : ScriptableRenderPass
{
    RayTracingShader rayTracingShader;
    RayTracingAccelerationStructure accelerationStructure = null;
    RenderTexture targetRT;
    Texture2D source;
    int times = 1;
    public void setup()
    {
        rayTracingShader = PathTracingSettings.Instance.rayTracingShader;
        source = PathTracingSettings.Instance.texture;
        if (accelerationStructure == null)
        {
            accelerationStructure= new RayTracingAccelerationStructure();
            accelerationStructure.Build();
        }
        targetRT = CreateRenderTarget(2048, 1024);
    }
    public PathTracingRenderPass()
    {

    }
    RenderTexture CreateRenderTarget(int width, int height)
    {
        var rt = new RenderTexture(width, height, 0,
            UnityEngine.Experimental.Rendering.GraphicsFormat.R32G32B32A32_SFloat)
        {
            enableRandomWrite = true,
            dimension = UnityEngine.Rendering.TextureDimension.Tex2D,
            useMipMap = false,
            autoGenerateMips = false,
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
            name = "PathTracingRT"
        };

        rt.Create();
        //FillRed(rt);
        return rt;
    }

    public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
    {
        if (!Application.isPlaying)
        {
            return;
        }
        accelerationStructure.Build();
        RenderTargetIdentifier cameraTarget = renderingData.cameraData.renderer.cameraColorTargetHandle;
        CommandBuffer cmd = CommandBufferPool.Get("Blit Visualize Result");

        cmd.SetRayTracingAccelerationStructure(rayTracingShader, "_AccelerationStructure", accelerationStructure);
        cmd.SetRayTracingTextureParam(rayTracingShader, "RenderTarget", targetRT);
        cmd.SetRayTracingFloatParam(rayTracingShader, "_FrameIndex", Time.frameCount);

        cmd.DispatchRays(rayTracingShader, "MyRaygenShader", (uint)targetRT.width, (uint)targetRT.height, 1);
        //if (times >= 1)
        //{
        //    --times;
        //    SaveRenderTextureToPNG(targetRT, "Assets/Scripts/PathTracing/a.png");
        //}
        cmd.Blit(targetRT, cameraTarget);

        context.ExecuteCommandBuffer(cmd);
        CommandBufferPool.Release(cmd);
        context.Submit();
    }
    public static void SaveRenderTextureToPNG(RenderTexture rt, string path)
    {
        // 临时 RT：转成可写 PNG 格式
        RenderTexture temp = RenderTexture.GetTemporary(
            rt.width, rt.height, 0, RenderTextureFormat.ARGB32);

        // Blit 会自动做格式转换（HDR → LDR）
        Graphics.Blit(rt, temp);

        // 读取像素
        RenderTexture active = RenderTexture.active;
        RenderTexture.active = temp;

        Texture2D tex = new Texture2D(rt.width, rt.height, TextureFormat.ARGB32, false);
        tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
        tex.Apply();

        RenderTexture.active = active;

        // 写 PNG 文件
        byte[] bytes = tex.EncodeToPNG();
        System.IO.File.WriteAllBytes(path, bytes);

        // 清理
        RenderTexture.ReleaseTemporary(temp);
        Object.Destroy(tex);

        Debug.Log("Saved PNG to: " + path);
    }

}
