using System.Collections;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using static UnityEditor.Searcher.SearcherWindow.Alignment;

public class MiraiGIGPUScene
{
    public MiraiGIClipmap miraiGIClipmap;
    public MiraiGIRadianceCache miraiGIRadianceCache;
    public MiraiGIScreenGather miraiGIScreenGather;
    public GPUSceneData GPUSceneData;
    public SurfaceCache surfaceCache;
    public OccupancyMap occupancyMap;

    public RenderTargetIdentifier sceneColorTarget;

    public void CreateScene(RenderTargetIdentifier cameraTarget)
    {
        // 1. init GPU scene data
        GPUSceneData = new GPUSceneData();
        GPUSceneData.Init();

        // 2. capture mesh cards
        surfaceCache = new SurfaceCache();
        surfaceCache.Init();
        surfaceCache.CaptureSurfaceCache(GPUSceneData);

        // 3. capture ROMA
        occupancyMap = new OccupancyMap();
        occupancyMap.Init();
        occupancyMap.CaptureOccupancyMapAtlas(GPUSceneData.objectsInfo, GPUSceneData.meshes);

        // 4. create clipmap
        miraiGIClipmap = new MiraiGIClipmap();
        miraiGIClipmap.CreateClipmap();

        // 5. init radiance cache
        miraiGIRadianceCache = new MiraiGIRadianceCache();
        miraiGIRadianceCache.Init();
        miraiGIClipmap.radianceCache = miraiGIRadianceCache; // friend class

        // 6. init screen gather;
        miraiGIScreenGather = new MiraiGIScreenGather();
        miraiGIScreenGather.Init();

        sceneColorTarget = cameraTarget;
    }

    public void UpdateScene(ref RenderingData renderingData)
    {
        foreach (Camera camera in Camera.allCameras)
        {
            // update scene
            miraiGIClipmap.UpdateClipmap(camera, this);
            // voxel lighting
            // TODO: multi-view
            miraiGIRadianceCache.Update(ref renderingData, this);
            miraiGIScreenGather.Update(ref renderingData, this);
        }
    }

    public void VisualizeGIScene(ref RenderingData renderingData)
    {
        foreach (Camera camera in Camera.allCameras)
        {
            CommandBuffer cmd = CommandBufferPool.Get("Visualize GI Scene");
            miraiGIClipmap.VisualizeMiraiGIScene(cmd, this, camera);
            miraiGIScreenGather.VisualizeMiraiGIScreenGather(cmd, this);
            miraiGIRadianceCache.VisualizeProbe(cmd, this, ref renderingData, ProbeVisualizeMode.RadianceProbe);
            miraiGIRadianceCache.VisualizeProbe(cmd, this, ref renderingData, ProbeVisualizeMode.IrradianceProbe);
            Graphics.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
    }

    public void Release()
    {
        miraiGIClipmap.Release();
        miraiGIRadianceCache.Release();
        miraiGIScreenGather.Release();
        GPUSceneData.Release();
        surfaceCache.Release();
        occupancyMap.Release();
    }
}
