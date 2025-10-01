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

    public float voxelSize = 0.5f;
}