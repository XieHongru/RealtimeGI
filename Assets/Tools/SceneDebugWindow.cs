using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices.WindowsRuntime;
using Unity.VisualScripting;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;

public class SceneDebugWindow : EditorWindow
{
    static SceneDebugWindow instance;

    [MenuItem("Tools/场景Debug工具")]
    public static void OpenWindow()
    {
        GetWindow<SceneDebugWindow>("场景Debug工具");
    }

    static DebugRendererFeature m_DebugRenderFeature = null;

    static bool m_EnableDebugScene = false;
    static DebugMode m_DebugMode = DebugMode.CascadeId;
    public bool enableDebugScene { get => m_EnableDebugScene; set => EnableDebugScene(value); }
    public DebugMode debugMode { get => m_DebugMode; set => SwitchDebugMode(value); }


    private void OnGUI()
    {
        enableDebugScene = EditorGUILayout.Toggle("DebugView", enableDebugScene);
        if (m_EnableDebugScene)
            debugMode = (DebugMode)EditorGUILayout.EnumPopup("debug", debugMode);
    }

    private void EnableDebugScene(bool value)
    {
        m_EnableDebugScene = value;

        if (m_DebugRenderFeature == null)
        {
            var rendererData = AssetDatabase.LoadAssetAtPath<UniversalRendererData>("Assets/Settings/URP-HighFidelity-Renderer.asset");
            foreach (var feature in rendererData.rendererFeatures)
            {
                if (feature.name == "DebugRenderFeature")
                {
                    m_DebugRenderFeature = feature as DebugRendererFeature;
                }
            }
        }

        m_DebugRenderFeature.SetActive(m_EnableDebugScene);
        EditorUtility.SetDirty(m_DebugRenderFeature);
    }

    private void SwitchDebugMode(DebugMode mode)
    {
        m_DebugMode = mode;

        if (m_DebugRenderFeature == null)
        {
            var rendererData = AssetDatabase.LoadAssetAtPath<UniversalRendererData>("Assets/Settings/URP-HighFidelity-Renderer.asset");
            foreach (var feature in rendererData.rendererFeatures)
            {
                if (feature.name == "DebugRenderFeature")
                {
                    m_DebugRenderFeature = feature as DebugRendererFeature;
                }
            }
        }

        m_DebugRenderFeature.debugMode = m_DebugMode;
        m_DebugRenderFeature.Refresh();
        EditorUtility.SetDirty(m_DebugRenderFeature);
        
    }
}
