using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public class SceneData
{
    public List<GameObject> objects;
    public Vector3 cameraPositionPrev;
    public Vector3 cameraPosition;

    bool m_MeshIsDirty = true;

    public void Init()
    {
        objects = new List<GameObject>();
        GetAllObjects();
        cameraPosition = Camera.main.transform.position;
    }

    public void Update()
    {
        if (m_MeshIsDirty)
        {
            
            m_MeshIsDirty = false;
        }
        cameraPositionPrev = cameraPosition;
        cameraPosition = Camera.main.transform.position;
    }

    public void Release()
    {
        
    }

    void GetAllObjects()
    {
        // TODO: Get SkinnedMeshRenderer
        MeshFilter[] meshFilters = GameObject.FindObjectsOfType<MeshFilter>();

        foreach (MeshFilter mf in meshFilters)
        {
            if (mf.sharedMesh != null)
            {
                objects.Add(mf.gameObject);
            }
        }
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
}