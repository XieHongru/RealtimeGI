
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using static UnityEngine.GraphicsBuffer;

public struct CameraParams
{
    public Matrix4x4 viewProjMat;
    public Matrix4x4 invViewProjMat;
    public Vector3 viewDir;
    public float padding;
}

public class OccupancyMap
{
    RenderTexture m_BaseOccupancyMap;
    RenderTexture m_MergedBaseOccupancyMap;
    RenderTexture m_OccupancyMapAtlas;
    Vector3Int m_OccupancyMapResolution;
    const int OCCUPANCY_MAP_COUNT = 16;
    RenderTexture m_DummyRT;

    Matrix4x4 m_BaseOMViewProjMat;

    ComputeBuffer m_CameraParamsArray;

    ComputeShader m_OccupancyMapMergeCS;
    ComputeShader m_ROMAGenerateCS;

    public void Init()
    {
        m_OccupancyMapResolution = new Vector3Int(128, 128, 128);

        //m_BaseOccupancyMap = new RenderTexture(128, 128, 0, RenderTextureFormat.ARGBInt);
        // TODO: could we use uint4 directly without write conflict?
        m_BaseOccupancyMap = new RenderTexture(128, 128, 0, RenderTextureFormat.RInt);
        m_BaseOccupancyMap.dimension = TextureDimension.Tex2DArray;
        m_BaseOccupancyMap.volumeDepth = 4;
        m_BaseOccupancyMap.enableRandomWrite = true;
        m_BaseOccupancyMap.Create();

        m_MergedBaseOccupancyMap = new RenderTexture(128, 128, 0, RenderTextureFormat.ARGBInt);
        m_MergedBaseOccupancyMap.enableRandomWrite = true;
        m_MergedBaseOccupancyMap.Create();

        m_OccupancyMapAtlas = new RenderTexture(128, 128, 0, RenderTextureFormat.ARGBInt);
        m_OccupancyMapAtlas.enableRandomWrite = true;
        m_OccupancyMapAtlas.dimension = TextureDimension.Tex2DArray;
        m_OccupancyMapAtlas.volumeDepth = 16;
        m_OccupancyMapAtlas.Create();

        m_DummyRT = new RenderTexture(128, 128, 0, RenderTextureFormat.RFloat);

        m_CameraParamsArray = new ComputeBuffer(OCCUPANCY_MAP_COUNT, Marshal.SizeOf<CameraParams>());

        m_OccupancyMapMergeCS = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/Shaders/MiraiGI/ROMA/OccupancyMapMerge.compute");
        m_ROMAGenerateCS = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/Shaders/MiraiGI/ROMA/ROMAGenerate.compute");
    }

    public void Release()
    {
        m_BaseOccupancyMap?.Release();
        m_OccupancyMapAtlas?.Release();
        m_CameraParamsArray?.Release();

        m_BaseOccupancyMap = null;
        m_OccupancyMapAtlas = null;
        m_CameraParamsArray = null;
    }

    public void CaptureOccupancyMapAtlas(List<ObjectInfo> objectsInfo, List<Mesh> meshes)
    {
        CommandBuffer cmd = CommandBufferPool.Get("Capture Occupancy Map Atlas");

        CaptureBaseOccupancyMap(cmd, objectsInfo, meshes);
        cmd.ClearRandomWriteTargets();
        MergeBaseOccupancyMap(cmd);
        GenerateROMA(cmd);

        Graphics.ExecuteCommandBuffer(cmd);
        cmd.Release();
    }

    // ---------------------------------
    // objectsInfo should be culled list
    // TODO: refactor with MiraiGIClipmap
    // ---------------------------------
    public void CaptureBaseOccupancyMap(CommandBuffer cmd, List<ObjectInfo> objectsInfo, List<Mesh> meshes)
    {
        Shader occupancyMapShader = Shader.Find("Mirai/OccupancyMapCapture");

        // clip to voxel offset to ensure grid-aligned? if need refactor
        //Vector3 center = Camera.main.transform.position;
        Vector3 center = Vector3.zero;

        float halfSize = 32.0f * 0.5f;

        Vector3 viewDir = Vector3.forward;
        //Vector3 viewDir = new Vector3(-0.26f, 0.93f, 0.24f);
        Vector3 up = Vector3.up;
        Matrix4x4 viewMatrix = Matrix4x4.LookAt(center, center + viewDir, up).inverse;
        Matrix4x4 projectionMatrix = Matrix4x4.Ortho(-halfSize, halfSize, -halfSize, halfSize, -halfSize, halfSize);
        if (SystemInfo.usesReversedZBuffer)
        {
            projectionMatrix = Matrix4x4.Ortho(-halfSize, halfSize, -halfSize, halfSize, halfSize, -halfSize);
        }
        m_BaseOMViewProjMat = projectionMatrix * viewMatrix;

        cmd.SetRenderTarget(m_DummyRT);
        cmd.SetRandomWriteTarget(1, m_BaseOccupancyMap);

        foreach (var objInfo in objectsInfo)
        {
            Mesh mesh = meshes[objInfo.meshId];
            int subMeshCount = mesh.subMeshCount;

            Material captureMaterial = new Material(occupancyMapShader);

            for (int i = 0; i < subMeshCount; i++)
            {
                cmd.DrawMesh(mesh, m_BaseOMViewProjMat * objInfo.localToWorldMatrix, captureMaterial, i);
            }
        }
    }

