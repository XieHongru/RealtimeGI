using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class PathTracingRenderPass : ScriptableRenderPass
{
    public PathTracingRenderPass()
    {

    }
    public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
    {
        if (!Application.isPlaying)
        {
            return;
        }
        RenderTargetIdentifier cameraTarget = renderingData.cameraData.renderer.cameraColorTargetHandle;
        CommandBuffer cmd = CommandBufferPool.Get("Blit Visualize Result");

        //cmd.Blit(, cameraTarget);

        context.ExecuteCommandBuffer(cmd);
        CommandBufferPool.Release(cmd);
        context.Submit();
    }
}
