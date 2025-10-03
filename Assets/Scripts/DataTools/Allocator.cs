using System.Collections.Generic;
using UnityEngine;

public class LinearAllocator<T> where T : System.IEquatable<T>
{
    Queue<int> m_UnusedElementIds = new Queue<int>();
    Dictionary<T, int> m_AllocatedElementIds = new Dictionary<T, int>();
    int m_MaxNumElements = 0;

    public void Init(int numElements)
    {
        if (m_MaxNumElements == 0)
        {
            m_MaxNumElements = numElements;
            for (int i = 0; i < numElements; i++)
            {
                m_UnusedElementIds.Enqueue(i);
            }
        }

        // @TODO: change size
    }

    public int ReleaseElement(T keyValue)
    {
        if (!m_AllocatedElementIds.TryGetValue(keyValue, out int freeId))
        {
            Debug.LogError($"Failed to release element: {keyValue} not found");
            return -1;
        }

        bool removeSuccess = m_AllocatedElementIds.Remove(keyValue);
        if (!removeSuccess)
        {
            Debug.LogError($"Failed to remove element: {keyValue}");
            return -1;
        }

        m_UnusedElementIds.Enqueue(freeId);
        return freeId;
    }

    public int AllocateElement(T keyValue)
    {
        if (m_UnusedElementIds.Count == 0)
        {
            Debug.LogError("No available element IDs");
            return -1;
        }

        int id = m_UnusedElementIds.Dequeue();
        m_AllocatedElementIds.Add(keyValue, id);
        return id;
    }

    public bool Find(T keyValue, out int outId)
    {
        return m_AllocatedElementIds.TryGetValue(keyValue, out outId);
    }

    public bool Find(T keyValue)
    {
        return m_AllocatedElementIds.ContainsKey(keyValue);
    }

    public int GetMaxNumElements() => m_MaxNumElements;
    public int GetAllocatedElementNums() => m_AllocatedElementIds.Count;

    public List<int> GetAllocatedElements()
    {
        return new List<int>(m_AllocatedElementIds.Values);
    }

    public int GetAvailableElementCount() => m_UnusedElementIds.Count;
}
