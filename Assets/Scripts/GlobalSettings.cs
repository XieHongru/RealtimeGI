using UnityEditor;
using UnityEditor.ShaderGraph.Internal;
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

    public static bool NeedVisualize()
    {
        return (m_Instance.voxelVisualizeMode > 0 && m_Instance.voxelVisualizeMode <= 4) || (m_Instance.visualizeScreenGather > 0) ||
                (m_Instance.visualizeRadianceProbe > 0) || (m_Instance.visualizeIrradianceProbe > 0);
    }

    public int maxObjectCount = 16384;
    public int maxSurfaceCacheCount = 4096;
    public int meshCardDefaultResolution = 32;
    public float meshCardResolutionThreshold1 = 4;
    public float meshCardResolutionThreshold2 = 16;
    public float meshCardResolutionThreshold3 = 64;
    public int adaptiveSurfaceCacheCount = 8;

    // Clipmap
    public int chunkCountToUpdatePerFrame = 16;
    public int cullingObjectCountPerChunk = 64;
    public int voxelCascadeCount = 4;
    public float voxelSize = 0.2f;
    public int voxelVisualizeMode = 4;
    public int voxelVisualizeCascadeLevel = 0;
    public int voxelVisualizeUpdateChunk = 0;
    public float clipmapObjectCullingRejectFactor1 = 1.0f;
    public float clipmapObjectCullingRejectFactor2 = 2.0f;
    public float clipmapObjectCullingRejectFactor3 = 3.0f;
    public float clipmapObjectCullingRejectFactor4 = 4.0f;

    // Voxel Lighting
    public float shadowRayMaxDistance = 128;
    public int shadowRayBoostClipmapOffset = 1;
    public int voxelLightingCheckerBoardSize = 2;
    public int freezeLightingForDebug = 0;
    public int probeUpdateCheckerBoardSize = 2;
    public int visualizeIrradianceProbe = 0;
    public int visualizeRadianceProbe = 0;
    public int radianceProbeResolution = 16;
    public int reuseRadianceProbe = 1;
    public int useProbeOcclusionTest = 1;
    public int radianceProbeMinCascadeLevel = 2;
    public int irradianceProbeSampleCount = 1;
    public float irradianceProbeTemporalWeight = 0.5f;

    // Screen Gather
    public int visualizeScreenGather = 0;

    // Distance Field
    public int useDistanceField = 1;
}