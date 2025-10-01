using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class DebugPass : ScriptableRenderPass
{
    public DebugMode debugMode;

    VoxelScene m_VoxelScene;
    Material m_DebugMaterial;
    string m_ProfilerTag;

    float[] voxelSize = new float[4];
    Vector4[] cascadeMin = new Vector4[4];
    Vector4[] cascadeMax = new Vector4[4];

    public DebugPass(Material debugMaterial, string profilerTag)
    {
        m_DebugMaterial = debugMaterial;
        m_ProfilerTag = profilerTag;
    }

    public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
    {
        if (m_DebugMaterial == null)
        {
            return;
        }

        if (m_VoxelScene == null)
        {
            m_VoxelScene = GameObject.Find("GIController").GetComponent<GIController>().voxelScene;
        }

        m_VoxelScene.UpdateDebugInfo(voxelSize, cascadeMin, cascadeMax);

        m_DebugMaterial.SetInteger("_DebugMode", (int)debugMode);
        m_DebugMaterial.SetFloatArray("_VoxelSize", voxelSize);
        m_DebugMaterial.SetVectorArray("_CascadeMin", cascadeMin);
        m_DebugMaterial.SetVectorArray("_CascadeMax", cascadeMax);

        CommandBuffer cmd = CommandBufferPool.Get(m_ProfilerTag);

        using (new ProfilingScope(cmd, new ProfilingSampler(m_ProfilerTag)))
        {
            var drawingSettings = CreateDrawingSettings(
                new ShaderTagId("UniversalForward"),
                ref renderingData,
                SortingCriteria.CommonOpaque
            );
            drawingSettings.overrideMaterial = m_DebugMaterial;
            drawingSettings.overrideMaterialPassIndex = 0;
            var filteringSettings = new FilteringSettings(RenderQueueRange.all, -1);

            context.DrawRenderers(
                renderingData.cullResults, ref drawingSettings, ref filteringSettings
            );
        }

        context.ExecuteCommandBuffer(cmd);
        CommandBufferPool.Release(cmd);
    }
}