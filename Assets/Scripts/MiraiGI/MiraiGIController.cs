using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR;

public class GIController : MonoBehaviour
{
    public MiraiGIGPUScene miraiGIGPUScene;
    public bool isInitialized = false;

    // Refactor to renderer feature
    void Start()
    {
        //MiraiGISceneCreate();
    }

    void Update()
    {
        //MiraiGISceneUpdate();
    }

    private void OnDestroy()
    {
        miraiGIGPUScene?.Release();
    }

    public void MiraiGISceneCreate(RenderTargetIdentifier sceneColorTarget)
    {
        miraiGIGPUScene = new MiraiGIGPUScene();
        miraiGIGPUScene.CreateScene(sceneColorTarget);
        isInitialized = true;
    }

    public void MiraiGISceneUpdate(ref RenderingData renderingData)
    {
        miraiGIGPUScene.UpdateScene(ref renderingData);
        miraiGIGPUScene.VisualizeGIScene(ref renderingData);
    }
}
