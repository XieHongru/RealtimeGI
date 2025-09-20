using UnityEngine;

public class SceneData
{
    public ComputeBuffer vertexBuffer;
    public ComputeBuffer indexBuffer;

    public void Release()
    {
        vertexBuffer.Release();
        indexBuffer.Release();

        vertexBuffer = null;
        indexBuffer = null;
    }
}