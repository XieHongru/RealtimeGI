using UnityEditor;
using UnityEngine;

[CreateAssetMenu(menuName = "Settings/Realtime GI Settings")]
public class GlobalSettings : ScriptableObject
{
    private static GlobalSettings m_Instance;
    public static GlobalSettings Instance
    {
        get
        {
            if (m_Instance == null)
            {
                m_Instance = AssetDatabase.LoadAssetAtPath<GlobalSettings>("Assets/Settings/GISettings.asset");
            }
            return m_Instance;
        }
    }

    public int maxObjectCount = 16384;
    public int maxSurfaceCacheCount = 4096;
    public int meshCardDefaultResolution = 32;
    public float meshCardResolutionThreshold1 = 4;
    public float meshCardResolutionThreshold2 = 16;
    public float meshCardResolutionThreshold3 = 64;
    public int adaptiveSurfaceCacheCount = 8;
    public float clipmapObjectCullingRejectFactor1 = 1.0f;
    public float clipmapObjectCullingRejectFactor2 = 2.0f;
    public float clipmapObjectCullingRejectFactor3 = 3.0f;
    public float clipmapObjectCullingRejectFactor4 = 4.0f;
}