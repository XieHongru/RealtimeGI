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
                m_Instance = AssetDatabase.LoadAssetAtPath<PathTracingSettings>("Assets/Settings/PathTracingSettings.asset");
            }
            return m_Instance;
        }
    }
    public RayTracingShader rayTracingShader;
    public Texture2D texture;
    public int bounceCountOpaque;
    public int bounceCountTransparent;
    public Vector3 sunDirection = new Vector3(1.0f, 0.0f, 0.0f);
    public Vector3 sunColor = new Vector3(1.0f, 1.0f, 1.0f);

}
