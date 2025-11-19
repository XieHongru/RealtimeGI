using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class systemTest : MonoBehaviour
{
    // Start is called before the first frame update
    void Start()
    {
        
    }

    // Update is called once per frame
    void Update()
    {
        Debug.Log(SystemInfo.usesReversedZBuffer);
    }
}
