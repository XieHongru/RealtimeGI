using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class DebugPass : ScriptableRenderPass
{
    public DebugMode debugMode;

    Material m_DebugMaterial;
    string m_ProfilerTag;

    float[] voxelSize = { 0.2f, 0.4f, 0.8f, 1.6f };
    Vector4[] cascadeMin = { new Vector3(-6.4f, -6.4f, -6.4f), new Vector3(-12.8f, -12.8f, -12.8f), new Vector3(-25.6f, -25.6f, -25.6f), new Vector3(-51.2f, -51.2f, -51.2f) };
    Vector4[] cascadeMax = { new Vector3(6.4f, 6.4f, 6.4f), new Vector3(12.8f, 12.8f, 12.8f), new Vector3(25.6f, 25.6f, 25.6f), new Vector3(51.2f, 51.2f, 51.2f) };

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