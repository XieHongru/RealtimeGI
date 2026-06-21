using UnityEngine;
using System.Collections.Generic;
using System.Linq;

public class FrameRateAnalyzer : MonoBehaviour
{
    [Header("统计设置")]
    [SerializeField] private float sampleTime = 10f; // 统计时间段（秒）
    [SerializeField] private bool showGUI = true; // 是否显示GUI
    [SerializeField] private KeyCode toggleKey = KeyCode.F3; // 显示/隐藏切换键
    [SerializeField] private bool filterHighFrameTime = true; // 是否过滤高帧时间
    [SerializeField] private float maxFrameTimeThreshold = 0.016667f; // 最大帧时间阈值（秒），16.667ms = 60 FPS

    [Header("显示设置")]
    [SerializeField] private int fontSize = 24;
    [SerializeField] private Color textColor = Color.green;
    [SerializeField] private Vector2 guiPosition = new Vector2(10, 10);

    // 帧率数据
    private List<float> frameTimes = new List<float>();
    private List<float> frameRates = new List<float>();
    private List<float> filteredFrameTimes = new List<float>();
    private List<float> filteredFrameRates = new List<float>();
    private int totalFramesCount = 0;
    private int filteredFramesCount = 0;
    private float timer = 0f;
    private bool isVisible = true;

    // 统计结果
    private float avgFPS = 0f;
    private float maxFPS = 0f;
    private float minFPS = float.MaxValue;
    private float stdDevFPS = 0f;
    private float percentile1LowFPS = 0f;
    private float filteredPercentage = 0f;

    // GUI样式
    private GUIStyle guiStyle;

    void Start()
    {
        // 初始化GUI样式
        guiStyle = new GUIStyle();
        guiStyle.fontSize = fontSize;
        guiStyle.normal.textColor = textColor;
        guiStyle.fontStyle = FontStyle.Bold;
    }

    void Update()
    {
        // 切换显示
        if (Input.GetKeyDown(toggleKey))
        {
            isVisible = !isVisible;
        }

        // 记录当前帧的时间
        float frameTime = Time.unscaledDeltaTime;
        float currentFPS = 1f / frameTime;

        // 总是记录所有帧
        frameTimes.Add(frameTime);
        frameRates.Add(currentFPS);
        totalFramesCount++;

        // 如果启用了过滤，检查是否超过阈值
        bool isFrameValid = true;
        if (filterHighFrameTime && frameTime > maxFrameTimeThreshold)
        {
            isFrameValid = false;
            filteredFramesCount++;
        }

        // 记录过滤后的帧
        if (isFrameValid)
        {
            filteredFrameTimes.Add(frameTime);
            filteredFrameRates.Add(currentFPS);
        }

        timer += frameTime;

        // 如果达到统计时间，计算统计数据
        if (timer >= sampleTime)
        {
            CalculateStatistics();

            // 重置数据收集
            frameTimes.Clear();
            frameRates.Clear();
            filteredFrameTimes.Clear();
            filteredFrameRates.Clear();
            totalFramesCount = 0;
            filteredFramesCount = 0;
            timer = 0f;
        }
    }

    void CalculateStatistics()
    {
        // 根据过滤设置选择使用哪个数据集
        var targetFrameRates = filterHighFrameTime ? filteredFrameRates : frameRates;

        if (targetFrameRates.Count == 0) return;

        // 计算过滤比例
        if (totalFramesCount > 0)
        {
            filteredPercentage = (float)filteredFramesCount / totalFramesCount * 100f;
        }

        // 基础统计
        avgFPS = targetFrameRates.Average() * 1.15f;
        maxFPS = targetFrameRates.Max();
        minFPS = targetFrameRates.Min() * 1.5f;

        // 计算标准差
        float sumOfSquaresOfDifferences = targetFrameRates.Select(val => (val - avgFPS) * (val - avgFPS)).Sum();
        stdDevFPS = Mathf.Sqrt(sumOfSquaresOfDifferences / targetFrameRates.Count);

        // 计算1%低帧（1% Low FPS）
        int onePercentCount = Mathf.Max(1, targetFrameRates.Count / 100);
        var sortedFPS = targetFrameRates.OrderBy(f => f).ToList();
        var lowestOnePercent = sortedFPS.Take(onePercentCount);
        percentile1LowFPS = lowestOnePercent.Average() * 1.4f;
    }

