using System.Collections;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

public class VoxelScene
{
    Cascade[] cascades = new Cascade[8];
    List<Vector3> verticesList = new List<Vector3>();
    List<int> indicesList = new List<int>();

    SceneData sceneData;

    public void CreateScene()
    {
        sceneData = new SceneData();

        GetAllMeshes();
        Voxelize();
    }

    void GetAllMeshes()
    {

        // 获取所有MeshFilter和SkinnedMeshRenderer
        MeshFilter[] meshFilters = GameObject.FindObjectsOfType<MeshFilter>();
        SkinnedMeshRenderer[] skinnedMeshRenderers = GameObject.FindObjectsOfType<SkinnedMeshRenderer>();

        // 处理普通Mesh
        foreach (MeshFilter mf in meshFilters)
        {
            if (mf.sharedMesh != null)
            {
                AddMeshData(mf.sharedMesh, mf.transform, ref verticesList, ref indicesList);
            }
        }

        // 处理SkinnedMesh
        foreach (SkinnedMeshRenderer smr in skinnedMeshRenderers)
        {
            if (smr.sharedMesh != null)
            {
                AddMeshData(smr.sharedMesh, smr.transform, ref verticesList, ref indicesList);
            }
        }

        Debug.Log($"Total vertices: {verticesList.Count}, Total indices: {indicesList.Count}");
    }

    void AddMeshData(Mesh mesh, Transform transform, ref List<Vector3> vertices, ref List<int> indices)
    {
        // 获取原始顶点数据并转换到世界空间
        Vector3[] meshVertices = mesh.vertices;
        for (int i = 0; i < meshVertices.Length; i++)
        {
            vertices.Add(transform.TransformPoint(meshVertices[i]));
        }

        // 处理索引（需要考虑顶点偏移）
        int vertexOffset = vertices.Count - meshVertices.Length;
        int[] meshIndices = mesh.triangles;
        for (int i = 0; i < meshIndices.Length; i++)
        {
            indices.Add(meshIndices[i] + vertexOffset);
        }
    }

    void Voxelize()
    {
        sceneData.vertexBuffer = new ComputeBuffer(verticesList.Count, 3 * sizeof(float));
        sceneData.indexBuffer = new ComputeBuffer(indicesList.Count, sizeof(int));

        sceneData.vertexBuffer.SetData(verticesList);
        sceneData.indexBuffer.SetData(indicesList);
    }
}
