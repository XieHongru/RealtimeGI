using System.Collections;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

public class CascadeData
{
    public List<GameObject> objects = new List<GameObject>();
    public List<float3> vertices = new List<float3>();
    public List<int> indices = new List<int>();

    public ComputeBuffer vertexBuffer;
    public ComputeBuffer indexBuffer;
}
