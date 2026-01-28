using UnityEditor;
using UnityEditor.ShaderGraph.Internal;
using UnityEngine;

public class ChineseLabelAttribute : PropertyAttribute
{
    public string Label { get; private set; }

    public ChineseLabelAttribute(string label)
    {
        Label = label;
    }
}

#if UNITY_EDITOR

[CustomPropertyDrawer(typeof(ChineseLabelAttribute))]
public class ChineseLabelDrawer : PropertyDrawer
{
    public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
    {
        ChineseLabelAttribute chineseLabel = (ChineseLabelAttribute)attribute;
        label.text = chineseLabel.Label;
        EditorGUI.PropertyField(position, property, label);
    }
}
#endif

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
        return (m_Instance.voxelVisualizeMode > 0 && m_Instance.voxelVisualizeMode <= 5) || (m_Instance.visualizeScreenGather > 0) ||
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
    [Header("Clipmap Settings (禁止运行时修改)")]

    [ChineseLabel("每帧最大更新区块数")]
    public int chunkCountToUpdatePerFrame = 64;
    [ChineseLabel("每区块支持的最大对象数")]
    public int cullingObjectCountPerChunk = 64;
    //public int voxelCascadeCount = 4;
    [ChineseLabel("体素尺寸")]
    public float voxelSize = 0.2f;
    [ChineseLabel("体素分辨率")]
    public int voxelResolution = 128;

    // Clipmap Visualize
    [Header("Clipmap Visualize Settings")]

    public int voxelVisualizeMode = 4;
    public int voxelVisualizeCascadeLevel = 0;
    public int voxelVisualizeUpdateChunk = 0;

    // Voxel Lighting
    [Header("Voxel Lighting Settings")]

    public int voxelLightingCheckerBoardSize = 2;
    public int freezeLightingForDebug = 0;
    public int probeUpdateCheckerBoardSize = 2;
    public int visualizeIrradianceProbe = 0;
    public int visualizeRadianceProbe = 0;
    public int radianceProbeResolution = 16;
    public int reuseRadianceProbe = 0;
    public int useProbeOcclusionTest = 1;
    public int radianceProbeMinCascadeLevel = 2;
    public int irradianceProbeMinCascadeLevel = 0;
    public int irradianceProbeSampleCount = 1;
    public float irradianceProbeTemporalWeight = 0.75f;

    // Screen Gather
    [Header("Screen Gather Settings")]

    [ChineseLabel("启用漫反射间接光")]
    public int diffuseIndirectEnable = 1;
    [ChineseLabel("启用镜面反射间接光")]
    public int specularIndirectEnable = 1;

    [ChineseLabel("可视化模式")]
    public int visualizeScreenGather = 0;
    [ChineseLabel("降采样分辨率比例")]
    public int screenGatherDownsampleFactor = 2;
    [ChineseLabel("启用Reservoir空间重用")]
    public int useReservoirSpatialReuse = 1;
    [ChineseLabel("空间重用采样数")]
    public int spatialReuseSampleCount = 8;
    [ChineseLabel("空间次反射重用采样数")]
    public int spatialSecondaryReuseSampleCount = 4;
    [ChineseLabel("启用空间次反射重用")]
    public int spatialSecondaryReuse = 0;
    [ChineseLabel("启用间接阴影")]
    public int indirectShadowEnable = 1;
    [ChineseLabel("间接阴影锐利度")]
    public float indirectShadowSharpness = 1.0f;
    [ChineseLabel("间接阴影强度")]
    public float indirectShadowIntensity = 0.75f;
    [ChineseLabel("间接阴影空间滤波迭代次数")]
    public int indirectShadowSpatialFilterIterationCount = 3;
    [ChineseLabel("空间重用贡献范围")]
    public float spatialReuseSearchRange = 0.5f;

    [ChineseLabel("漫反射累积帧数")]
    public float diffuseMaxAccumulatedFrame = 32.0f;
    [ChineseLabel("漫反射空间滤波迭代次数")]
    public int diffuseSpatialFilterIterationCount = 5;

    [ChineseLabel("SSAO范围")]
    public float filterGuidanceSSAORange = 2.0f;
    [ChineseLabel("SSAO强度")]
    public float filterGuidanceSSAOIntensity = 0.5f;
    [ChineseLabel("SSAO锐利度")]
    public float filterGuidanceSSAOSharpness = 1.0f;
    [ChineseLabel("SSAO滤波权重")]
    public float filterGuidanceSSAOWeight = 10.0f;

    [ChineseLabel("高光累计帧数")]
    public float specularMaxAccumulatedFrame = 32.0f;
    [ChineseLabel("高光重建贡献范围")]
    public float specularResolveSearchRange = 4.0f;
    [ChineseLabel("高光过滤范围")]
    public float specularFilterSearchRange = 4.0f;

    [Header("SDF Settings")]

    // Distance Field
    public int useDistanceField = 1;

    [Header("ROMA Settings")]
    [ChineseLabel("启用ROMA")]
    public int useROMA = 0;
    [ChineseLabel("可视化模式")]
    public int visualizeUseROMA = 0;
    [ChineseLabel("占用图X方向数")]
    public int occupancyMapXCount = 4;
    [ChineseLabel("占用图Y方向数")]
    public int occupancyMapYCount = 4;
}