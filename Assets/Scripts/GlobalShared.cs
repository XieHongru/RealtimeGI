using System;
using UnityEngine;

public class GlobalShared
{
    public static int OBJECT_ID_INVALID = -1;
    public static int MAX_CARDS_PER_MESH = 12;
    public static int VOXEL_BLOCK_SIZE = 4;
    public static int VOXEL_COUNT_PER_BLOCK = VOXEL_BLOCK_SIZE * VOXEL_BLOCK_SIZE * VOXEL_BLOCK_SIZE;
    public static int MAX_CASCADE_COUNT = 4;
    public static int PAGE_ID_INVALID = 0x3FFFFFFF;
    public static int PROBE_ID_INVALID = 0x3FFFFFFF;
    public static int MAX_HZB_LEVEL = 6;

    public static int Index3DTo1DLinear(Vector3Int index3D, Vector3Int size3D)
    {
        int res = 0;
        res += index3D.x * 1;
	    res += index3D.y * size3D.x;
        res += index3D.z * (size3D.x * size3D.y);
	    return res;
    }

    public static Vector3Int Index1DTo3DLinear(int index1D, Vector3Int size3D)
    {
        Vector3Int res = new Vector3Int(0, 0, 0);

        res.z = index1D / (size3D.x * size3D.y);
        index1D -= res.z * (size3D.x * size3D.y);

        res.y = index1D / size3D.x;
        index1D -= res.y * size3D.x;

        res.x = index1D;

        return res;
    }

    public static void InitTexture3D(RenderTexture texture, Vector3Int size, string kernel)
    {

    }
}

public enum CardCaptureRTSlot
{
    BaseColor = 0,
    Normal,
    Emissive,
    Depth,
    Num
}