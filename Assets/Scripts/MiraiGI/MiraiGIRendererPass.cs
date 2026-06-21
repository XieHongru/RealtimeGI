using System.Collections;
using System.Collections.Generic;
using UnityEditor.Experimental.GraphView;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class MiraiGIRendererFeature : ScriptableRendererFeature
{
    MiraiGIRenderPass m_MiraiGIRenderPass;

    public override void Create()
    {
        m_MiraiGIRenderPass = new MiraiGIRenderPass();
        m_MiraiGIRenderPass.renderPassEvent = RenderPassEvent.BeforeRenderingTransparents;
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

        RenderTargetIdentifier cameraTarget = renderingData.cameraData.renderer.cameraColorTargetHandle;
        if (m_GIController == null)
        {
            m_GIController = GameObject.Find("GIController").GetComponent<GIController>();

            m_GIController.MiraiGISceneCreate(cameraTarget);
        }

        m_GIController.MiraiGISceneUpdate(ref renderingData);

        CommandBuffer cmd = CommandBufferPool.Get("Blit Visualize Result");

        if (GlobalSettings.NeedVisualize())
        {
            cmd.Blit(m_GIController.miraiGIGPUScene.miraiGIClipmap.GetVisualizeColorTarget(), cameraTarget);
        }
        else
        {
            if (GlobalSettings.Instance.diffuseIndirectEnable > 0)
            {
                m_GIController.MiraiGIDiffuseComposite(ref renderingData);
                cmd.Blit(m_GIController.miraiGIGPUScene.miraiGIScreenGather.GetDiffuseCompositeTexture(), cameraTarget);
            }
            if (GlobalSettings.Instance.specularIndirectEnable > 0)
            {
                m_GIController.MiraiGISpecularComposite(ref renderingData);
                cmd.Blit(m_GIController.miraiGIGPUScene.miraiGIScreenGather.GetSpecularCompositeTexture(), cameraTarget);
            }
        }

        context.ExecuteCommandBuffer(cmd);
        CommandBufferPool.Release(cmd);
        context.Submit();
    }
}