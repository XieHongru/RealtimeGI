using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.XR;

public class GIController : MonoBehaviour
{
    public MiraiGIGPUScene miraiGIGPUScene;

    void Start()
    {
        MiraiGISceneCreate();
    }

    void Update()
    {
        MiraiGISceneUpdate();
    }

    private void OnDestroy()
    {
        miraiGIGPUScene?.Release();
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
}
