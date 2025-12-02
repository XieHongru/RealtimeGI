using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
[CreateAssetMenu(menuName = "Settings/Path Tracing Settings")]
public class PathTracingSettings : ScriptableObject
{
    private static PathTracingSettings m_Instance;
    public static PathTracingSettings Instance
    {
        get
        {
            if (m_Instance == null)
            {
                m_Instance = AssetDatabase.LoadAssetAtPath<PathTracingSettings>("Assets/Settings/PathTracingSettings.asset");
            }
            return m_Instance;
        }
    }
    public int bounceCountOpaque = 4;
    public int bounceCountTransparent = 4;
    public RayTracingShader rayTracingShader = null;
    public Vector3 sunDirection = new Vector3(1.0f, 0.0f, 0.0f);
    public Vector3 sunColor = new Vector3(1.0f, 1.0f, 1.0f);
}
