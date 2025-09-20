using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public class GIController : MonoBehaviour
{
    public VoxelScene VoxelScene;

    // Start is called before the first frame update
    void Start()
    {
        VoxelScene = new VoxelScene();
        VoxelScene.CreateScene();
    }

    // Update is called once per frame
    void Update()
    {
        
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
}
