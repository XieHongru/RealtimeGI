using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
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

    public void MiraiGISceneCreate()
    {
        miraiGIGPUScene = new MiraiGIGPUScene();
        miraiGIGPUScene.CreateScene();
        isInitialized = true;
    }

    public void MiraiGISceneUpdate(ref RenderingData renderingData)
    {
        miraiGIGPUScene.UpdateScene(ref renderingData);
    }
}
