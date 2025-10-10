using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.XR;

public class GIController : MonoBehaviour
{
    public MiraiGIGPUScene miraiGIGPUScene;
    public MiraiGIClipmap miraiGIClipmap;

    void Start()
    {
        MiraiGISceneCreate();
        MiraiGIClipmapCreate();
    }

    void Update()
    {
        MiraiGISceneUpdate();
        MiraiGIClipmapUpdate(miraiGIGPUScene);
    }

    private void OnDestroy()
    {
        miraiGIGPUScene?.Release();
        miraiGIClipmap?.Release();
    }

    void MiraiGISceneCreate()
    {
        miraiGIGPUScene = new MiraiGIGPUScene();
        miraiGIGPUScene.CreateScene();
    }

    void MiraiGISceneUpdate()
    {
        miraiGIGPUScene.UpdateScene();
    }

    void MiraiGIClipmapCreate()
    {
        miraiGIClipmap = new MiraiGIClipmap();
        miraiGIClipmap.CreateClipmap();
    }

    void MiraiGIClipmapUpdate(MiraiGIGPUScene gpuScene)
    {
        foreach (Camera camera in Camera.allCameras)
        {
            miraiGIClipmap.UpdateClipmap(camera, gpuScene);
        }
    }
}
