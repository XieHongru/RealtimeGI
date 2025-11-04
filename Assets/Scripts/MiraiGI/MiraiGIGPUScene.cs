using System.Collections;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using static UnityEditor.Searcher.SearcherWindow.Alignment;

public class MiraiGIGPUScene
{
    public MiraiGIClipmap miraiGIClipmap;
    public GPUSceneData GPUSceneData;
    public SurfaceCache surfaceCache;
    public OccupancyMap occupancyMap;

    public void CreateScene()
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
    }

    public void UpdateScene()
    {
        foreach (Camera camera in Camera.allCameras)
        {
            miraiGIClipmap.UpdateClipmap(camera, this);
        }
    }

    public void Release()
    {
        miraiGIClipmap.Release();
        GPUSceneData.Release();
        surfaceCache.Release();
        occupancyMap.Release();
    }
}
