using System.Collections;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using static UnityEditor.Searcher.SearcherWindow.Alignment;

public class MiraiGIGPUScene
{
    public MiraiGIClipmap miraiGIClipmap;
    public GPUSceneData GPUSceneData;
    public SurfaceCache surfaceCache;
    public OccupancyMap occupancyMap;
    Cascade[] m_Cascades;

    public void CreateScene()
    {
        // 1. init GPU scene data
        GPUSceneData = new GPUSceneData();
        GPUSceneData.Init();

        // 2. capture mesh cards
        surfaceCache = new SurfaceCache();
        surfaceCache.Init();
        surfaceCache.CaptureSurfaceCache(GPUSceneData.objectsInfo, GPUSceneData.objects, GPUSceneData.meshes);

        // 3. capture ROMA
        occupancyMap = new OccupancyMap();
        occupancyMap.Init();
        occupancyMap.CaptureOccupancyMapAtlas(GPUSceneData.objectsInfo, GPUSceneData.meshes);

        //CreateCascade();

        // 4. create clipmap
        miraiGIClipmap = new MiraiGIClipmap();
        miraiGIClipmap.CreateClipmap();
    }

    public void UpdateScene()
    {
        //surfaceCache.CaptureSurfaceCache(GPUSceneData.objectsInfo, GPUSceneData.objects, GPUSceneData.meshes);
        //occupancyMap.CaptureOccupancyMapAtlas(GPUSceneData.objectsInfo, GPUSceneData.meshes);
        //m_SceneData.Update();

        foreach (Camera camera in Camera.allCameras)
        {
            miraiGIClipmap.UpdateClipmap(camera, this);
        }
    }

    public void CreateCascade()
    {
        m_Cascades = new Cascade[4];

        float voxelSize = GlobalSettings.Instance.voxelSize;
        for (int i = 0; i < m_Cascades.Length; i++)
        {
            m_Cascades[i] = new Cascade();
            m_Cascades[i].Init(i, voxelSize, GPUSceneData.cameraPosition);
            voxelSize *= 2;
        }

        CullMesh();
    }

    public void UpdateCascade()
    {
        foreach (var cascade in m_Cascades)
        {
            cascade.Update(ref GPUSceneData);
        }
    }

    public void Release()
    {
        miraiGIClipmap.Release();
        GPUSceneData.Release();
        surfaceCache.Release();
        occupancyMap.Release();
    }

    void CullMesh()
    {
        var objectList = GPUSceneData.objects;
        for (int i = 0; i < objectList.Count; i++)
        {
            MeshRenderer meshRenderer = objectList[i].GetComponent<MeshRenderer>();
            Mesh mesh = objectList[i].GetComponent<MeshFilter>().sharedMesh;
            if (meshRenderer != null && mesh != null)
            {
                // aabb bounds
                var bounds = meshRenderer.bounds;
                // vertices data
                Vector3[] meshVertices = mesh.vertices;

                for (int j = 0; j < m_Cascades.Length; j++)
                {
                    if (m_Cascades[j].CheckBounds(bounds))
                    {
                        // calculate vertices global position
                        List<Vector3> vertices = new List<Vector3>();
                        foreach (var vert in meshVertices)
                        {
                            vertices.Add(meshRenderer.transform.TransformPoint(vert));
                        }
                        int[] indices = mesh.triangles;

                        // push vertices and indices into following cascade data holder
                        for (int k = j; k < m_Cascades.Length; k++)
                        {
                            m_Cascades[k].AddObject(objectList[i], vertices, indices);
                        }

                        break;
                    }
                    else
                    {
                        continue;
                    }
                }
            }
        }
    }

    public void UpdateDebugInfo(float[] voxelSize, Vector4[] cascadeMin, Vector4[] cascadeMax)
    {
        for (int i = 0; i < m_Cascades.Length; i++)
        {
            m_Cascades[i].GetDebugInfo(out voxelSize[i], out cascadeMin[i], out cascadeMax[i]);
        }
    }
}
