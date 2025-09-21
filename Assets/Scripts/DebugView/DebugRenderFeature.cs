using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.Universal;

public enum DebugMode
{
    CascadeId,
    VoxelId
}

public class DebugRendererFeature : ScriptableRendererFeature
{
    Material m_DebugMaterial;
    DebugPass m_DebugPass;

    [SerializeField]
    public DebugMode debugMode = DebugMode.CascadeId;

    public override void Create()
    {
        if (m_DebugMaterial == null)
        {
            m_DebugMaterial = new Material(Shader.Find("Mirai/Debug"));
        }

        m_DebugPass = new DebugPass(m_DebugMaterial, "DebugPass");
        m_DebugPass.debugMode = debugMode;
        m_DebugPass.renderPassEvent = RenderPassEvent.AfterRenderingOpaques;
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        renderer.EnqueuePass(m_DebugPass);
    }

    public void Refresh()
    {
        m_DebugPass.debugMode = debugMode;
    }
}
