using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
[CreateAssetMenu(menuName = "Settings/Path Tracing Settings")]
public class PathTracingSettings : ScriptableObject
{
    // Start is called before the first frame update
    private static PathTracingSettings m_Instance;
    public static PathTracingSettings Instance
    {
        get
        {
            if (m_Instance == null)
            {
                m_Instance = AssetDatabase.LoadAssetAtPath<PathTracingSettings>("Assets/Settings/PTSettings.asset");
            }
            return m_Instance;
        }
    }
    public RayTracingShader rayTracingShader;
    public Texture2D texture;
}
