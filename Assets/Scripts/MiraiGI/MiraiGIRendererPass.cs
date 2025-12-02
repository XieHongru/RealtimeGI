using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class MiraiGIRendererFeature : ScriptableRendererFeature
{
    MiraiGIRenderPass m_MiraiGIRenderPass;
    PathTracingRenderPass m_PathTracingRenderPass;

    public override void Create()
    {
        if(!GlobalSettings.Instance.usePathTracing)
        {
            ReplaceShader("Universal Render Pipeline/Lit");
            m_MiraiGIRenderPass = new MiraiGIRenderPass();
            m_MiraiGIRenderPass.renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing;
        }
        else
        {
            ReplaceShader("PathTracing/Standard");
            m_PathTracingRenderPass = new PathTracingRenderPass();
            m_PathTracingRenderPass.renderPassEvent = RenderPassEvent.AfterRenderingPrePasses;
        }
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (!GlobalSettings.Instance.usePathTracing)
        {
            renderer.EnqueuePass(m_MiraiGIRenderPass);
        }
        else
        {
            renderer.EnqueuePass(m_PathTracingRenderPass);
            m_PathTracingRenderPass.setup();
        }
    }

    public void Refresh()
    {
        
    }
    public void ReplaceShader(string shaderPos)
    {
        string folderPath = "Assets/Models/Sponza-master/Materials";
        string[] matGUIDs = AssetDatabase.FindAssets("t:Material", new[] { folderPath });

        Shader targetShader = Shader.Find(shaderPos);
        if (targetShader == null)
        {
            Debug.LogError("Shader 'PathTracing/Standard' not found! Check the shader name.");
            return;
        }

        int count = 0;

        foreach (string guid in matGUIDs)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            Material mat = AssetDatabase.LoadAssetAtPath<Material>(path);

            if (mat != null)
            {
                Undo.RecordObject(mat, "Batch Replace Shader");
                mat.shader = targetShader;
                EditorUtility.SetDirty(mat);
                count++;
            }
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
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
        
        cmd.Blit(m_GIController.miraiGIGPUScene.miraiGIClipmap.GetVisualizeColorTarget(), cameraTarget);

        context.ExecuteCommandBuffer(cmd);
        CommandBufferPool.Release(cmd);
        context.Submit();
    }
}