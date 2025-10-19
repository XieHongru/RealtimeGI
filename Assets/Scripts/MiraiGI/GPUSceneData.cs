using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEditor;
using UnityEngine;

public struct ObjectInfo
{
    public int objectId;
    public int cardCount;
    public int resolution;
    public int meshId;

    public Vector3 localBoundsMin;
    public Vector3 localBoundsMax;
    public Vector3 worldBoundsMin;
    public Vector3 worldBoundsMax;
    public Matrix4x4 localToWorldMatrix;
    public Matrix4x4 worldToLocalMatrix;
}

public struct MeshInfo
{
    public int meshId;
    public int vertexCount;
    public int vertexOffset;
    public int indexCount;
    public int indexOffset;
}

public class GPUSceneData
{
    // ---------------------------------------------------
    // assume there is no object add or delete in scene,
    // because we are study GI, not streaming load
    // no object spawned or broken
    // ---------------------------------------------------
    // TODO: if we need scene objects update, we need
    // limit max objects count and vertices count,
    // then implement a system of object listen
    // ---------------------------------------------------
    public List<GameObject> objects;            // all object instances in scene 
    public List<ObjectInfo> objectsInfo;        // all object instances info in scene
    public List<Mesh> meshes;
    public List<MeshInfo>   meshesInfo;         // all meshes info in scene
    public List<Vector3>    vertices;           // all vertices in scene (local position)
    public List<int>        indices;            // all meshes tri indices in scene
    public Dictionary<Mesh, MeshInfo> meshMap;  //

    public Vector3 cameraPositionPrev;
    public Vector3 cameraPosition;

    public ComputeBuffer objectInfoBuffer;
    public ComputeBuffer vertexBuffer;
    public ComputeBuffer indexBuffer;

    public void Init()
    {
        objects     = new List<GameObject>();
        objectsInfo = new List<ObjectInfo>();
        meshes      = new List<Mesh>();
        meshesInfo  = new List<MeshInfo>();
        vertices    = new List<Vector3>();
        indices     = new List<int>();
        meshMap     = new Dictionary<Mesh, MeshInfo>();

        // init for all scene objects
        GetAllObjects();

        cameraPosition = Camera.main.transform.position;

        objectInfoBuffer = new ComputeBuffer(objectsInfo.Count, Marshal.SizeOf<ObjectInfo>(), ComputeBufferType.Structured);
        vertexBuffer = new ComputeBuffer(vertices.Count, sizeof(float) * 3, ComputeBufferType.Structured);
        indexBuffer = new ComputeBuffer(indices.Count, sizeof(int), ComputeBufferType.Structured);
        objectInfoBuffer.SetData(objectsInfo);
        vertexBuffer.SetData(vertices);
        indexBuffer.SetData(indices);
    }

    public void Update()
    {
        cameraPositionPrev = cameraPosition;
        cameraPosition = Camera.main.transform.position;

        // TODO: objects remove or add/update
    }

    public void Release()
    {
        objectInfoBuffer?.Release();
        vertexBuffer?.Release();
        indexBuffer?.Release();
        objectInfoBuffer = null;
        vertexBuffer = null;
        indexBuffer = null;
    }

    void GetAllObjects()
    {
        // TODO: Get SkinnedMeshRenderer
        MeshFilter[] meshFilters = GameObject.FindObjectsOfType<MeshFilter>();

        foreach (MeshFilter mf in meshFilters)
        {
            Mesh mesh = mf.sharedMesh;
            if (mesh != null)
            {
                objects.Add(mf.gameObject);

                ObjectInfo objectInfo = new ObjectInfo();
                objectInfo.objectId = objectsInfo.Count;
                objectInfo.cardCount = 6;
                objectInfo.resolution = 32;
                if (meshMap.ContainsKey(mesh))
                {
                    objectInfo.meshId = meshMap[mesh].meshId;
                }
                else
                {
                    objectInfo.meshId = meshesInfo.Count;

                    Vector3[] meshVertices = mesh.vertices;
                    int[] meshIndices = mesh.triangles;

                    MeshInfo meshInfo = new MeshInfo();
                    meshInfo.meshId = meshesInfo.Count;
                    meshInfo.vertexCount = meshVertices.Length;
                    meshInfo.vertexOffset = vertices.Count;
                    meshInfo.indexCount = meshIndices.Length;
                    meshInfo.indexOffset = indices.Count;

                    foreach (Vector3 v in meshVertices)
                        vertices.Add(v);
                    foreach (int i in meshIndices)
                        indices.Add(i);

                    meshes.Add(mesh);
                    meshesInfo.Add(meshInfo);
                    meshMap.Add(mesh, meshInfo);
                }

                objectInfo.localBoundsMin = mesh.bounds.min;
                objectInfo.localBoundsMax = mesh.bounds.max;
                objectInfo.worldBoundsMin = mf.GetComponent<MeshRenderer>().bounds.min;
                objectInfo.worldBoundsMax = mf.GetComponent<MeshRenderer>().bounds.max;
                objectInfo.localToWorldMatrix = mf.transform.localToWorldMatrix;
                objectInfo.worldToLocalMatrix = mf.transform.worldToLocalMatrix;

                objectsInfo.Add(objectInfo);
            }
        }
    }
}