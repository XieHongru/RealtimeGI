using System;
using System.Collections.Generic;
using TreeEditor;
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
}

public enum QuadTreeChild
{
    TC_TopLeft = 0,
    TC_TopRight,
    TC_BottomLeft,
    TC_BottomRight,
    Child_Num
};

class NodeAllocationInfo
{
    // TODO: bit compress
    public bool isEmpty;    // means all empty
    public bool isFull;     // means all full
};

class QuadTreeNode
{
    public int index = 0;
    public int size = 0;
    public Vector2Int min = new Vector2Int(0, 0);
    public Vector2Int max = new Vector2Int(0, 0);
    public Vector2Int center = new Vector2Int(0, 0);

    public static bool operator ==(QuadTreeNode left, QuadTreeNode right)
	{
		return left.min == right.min && left.max == right.max;
	}

    public static bool operator !=(QuadTreeNode left, QuadTreeNode right)
    {
        return left.min != right.min || left.max != right.max;
    }
};

class QuadTreeAllocator
{
    public List<NodeAllocationInfo> m_NodeAllocationInfos;
    public int m_AtlasResolution = 0;
    public int m_MinNodeSize = 16;
    public int m_MaxNodeSize = 256;

    public int GetMinNodeSize() 
    { 
        return m_MinNodeSize;
    }

    public int GetMaxNodeSize() 
    { 
        return m_MaxNodeSize; 
    }

    public void Init(int inAtlasResolution)
    {
        if (m_AtlasResolution == 0)
        {
            m_AtlasResolution = inAtlasResolution;

            // 64 + 16 + 4 + 1
            int numNodes = 0;
            int minLevelNodeNum = (int) Mathf.Pow(m_AtlasResolution / m_MinNodeSize, 2);
            for (int i = minLevelNodeNum; i >= 1; i /= 4)
            {
                numNodes += i;
            }

            m_NodeAllocationInfos = new List<NodeAllocationInfo>();

            for (int i = 0; i < numNodes; i++)
            {
                NodeAllocationInfo info = new NodeAllocationInfo();
                info.isEmpty = true;
                info.isFull = false;
                m_NodeAllocationInfos.Add(info);
            }
        }
        // @TODO: change size
    }

    public QuadTreeNode AllocateElement(int targetSize)
    {
        QuadTreeNode rootNode = GetRootNode();
        QuadTreeNode result = AllocateElementRecursive(rootNode, targetSize);
        return result;
    }

    public void ReleaseElement(QuadTreeNode freeNode)
    {
        if (freeNode.size == 0)
            return;
        QuadTreeNode rootNode = GetRootNode();
        ReleaseElementRecursive(rootNode, freeNode);
    }

	QuadTreeNode GetRootNode()
    {
        QuadTreeNode rootNode = new QuadTreeNode();
        rootNode.index = 0;
        rootNode.size = m_AtlasResolution;
        rootNode.min = new Vector2Int(0, 0);
        rootNode.max = new Vector2Int(m_AtlasResolution, m_AtlasResolution);
        rootNode.center = (rootNode.max + rootNode.min) / 2;

        return rootNode;
    }

    void UpdateAllocationInfoFromChild(QuadTreeNode parentNode)
    {
        NodeAllocationInfo parent = m_NodeAllocationInfos[parentNode.index];

        int nextLevelNodeIndexBase = parentNode.index * 4 + 1;
        NodeAllocationInfo child0 = m_NodeAllocationInfos[nextLevelNodeIndexBase + 0];
        NodeAllocationInfo child1 = m_NodeAllocationInfos[nextLevelNodeIndexBase + 1];
        NodeAllocationInfo child2 = m_NodeAllocationInfos[nextLevelNodeIndexBase + 2];
        NodeAllocationInfo child3 = m_NodeAllocationInfos[nextLevelNodeIndexBase + 3];

        bool IsAllChildsEmpty = child0.isEmpty && child1.isEmpty && child2.isEmpty && child3.isEmpty;
        bool IsAllChildsFull = child0.isFull && child1.isFull && child2.isFull && child3.isFull;

        parent.isEmpty = IsAllChildsEmpty;
        parent.isFull = IsAllChildsFull;
    }

