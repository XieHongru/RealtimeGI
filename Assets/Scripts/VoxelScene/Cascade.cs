using System.Collections.Generic;
using UnityEngine;
using static UnityEditor.Searcher.SearcherWindow.Alignment;

public class Cascade
{
    int m_CascadeId;
    float m_VoxelSize;
    Vector3 m_CascadeCenter;
    Bounds m_Bounds;

    CascadeData m_CascadeData = new CascadeData();

    public void Init(int id, float voxelSize, Vector3 center)
    {
        m_CascadeId = id;
        m_VoxelSize = voxelSize;
        m_CascadeCenter = center;
        m_Bounds = new Bounds(m_CascadeCenter, Vector3.one * m_VoxelSize * 64);
    }

    public void Update(ref SceneData sceneData)
    {

    }

    public bool CheckBounds(Bounds bounds)
    {
        return m_Bounds.Intersects(bounds);
    }

    public void AddObject(GameObject gameObject, List<Vector3> vertices, int[] indices)
    {
        m_CascadeData.objects.Add(gameObject);

        int beginOffset = m_CascadeData.vertices.Count;
        foreach (var idx in indices)
        {
            m_CascadeData.indices.Add(idx + beginOffset);
        }
        foreach (var vert in vertices)
        {
            m_CascadeData.vertices.Add(vert);
        }
    }

    public void GetDebugInfo(out float voxelSize, out Vector4 cascadeMin, out Vector4 cascadeMax)
    {
        voxelSize = m_VoxelSize;
        cascadeMin = m_Bounds.min;
        cascadeMax = m_Bounds.max;

        Debug.Log(m_CascadeId + "," + m_CascadeData.objects.Count);
    }
}