    public void MergeBaseOccupancyMap(CommandBuffer cmd)
    {
        int kernel = m_OccupancyMapMergeCS.FindKernel("MergeOM");

        cmd.SetComputeTextureParam(m_OccupancyMapMergeCS, kernel, Shader.PropertyToID("_OccupancyMap"), m_BaseOccupancyMap);
        cmd.SetComputeTextureParam(m_OccupancyMapMergeCS, kernel, Shader.PropertyToID("_RWBaseOccupancyMap"), m_MergedBaseOccupancyMap);

        cmd.DispatchCompute(m_OccupancyMapMergeCS, kernel, 128 / 16, 128 / 16, 1);
    }

    public void GenerateROMA(CommandBuffer cmd)
    {
        List<Vector3> rotateDirection = new List<Vector3>();
        GenerateUniformHemisphereDirections(rotateDirection);

        string result = "rotateDirection: " + string.Join(" | ", rotateDirection.Select(v => $"({v.x:F2}, {v.y:F2}, {v.z:F2})"));
        //Debug.Log(result);

        CameraParams[] cameraParamsArray = new CameraParams[OCCUPANCY_MAP_COUNT];
        for (int omId = 0; omId < OCCUPANCY_MAP_COUNT; omId++)
        {
            cameraParamsArray[omId] = new CameraParams();

            //Vector3 center = Camera.main.transform.position;
            Vector3 center = Vector3.zero;
            float halfSize = 32.0f * 0.5f;

            Vector3 viewDir = rotateDirection[omId];
            Vector3 up = Vector3.up;
            Matrix4x4 viewMatrix = Matrix4x4.LookAt(center, center + viewDir, up).inverse;
            Matrix4x4 projectionMatrix = Matrix4x4.Ortho(-halfSize, halfSize, -halfSize, halfSize, -halfSize, halfSize);
            if (SystemInfo.usesReversedZBuffer)
            {
                projectionMatrix = Matrix4x4.Ortho(-halfSize, halfSize, -halfSize, halfSize, halfSize, -halfSize);
            }
            Matrix4x4 viewProjMatrix = projectionMatrix * viewMatrix;

            cameraParamsArray[omId].viewProjMat = viewProjMatrix;
            cameraParamsArray[omId].invViewProjMat = viewProjMatrix.inverse;
            cameraParamsArray[omId].viewDir = viewDir;
        }
        m_CameraParamsArray.SetData(cameraParamsArray);

        int kernel = m_ROMAGenerateCS.FindKernel("GenerateROMA");

        cmd.SetComputeMatrixParam(m_ROMAGenerateCS, Shader.PropertyToID("_BaseOMViewProjMat"), m_BaseOMViewProjMat);
        cmd.SetComputeBufferParam(m_ROMAGenerateCS, kernel, Shader.PropertyToID("_CameraParamsArray"), m_CameraParamsArray);
        cmd.SetComputeTextureParam(m_ROMAGenerateCS, kernel, Shader.PropertyToID("_BaseOccupancyMap"), m_MergedBaseOccupancyMap);
        cmd.SetComputeTextureParam(m_ROMAGenerateCS, kernel, Shader.PropertyToID("_RWOccupancyMapAtlas"), m_OccupancyMapAtlas);

        cmd.DispatchCompute(m_ROMAGenerateCS, kernel, 128 / 16, 128 / 16, OCCUPANCY_MAP_COUNT);
    }

    void GenerateUniformHemisphereDirections(List<Vector3> directions)
    {
        float goldenAngle = Mathf.PI * (3.0f - Mathf.Sqrt(5.0f));

        for (int i = 0; i < OCCUPANCY_MAP_COUNT; i++)
        {
            // Fibonacci Hemisphere Sample
            float y = 1.0f - (i / (float)(OCCUPANCY_MAP_COUNT - 1)); // y ¡Ê [0, 1]
            float radius = Mathf.Sqrt(1.0f - y * y);
            float theta = goldenAngle * i;

            float x = Mathf.Cos(theta) * radius;
            float z = Mathf.Sin(theta) * radius;

            directions.Add(math.normalize(new Vector3(x, y, z)));
        }
    }
}