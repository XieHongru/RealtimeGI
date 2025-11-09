using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class MiraiGIRendererFeature : ScriptableRendererFeature
{
    MiraiGIRenderPass m_MiraiGIRenderPass;

    public override void Create()
    {
        m_MiraiGIRenderPass = new MiraiGIRenderPass();
        m_MiraiGIRenderPass.renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing;
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        renderer.EnqueuePass(m_MiraiGIRenderPass);
    }

    public void Refresh()
    {
        
    }
}

public class MiraiGIRenderPass : ScriptableRenderPass
{
    GIController m_GIController;

    public MiraiGIRenderPass()
    {
        
    }

    public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
    {
        if (!Application.isPlaying)
        {
            return;
        }

        if (m_GIController == null)
        {
            m_GIController = GameObject.Find("GIController").GetComponent<GIController>();

            m_GIController.MiraiGISceneCreate();
        }

        m_GIController.MiraiGISceneUpdate(ref renderingData);

        CommandBuffer cmd = CommandBufferPool.Get("Blit Visualize Result");
        
        RenderTargetIdentifier cameraTarget = renderingData.cameraData.renderer.cameraColorTargetHandle;
        cmd.Blit(m_GIController.miraiGIGPUScene.miraiGIClipmap.GetVisualizeColorTarget(), cameraTarget);

        context.ExecuteCommandBuffer(cmd);
        CommandBufferPool.Release(cmd);
        context.Submit();
    }
}