	QuadTreeNode AllocateElementRecursive(QuadTreeNode curLevelNode, int targetSize)
    {
        NodeAllocationInfo nodeAllocationInfo = m_NodeAllocationInfos[curLevelNode.index];

        if (nodeAllocationInfo.isFull)
        {
            return new QuadTreeNode();
        }

        // allocate self
        if (curLevelNode.size == targetSize)
        {
            if (nodeAllocationInfo.isEmpty)
            {
                nodeAllocationInfo.isEmpty = false;
                nodeAllocationInfo.isFull = true;
                return curLevelNode;
            }
            return new QuadTreeNode();
        }

        // try allocate from childs
        List<QuadTreeNode> childNodes = GetChildNodes(curLevelNode);
        foreach (QuadTreeNode childNode in childNodes)
        {
            QuadTreeNode result = AllocateElementRecursive(childNode, targetSize);

            // child allocate fail
            if (!IsNodeValid(result))
            {
                continue;
            }

            // if success, mark current node 's allocation flag
            UpdateAllocationInfoFromChild(curLevelNode);
            return result;
        }

        return new QuadTreeNode();
    }

    void ReleaseElementRecursive(QuadTreeNode curLevelNode, QuadTreeNode freeNode)
    {
        NodeAllocationInfo nodeAllocationInfo = m_NodeAllocationInfos[curLevelNode.index];

        // release self
        if (freeNode == curLevelNode)
        {
            if (!nodeAllocationInfo.isFull)
            {
                Debug.LogError("nodeAllocationInfo Bug");
            }

            nodeAllocationInfo.isEmpty = true;
            nodeAllocationInfo.isFull = false;
            return;
        }

        // try release to childs
        int childId = 0;
        childId += (freeNode.center.x > curLevelNode.center.x) ? (0x01 << 0) : 0;   // left or right (ฑฌมห)
        childId += (freeNode.center.y > curLevelNode.center.y) ? (0x01 << 1) : 0;   // top or bottom

        List<QuadTreeNode> childNodes = GetChildNodes(curLevelNode);
        QuadTreeNode childNode = childNodes[childId];

        ReleaseElementRecursive(childNode, freeNode);

        // update allocate flags
        UpdateAllocationInfoFromChild(curLevelNode);
    }

    bool IsNodeValid(QuadTreeNode node)
    {
        return node.size > 0;
    }

    List<QuadTreeNode> GetChildNodes(QuadTreeNode parentNode)
    {
	    int halfSize = parentNode.size / 2;
        int nextLevelNodeIndexBase = parentNode.index * 4 + 1;

        QuadTreeNode topLeft = new QuadTreeNode();
        topLeft.index = nextLevelNodeIndexBase + (int) QuadTreeChild.TC_TopLeft;
	    topLeft.size = halfSize;
	    topLeft.min = parentNode.min + new Vector2Int(0, 0) * halfSize;
	    topLeft.max = parentNode.min + new Vector2Int(1, 1) * halfSize;
	    topLeft.center = (topLeft.max + topLeft.min) / 2;

	    QuadTreeNode topRight = new QuadTreeNode();
        topRight.index = nextLevelNodeIndexBase + (int) QuadTreeChild.TC_TopRight;
	    topRight.size = halfSize;
	    topRight.min = parentNode.min + new Vector2Int(1, 0) * halfSize;
	    topRight.max = parentNode.min + new Vector2Int(2, 1) * halfSize;
	    topRight.center = (topRight.max + topRight.min) / 2;

	    QuadTreeNode bottomLeft = new QuadTreeNode();
        bottomLeft.index = nextLevelNodeIndexBase + (int) QuadTreeChild.TC_BottomLeft;
	    bottomLeft.size = halfSize;
	    bottomLeft.min = parentNode.min + new Vector2Int(0, 1) * halfSize;
	    bottomLeft.max = parentNode.min + new Vector2Int(1, 2) * halfSize;
	    bottomLeft.center = (bottomLeft.max + bottomLeft.min) / 2;

	    QuadTreeNode bottomRight = new QuadTreeNode();
        bottomRight.index = nextLevelNodeIndexBase + (int) QuadTreeChild.TC_BottomRight;
	    bottomRight.size = halfSize;
	    bottomRight.min = parentNode.min + new Vector2Int(1, 1) * halfSize;
	    bottomRight.max = parentNode.min + new Vector2Int(2, 2) * halfSize;
	    bottomRight.center = (bottomRight.max + bottomRight.min) / 2;

	    List<QuadTreeNode> result = new List<QuadTreeNode>();
        result.Add(topLeft);
	    result.Add(topRight);
	    result.Add(bottomLeft);
	    result.Add(bottomRight);
	    return result;
    }
};