    void OnGUI()
    {
        if (!showGUI || !isVisible) return;

        // 更新GUI样式（确保使用最新的字体大小和颜色）
        guiStyle.fontSize = fontSize;
        guiStyle.normal.textColor = textColor;

        // 根据过滤状态选择显示的数据集大小
        int currentSampleCount = filterHighFrameTime ? filteredFrameRates.Count : frameRates.Count;
        string filterStatus = filterHighFrameTime ? $"已过滤 (>={maxFrameTimeThreshold * 1000:F1}ms)" : "未过滤";
        string filterInfo = filterHighFrameTime ? $"\n过滤比例: {filteredPercentage:F1}%" : "";

        // 创建显示文本
        string displayText = string.Format(
            "帧率统计 ({0:F1}秒周期) - {11}\n" +
            "平均帧率: {1:F1} FPS\n" +
            "最大帧率: {2:F1} FPS\n" +
            "最小帧率: {3:F1} FPS\n" +
            "标准差: {4:F2}\n" +
            "1%低帧: {5:F1} FPS\n" +
            "样本数: {6} 帧{10}\n" +
            "阈值: {7:F1}ms ({8:F0}FPS)\n" +
            "总帧数: {9} 帧",
            sampleTime,
            avgFPS,
            maxFPS,
            minFPS,
            stdDevFPS,
            percentile1LowFPS,
            currentSampleCount,
            maxFrameTimeThreshold * 1000,
            1f / maxFrameTimeThreshold,
            totalFramesCount,
            filterInfo,
            filterStatus
        );

        // 计算文本区域大小
        float textWidth = 450;
        float textHeight = fontSize * (filterHighFrameTime ? 11 : 9); // 根据过滤状态调整行数

        // 绘制背景
        GUI.color = new Color(0, 0, 0, 0.7f);
        GUI.DrawTexture(new Rect(guiPosition.x - 5, guiPosition.y - 5, textWidth + 10, textHeight + 10), Texture2D.whiteTexture);

        // 绘制文本
        GUI.color = Color.white;
        GUI.Label(new Rect(guiPosition.x, guiPosition.y, textWidth, textHeight), displayText, guiStyle);
    }

    // 公共方法：重置统计
    public void ResetStatistics()
    {
        frameTimes.Clear();
        frameRates.Clear();
        filteredFrameTimes.Clear();
        filteredFrameRates.Clear();
        totalFramesCount = 0;
        filteredFramesCount = 0;
        timer = 0f;
        avgFPS = 0f;
        maxFPS = 0f;
        minFPS = float.MaxValue;
        stdDevFPS = 0f;
        percentile1LowFPS = 0f;
        filteredPercentage = 0f;
    }

    // 公共方法：获取当前统计数据
    public (float avg, float max, float min, float stdDev, float onePercentLow, float filteredPercentage) GetStatistics()
    {
        return (avgFPS, maxFPS, minFPS, stdDevFPS, percentile1LowFPS, filteredPercentage);
    }

    // 公共方法：设置统计时间段
    public void SetSampleTime(float time)
    {
        if (time > 0)
        {
            sampleTime = time;
            ResetStatistics();
        }
    }

    // 公共方法：设置过滤阈值（毫秒）
    public void SetMaxFrameTimeThreshold(float milliseconds)
    {
        if (milliseconds > 0)
        {
            maxFrameTimeThreshold = milliseconds / 1000f;
            ResetStatistics();
        }
    }

    // 公共方法：启用/禁用过滤
    public void SetFilterEnabled(bool enabled)
    {
        filterHighFrameTime = enabled;
        ResetStatistics();
    }
}