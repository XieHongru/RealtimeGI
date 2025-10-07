using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.XR;

[ExecuteAlways]
public class GIController : MonoBehaviour
{
    public VoxelScene voxelScene;

    // Start is called before the first frame update
    void Start()
    {
        voxelScene = new VoxelScene();
        voxelScene.CreateScene();
    }

    // Update is called once per frame
    void Update()
    {
        voxelScene.UpdateScene();
    }

    [MenuItem("Tools/Search Prefabs")]
    public static void SpecialFunction()
    {
        string[] guids = AssetDatabase.FindAssets("t:Model", new string[] { "Assets/Models" });
        List<string> fbxPaths = new List<string>();

        foreach (string guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            fbxPaths.Add(path);
            Debug.Log("fbx found: " + path);
        }

        foreach (string fbxPath in fbxPaths)
        {
            var fbxObject = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            foreach (var child in fbxObject.GetComponentsInChildren<Transform>())
            {
                Debug.Log("child of " + fbxObject.name + " : " + child.gameObject.name);
            }
        }
    }

    [MenuItem("Tools/Search Meshes")]
    public static void SearchMeshes()
    {
        Dictionary<Mesh, int> meshMap = new Dictionary<Mesh, int>();
        // 获取所有MeshFilter和SkinnedMeshRenderer
        MeshFilter[] meshFilters = GameObject.FindObjectsOfType<MeshFilter>();
        SkinnedMeshRenderer[] skinnedMeshRenderers = GameObject.FindObjectsOfType<SkinnedMeshRenderer>();

        // 处理普通Mesh
        foreach (MeshFilter mf in meshFilters)
        {
            if (mf.sharedMesh != null)
            {
                if (!meshMap.ContainsKey(mf.sharedMesh))
                {
                    meshMap.Add(mf.sharedMesh, 0);
                }
                else
                {
                    meshMap[mf.sharedMesh]++;
                }
                //Debug.Log($"local bounds: {mf.sharedMesh.bounds}, world bounds: {mf.GetComponent<MeshRenderer>().bounds}");
            }
        }
        Debug.Log($"there are {meshMap.Count} meshes");

        // 处理SkinnedMesh
        foreach (SkinnedMeshRenderer smr in skinnedMeshRenderers)
        {
            if (smr.sharedMesh != null)
            {
                
            }
        }
    }
}
